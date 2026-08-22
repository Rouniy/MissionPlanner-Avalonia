using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MissionPlanner;
using MissionPlanner.Comms;
using MissionPlanner.Utilities;

namespace MissionPlannerAvalonia.Services;

internal enum ConnectionListTransport {
  TcpClient,
  UdpListener,
  UdpClient,
  Serial,
}

internal sealed record ConnectionListEndpoint(
    ConnectionListTransport Transport,
    string Host,
    int Port,
    string SerialPort,
    int BaudRate,
    int SourceLine) {
  internal string Canonical => Transport switch {
    ConnectionListTransport.TcpClient => $"tcp://{NormalizeHost(Host)}:{Port}",
    ConnectionListTransport.UdpListener => $"udp://{NormalizeHost(Host)}:{Port}",
    ConnectionListTransport.UdpClient => $"udpcl://{NormalizeHost(Host)}:{Port}",
    ConnectionListTransport.Serial => $"serial:{SerialPort}:{BaudRate}",
    _ => throw new ArgumentOutOfRangeException(),
  };

  internal string DisplayName => Canonical;

  private static string NormalizeHost(string value) {
    string host = value.Trim();
    if (host.Length > 1 && host[0] == '[' && host[^1] == ']') {
      host = host[1..^1];
    }
    host = host.ToLowerInvariant();
    return host.Contains(':') ? $"[{host}]" : host;
  }
}

internal sealed record ConnectionListParseError(int Line, string Text, string Message);

internal sealed record ConnectionListParseResult(
    IReadOnlyList<ConnectionListEndpoint> Endpoints,
    IReadOnlyList<ConnectionListParseError> Errors);

/// <summary>
/// Parses the exact endpoint families accepted by Mission Planner's Connection List action.
/// Blank lines and comments are a port extension so operators can document large modem lists.
/// </summary>
internal static partial class ConnectionListParser {
  [GeneratedRegex(@"^(tcp|udp|udpcl)://(\[[^\]]+\]|[^:]+):([0-9]+)$",
      RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
  private static partial Regex NetworkPattern();

  [GeneratedRegex(@"^serial:(.+):([0-9]+)$",
      RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
  private static partial Regex SerialPattern();

  internal static ConnectionListParseResult Parse(IEnumerable<string> lines) {
    ArgumentNullException.ThrowIfNull(lines);
    var endpoints = new List<ConnectionListEndpoint>();
    var errors = new List<ConnectionListParseError>();
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    int lineNumber = 0;

    foreach (string? raw in lines) {
      lineNumber++;
      string text = (raw ?? "").Trim();
      if (text.Length == 0 || text.StartsWith('#') || text.StartsWith(';')) {
        continue;
      }

      ConnectionListEndpoint? endpoint = ParseLine(text, lineNumber, out string? error);
      if (endpoint == null) {
        errors.Add(new ConnectionListParseError(
            lineNumber, text, error ?? "Unsupported connection entry."));
        continue;
      }
      if (!seen.Add(endpoint.Canonical)) {
        errors.Add(new ConnectionListParseError(
            lineNumber, text, "Duplicate connection entry."));
        continue;
      }
      endpoints.Add(endpoint);
    }

    return new ConnectionListParseResult(endpoints, errors);
  }

  private static ConnectionListEndpoint? ParseLine(
      string text, int sourceLine, out string? error) {
    error = null;
    Match network = NetworkPattern().Match(text);
    if (network.Success) {
      string scheme = network.Groups[1].Value.ToLowerInvariant();
      string host = network.Groups[2].Value;
      if (!TryPort(network.Groups[3].Value, out int port)) {
        error = "Network port must be between 1 and 65535.";
        return null;
      }
      if (string.IsNullOrWhiteSpace(host)) {
        error = "Network host cannot be empty.";
        return null;
      }
      return new ConnectionListEndpoint(
          scheme switch {
            "tcp" => ConnectionListTransport.TcpClient,
            "udp" => ConnectionListTransport.UdpListener,
            "udpcl" => ConnectionListTransport.UdpClient,
            _ => throw new InvalidOperationException(),
          },
          host, port, "", 0, sourceLine);
    }

    Match serial = SerialPattern().Match(text);
    if (serial.Success) {
      string serialPort = serial.Groups[1].Value.Trim();
      if (serialPort.Length == 0) {
        error = "Serial port cannot be empty.";
        return null;
      }
      if (!int.TryParse(serial.Groups[2].Value, NumberStyles.None,
              CultureInfo.InvariantCulture, out int baud) || baud <= 0) {
        error = "Serial baud rate must be a positive integer.";
        return null;
      }
      return new ConnectionListEndpoint(
          ConnectionListTransport.Serial, "", 0, serialPort, baud, sourceLine);
    }

    error = "Expected tcp://host:port, udp://host:port, udpcl://host:port, " +
        "or serial:port:baud.";
    return null;
  }

  private static bool TryPort(string text, out int port) =>
      int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out port) &&
      port is >= 1 and <= 65535;
}

internal sealed record ConnectionListOpenFailure(
    ConnectionListEndpoint Endpoint, string Message);

internal sealed record ConnectionListOpenResult(
    IReadOnlyList<MavLinkConnection> Opened,
    IReadOnlyList<ConnectionListOpenFailure> Failures,
    IReadOnlyList<ConnectionListParseError> ParseErrors) {
  internal int Requested => Opened.Count + Failures.Count;
}

internal static class ConnectionListService {
  private sealed record OpenOutcome(
      MavLinkConnection? Connection,
      ConnectionListOpenFailure? Failure);

  internal static async Task<ConnectionListOpenResult> OpenFileAsync(
      string path,
      MavLinkConnectionManager manager,
      CancellationToken cancellationToken = default,
      Action<int, string>? progress = null,
      bool openTelemetryLogs = true) {
    ArgumentException.ThrowIfNullOrWhiteSpace(path);
    if (!File.Exists(path)) {
      throw new FileNotFoundException("Connection list not found.", path);
    }
    ConnectionListParseResult parsed = ConnectionListParser.Parse(
        await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false));
    if (parsed.Endpoints.Count == 0) {
      progress?.Invoke(100, "No valid connection entries found.");
      return new ConnectionListOpenResult([], [], parsed.Errors);
    }

    // Upstream Mission Planner opens Connection List rows in parallel. Keep that behavior for
    // modem fleets, but cap concurrency so a large field file cannot exhaust the thread pool.
    int maximumConcurrency = Math.Min(8, parsed.Endpoints.Count);
    using var concurrency = new SemaphoreSlim(maximumConcurrency, maximumConcurrency);
    int completed = 0;
    Task<OpenOutcome>[] tasks = parsed.Endpoints.Select(async endpoint => {
      await concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
      try {
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Invoke(
            Volatile.Read(ref completed) * 100 / parsed.Endpoints.Count,
            $"Opening {endpoint.DisplayName}…");
        return await OpenEndpointAsync(
                endpoint, manager, cancellationToken, openTelemetryLogs)
            .ConfigureAwait(false);
      } finally {
        int done = Interlocked.Increment(ref completed);
        progress?.Invoke(done * 100 / parsed.Endpoints.Count,
            $"Processed {done} of {parsed.Endpoints.Count} connection(s)…");
        concurrency.Release();
      }
    }).ToArray();

    OpenOutcome[] outcomes = await Task.WhenAll(tasks).ConfigureAwait(false);
    MavLinkConnection[] opened = [.. outcomes
        .Where(outcome => outcome.Connection != null)
        .Select(outcome => outcome.Connection!)];
    ConnectionListOpenFailure[] failures = [.. outcomes
        .Where(outcome => outcome.Failure != null)
        .Select(outcome => outcome.Failure!)];
    progress?.Invoke(100,
        $"Opened {opened.Length} of {parsed.Endpoints.Count} connection(s).");
    return new ConnectionListOpenResult(opened, failures, parsed.Errors);
  }

  private static async Task<OpenOutcome> OpenEndpointAsync(
      ConnectionListEndpoint endpoint,
      MavLinkConnectionManager manager,
      CancellationToken cancellationToken,
      bool openTelemetryLogs) {
    if (manager.ContainsEndpoint(endpoint.Canonical)) {
      return new OpenOutcome(null,
          new ConnectionListOpenFailure(endpoint, "Connection is already open."));
    }

    MAVLinkInterface? link = null;
    ICommsSerial? stream = null;
    try {
      stream = CreateStream(endpoint);
      link = new MAVLinkInterface { BaseStream = stream };
      ViewModels.ConnectionViewModel.ResetAllVehicleParameters(link);
      using CancellationTokenRegistration cancellation = cancellationToken.Register(() => {
        try {
          stream.Close();
        } catch {
        }
      });
      // Closing the transport is what interrupts upstream's synchronous heartbeat wait. Do not
      // rely on Task.Run cancellation alone: it cannot stop work that has already started.
      await Task.Run(() => {
        using IDisposable progressScope = MavLinkProgressContext.Use(cancellationToken);
        link.Open(getparams: false, skipconnectedcheck: true, showui: true);
      })
          .ConfigureAwait(false);
      cancellationToken.ThrowIfCancellationRequested();
      if (link.BaseStream?.IsOpen != true) {
        throw new IOException("The transport closed before MAVLink was detected.");
      }
      if (openTelemetryLogs) {
        OpenTelemetryLogs(link, endpoint);
      }
      MavLinkConnection connection = manager.Add(
          link, endpoint,
          item => new MavLinkSecondaryRuntime(item, manager.NotifyClosed));
      link = null;
      stream = null;
      return new OpenOutcome(connection, null);
    } catch (Exception) when (cancellationToken.IsCancellationRequested) {
      CloseUnregistered(link, stream);
      throw new OperationCanceledException(cancellationToken);
    } catch (Exception ex) {
      CloseUnregistered(link, stream);
      return new OpenOutcome(null,
          new ConnectionListOpenFailure(endpoint, UserMessage(ex)));
    }
  }

  private static void CloseUnregistered(MAVLinkInterface? link, ICommsSerial? stream) {
    if (link != null) {
      MavLinkConnectionManager.SafeClose(link);
      return;
    }
    try {
      stream?.Close();
    } catch {
    }
    (stream as IDisposable)?.Dispose();
  }

  internal static ICommsSerial CreateStream(ConnectionListEndpoint endpoint) =>
      endpoint.Transport switch {
        ConnectionListTransport.TcpClient =>
            ViewModels.ConnectionViewModel.CreateConfiguredNetworkStream(
                "TCP", NormalizeHost(endpoint.Host), endpoint.Port.ToString(CultureInfo.InvariantCulture)),
        ConnectionListTransport.UdpListener => new UdpSerial(
            new UdpClient(endpoint.Port)),
        ConnectionListTransport.UdpClient =>
            ViewModels.ConnectionViewModel.CreateConfiguredNetworkStream(
                "UDPCl", NormalizeHost(endpoint.Host),
                endpoint.Port.ToString(CultureInfo.InvariantCulture)),
        ConnectionListTransport.Serial => new SerialPort {
          PortName = endpoint.SerialPort,
          BaudRate = endpoint.BaudRate,
          espFix = Settings.Instance.GetBoolean("CHK_rtsresetesp32", false),
        },
        _ => throw new ArgumentOutOfRangeException(nameof(endpoint)),
      };

  private static string NormalizeHost(string host) =>
      host.Length > 1 && host[0] == '[' && host[^1] == ']'
          ? host[1..^1]
          : host;

  private static void OpenTelemetryLogs(
      MAVLinkInterface link, ConnectionListEndpoint endpoint) {
    try {
      Directory.CreateDirectory(Settings.Instance.LogDir);
      string safeEndpoint = new(endpoint.Canonical
          .Select(character => char.IsLetterOrDigit(character) ? character : '-')
          .ToArray());
      safeEndpoint = safeEndpoint.Trim('-');
      if (safeEndpoint.Length > 48) {
        safeEndpoint = safeEndpoint[..48];
      }
      string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture);
      string stem = $"{timestamp}-{safeEndpoint}";
      string tlog = Path.Combine(Settings.Instance.LogDir, stem + ".tlog");
      string rlog = Path.Combine(Settings.Instance.LogDir, stem + ".rlog");
      int suffix = 1;
      while (File.Exists(tlog) || File.Exists(rlog)) {
        tlog = Path.Combine(Settings.Instance.LogDir, $"{stem}-{suffix}.tlog");
        rlog = Path.Combine(Settings.Instance.LogDir, $"{stem}-{suffix}.rlog");
        suffix++;
      }
      link.logfile = new BufferedStream(
          File.Open(tlog, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None));
      link.rawlogfile = new BufferedStream(
          File.Open(rlog, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None));
    } catch {
      CloseTelemetryLogs(link);
      // A read-only log directory must not prevent a modem link from being usable.
    }
  }

  internal static void CloseTelemetryLogs(MAVLinkInterface link) {
    try {
      link.logfile?.Close();
    } catch {
    }
    try {
      link.rawlogfile?.Close();
    } catch {
    }
    link.logfile = null;
    link.rawlogfile = null;
  }

  private static string UserMessage(Exception exception) {
    Exception current = exception;
    while (current.InnerException != null &&
           current is AggregateException or System.Reflection.TargetInvocationException) {
      current = current.InnerException;
    }
    return string.IsNullOrWhiteSpace(current.Message)
        ? current.GetType().Name
        : current.Message;
  }
}

internal sealed class MavLinkConnection {
  internal MavLinkConnection(
      MAVLinkInterface link, string endpoint, bool primary, ConnectionListEndpoint? source) {
    Link = link;
    Endpoint = endpoint;
    IsPrimary = primary;
    Source = source;
  }

  internal MAVLinkInterface Link { get; }
  internal string Endpoint { get; set; }
  internal bool IsPrimary { get; }
  internal ConnectionListEndpoint? Source { get; }
  internal MavLinkSecondaryRuntime? Runtime { get; set; }
  internal bool IsOpen => Link.BaseStream?.IsOpen == true;
}

/// <summary>
/// Thread-safe equivalent of MainV2.Comports plus its active comPort pointer. The primary entry is
/// permanent; imported entries are removed when they close. Switching never aliases MAVState data.
/// </summary>
internal sealed class MavLinkConnectionManager : IDisposable {
  private readonly object _sync = new();
  private readonly List<MavLinkConnection> _connections = [];
  private MavLinkConnection _active;
  private bool _disposed;

  internal MavLinkConnectionManager(MAVLinkInterface primary) {
    Primary = new MavLinkConnection(primary, "Primary", primary: true, source: null);
    _connections.Add(Primary);
    _active = Primary;
  }

  internal MavLinkConnection Primary { get; }

  internal MavLinkConnection Active {
    get {
      lock (_sync) {
        return _active;
      }
    }
  }

  internal event Action? Changed;
  internal event Action<MavLinkConnection, MavLinkConnection>? ActiveChanged;

  internal IReadOnlyList<MavLinkConnection> Snapshot() {
    lock (_sync) {
      return _connections.ToArray();
    }
  }

  internal MavLinkConnection? Find(MAVLinkInterface link) {
    lock (_sync) {
      return _connections.FirstOrDefault(item => ReferenceEquals(item.Link, link));
    }
  }

  internal bool ContainsEndpoint(string canonical) {
    lock (_sync) {
      return _connections.Any(connection => connection.Source != null &&
          string.Equals(connection.Source.Canonical, canonical,
              StringComparison.OrdinalIgnoreCase));
    }
  }

  internal MavLinkConnection Add(
      MAVLinkInterface link, ConnectionListEndpoint endpoint,
      Func<MavLinkConnection, MavLinkSecondaryRuntime>? runtimeFactory = null) {
    ArgumentNullException.ThrowIfNull(link);
    ArgumentNullException.ThrowIfNull(endpoint);
    MavLinkConnection connection;
    lock (_sync) {
      ObjectDisposedException.ThrowIf(_disposed, this);
      if (_connections.Any(item => ReferenceEquals(item.Link, link))) {
        throw new InvalidOperationException("The MAVLink interface is already registered.");
      }
      if (_connections.Any(item => item.Source != null &&
          string.Equals(item.Source.Canonical, endpoint.Canonical,
              StringComparison.OrdinalIgnoreCase))) {
        throw new InvalidOperationException($"Connection already exists: {endpoint.Canonical}");
      }
      connection = new MavLinkConnection(link, endpoint.DisplayName, primary: false, endpoint);
      _connections.Add(connection);
    }

    try {
      connection.Runtime = runtimeFactory?.Invoke(connection);
      connection.Runtime?.Start();
    } catch {
      lock (_sync) {
        _connections.Remove(connection);
      }
      throw;
    }
    Changed?.Invoke();
    return connection;
  }

  internal bool SetActive(MavLinkConnection connection) {
    ArgumentNullException.ThrowIfNull(connection);
    MavLinkConnection previous;
    lock (_sync) {
      ObjectDisposedException.ThrowIf(_disposed, this);
      if (!_connections.Contains(connection) || !connection.IsOpen) {
        return false;
      }
      if (ReferenceEquals(_active, connection)) {
        return true;
      }
      previous = _active;
      _active = connection;
    }
    ActiveChanged?.Invoke(previous, connection);
    Changed?.Invoke();
    return true;
  }

  internal bool SetActive(MAVLinkInterface link) {
    ArgumentNullException.ThrowIfNull(link);
    MavLinkConnection? connection;
    lock (_sync) {
      connection = _connections.FirstOrDefault(item => ReferenceEquals(item.Link, link));
    }
    return connection != null && SetActive(connection);
  }

  internal async Task<bool> RemoveAsync(MavLinkConnection connection, bool close = true) {
    ArgumentNullException.ThrowIfNull(connection);
    if (connection.IsPrimary) {
      return false;
    }

    MavLinkConnection? previous = null;
    MavLinkConnection? replacement = null;
    lock (_sync) {
      if (!_connections.Remove(connection)) {
        return false;
      }
      if (ReferenceEquals(_active, connection)) {
        previous = connection;
        replacement = _connections.FirstOrDefault(item => item.IsOpen) ?? Primary;
        _active = replacement;
      }
    }

    // Publish fallback before waiting for the old reader to stop. Parameter views must clear and
    // switch immediately even if closing a transport takes a moment.
    if (previous != null && replacement != null) {
      ActiveChanged?.Invoke(previous, replacement);
    }
    if (connection.Runtime != null) {
      await connection.Runtime.StopAsync(close).ConfigureAwait(false);
    } else if (close) {
      SafeClose(connection.Link);
    }
    Changed?.Invoke();
    return true;
  }

  internal void NotifyClosed(MavLinkConnection connection) {
    if (connection.IsPrimary) {
      MavLinkConnection? primaryReplacement = null;
      lock (_sync) {
        if (ReferenceEquals(_active, connection)) {
          primaryReplacement = _connections.FirstOrDefault(item => !item.IsPrimary && item.IsOpen);
          if (primaryReplacement != null) {
            _active = primaryReplacement;
          }
        }
      }
      if (primaryReplacement != null) {
        ActiveChanged?.Invoke(connection, primaryReplacement);
      }
      Changed?.Invoke();
      return;
    }
    MavLinkConnection? replacement = null;
    bool activeChanged = false;
    lock (_sync) {
      if (!_connections.Remove(connection)) {
        return;
      }
      if (ReferenceEquals(_active, connection)) {
        replacement = _connections.FirstOrDefault(item => item.IsOpen) ?? Primary;
        _active = replacement;
        activeChanged = true;
      }
    }
    // The runtime calls this method from its own finally block, so do not ask it to await itself.
    SafeClose(connection.Link);
    if (activeChanged && replacement != null) {
      ActiveChanged?.Invoke(connection, replacement);
    }
    Changed?.Invoke();
  }

  public void Dispose() {
    MavLinkConnection[] extras;
    lock (_sync) {
      if (_disposed) {
        return;
      }
      _disposed = true;
      extras = _connections.Where(connection => !connection.IsPrimary).ToArray();
      _connections.RemoveAll(connection => !connection.IsPrimary);
      _active = Primary;
    }
    foreach (MavLinkConnection connection in extras) {
      try {
        if (connection.Runtime != null) {
          connection.Runtime.StopAsync(close: true).GetAwaiter().GetResult();
        } else {
          SafeClose(connection.Link);
        }
      } catch {
        SafeClose(connection.Link);
      }
    }
  }

  internal static void SafeClose(MAVLinkInterface link) {
    ConnectionListService.CloseTelemetryLogs(link);
    try {
      link.Close();
    } catch {
    }
    try {
      link.Dispose();
    } catch {
    }
  }
}

/// <summary>
/// Owns reads and heartbeats for one imported/passive Mission Planner connection. The primary link
/// keeps its existing richer UI lifecycle; both paths apply the same silent-link safety policy.
/// </summary>
internal sealed class MavLinkSecondaryRuntime {
  private readonly MavLinkConnection _connection;
  private readonly Action<MavLinkConnection> _closed;
  private readonly TimeSpan _silenceTimeout;
  private readonly CancellationTokenSource _shutdown = new();
  private Task? _runner;
  private DateTime _connectedAtUtc = DateTime.UtcNow;
  private DateTime _lastHeartbeatUtc = DateTime.MinValue;
  private DateTime _lastVersionPollUtc = DateTime.MinValue;
  private bool _lastArmed;
  private int _homeRefreshRunning;

  internal MavLinkSecondaryRuntime(
      MavLinkConnection connection, Action<MavLinkConnection> closed,
      TimeSpan? silenceTimeout = null) {
    _connection = connection;
    _closed = closed;
    _silenceTimeout = silenceTimeout ?? TimeSpan.FromSeconds(10);
    if (_silenceTimeout <= TimeSpan.Zero) {
      throw new ArgumentOutOfRangeException(nameof(silenceTimeout));
    }
  }

  internal void Start() {
    if (_runner != null) {
      throw new InvalidOperationException("Connection reader already started.");
    }
    ApplyRatesAndRequestStreams(_connection.Link);
    _connectedAtUtc = DateTime.UtcNow;
    _runner = Task.Run(() => RunAsync(_shutdown.Token));
  }

  internal async Task StopAsync(bool close) {
    try {
      _shutdown.Cancel();
    } catch (ObjectDisposedException) {
    }
    if (close) {
      try {
        _connection.Link.Close();
      } catch {
      }
    }
    if (_runner != null && Task.CurrentId != _runner.Id) {
      try {
        await _runner.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
      } catch (OperationCanceledException) {
      } catch (TimeoutException) {
      }
    }
    _shutdown.Dispose();
    if (close) {
      try {
        _connection.Link.Dispose();
      } catch {
      }
    }
  }

  private async Task RunAsync(CancellationToken cancellationToken) {
    int consecutiveErrors = 0;
    bool unexpectedlyClosed = false;
    try {
      while (!cancellationToken.IsCancellationRequested) {
        MAVLinkInterface link = _connection.Link;
        if (link.BaseStream?.IsOpen != true) {
          unexpectedlyClosed = true;
          break;
        }
        try {
          DateTime now = DateTime.UtcNow;
          if (!link.giveComport) {
            DateTime newest = NewestPacketUtc(link);
            if (ViewModels.ConnectionHealth.IsSilent(
                    now, newest, _connectedAtUtc, _silenceTimeout)) {
              SetLinkQualityLost(link);
              if (ViewModels.ConnectionHealth.ShouldCloseSilentLink(
                      link.MAV.cs.armed, now, newest, _connectedAtUtc, _silenceTimeout)) {
                unexpectedlyClosed = true;
                break;
              }
            }

            DateTime readDeadline = now.AddSeconds(1);
            while (!link.giveComport && link.BaseStream?.IsOpen == true &&
                   link.BaseStream.BytesToRead > 10 && !cancellationToken.IsCancellationRequested &&
                   DateTime.UtcNow < readDeadline) {
              await link.readPacketAsync().ConfigureAwait(false);
            }
            foreach (MAVState mav in link.MAVlist) {
              mav.cs.UpdateCurrentSettings(null, false, link, mav);
            }
            RefreshHomeOnArmTransition(link, cancellationToken);
          }
          RunPeriodicWork(link, now);
          consecutiveErrors = 0;
          await Task.Delay(link.giveComport ? 50 : 1, cancellationToken).ConfigureAwait(false);
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
          break;
        } catch {
          if (++consecutiveErrors >= 5) {
            unexpectedlyClosed = true;
            break;
          }
          await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
      }
    } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
    } finally {
      if (unexpectedlyClosed && !cancellationToken.IsCancellationRequested) {
        ViewModels.ConnectionViewModel.ResetAllVehicleParameters(_connection.Link);
        try {
          _connection.Link.Close();
        } catch {
        }
        _closed(_connection);
      }
    }
  }

  private void RunPeriodicWork(MAVLinkInterface link, DateTime now) {
    if (now >= _lastHeartbeatUtc.AddSeconds(1)) {
      _lastHeartbeatUtc = now;
      if (Settings.Instance.GetBoolean("CHK_GCSheartbeat", true)) {
        SendHeartbeat(link);
      }
      if (!link.giveComport && !link.MAV.cs.armed &&
          now > _connectedAtUtc.AddSeconds(60)) {
        link.getParamPoll();
        link.getParamPoll();
      }
    }
    if (!link.giveComport && now >= _lastVersionPollUtc.AddSeconds(20)) {
      _lastVersionPollUtc = now;
      foreach (MAVState mav in link.MAVlist) {
        if (mav.cs.capabilities == 0 && mav.cs.version < new Version(0, 1)) {
          link.getVersion(mav.sysid, mav.compid, false);
        }
      }
    }
  }

  private static void ApplyRatesAndRequestStreams(MAVLinkInterface link) {
    foreach (MAVState mav in link.MAVlist) {
      mav.cs.rateattitude = CurrentState.rateattitudebackup;
      mav.cs.rateposition = CurrentState.ratepositionbackup;
      mav.cs.ratestatus = CurrentState.ratestatusbackup;
      mav.cs.ratesensors = CurrentState.ratesensorsbackup;
      mav.cs.raterc = CurrentState.ratercbackup;
    }
    try {
      foreach (MAVState mav in link.MAVlist) {
        link.requestDatastream(MAVLink.MAV_DATA_STREAM.EXTENDED_STATUS,
            mav.cs.ratestatus, mav.sysid, mav.compid);
        link.requestDatastream(MAVLink.MAV_DATA_STREAM.POSITION,
            mav.cs.rateposition, mav.sysid, mav.compid);
        link.requestDatastream(MAVLink.MAV_DATA_STREAM.EXTRA1,
            mav.cs.rateattitude, mav.sysid, mav.compid);
        link.requestDatastream(MAVLink.MAV_DATA_STREAM.EXTRA2,
            mav.cs.rateattitude, mav.sysid, mav.compid);
        link.requestDatastream(MAVLink.MAV_DATA_STREAM.EXTRA3,
            mav.cs.ratesensors, mav.sysid, mav.compid);
        link.requestDatastream(MAVLink.MAV_DATA_STREAM.RAW_SENSORS,
            mav.cs.ratesensors, mav.sysid, mav.compid);
        link.requestDatastream(MAVLink.MAV_DATA_STREAM.RC_CHANNELS,
            mav.cs.raterc, mav.sysid, mav.compid);
      }
    } catch {
    }
  }

  private static DateTime NewestPacketUtc(MAVLinkInterface link) =>
      link.MAVlist.ToArray().Select(mav => mav.lastvalidpacket).DefaultIfEmpty().Max();

  private static void SetLinkQualityLost(MAVLinkInterface link) {
    foreach (MAVState mav in link.MAVlist) {
      mav.cs.linkqualitygcs = 0;
    }
  }

  private static void SendHeartbeat(MAVLinkInterface link) {
    try {
      MAVState mav = link.MAV;
      link.sendPacket(new MAVLink.mavlink_heartbeat_t {
        type = (byte)MAVLink.MAV_TYPE.GCS,
        autopilot = (byte)MAVLink.MAV_AUTOPILOT.INVALID,
        mavlink_version = 3,
      }, mav.sysid, mav.compid);
    } catch {
    }
  }

  private void RefreshHomeOnArmTransition(MAVLinkInterface link, CancellationToken token) {
    bool armed = link.MAV.cs.armed;
    bool becameArmed = armed && !_lastArmed;
    _lastArmed = armed;
    if (!becameArmed || link.MAV.apname == MAVLink.MAV_AUTOPILOT.INVALID ||
        link.MAV.aptype == MAVLink.MAV_TYPE.GIMBAL ||
        Interlocked.Exchange(ref _homeRefreshRunning, 1) != 0) {
      return;
    }
    _ = Task.Run(async () => {
      try {
        while (link.giveComport && link.BaseStream?.IsOpen == true) {
          await Task.Delay(100, token).ConfigureAwait(false);
        }
        if (!token.IsCancellationRequested && link.BaseStream?.IsOpen == true) {
          link.MAV.cs.HomeLocation = new PointLatLngAlt(
              link.getWP(link.MAV.sysid, link.MAV.compid, 0));
        }
      } catch (OperationCanceledException) {
      } catch {
      } finally {
        Interlocked.Exchange(ref _homeRefreshRunning, 0);
      }
    }, token);
  }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MissionPlanner.Utilities;

namespace MissionPlannerAvalonia.Services;

internal sealed record ExternalAdsbOptions {
  internal const string DefaultServer = "https://api.adsb.lol";
  internal const int DefaultPort = 30003;

  internal ExternalAdsbOptions(bool enabled, string? server, int port) {
    Enabled = enabled;
    Server = NormalizeServer(server);
    Port = port is >= 1 and <= 65535 ? port : DefaultPort;
  }

  internal bool Enabled { get; }
  internal string Server { get; }
  internal int Port { get; }
  internal bool IsHttp => Uri.TryCreate(Server, UriKind.Absolute, out Uri? uri)
      && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

  internal static string NormalizeServer(string? server) => (server ?? "").Trim().TrimEnd('/');
}

internal sealed record HttpPollResult(HttpStatusCode StatusCode, int AircraftCount);

/// <summary>
/// Cancellable replacement for upstream Utilities.adsb's static Thread/Thread.Abort lifecycle.
/// It accepts the same SBS-1, AVR, Beast and adsb.lol-compatible HTTP inputs.
/// </summary>
internal sealed class ExternalAdsbReceiver : IAsyncDisposable {
  private static readonly (string Host, int Port)[] _fallbackEndpoints = {
    ("127.0.0.1", 30003),
    ("127.0.0.1", 30002),
    ("127.0.0.1", 31004),
    ("127.0.0.1", 31001),
    ("127.0.0.1", 47806),
  };
  private readonly Action<adsb.PointLatLngAltHdg> _onPlane;
  private readonly Func<(double Lat, double Lng)?> _positionProvider;
  private readonly HttpClient _http;
  private readonly bool _ownsHttp;
  private readonly SemaphoreSlim _lifecycle = new(1, 1);
  private readonly object _stateSync = new();
  private CancellationTokenSource? _runCancellation;
  private Task? _runTask;
  private TcpClient? _activeClient;
  private string _status = "external ADS-B disabled";
  private bool _disposed;

  internal ExternalAdsbReceiver(Action<adsb.PointLatLngAltHdg> onPlane,
      Func<(double Lat, double Lng)?> positionProvider, HttpClient? httpClient = null) {
    _onPlane = onPlane;
    _positionProvider = positionProvider;
    _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    _ownsHttp = httpClient is null;
  }

  internal string Status {
    get {
      lock (_stateSync) {
        return _status;
      }
    }
  }

  internal async Task ConfigureAsync(ExternalAdsbOptions options) {
    await _lifecycle.WaitAsync().ConfigureAwait(false);
    try {
      ThrowIfDisposed();
      await StopCoreAsync().ConfigureAwait(false);
      if (!options.Enabled) {
        SetStatus("external ADS-B disabled");
        return;
      }
      _runCancellation = new CancellationTokenSource();
      _runTask = Task.Run(() => RunAsync(options, _runCancellation.Token));
    } finally {
      _lifecycle.Release();
    }
  }

  public async ValueTask DisposeAsync() {
    await _lifecycle.WaitAsync().ConfigureAwait(false);
    try {
      if (_disposed) {
        return;
      }
      _disposed = true;
      await StopCoreAsync().ConfigureAwait(false);
      if (_ownsHttp) {
        _http.Dispose();
      }
    } finally {
      _lifecycle.Release();
    }
  }

  internal static string BuildHttpUrl(string server, double lat, double lng, int radius) =>
      string.Create(CultureInfo.InvariantCulture,
          $"{ExternalAdsbOptions.NormalizeServer(server)}/v2/point/{lat}/{lng}/{radius}");

  internal static int NextHttpRadius(int currentRadius, int aircraftCount) => aircraftCount < 10
      ? Math.Min(currentRadius + 10, 250)
      : currentRadius;

  internal static int NextHttpDelay(int currentDelayMilliseconds) =>
      Math.Min(currentDelayMilliseconds * 2, 10_000);

  internal async Task<HttpPollResult> PollHttpOnceAsync(
      ExternalAdsbOptions options, int radius, CancellationToken cancellationToken) {
    if (!options.IsHttp) {
      throw new ArgumentException("HTTP polling requires an HTTP(S) ADS-B server.", nameof(options));
    }
    if (_positionProvider() is not { } position) {
      return new HttpPollResult(HttpStatusCode.NoContent, 0);
    }

    string url = BuildHttpUrl(options.Server, position.Lat, position.Lng, radius);
    using var request = new HttpRequestMessage(HttpMethod.Get, url);
    string version = typeof(ExternalAdsbReceiver).Assembly.GetName().Version?.ToString() ?? "unknown";
    request.Headers.UserAgent.ParseAdd($"Mission-Planner/{version}");
    using HttpResponseMessage response = await _http.SendAsync(
        request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
    if (!response.IsSuccessStatusCode) {
      return new HttpPollResult(response.StatusCode, 0);
    }

    await using Stream body = await response.Content.ReadAsStreamAsync(cancellationToken)
        .ConfigureAwait(false);
    using JsonDocument document = await JsonDocument.ParseAsync(body,
        cancellationToken: cancellationToken).ConfigureAwait(false);
    if (!document.RootElement.TryGetProperty("ac", out JsonElement aircraft)
        || aircraft.ValueKind != JsonValueKind.Array) {
      return new HttpPollResult(response.StatusCode, 0);
    }

    int count = 0;
    foreach (JsonElement item in aircraft.EnumerateArray()) {
      if (TryDecodeHttpAircraft(item, out adsb.PointLatLngAltHdg? plane)) {
        _onPlane(plane!);
        count++;
      }
    }
    return new HttpPollResult(response.StatusCode, count);
  }

  private async Task RunAsync(ExternalAdsbOptions options, CancellationToken cancellationToken) {
    try {
      if (options.IsHttp) {
        await RunHttpAsync(options, cancellationToken).ConfigureAwait(false);
        return;
      }

      while (!cancellationToken.IsCancellationRequested) {
        if (options.Server.Length > 0) {
          await TryTcpEndpointAsync(options.Server, options.Port, TimeSpan.FromSeconds(5),
              cancellationToken).ConfigureAwait(false);
        }
        foreach (var endpoint in _fallbackEndpoints) {
          if (cancellationToken.IsCancellationRequested) {
            break;
          }
          if (options.Server.Equals(endpoint.Host, StringComparison.OrdinalIgnoreCase)
              && options.Port == endpoint.Port) {
            continue;
          }
          await TryTcpEndpointAsync(endpoint.Host, endpoint.Port, TimeSpan.FromMilliseconds(250),
              cancellationToken).ConfigureAwait(false);
        }
        SetStatus("waiting for an ADS-B TCP source");
        await Task.Delay(TimeSpan.FromSeconds(2.5), cancellationToken).ConfigureAwait(false);
      }
    } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
    } catch (Exception ex) {
      SetStatus($"external ADS-B stopped: {ex.Message}");
      Trace.WriteLine($"External ADS-B receiver stopped: {ex}");
    }
  }

  private async Task RunHttpAsync(ExternalAdsbOptions options,
      CancellationToken cancellationToken) {
    int radius = 100;
    int delayMilliseconds = 1000;
    while (!cancellationToken.IsCancellationRequested) {
      Stopwatch timer = Stopwatch.StartNew();
      try {
        if (_positionProvider() is null) {
          SetStatus("external ADS-B waiting for a map or vehicle position");
        } else {
          HttpPollResult result = await PollHttpOnceAsync(options, radius, cancellationToken)
              .ConfigureAwait(false);
          if (result.StatusCode == HttpStatusCode.OK) {
            SetStatus($"external ADS-B HTTP: {result.AircraftCount} aircraft, {radius} nm");
            radius = NextHttpRadius(radius, result.AircraftCount);
          } else if (result.StatusCode != HttpStatusCode.NoContent) {
            delayMilliseconds = NextHttpDelay(delayMilliseconds);
            SetStatus($"external ADS-B HTTP: {(int)result.StatusCode} {result.StatusCode}");
          }
        }
      } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
        throw;
      } catch (Exception ex) {
        delayMilliseconds = NextHttpDelay(delayMilliseconds);
        SetStatus($"external ADS-B HTTP retry: {ex.Message}");
      }
      int remaining = delayMilliseconds - (int)timer.ElapsedMilliseconds;
      if (remaining > 0) {
        await Task.Delay(remaining, cancellationToken).ConfigureAwait(false);
      }
    }
  }

  private async Task TryTcpEndpointAsync(string host, int port, TimeSpan connectTimeout,
      CancellationToken cancellationToken) {
    using var client = new TcpClient();
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    timeout.CancelAfter(connectTimeout);
    try {
      await client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
    } catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) {
      return;
    } catch (SocketException) {
      return;
    }

    lock (_stateSync) {
      _activeClient = client;
    }
    try {
      SetStatus($"external ADS-B connected to {host}:{port}");
      var decoder = new ExternalAdsbDecoder();
      await ReadTcpStreamAsync(client.GetStream(), decoder, cancellationToken).ConfigureAwait(false);
    } catch (IOException) when (!cancellationToken.IsCancellationRequested) {
    } catch (SocketException) when (!cancellationToken.IsCancellationRequested) {
    } finally {
      lock (_stateSync) {
        if (ReferenceEquals(_activeClient, client)) {
          _activeClient = null;
        }
      }
    }
  }

  private async Task ReadTcpStreamAsync(Stream stream, ExternalAdsbDecoder decoder,
      CancellationToken cancellationToken) {
    var line = new List<byte>(256);
    var one = new byte[1];
    while (await stream.ReadAsync(one, cancellationToken).ConfigureAwait(false) == 1) {
      byte value = one[0];
      if (value == 0x1a) {
        line.Clear();
        byte[]? frame = await ReadBeastFrameAsync(stream, cancellationToken).ConfigureAwait(false);
        if (frame is not null && decoder.TryDecodeBeast(frame, out var beastPlane)) {
          _onPlane(beastPlane);
        }
        continue;
      }
      if (value is (byte)'\r' or (byte)'\n') {
        if (line.Count > 0) {
          string text = Encoding.ASCII.GetString(line.ToArray());
          line.Clear();
          if (decoder.TryDecodeLine(text, out var linePlane)) {
            _onPlane(linePlane);
          }
        }
        continue;
      }
      if (line.Count < 4096) {
        line.Add(value);
      } else {
        line.Clear();
      }
    }
  }

  private static async Task<byte[]?> ReadBeastFrameAsync(Stream stream,
      CancellationToken cancellationToken) {
    int type = await ReadByteAsync(stream, cancellationToken).ConfigureAwait(false);
    int messageLength = type switch {
      '1' => 2,
      '2' => 7,
      '3' => 14,
      _ => 0,
    };
    if (messageLength == 0) {
      return null;
    }

    var frame = new byte[2 + 7 + messageLength];
    frame[0] = 0x1a;
    frame[1] = (byte)type;
    for (int index = 2; index < frame.Length; index++) {
      int value = await ReadByteAsync(stream, cancellationToken).ConfigureAwait(false);
      if (value < 0) {
        return null;
      }
      if (value == 0x1a) {
        int escaped = await ReadByteAsync(stream, cancellationToken).ConfigureAwait(false);
        if (escaped != 0x1a) {
          return null;
        }
      }
      frame[index] = (byte)value;
    }
    return frame;
  }

  private static async Task<int> ReadByteAsync(Stream stream, CancellationToken cancellationToken) {
    var one = new byte[1];
    return await stream.ReadAsync(one, cancellationToken).ConfigureAwait(false) == 1 ? one[0] : -1;
  }

  private async Task StopCoreAsync() {
    CancellationTokenSource? cancellation = _runCancellation;
    Task? task = _runTask;
    _runCancellation = null;
    _runTask = null;
    if (cancellation is null) {
      return;
    }
    cancellation.Cancel();
    lock (_stateSync) {
      _activeClient?.Dispose();
      _activeClient = null;
    }
    try {
      if (task is not null) {
        await task.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
      }
    } catch (OperationCanceledException) {
    } catch (TimeoutException) {
      Trace.WriteLine("External ADS-B receiver did not stop within three seconds.");
    } finally {
      cancellation.Dispose();
    }
  }

  private void SetStatus(string status) {
    lock (_stateSync) {
      _status = status;
    }
  }

  private void ThrowIfDisposed() {
    ObjectDisposedException.ThrowIf(_disposed, this);
  }

  private static bool TryDecodeHttpAircraft(JsonElement item,
      out adsb.PointLatLngAltHdg? plane) {
    plane = null;
    string id = GetString(item, "hex").TrimStart('~').Trim().ToUpperInvariant();
    if (id.Length == 0 || !TryGetDouble(item, "lat", out double lat)
        || !TryGetDouble(item, "lon", out double lng)
        || !double.IsFinite(lat) || !double.IsFinite(lng)
        || lat is < -90 or > 90 || lng is < -180 or > 180
        || (lat == 0 && lng == 0)) {
      return false;
    }
    double altFeet = TryGetDouble(item, "alt_baro", out double parsedAlt) ? parsedAlt : 0;
    double heading = TryGetDouble(item, "track", out double parsedTrack) ? parsedTrack : 0;
    double speedKnots = TryGetDouble(item, "gs", out double parsedSpeed) ? parsedSpeed : 0;
    double verticalFeetPerMinute = TryGetDouble(item, "baro_rate", out double parsedVertical)
        ? parsedVertical
        : 0;
    ushort squawk = ushort.TryParse(GetString(item, "squawk"), NumberStyles.HexNumber,
        CultureInfo.InvariantCulture, out ushort parsedSquawk) ? parsedSquawk : (ushort)0;
    plane = new adsb.PointLatLngAltHdg(lat, lng, altFeet * 0.3048,
        (float)heading, speedKnots * 51.444, id, DateTime.UtcNow) {
      VerticalSpeed = verticalFeetPerMinute / 1.968,
      CallSign = GetString(item, "flight").Trim().ToUpperInvariant(),
      Squawk = squawk,
    };
    return true;
  }

  private static string GetString(JsonElement item, string property) {
    if (!item.TryGetProperty(property, out JsonElement value)) {
      return "";
    }
    return value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString();
  }

  private static bool TryGetDouble(JsonElement item, string property, out double value) {
    value = 0;
    if (!item.TryGetProperty(property, out JsonElement element)) {
      return false;
    }
    return element.ValueKind == JsonValueKind.Number
        ? element.TryGetDouble(out value)
        : element.ValueKind == JsonValueKind.String
          && double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture,
              out value);
  }
}

internal sealed class ExternalAdsbDecoder {
  private sealed class SbsState {
    internal string CallSign = "";
    internal double Heading;
    internal double Speed;
  }

  private static readonly object _modeSLock = new();
  private static readonly FieldInfo? _groundSpeed = typeof(adsb.Plane).GetField("ground_speed",
      BindingFlags.Instance | BindingFlags.NonPublic);
  private readonly Dictionary<string, SbsState> _sbs = new(StringComparer.OrdinalIgnoreCase);

  internal bool TryDecodeLine(string line, out adsb.PointLatLngAltHdg plane) {
    plane = null!;
    string value = line.Trim();
    if (value.StartsWith("MSG,", StringComparison.OrdinalIgnoreCase)) {
      return TryDecodeSbs(value, out plane);
    }
    if (!value.StartsWith('*')) {
      return false;
    }
    try {
      lock (_modeSLock) {
        return TryConvertModeS(adsb.ReadMessage(value), out plane);
      }
    } catch {
      return false;
    }
  }

  internal bool TryDecodeBeast(byte[] frame, out adsb.PointLatLngAltHdg plane) {
    plane = null!;
    try {
      lock (_modeSLock) {
        return TryConvertModeS(adsb.ReadMessage(frame), out plane);
      }
    } catch {
      return false;
    }
  }

  private bool TryDecodeSbs(string line, out adsb.PointLatLngAltHdg plane) {
    plane = null!;
    string[] fields = line.Split(',');
    if (fields.Length < 11 || fields[4].Trim().Length == 0) {
      return false;
    }
    string id = fields[4].Trim().ToUpperInvariant();
    if (!_sbs.TryGetValue(id, out SbsState? state)) {
      state = new SbsState();
      _sbs[id] = state;
    }
    switch (fields[1]) {
      case "1":
      case "5":
      case "6":
        state.CallSign = fields[10].Trim();
        return false;
      case "4":
        if (fields.Length > 12 && double.TryParse(fields[12], NumberStyles.Float,
            CultureInfo.InvariantCulture, out double knots)) {
          state.Speed = knots * 51.444;
        }
        if (fields.Length > 13 && double.TryParse(fields[13], NumberStyles.Float,
            CultureInfo.InvariantCulture, out double heading)) {
          state.Heading = heading;
        }
        return false;
      case "3":
        if (fields.Length < 22
            || !double.TryParse(fields[14], NumberStyles.Float, CultureInfo.InvariantCulture,
                out double lat)
            || !double.TryParse(fields[15], NumberStyles.Float, CultureInfo.InvariantCulture,
                out double lng)
            || (lat == 0 && lng == 0)) {
          return false;
        }
        double.TryParse(fields[11].TrimEnd('H', 'h'), NumberStyles.Float,
            CultureInfo.InvariantCulture, out double altitudeFeet);
        double.TryParse(fields[16], NumberStyles.Float, CultureInfo.InvariantCulture,
            out double verticalFeetPerMinute);
        ushort.TryParse(fields[17], NumberStyles.HexNumber, CultureInfo.InvariantCulture,
            out ushort squawk);
        plane = new adsb.PointLatLngAltHdg(lat, lng, altitudeFeet * 0.3048,
            (float)state.Heading, state.Speed, id, DateTime.UtcNow) {
          CallSign = state.CallSign,
          VerticalSpeed = verticalFeetPerMinute / 1.968,
          Squawk = squawk,
        };
        return true;
      default:
        return false;
    }
  }

  private static bool TryConvertModeS(adsb.Plane? decoded, out adsb.PointLatLngAltHdg plane) {
    plane = null!;
    if (decoded is null) {
      return false;
    }
    PointLatLngAlt position = decoded.plla();
    if ((position.Lat == 0 && position.Lng == 0) || !double.IsFinite(position.Lat)
        || !double.IsFinite(position.Lng)) {
      return false;
    }
    double speed = _groundSpeed?.GetValue(decoded) is int value ? value : 0;
    plane = new adsb.PointLatLngAltHdg(position.Lat, position.Lng, position.Alt,
        (float)decoded.heading, speed, decoded.ID ?? position.Tag ?? "", DateTime.UtcNow) {
      CallSign = decoded.CallSign ?? "",
    };
    return true;
  }
}

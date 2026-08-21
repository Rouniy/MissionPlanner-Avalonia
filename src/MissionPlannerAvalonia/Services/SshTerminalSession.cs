using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace MissionPlannerAvalonia.Services;

internal sealed record SshTerminalConnection(
    string Host,
    int Port,
    string Username,
    string Password);

internal sealed record SshHostKeyChallenge(
    string Host,
    int Port,
    string Algorithm,
    int KeyLength,
    string PresentedFingerprint,
    string? ExpectedFingerprint) {
  public bool IsChanged => !string.IsNullOrWhiteSpace(ExpectedFingerprint);
}

internal sealed class SshHostKeyException : Exception {
  public SshHostKeyException(SshHostKeyChallenge challenge, Exception? innerException = null)
      : base(challenge.IsChanged
          ? "The SSH server host key does not match the trusted key."
          : "The SSH server host key has not been trusted yet.", innerException) {
    Challenge = challenge;
  }

  public SshHostKeyChallenge Challenge { get; }
}

/// <summary>
/// Applies the same fail-closed SHA256 host-key policy to every SSH.NET client used
/// by the port. Authentication never starts when the presented key is unknown or
/// differs from the stored pin: SSH.NET raises this event during key exchange.
/// </summary>
internal sealed class SshHostKeyGuard {
  private readonly string _host;
  private readonly int _port;
  private readonly string? _trustedFingerprint;

  public SshHostKeyGuard(string host, int port, string? trustedFingerprint) {
    _host = host;
    _port = port;
    _trustedFingerprint = trustedFingerprint;
  }

  public SshHostKeyChallenge? Challenge { get; private set; }

  public void Attach(BaseClient client) => client.HostKeyReceived += OnHostKeyReceived;

  private void OnHostKeyReceived(object? sender, HostKeyEventArgs args) {
    string presented = "SHA256:" + args.FingerPrintSHA256;
    bool trusted = FingerprintsEqual(_trustedFingerprint, presented);
    if (!trusted) {
      Challenge = new SshHostKeyChallenge(
          _host,
          _port,
          args.HostKeyName,
          args.KeyLength,
          presented,
          string.IsNullOrWhiteSpace(_trustedFingerprint) ? null : _trustedFingerprint);
    }
    args.CanTrust = trusted;
  }

  public void ThrowIfRejected(Exception? innerException = null) {
    if (Challenge != null) {
      throw new SshHostKeyException(Challenge, innerException);
    }
  }

  internal static bool FingerprintsEqual(string? expected, string presented) {
    if (string.IsNullOrWhiteSpace(expected)) {
      return false;
    }
    byte[] left = Encoding.ASCII.GetBytes(expected.Trim());
    byte[] right = Encoding.ASCII.GetBytes(presented.Trim());
    return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
  }
}

internal interface ISshTerminalSession : IAsyncDisposable {
  event Action<string>? TextReceived;
  event Action<string>? ConnectionClosed;
  bool IsConnected { get; }
  Task ConnectAsync(SshTerminalConnection connection, string? trustedFingerprint,
      CancellationToken cancellationToken);
  Task WriteAsync(string text, CancellationToken cancellationToken = default);
  Task StopAsync();
  void Stop();
}

/// <summary>
/// Async SSH.NET shell lifecycle with mandatory host-key pinning. Unknown or changed
/// keys are surfaced to the view model before authentication is allowed to continue.
/// </summary>
internal sealed class SshTerminalSession : ISshTerminalSession {
  private readonly object _sync = new();
  private readonly SemaphoreSlim _writeGate = new(1, 1);
  private SshClient? _client;
  private ShellStream? _shell;
  private CancellationTokenSource? _readCancellation;
  private Task? _readTask;
  private int _generation;
  private volatile bool _disposed;

  public event Action<string>? TextReceived;
  public event Action<string>? ConnectionClosed;

  public bool IsConnected {
    get {
      lock (_sync) {
        return _client?.IsConnected == true && _shell != null;
      }
    }
  }

  public async Task ConnectAsync(SshTerminalConnection connection, string? trustedFingerprint,
      CancellationToken cancellationToken) {
    ObjectDisposedException.ThrowIf(_disposed, this);
    if (string.IsNullOrWhiteSpace(connection.Host)) {
      throw new ArgumentException("SSH host is required.", nameof(connection));
    }
    if (connection.Port is < 1 or > 65535) {
      throw new ArgumentOutOfRangeException(nameof(connection), "SSH port must be between 1 and 65535.");
    }
    if (string.IsNullOrWhiteSpace(connection.Username)) {
      throw new ArgumentException("SSH username is required.", nameof(connection));
    }

    await StopAsync().ConfigureAwait(false);

    var info = new ConnectionInfo(
        connection.Host,
        connection.Port,
        connection.Username,
        new PasswordAuthenticationMethod(connection.Username, connection.Password)) {
      Timeout = TimeSpan.FromSeconds(20),
    };
    var client = new SshClient(info);
    var hostKey = new SshHostKeyGuard(connection.Host, connection.Port, trustedFingerprint);
    hostKey.Attach(client);

    try {
      await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
      cancellationToken.ThrowIfCancellationRequested();
      if (hostKey.Challenge != null) {
        // SSH.NET currently rejects CanTrust=false before authentication. Keep the
        // security invariant explicit in case a future implementation changes that flow.
        hostKey.ThrowIfRejected();
      }
      var shell = client.CreateShellStream("xterm", 80, 24, 800, 600, 4096);
      var readCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
      int generation;
      lock (_sync) {
        if (_disposed) {
          throw new ObjectDisposedException(nameof(SshTerminalSession));
        }
        _client = client;
        _shell = shell;
        _readCancellation = readCancellation;
        generation = ++_generation;
        _readTask = ReadLoopAsync(shell, generation, readCancellation.Token);
      }
    } catch (Exception ex) {
      try {
        client.Dispose();
      } catch {
        // The transport may already have torn itself down.
      }
      if (ex is SshHostKeyException) {
        throw;
      }
      if (hostKey.Challenge != null && ex is not OperationCanceledException) {
        hostKey.ThrowIfRejected(ex);
      }
      throw;
    }
  }

  public async Task WriteAsync(string text, CancellationToken cancellationToken = default) {
    if (string.IsNullOrEmpty(text)) {
      return;
    }

    ShellStream shell;
    lock (_sync) {
      shell = _shell ?? throw new InvalidOperationException("The SSH terminal is not connected.");
    }

    byte[] bytes = Encoding.UTF8.GetBytes(text);
    await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
    try {
      await shell.WriteAsync(bytes.AsMemory(), cancellationToken).ConfigureAwait(false);
      await shell.FlushAsync(cancellationToken).ConfigureAwait(false);
    } finally {
      _writeGate.Release();
    }
  }

  public async Task StopAsync() {
    Task? readTask = StopCore();
    if (readTask == null || readTask.IsCompleted) {
      return;
    }
    try {
      await readTask.ConfigureAwait(false);
    } catch (OperationCanceledException) {
    } catch (ObjectDisposedException) {
    }
  }

  public void Stop() => _ = StopCore();

  private Task? StopCore() {
    SshClient? client;
    ShellStream? shell;
    CancellationTokenSource? cancellation;
    Task? readTask;
    lock (_sync) {
      client = _client;
      shell = _shell;
      cancellation = _readCancellation;
      readTask = _readTask;
      _client = null;
      _shell = null;
      _readCancellation = null;
      _readTask = null;
      _generation++;
    }

    try {
      cancellation?.Cancel();
    } catch (ObjectDisposedException) {
    }
    try {
      shell?.Dispose();
    } catch {
    }
    try {
      if (client?.IsConnected == true) {
        client.Disconnect();
      }
    } catch {
    }
    try {
      client?.Dispose();
    } catch {
    }
    cancellation?.Dispose();
    return readTask;
  }

  private async Task ReadLoopAsync(ShellStream shell, int generation,
      CancellationToken cancellationToken) {
    string? closeReason = null;
    try {
      using var reader = new StreamReader(
          shell, new UTF8Encoding(false, false), false, 4096, leaveOpen: true);
      var buffer = new char[4096];
      while (!cancellationToken.IsCancellationRequested) {
        int count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
        if (count == 0) {
          closeReason = "The SSH server closed the shell.";
          break;
        }
        lock (_sync) {
          if (generation != _generation || !ReferenceEquals(shell, _shell)) {
            return;
          }
        }
        TextReceived?.Invoke(new string(buffer, 0, count));
      }
    } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
    } catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) {
    } catch (IOException ex) {
      closeReason = "SSH shell read failed: " + ex.Message;
    } catch (SshException ex) {
      closeReason = "SSH connection failed: " + ex.Message;
    } catch (Exception ex) {
      closeReason = "SSH shell stopped: " + ex.Message;
    }

    if (closeReason == null) {
      return;
    }
    lock (_sync) {
      if (generation != _generation) {
        return;
      }
    }
    ConnectionClosed?.Invoke(closeReason);
  }

  public async ValueTask DisposeAsync() {
    if (_disposed) {
      return;
    }
    _disposed = true;
    await StopAsync().ConfigureAwait(false);
    _writeGate.Dispose();
  }
}

internal static class SshEndpoint {
  public static bool TryParse(string? value, int defaultPort, out string host, out int port) {
    host = (value ?? "").Trim();
    port = defaultPort;
    if (host.Length == 0 || defaultPort is < 1 or > 65535) {
      return false;
    }

    if (host[0] == '[') {
      int closing = host.IndexOf(']');
      if (closing < 2) {
        return false;
      }
      string address = host[1..closing];
      string suffix = host[(closing + 1)..];
      if (suffix.Length > 0) {
        if (!suffix.StartsWith(':') || !TryPort(suffix[1..], out port)) {
          return false;
        }
      }
      host = address;
      return true;
    }

    int firstColon = host.IndexOf(':');
    if (firstColon >= 0 && firstColon == host.LastIndexOf(':')) {
      string suffix = host[(firstColon + 1)..];
      if (!TryPort(suffix, out port)) {
        return false;
      }
      host = host[..firstColon].Trim();
    }
    return host.Length > 0;
  }

  public static string TrustedKeySettingName(string host, int port) {
    string identity = host.Trim().ToLowerInvariant() + ":" + port;
    byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
    return "SSHHostKey_" + Convert.ToHexString(hash);
  }

  private static bool TryPort(string value, out int port) =>
      int.TryParse(value, out port) && port is >= 1 and <= 65535;
}

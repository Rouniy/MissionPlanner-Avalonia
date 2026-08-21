using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Renci.SshNet;
using Renci.SshNet.Sftp;

namespace MissionPlannerAvalonia.Services;

internal sealed record SftpLogConnection(
    string Host,
    int Port,
    string Username,
    string Password);

internal sealed record SftpLogEntry(
    string RemoteDirectory,
    string Name,
    long Length,
    DateTime LastWriteTimeUtc) {
  public string RemotePath => SftpRemotePath.Combine(RemoteDirectory, Name);
}

internal interface ISftpLogSession : IAsyncDisposable {
  bool IsConnected { get; }
  Task ConnectAsync(SftpLogConnection connection, string? trustedFingerprint,
      CancellationToken cancellationToken);
  Task<IReadOnlyList<SftpLogEntry>> ListLogsAsync(
      string remoteDirectory, CancellationToken cancellationToken);
  Task<long> DownloadAsync(SftpLogEntry entry, Stream destination,
      IProgress<long>? progress, CancellationToken cancellationToken);
  Task DeleteAsync(SftpLogEntry entry, CancellationToken cancellationToken);
  Task StopAsync();
  void Stop();
}

/// <summary>
/// One pinned SSH/SFTP connection used to list, download and remove companion-computer
/// DataFlash logs. Directory entries are revalidated before opening or deleting them.
/// </summary>
internal sealed class SftpLogSession : ISftpLogSession {
  private readonly object _sync = new();
  private SftpClient? _client;
  private volatile bool _disposed;

  public bool IsConnected {
    get {
      lock (_sync) {
        return _client?.IsConnected == true;
      }
    }
  }

  public async Task ConnectAsync(SftpLogConnection connection, string? trustedFingerprint,
      CancellationToken cancellationToken) {
    ObjectDisposedException.ThrowIf(_disposed, this);
    ValidateConnection(connection);
    await StopAsync().ConfigureAwait(false);

    var info = new ConnectionInfo(
        connection.Host,
        connection.Port,
        connection.Username,
        new PasswordAuthenticationMethod(connection.Username, connection.Password)) {
      Timeout = TimeSpan.FromSeconds(20),
    };
    var client = new SftpClient(info) {
      KeepAliveInterval = TimeSpan.FromSeconds(15),
      OperationTimeout = TimeSpan.FromSeconds(30),
      BufferSize = 64 * 1024,
    };
    var hostKey = new SshHostKeyGuard(connection.Host, connection.Port, trustedFingerprint);
    hostKey.Attach(client);

    try {
      await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
      cancellationToken.ThrowIfCancellationRequested();
      hostKey.ThrowIfRejected();
      lock (_sync) {
        if (_disposed) {
          throw new ObjectDisposedException(nameof(SftpLogSession));
        }
        _client = client;
      }
    } catch (Exception ex) {
      client.Dispose();
      if (ex is SshHostKeyException) {
        throw;
      }
      if (hostKey.Challenge != null && ex is not OperationCanceledException) {
        hostKey.ThrowIfRejected(ex);
      }
      throw;
    }
  }

  public async Task<IReadOnlyList<SftpLogEntry>> ListLogsAsync(
      string remoteDirectory, CancellationToken cancellationToken) {
    string directory = SftpRemotePath.NormalizeDirectory(remoteDirectory);
    SftpClient client = ConnectedClient();
    var logs = new List<SftpLogEntry>();
    await foreach (ISftpFile file in client.ListDirectoryAsync(directory, cancellationToken)
                       .ConfigureAwait(false)) {
      cancellationToken.ThrowIfCancellationRequested();
      if (!IsSafeRegularBin(file)) {
        continue;
      }
      logs.Add(new SftpLogEntry(directory, file.Name, file.Length, file.LastWriteTimeUtc));
    }

    return logs
        .OrderByDescending(item => item.LastWriteTimeUtc)
        .ThenBy(item => item.Name, StringComparer.Ordinal)
        .ToArray();
  }

  public async Task<long> DownloadAsync(SftpLogEntry entry, Stream destination,
      IProgress<long>? progress, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(destination);
    SftpClient client = ConnectedClient();
    SftpLogEntry current = await RevalidateAsync(client, entry, requireUnchanged: false,
        cancellationToken).ConfigureAwait(false);
    await using Stream remote = await client.OpenAsync(
        current.RemotePath, FileMode.Open, FileAccess.Read, cancellationToken).ConfigureAwait(false);
    byte[] buffer = new byte[64 * 1024];
    long copied = 0;
    while (true) {
      int count = await remote.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
      if (count == 0) {
        break;
      }
      await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
      copied = checked(copied + count);
      progress?.Report(copied);
    }
    return copied;
  }

  public async Task DeleteAsync(SftpLogEntry entry, CancellationToken cancellationToken) {
    SftpClient client = ConnectedClient();
    SftpLogEntry current = await RevalidateAsync(client, entry, requireUnchanged: true,
        cancellationToken).ConfigureAwait(false);
    await client.DeleteFileAsync(current.RemotePath, cancellationToken).ConfigureAwait(false);
  }

  public Task StopAsync() {
    Stop();
    return Task.CompletedTask;
  }

  public void Stop() {
    SftpClient? client;
    lock (_sync) {
      client = _client;
      _client = null;
    }
    if (client == null) {
      return;
    }
    try {
      if (client.IsConnected) {
        client.Disconnect();
      }
    } catch {
      // Cancellation may tear down the underlying socket first.
    }
    try {
      client.Dispose();
    } catch {
    }
  }

  private static async Task<SftpLogEntry> RevalidateAsync(SftpClient client,
      SftpLogEntry expected, bool requireUnchanged, CancellationToken cancellationToken) {
    string directory = SftpRemotePath.NormalizeDirectory(expected.RemoteDirectory);
    await foreach (ISftpFile file in client.ListDirectoryAsync(directory, cancellationToken)
                       .ConfigureAwait(false)) {
      if (!string.Equals(file.Name, expected.Name, StringComparison.Ordinal)) {
        continue;
      }
      if (!IsSafeRegularBin(file)) {
        throw new IOException($"Remote log '{expected.Name}' is no longer a regular BIN file.");
      }
      var current = new SftpLogEntry(directory, file.Name, file.Length, file.LastWriteTimeUtc);
      if (requireUnchanged &&
          (current.Length != expected.Length || current.LastWriteTimeUtc != expected.LastWriteTimeUtc)) {
        throw new IOException($"Remote log '{expected.Name}' changed since it was listed; refresh before deleting.");
      }
      return current;
    }
    throw new FileNotFoundException($"Remote log '{expected.Name}' no longer exists.", expected.Name);
  }

  private static bool IsSafeRegularBin(ISftpFile file) =>
      file.IsRegularFile && !file.IsSymbolicLink &&
      SftpRemotePath.IsSafeName(file.Name) &&
      file.Name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase);

  private SftpClient ConnectedClient() {
    ObjectDisposedException.ThrowIf(_disposed, this);
    lock (_sync) {
      return _client?.IsConnected == true
          ? _client
          : throw new InvalidOperationException("The SFTP session is not connected.");
    }
  }

  private static void ValidateConnection(SftpLogConnection connection) {
    if (string.IsNullOrWhiteSpace(connection.Host)) {
      throw new ArgumentException("SSH host is required.", nameof(connection));
    }
    if (connection.Port is < 1 or > 65535) {
      throw new ArgumentOutOfRangeException(nameof(connection), "SSH port must be between 1 and 65535.");
    }
    if (string.IsNullOrWhiteSpace(connection.Username)) {
      throw new ArgumentException("SSH username is required.", nameof(connection));
    }
  }

  public ValueTask DisposeAsync() {
    if (!_disposed) {
      _disposed = true;
      Stop();
    }
    return ValueTask.CompletedTask;
  }
}

internal static class SftpRemotePath {
  public static string NormalizeDirectory(string? value) {
    string path = (value ?? "").Trim();
    if (path.Length == 0 || path[0] != '/' || path.IndexOf('\0') >= 0) {
      throw new ArgumentException("Remote log directory must be an absolute POSIX path.", nameof(value));
    }
    var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
    if (parts.Any(part => part is "." or "..")) {
      throw new ArgumentException("Remote log directory cannot contain '.' or '..' segments.", nameof(value));
    }
    return parts.Length == 0 ? "/" : "/" + string.Join('/', parts);
  }

  public static bool IsSafeName(string? name) =>
      !string.IsNullOrEmpty(name) && name is not "." and not ".." &&
      name.IndexOf('/') < 0 && name.IndexOf('\0') < 0;

  public static string Combine(string directory, string name) {
    string normalized = NormalizeDirectory(directory);
    if (!IsSafeName(name)) {
      throw new ArgumentException("Remote filename is invalid.", nameof(name));
    }
    return normalized == "/" ? "/" + name : normalized + "/" + name;
  }
}

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner.Utilities;
using MissionPlannerAvalonia.Services;

namespace MissionPlannerAvalonia.ViewModels;

public partial class SftpLogDownloadRow : ObservableObject {
  internal SftpLogDownloadRow(SftpLogEntry entry) => Entry = entry;

  internal SftpLogEntry Entry { get; }
  public string Name => Entry.Name;
  public long SizeBytes => Entry.Length;
  public DateTime LastWriteTimeUtc => Entry.LastWriteTimeUtc;
  public string SizeText => FormatBytes(SizeBytes);
  public string TimeText => LastWriteTimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

  [ObservableProperty]
  private bool _isSelected;

  internal static string FormatBytes(long bytes) => bytes switch {
    >= 1024L * 1024 * 1024 => (bytes / 1024.0 / 1024 / 1024).ToString("0.0") + " GB",
    >= 1024L * 1024 => (bytes / 1024.0 / 1024).ToString("0.0") + " MB",
    >= 1024 => (bytes / 1024.0).ToString("0.0") + " KB",
    _ => Math.Max(0, bytes) + " B",
  };
}

public sealed partial class SftpLogDownloadViewModel : ViewModelBase, IDisposable, IAsyncDisposable {
  private const string _legacySetting = "LogDownloadscppath";
  private const string _hostSetting = "SftpLogHost";
  private const string _portSetting = "SftpLogPort";
  private const string _usernameSetting = "SftpLogUsername";
  private const string _directorySetting = "SftpLogDirectory";

  private readonly ISftpLogSession _session;
  private readonly Func<SshHostKeyChallenge, Task<bool>> _confirmHostKey;
  private readonly Func<string, string, string, Task<bool>> _confirmDangerous;
  private readonly Func<Task<string?>> _pickFolder;
  private readonly bool _usePersistentSettings;
  private CancellationTokenSource? _operationCancellation;
  private TaskCompletionSource? _operationCompletion;
  private string? _connectedIdentity;
  private bool _disposed;

  public SftpLogDownloadViewModel()
      : this(new SftpLogSession(), SshHostKeyPrompt.ConfirmAsync,
          Dialogs.ConfirmDangerous, PickFolderAsync, usePersistentSettings: true) {
  }

  internal SftpLogDownloadViewModel(
      ISftpLogSession session,
      Func<SshHostKeyChallenge, Task<bool>>? confirmHostKey = null,
      Func<string, string, string, Task<bool>>? confirmDangerous = null,
      Func<Task<string?>>? pickFolder = null,
      bool usePersistentSettings = false) {
    _session = session;
    _confirmHostKey = confirmHostKey ?? (_ => Task.FromResult(false));
    _confirmDangerous = confirmDangerous ?? ((_, _, _) => Task.FromResult(false));
    _pickFolder = pickFolder ?? (() => Task.FromResult<string?>(null));
    _usePersistentSettings = usePersistentSettings;
    if (usePersistentSettings) {
      LoadSettingsAndEraseLegacyCredential();
    }
  }

  public ObservableCollection<SftpLogDownloadRow> Logs { get; } = new();

  [ObservableProperty]
  private string _host = "10.0.1.128";

  [ObservableProperty]
  private string _port = "22";

  [ObservableProperty]
  private string _username = "user";

  [ObservableProperty]
  private string _password = "";

  [ObservableProperty]
  private string _remoteDirectory = "/home/user/dflogger/dataflash/";

  [ObservableProperty]
  private bool _createKmlAfterDownload = true;

  [ObservableProperty]
  private bool _isBusy;

  [ObservableProperty]
  private bool _isConnected;

  [ObservableProperty]
  private double _progress;

  [ObservableProperty]
  private string _status = "Enter the companion-computer SSH details, then refresh the list.";

  [ObservableProperty]
  private string _activityLog = "";

  [RelayCommand]
  private Task RefreshLogsAsync() => RunOperationAsync(async cancellationToken => {
    await EnsureConnectedAsync(cancellationToken);
    Status = $"Listing {RemoteDirectory}…";
    IReadOnlyList<SftpLogEntry> entries = await _session.ListLogsAsync(
        RemoteDirectory, cancellationToken);
    Logs.Clear();
    foreach (SftpLogEntry entry in entries) {
      Logs.Add(new SftpLogDownloadRow(entry));
    }
    Progress = 0;
    Status = entries.Count == 0
        ? "No DataFlash BIN logs were found in the remote directory."
        : $"Found {entries.Count} DataFlash log(s).";
    AppendActivity(Status);
  });

  [RelayCommand]
  private async Task DownloadSelectedAsync() {
    var selected = Logs.Where(row => row.IsSelected).ToArray();
    if (selected.Length == 0) {
      Status = "Select at least one remote log first.";
      return;
    }
    await ChooseAndDownloadAsync(selected);
  }

  [RelayCommand]
  private async Task DownloadAllAsync() {
    if (Logs.Count == 0) {
      Status = "Refresh the remote log list first.";
      return;
    }
    await ChooseAndDownloadAsync(Logs.ToArray());
  }

  [RelayCommand]
  private Task DeleteSelectedAsync() {
    var selected = Logs.Where(row => row.IsSelected).ToArray();
    if (selected.Length == 0) {
      Status = "Select at least one remote log first.";
      return Task.CompletedTask;
    }
    return ConfirmAndDeleteAsync(selected, "Delete selected logs");
  }

  [RelayCommand]
  private Task DeleteAllAsync() {
    if (Logs.Count == 0) {
      Status = "There are no listed remote logs to delete.";
      return Task.CompletedTask;
    }
    return ConfirmAndDeleteAsync(Logs.ToArray(), "Delete all logs");
  }

  [RelayCommand]
  private void Cancel() {
    if (!IsBusy) {
      return;
    }
    Status = "Cancelling SFTP operation…";
    TryCancel(_operationCancellation);
    _session.Stop();
  }

  private async Task ChooseAndDownloadAsync(IReadOnlyList<SftpLogDownloadRow> rows) {
    if (IsBusy) {
      return;
    }
    string? destinationDirectory = await _pickFolder();
    if (destinationDirectory == null) {
      return;
    }
    await RunOperationAsync(cancellationToken =>
        DownloadRowsAsync(rows, destinationDirectory, cancellationToken));
  }

  private async Task DownloadRowsAsync(IReadOnlyList<SftpLogDownloadRow> rows,
      string destinationDirectory, CancellationToken cancellationToken) {
    await EnsureConnectedAsync(cancellationToken);
    Directory.CreateDirectory(destinationDirectory);
    long total = rows.Aggregate(0L, (sum, row) => SaturatingAdd(sum, Math.Max(0, row.SizeBytes)));
    long completed = 0;
    int saved = 0;
    var warnings = new List<string>();

    foreach (SftpLogDownloadRow row in rows) {
      cancellationToken.ThrowIfCancellationRequested();
      int currentNumber = saved + 1;
      string safeName = SftpLocalFile.SafeBinName(row.Name, currentNumber);
      string destination = SftpLocalFile.UniquePath(
          SftpLocalFile.CombineContained(destinationDirectory, safeName), ".log", ".kml");
      string partial = SftpLocalFile.NewPartialPath(destination);
      long copied = 0;
      Status = $"Downloading {row.Name} ({currentNumber}/{rows.Count})…";
      AppendActivity(Status);

      try {
        await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write,
                         FileShare.None, 64 * 1024, FileOptions.Asynchronous)) {
          var progress = new InlineProgress<long>(value => {
            copied = Math.Max(0, value);
            long current = SaturatingAdd(completed, copied);
            double percentage = total > 0
                ? Math.Min(100, 100.0 * current / total)
                : 0;
            Dispatcher.UIThread.Post(() => Progress = percentage);
          });
          copied = await _session.DownloadAsync(row.Entry, output, progress, cancellationToken);
          await output.FlushAsync(cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        destination = MovePartialWithoutOverwrite(partial, destination);
        partial = "";
        if (copied != row.SizeBytes) {
          warnings.Add($"{row.Name}: listed {SftpLogDownloadRow.FormatBytes(row.SizeBytes)}, " +
              $"received {SftpLogDownloadRow.FormatBytes(copied)} (the log may still be active)");
        }

        var processed = await DataFlashDownloadPostProcessor.ProcessAsync(
            destination, CreateKmlAfterDownload, cancellationToken);
        foreach (string warning in processed.Warnings) {
          warnings.Add(row.Name + ": " + warning);
        }
        completed = SaturatingAdd(completed, copied);
        saved++;
        AppendActivity($"Saved {row.Name} as {Path.GetFileName(processed.BinPath)}" +
            (string.IsNullOrEmpty(processed.LogPath) ? "" : " and " + Path.GetFileName(processed.LogPath)) +
            (processed.KmlPath == null ? "" : " with KML"));
      } finally {
        if (partial.Length > 0) {
          TryDelete(partial);
        }
      }
    }

    Progress = 100;
    Status = $"Downloaded {saved} log(s) to {destinationDirectory}." +
        (warnings.Count == 0 ? "" : $" {warnings.Count} warning(s); see activity log.");
    foreach (string warning in warnings) {
      AppendActivity("Warning: " + warning);
    }
    AppendActivity(Status);
  }

  private async Task ConfirmAndDeleteAsync(IReadOnlyList<SftpLogDownloadRow> rows, string title) {
    if (IsBusy) {
      return;
    }
    string directory;
    try {
      directory = SftpRemotePath.NormalizeDirectory(RemoteDirectory);
    } catch (Exception ex) {
      Status = ex.Message;
      return;
    }
    if (!await _confirmDangerous(
            title,
            $"Permanently delete {rows.Count} DataFlash log(s) from {directory}?\n\n" +
            "Each file will be rechecked before deletion. This cannot be undone.",
            rows.Count == Logs.Count ? "Delete all" : "Delete selected")) {
      Status = "Remote deletion cancelled.";
      return;
    }

    await RunOperationAsync(async cancellationToken => {
      await EnsureConnectedAsync(cancellationToken);
      int deleted = 0;
      var failed = new List<string>();
      foreach (SftpLogDownloadRow row in rows) {
        cancellationToken.ThrowIfCancellationRequested();
        Status = $"Deleting {row.Name} ({deleted + failed.Count + 1}/{rows.Count})…";
        try {
          await _session.DeleteAsync(row.Entry, cancellationToken);
          Logs.Remove(row);
          deleted++;
          AppendActivity("Deleted remote log " + row.Name);
        } catch (OperationCanceledException) {
          throw;
        } catch (Exception ex) {
          failed.Add(row.Name + ": " + ex.Message);
          AppendActivity("Delete failed for " + row.Name + ": " + ex.Message);
        }
        Progress = 100.0 * (deleted + failed.Count) / rows.Count;
      }
      Status = $"Deleted {deleted}/{rows.Count} remote log(s)." +
          (failed.Count == 0 ? "" : $" {failed.Count} failed; see activity log.");
      AppendActivity(Status);
    });
  }

  private async Task EnsureConnectedAsync(CancellationToken cancellationToken) {
    if (!int.TryParse(Port, out int selectedPort) || selectedPort is < 1 or > 65535) {
      throw new ArgumentException("SSH port must be a number between 1 and 65535.");
    }
    if (!SshEndpoint.TryParse(Host, selectedPort, out string host, out int port)) {
      throw new ArgumentException("Enter an SSH host, optionally as host:port or [IPv6]:port.");
    }
    string username = Username.Trim();
    if (username.Length == 0) {
      throw new ArgumentException("Enter the SSH username.");
    }
    string directory = SftpRemotePath.NormalizeDirectory(RemoteDirectory);
    string identity = username + "@" + host.ToLowerInvariant() + ":" + port;
    if (_session.IsConnected && string.Equals(identity, _connectedIdentity, StringComparison.Ordinal)) {
      IsConnected = true;
      RemoteDirectory = directory;
      return;
    }

    if (_session.IsConnected) {
      await _session.StopAsync();
    }
    IsConnected = false;
    string password = Password ?? "";
    Password = "";
    var connection = new SftpLogConnection(host, port, username, password);
    string settingName = SshEndpoint.TrustedKeySettingName(host, port);
    string? trustedFingerprint = _usePersistentSettings ? Settings.Instance[settingName] : null;

    try {
      try {
        Status = $"Connecting to {host}:{port} over SFTP…";
        await _session.ConnectAsync(connection, trustedFingerprint, cancellationToken);
      } catch (SshHostKeyException ex) {
        if (!await _confirmHostKey(ex.Challenge)) {
          throw new OperationCanceledException(ex.Challenge.IsChanged
              ? "Connection rejected because the SSH host key changed."
              : "Connection cancelled because the SSH host key was not trusted.", ex,
              cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        trustedFingerprint = ex.Challenge.PresentedFingerprint;
        if (_usePersistentSettings) {
          Settings.Instance[settingName] = trustedFingerprint;
          Settings.Instance.Save();
        }
        Status = $"Host key trusted. Connecting to {host}:{port}…";
        await _session.ConnectAsync(connection, trustedFingerprint, cancellationToken);
      }

      _connectedIdentity = identity;
      IsConnected = true;
      Host = host;
      Port = port.ToString();
      Username = username;
      RemoteDirectory = directory;
      if (_usePersistentSettings) {
        Settings.Instance[_hostSetting] = host;
        Settings.Instance[_portSetting] = port.ToString();
        Settings.Instance[_usernameSetting] = username;
        Settings.Instance[_directorySetting] = directory;
        Settings.Instance.Save();
      }
      AppendActivity($"Connected to {username}@{host}:{port}; host key verified.");
    } finally {
      Password = "";
    }
  }

  private async Task RunOperationAsync(Func<CancellationToken, Task> operation) {
    ObjectDisposedException.ThrowIf(_disposed, this);
    if (IsBusy) {
      return;
    }
    var cancellation = new CancellationTokenSource();
    var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    _operationCancellation = cancellation;
    _operationCompletion = completion;
    IsBusy = true;
    Progress = 0;
    try {
      await operation(cancellation.Token);
    } catch (OperationCanceledException) {
      Status = "SFTP operation cancelled.";
      AppendActivity(Status);
    } catch (Exception ex) {
      IsConnected = _session.IsConnected;
      Status = "SFTP operation failed: " + ex.Message;
      AppendActivity(Status);
    } finally {
      if (ReferenceEquals(_operationCancellation, cancellation)) {
        _operationCancellation = null;
      }
      if (ReferenceEquals(_operationCompletion, completion)) {
        _operationCompletion = null;
      }
      IsBusy = false;
      cancellation.Dispose();
      completion.TrySetResult();
    }
  }

  internal async Task CancelAndWaitAsync() {
    TryCancel(_operationCancellation);
    _session.Stop();
    Task? completion = _operationCompletion?.Task;
    if (completion != null) {
      try {
        await completion;
      } catch {
      }
    }
    await _session.StopAsync();
    IsConnected = false;
  }

  private void LoadSettingsAndEraseLegacyCredential() {
    string? legacy = Settings.Instance[_legacySetting];
    if (!string.IsNullOrWhiteSpace(legacy) &&
        TryParseLegacyPath(legacy, out string host, out int port,
            out string username, out string directory)) {
      Host = host;
      Port = port.ToString();
      Username = username;
      RemoteDirectory = directory;
    }
    if (legacy != null) {
      Settings.Instance.Remove(_legacySetting);
      Settings.Instance.Save();
    }

    Host = Settings.Instance[_hostSetting] ?? Host;
    Port = Settings.Instance[_portSetting] ?? Port;
    Username = Settings.Instance[_usernameSetting] ?? Username;
    RemoteDirectory = Settings.Instance[_directorySetting] ?? RemoteDirectory;
  }

  internal static bool TryParseLegacyPath(string? value, out string host, out int port,
      out string username, out string directory) {
    host = "";
    port = 22;
    username = "";
    directory = "";
    string input = (value ?? "").Trim();
    int at = input.LastIndexOf('@');
    int credentialSeparator = input.IndexOf(':');
    int directorySeparator = at < 0 ? -1 : input.IndexOf(":/", at + 1, StringComparison.Ordinal);
    if (credentialSeparator <= 0 || at <= credentialSeparator || directorySeparator <= at + 1) {
      return false;
    }
    username = input[..credentialSeparator].Trim();
    string endpoint = input[(at + 1)..directorySeparator];
    directory = input[(directorySeparator + 1)..];
    return username.Length > 0 &&
        SshEndpoint.TryParse(endpoint, 22, out host, out port) &&
        TryNormalizeRemoteDirectory(directory, out directory);
  }

  private static bool TryNormalizeRemoteDirectory(string value, out string normalized) {
    try {
      normalized = SftpRemotePath.NormalizeDirectory(value);
      return true;
    } catch {
      normalized = "";
      return false;
    }
  }

  private void AppendActivity(string text) {
    string next = ActivityLog.Length == 0 ? text : ActivityLog + Environment.NewLine + text;
    ActivityLog = next.Length <= 16000 ? next : next[^16000..];
  }

  private static long SaturatingAdd(long left, long right) =>
      right > 0 && left > long.MaxValue - right ? long.MaxValue : left + right;

  private static string MovePartialWithoutOverwrite(string partial, string desired) {
    for (int attempt = 0; attempt < 100; attempt++) {
      string destination = attempt == 0 ? desired : SftpLocalFile.UniquePath(desired, ".log", ".kml");
      try {
        File.Move(partial, destination, overwrite: false);
        return destination;
      } catch (IOException) when (File.Exists(destination)) {
      }
    }
    throw new IOException("Unable to reserve a destination filename for the downloaded log.");
  }

  private static void TryDelete(string path) {
    try {
      File.Delete(path);
    } catch {
    }
  }

  private static void TryCancel(CancellationTokenSource? cancellation) {
    try {
      cancellation?.Cancel();
    } catch (ObjectDisposedException) {
    }
  }

  private static async Task<string?> PickFolderAsync() {
    if (Dialogs.Owner == null) {
      return null;
    }
    var folders = await Dialogs.Owner.StorageProvider.OpenFolderPickerAsync(
        new FolderPickerOpenOptions {
          Title = "Select folder for downloaded DataFlash logs",
          AllowMultiple = false,
        });
    return folders.FirstOrDefault()?.TryGetLocalPath();
  }

  public void Dispose() {
    if (_disposed) {
      return;
    }
    _disposed = true;
    TryCancel(_operationCancellation);
    _session.Stop();
    _ = _session.DisposeAsync();
  }

  public async ValueTask DisposeAsync() {
    if (_disposed) {
      return;
    }
    await CancelAndWaitAsync();
    _disposed = true;
    await _session.DisposeAsync();
  }

  private sealed class InlineProgress<T>(Action<T> report) : IProgress<T> {
    public void Report(T value) => report(value);
  }
}

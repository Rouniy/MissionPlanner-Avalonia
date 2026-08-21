using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using MissionPlannerAvalonia.Services;
using MissionPlannerAvalonia.ViewModels;
using MissionPlannerAvalonia.ViewModels.GCSViews.ConfigurationView;
using MissionPlannerAvalonia.Views;

namespace MissionPlannerAvalonia.Tests;

public class SftpLogDownloadTests {
  [Theory]
  [InlineData("../../etc/passwd", "passwd.bin")]
  [InlineData("..\\evil.bin", "evil.bin")]
  [InlineData("..", "log_7.bin")]
  [InlineData(".hidden.bin", "log_hidden.bin")]
  [InlineData("--flag.bin", "log_flag.bin")]
  [InlineData("flight\0name.bin", "flight_name.bin")]
  [InlineData("flight\u202ename.bin", "flight_name.bin")]
  [InlineData("UPPER.BIN", "UPPER.BIN")]
  public void Remote_filenames_are_sanitized_before_becoming_local_paths(
      string remote, string expected) {
    string safe = SftpLocalFile.SafeBinName(remote, 7);

    Assert.Equal(expected, safe);
    Assert.True(safe.Length <= 100);
    Assert.DoesNotContain('/', safe);
    Assert.DoesNotContain('\\', safe);
  }

  [Fact]
  public void Long_remote_filename_is_capped_and_combined_path_stays_in_destination() {
    string safe = SftpLocalFile.SafeBinName(new string('a', 300) + ".bin");
    string root = Path.Combine(Path.GetTempPath(), "mpa-safe-root");
    string combined = SftpLocalFile.CombineContained(root, safe);

    Assert.Equal(100, safe.Length);
    Assert.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, combined);
    Assert.Throws<IOException>(() => SftpLocalFile.CombineContained(root, "../escape.bin"));
  }

  [Theory]
  [InlineData("/var/log/dataflash/", "/var/log/dataflash")]
  [InlineData("/", "/")]
  [InlineData("//var///log", "/var/log")]
  public void Remote_directory_is_normalized_as_an_absolute_posix_path(
      string input, string expected) {
    Assert.Equal(expected, SftpRemotePath.NormalizeDirectory(input));
  }

  [Theory]
  [InlineData("relative/path")]
  [InlineData("/var/../etc")]
  [InlineData("/var/./log")]
  public void Unsafe_remote_directory_is_rejected(string input) {
    Assert.Throws<ArgumentException>(() => SftpRemotePath.NormalizeDirectory(input));
  }

  [Theory]
  [InlineData("pilot:p:a@ss@companion.local:/var/log/dataflash/",
      "companion.local", 22, "pilot", "/var/log/dataflash")]
  [InlineData("pilot:secret@companion.local:2202:/logs/",
      "companion.local", 2202, "pilot", "/logs")]
  [InlineData("root:secret@[fe80::1234]:2222:/log/dataflash/",
      "fe80::1234", 2222, "root", "/log/dataflash")]
  public void Legacy_upstream_setting_migrates_endpoint_without_returning_password(
      string input, string expectedHost, int expectedPort, string expectedUser, string expectedDirectory) {
    Assert.True(SftpLogDownloadViewModel.TryParseLegacyPath(
        input, out string host, out int port, out string username, out string directory));
    Assert.Equal(expectedHost, host);
    Assert.Equal(expectedPort, port);
    Assert.Equal(expectedUser, username);
    Assert.Equal(expectedDirectory, directory);
  }

  [AvaloniaFact]
  public async Task Refresh_uses_shared_host_key_challenge_and_never_retains_password() {
    var session = new FakeSftpLogSession {
      PresentedFingerprint = "SHA256:test-fingerprint",
      Entries = [Entry("00000001.BIN", 4096)],
    };
    int prompts = 0;
    await using var viewModel = ReadyViewModel(
        session,
        confirmHostKey: challenge => {
          prompts++;
          Assert.False(challenge.IsChanged);
          return Task.FromResult(true);
        });
    viewModel.Password = "top-secret";

    await viewModel.RefreshLogsCommand.ExecuteAsync(null);

    Assert.Equal(2, session.ConnectCalls.Count);
    Assert.Null(session.ConnectCalls[0].TrustedFingerprint);
    Assert.Equal(session.PresentedFingerprint, session.ConnectCalls[1].TrustedFingerprint);
    Assert.All(session.ConnectCalls, call => Assert.Equal("top-secret", call.Connection.Password));
    Assert.Equal(1, prompts);
    Assert.Equal("", viewModel.Password);
    Assert.True(viewModel.IsConnected);
    Assert.Single(viewModel.Logs);
  }

  [AvaloniaFact]
  public async Task Refresh_replaces_rows_instead_of_reproducing_upstream_duplicate_index_bug() {
    var session = new FakeSftpLogSession {
      Entries = [Entry("one.bin", 10), Entry("two.bin", 20)],
    };
    await using var viewModel = ReadyViewModel(session);

    await viewModel.RefreshLogsCommand.ExecuteAsync(null);
    await viewModel.RefreshLogsCommand.ExecuteAsync(null);

    Assert.Equal(new[] { "one.bin", "two.bin" },
        viewModel.Logs.OrderBy(row => row.Name).Select(row => row.Name));
  }

  [AvaloniaFact]
  public async Task Failed_download_removes_partial_and_never_creates_destination() {
    string directory = NewTempDirectory();
    try {
      var session = new FakeSftpLogSession {
        Entries = [Entry("bad.bin", 4)],
        FailDownload = true,
      };
      await using var viewModel = ReadyViewModel(session, pickFolder: () => Task.FromResult<string?>(directory));
      await viewModel.RefreshLogsCommand.ExecuteAsync(null);
      viewModel.Logs[0].IsSelected = true;

      await viewModel.DownloadSelectedCommand.ExecuteAsync(null);

      Assert.Empty(Directory.EnumerateFiles(directory));
      Assert.Contains("failed", viewModel.Status, StringComparison.OrdinalIgnoreCase);
    } finally {
      Directory.Delete(directory, recursive: true);
    }
  }

  [AvaloniaFact]
  public async Task Cancellation_drains_download_and_removes_partial_file() {
    string directory = NewTempDirectory();
    try {
      var session = new FakeSftpLogSession {
        Entries = [Entry("active.bin", 4)],
        BlockDownload = true,
      };
      await using var viewModel = ReadyViewModel(session, pickFolder: () => Task.FromResult<string?>(directory));
      await viewModel.RefreshLogsCommand.ExecuteAsync(null);
      viewModel.Logs[0].IsSelected = true;

      Task download = viewModel.DownloadSelectedCommand.ExecuteAsync(null);
      await session.DownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
      viewModel.CancelCommand.Execute(null);
      await download;

      Assert.False(viewModel.IsBusy);
      Assert.Empty(Directory.EnumerateFiles(directory));
      Assert.Contains("cancel", viewModel.Status, StringComparison.OrdinalIgnoreCase);
    } finally {
      Directory.Delete(directory, recursive: true);
    }
  }

  [AvaloniaFact]
  public async Task Remote_delete_is_selected_only_and_continues_after_one_failure() {
    var session = new FakeSftpLogSession {
      Entries = [Entry("one.bin", 10), Entry("two.bin", 20), Entry("three.bin", 30)],
    };
    session.DeleteFailures.Add("one.bin");
    await using var viewModel = ReadyViewModel(
        session, confirmDangerous: (_, _, _) => Task.FromResult(true));
    await viewModel.RefreshLogsCommand.ExecuteAsync(null);
    viewModel.Logs.Single(row => row.Name == "one.bin").IsSelected = true;
    viewModel.Logs.Single(row => row.Name == "two.bin").IsSelected = true;

    await viewModel.DeleteSelectedCommand.ExecuteAsync(null);

    Assert.Equal(new[] { "two.bin" }, session.DeletedNames);
    Assert.Equal(new[] { "one.bin", "three.bin" },
        viewModel.Logs.OrderBy(row => row.Name).Select(row => row.Name));
    Assert.Contains("1/2", viewModel.Status);
  }

  [AvaloniaFact]
  public void Sftp_window_and_developer_tools_expose_the_upstream_log_workflow() {
    using var developerTools = new ConfigDeveloperToolsViewModel();
    using var viewModel = ReadyViewModel(new FakeSftpLogSession());
    var window = new SftpLogDownloadWindow(viewModel);
    try {
      Assert.Contains(developerTools.Actions,
          action => action.Label == "Download DataFlash Logs over SFTP");
      Assert.NotNull(window.FindControl<TextBox>("SftpHost"));
      Assert.Equal('●', window.FindControl<TextBox>("SftpPassword")?.PasswordChar);
      Assert.NotNull(window.FindControl<DataGrid>("RemoteLogGrid"));
    } finally {
      window.Close();
    }
  }

  private static SftpLogDownloadViewModel ReadyViewModel(
      FakeSftpLogSession session,
      Func<SshHostKeyChallenge, Task<bool>>? confirmHostKey = null,
      Func<string, string, string, Task<bool>>? confirmDangerous = null,
      Func<Task<string?>>? pickFolder = null) => new(
          session,
          confirmHostKey ?? (_ => Task.FromResult(false)),
          confirmDangerous ?? ((_, _, _) => Task.FromResult(false)),
          pickFolder ?? (() => Task.FromResult<string?>(null)),
          usePersistentSettings: false) {
        Host = "companion.local",
        Port = "22",
        Username = "pilot",
        Password = "secret",
        RemoteDirectory = "/var/log/dataflash",
        CreateKmlAfterDownload = false,
      };

  private static SftpLogEntry Entry(string name, long length) =>
      new("/var/log/dataflash", name, length,
          new DateTime(2026, 8, 22, 10, 0, 0, DateTimeKind.Utc));

  private static string NewTempDirectory() {
    string path = Path.Combine(Path.GetTempPath(), "mpa-sftp-tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(path);
    return path;
  }

  private sealed class FakeSftpLogSession : ISftpLogSession {
    public bool IsConnected { get; private set; }
    public IReadOnlyList<SftpLogEntry> Entries { get; init; } = Array.Empty<SftpLogEntry>();
    public string? PresentedFingerprint { get; init; }
    public bool FailDownload { get; init; }
    public bool BlockDownload { get; init; }
    public List<(SftpLogConnection Connection, string? TrustedFingerprint)> ConnectCalls { get; } = new();
    public List<string> DeletedNames { get; } = new();
    public HashSet<string> DeleteFailures { get; } = new(StringComparer.Ordinal);
    public TaskCompletionSource DownloadStarted { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    public Task ConnectAsync(SftpLogConnection connection, string? trustedFingerprint,
        CancellationToken cancellationToken) {
      cancellationToken.ThrowIfCancellationRequested();
      ConnectCalls.Add((connection, trustedFingerprint));
      if (PresentedFingerprint != null && trustedFingerprint != PresentedFingerprint) {
        throw new SshHostKeyException(new SshHostKeyChallenge(
            connection.Host, connection.Port, "ssh-ed25519", 256,
            PresentedFingerprint, trustedFingerprint));
      }
      IsConnected = true;
      return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SftpLogEntry>> ListLogsAsync(
        string remoteDirectory, CancellationToken cancellationToken) {
      cancellationToken.ThrowIfCancellationRequested();
      return Task.FromResult(Entries);
    }

    public async Task<long> DownloadAsync(SftpLogEntry entry, Stream destination,
        IProgress<long>? progress, CancellationToken cancellationToken) {
      byte[] bytes = [1, 2, 3, 4];
      await destination.WriteAsync(bytes, cancellationToken);
      progress?.Report(bytes.Length);
      DownloadStarted.TrySetResult();
      if (BlockDownload) {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
      }
      if (FailDownload) {
        throw new IOException("simulated stream failure");
      }
      return bytes.Length;
    }

    public Task DeleteAsync(SftpLogEntry entry, CancellationToken cancellationToken) {
      cancellationToken.ThrowIfCancellationRequested();
      if (DeleteFailures.Contains(entry.Name)) {
        throw new IOException("simulated delete failure");
      }
      DeletedNames.Add(entry.Name);
      return Task.CompletedTask;
    }

    public Task StopAsync() {
      Stop();
      return Task.CompletedTask;
    }

    public void Stop() => IsConnected = false;

    public ValueTask DisposeAsync() {
      Stop();
      return ValueTask.CompletedTask;
    }
  }
}

using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using MissionPlannerAvalonia.Services;
using MissionPlannerAvalonia.ViewModels;
using MissionPlannerAvalonia.Views;
using SkiaSharp;

namespace MissionPlannerAvalonia.Tests;

public class LogIndexTests {
  [Fact]
  public void Discovery_is_recursive_case_insensitive_and_skips_unrelated_files() {
    using var directory = TempDirectory.Create();
    Directory.CreateDirectory(Path.Combine(directory.Path, "nested"));
    File.WriteAllText(Path.Combine(directory.Path, "one.TLOG"), "x");
    File.WriteAllText(Path.Combine(directory.Path, "nested", "two.Bin"), "x");
    File.WriteAllText(Path.Combine(directory.Path, "three.log"), "x");
    File.WriteAllText(Path.Combine(directory.Path, "ignore.rlog"), "x");
    File.WriteAllText(Path.Combine(directory.Path, "ignore.jpg"), "x");

    string[] names = LogIndexService.DiscoverFiles(directory.Path)
        .Select(Path.GetFileName).OrderBy(value => value).ToArray()!;

    Assert.Equal(new[] { "one.TLOG", "three.log", "two.Bin" }, names);
  }

  [Fact]
  public async Task DataFlash_index_recovers_upstream_columns_and_flight_metrics() {
    using var directory = TempDirectory.Create();
    string path = Path.Combine(directory.Path, "flight.log");
    File.WriteAllText(path, string.Join('\n', new[] {
      "FMT, 128, 89, FMT, BBnNZ, Type,Length,Name,Format,Columns",
      "FMT, 129, 44, GPS, QBffff, TimeUS,Status,Lat,Lng,Alt,Spd",
      "FMT, 130, 32, MSG, QZ, TimeUS,Message",
      "FMT, 131, 32, PARM, QNf, TimeUS,Name,Value",
      "FMT, 132, 16, CAM, Q, TimeUS",
      "MSG, 500000, ArduCopter V4.6",
      "PARM, 600000, SYSID_THISMAV, 7",
      "GPS, 1000000, 3, 35.100000, 33.200000, 100, 12",
      "GPS, 3000000, 3, 35.100180, 33.200000, 105, 12",
      "CAM, 3100000",
    }) + "\n");
    var service = new LogIndexService(new FixedThumbnailRenderer());

    LogIndexEntry entry = Assert.Single(await service.ScanAsync(
        directory.Path, null, CancellationToken.None));

    Assert.Equal("ArduCopter", entry.Frame);
    Assert.Equal(7, entry.SystemId);
    Assert.NotNull(entry.Home);
    Assert.Equal(35.1, entry.Home!.Latitude, 6);
    Assert.Equal(33.2, entry.Home.Longitude, 6);
    Assert.Equal(1, entry.CameraMessages);
    Assert.InRange(entry.DistanceMeters, 15, 30);
    Assert.InRange(entry.TimeInAir.TotalSeconds, 1.9, 2.1);
    Assert.InRange(entry.Duration.TotalSeconds, 1.9, 2.1);
    Assert.Equal(DateTime.MinValue, entry.Date);
    Assert.Null(entry.Error);
  }

  [Fact]
  public async Task Timestamped_tlog_index_reads_frame_sysid_duration_route_and_camera_count() {
    using var directory = TempDirectory.Create();
    string path = Path.Combine(directory.Path, "flight.tlog");
    DateTime start = new(2026, 8, 21, 10, 0, 0, DateTimeKind.Utc);
    using (var stream = File.Create(path)) {
      WriteTlogPacket(stream, start, MAVLink.MAVLINK_MSG_ID.HEARTBEAT,
          new MAVLink.mavlink_heartbeat_t(0, (byte)MAVLink.MAV_TYPE.QUADROTOR,
              (byte)MAVLink.MAV_AUTOPILOT.ARDUPILOTMEGA,
              (byte)MAVLink.MAV_MODE_FLAG.SAFETY_ARMED, 0, 3), 12);
      WriteTlogPacket(stream, start.AddMilliseconds(100), MAVLink.MAVLINK_MSG_ID.GPS_RAW_INT,
          new MAVLink.mavlink_gps_raw_int_t(
              0, 351000000, 332000000, 100000, 0, 0, 1000, 0, 3, 12,
              0, 0, 0, 0, 0, 0), 12);
      WriteTlogPacket(stream, start.AddSeconds(1), MAVLink.MAVLINK_MSG_ID.GLOBAL_POSITION_INT,
          new MAVLink.mavlink_global_position_int_t(
              1000, 351000000, 332000000, 100000, 0, 1200, 0, 0, 0), 12);
      WriteTlogPacket(stream, start.AddSeconds(3), MAVLink.MAVLINK_MSG_ID.GLOBAL_POSITION_INT,
          new MAVLink.mavlink_global_position_int_t(
              3000, 351001800, 332000000, 105000, 5000, 1200, 0, 0, 0), 12);
      WriteTlogPacket(stream, start.AddSeconds(4), MAVLink.MAVLINK_MSG_ID.CAMERA_FEEDBACK,
          new MAVLink.mavlink_camera_feedback_t(
              0, 351001800, 332000000, 105, 5, 0, 0, 0, 0, 1, 12, 0, 0, 1), 12);
    }
    var service = new LogIndexService(new FixedThumbnailRenderer());

    LogIndexEntry entry = Assert.Single(await service.ScanAsync(
        directory.Path, null, CancellationToken.None));

    Assert.Equal(MAVLink.MAV_TYPE.QUADROTOR.ToString(), entry.Frame);
    Assert.Equal(12, entry.SystemId);
    Assert.Equal(1, entry.CameraMessages);
    Assert.InRange(entry.Duration.TotalSeconds, 3.9, 4.1);
    Assert.InRange(entry.TimeInAir.TotalSeconds, 1.9, 2.1);
    Assert.InRange(entry.DistanceMeters, 15, 30);
    Assert.Equal(start.ToLocalTime(), entry.Date);
    Assert.Null(entry.Error);
  }

  [Fact]
  public async Task Empty_or_truncated_log_becomes_an_index_row_instead_of_aborting_scan() {
    using var directory = TempDirectory.Create();
    File.WriteAllBytes(Path.Combine(directory.Path, "empty.tlog"), []);
    File.WriteAllBytes(Path.Combine(directory.Path, "truncated.bin"), [0xa3, 0x95, 0x80]);
    var service = new LogIndexService(new FixedThumbnailRenderer());

    IReadOnlyList<LogIndexEntry> entries = await service.ScanAsync(
        directory.Path, null, CancellationToken.None);

    Assert.Equal(2, entries.Count);
    Assert.All(entries, entry => Assert.NotEmpty(entry.ThumbnailJpeg));
  }

  [Fact]
  public async Task Thumbnail_sidecar_is_exactly_source_plus_jpg_and_fresh_copy_is_reused() {
    using var directory = TempDirectory.Create();
    string source = Path.Combine(directory.Path, "same.name.tlog");
    File.WriteAllText(source, "log");
    DateTime write = DateTime.UtcNow.AddMinutes(-2);
    File.SetLastWriteTimeUtc(source, write);
    byte[] expected = LogThumbnailRenderer.RenderFallback([], "already cached");
    string sidecar = source + ".jpg";
    File.WriteAllBytes(sidecar, expected);
    File.SetLastWriteTimeUtc(sidecar, write.AddSeconds(1));
    var renderer = new LogThumbnailRenderer();

    byte[] actual = await renderer.GetOrCreateAsync(source,
        [(35.1, 33.2), (35.2, 33.3)], CancellationToken.None);

    Assert.Equal(expected, actual);
    Assert.True(File.Exists(source + ".jpg"));
    Assert.False(File.Exists(Path.ChangeExtension(source, ".jpg")));
  }

  [Fact]
  public async Task Stale_thumbnail_is_regenerated_as_a_valid_jpeg() {
    using var directory = TempDirectory.Create();
    string source = Path.Combine(directory.Path, "flight.log");
    File.WriteAllText(source, "log");
    string sidecar = source + ".jpg";
    File.WriteAllBytes(sidecar, Encoding.UTF8.GetBytes("not an image"));
    File.SetLastWriteTimeUtc(sidecar, DateTime.UtcNow.AddHours(-1));
    DateTime sourceWrite = DateTime.UtcNow.AddMinutes(-1);
    File.SetLastWriteTimeUtc(source, sourceWrite);

    byte[] jpeg = await new LogThumbnailRenderer().GetOrCreateAsync(
        source, [], CancellationToken.None);

    using SKBitmap? bitmap = SKBitmap.Decode(jpeg);
    Assert.NotNull(bitmap);
    Assert.Equal(240, bitmap!.Width);
    Assert.Equal(140, bitmap.Height);
    Assert.Equal(jpeg, File.ReadAllBytes(sidecar));
    Assert.Equal(File.GetLastWriteTimeUtc(source), File.GetLastWriteTimeUtc(sidecar));
  }

  [Fact]
  public async Task Delete_removes_only_selected_source_exact_thumbnail_and_paired_rlog() {
    using var directory = TempDirectory.Create();
    string source = Path.Combine(directory.Path, "flight.tlog");
    string thumbnail = source + ".jpg";
    string pairedRlog = Path.ChangeExtension(source, ".rlog");
    string unrelatedLog = Path.ChangeExtension(source, ".log");
    foreach (string file in new[] { source, thumbnail, pairedRlog, unrelatedLog }) {
      File.WriteAllText(file, file);
    }
    LogIndexEntry entry = EntryFor(source, directory.Path);

    LogIndexDeleteResult result = await new LogIndexService(new FixedThumbnailRenderer())
        .DeleteAsync(directory.Path, [entry], CancellationToken.None);

    Assert.Single(result.DeletedLogs);
    Assert.Empty(result.Errors);
    Assert.False(File.Exists(source));
    Assert.False(File.Exists(thumbnail));
    Assert.False(File.Exists(pairedRlog));
    Assert.True(File.Exists(unrelatedLog));
  }

  [Fact]
  public async Task Delete_refuses_a_log_changed_after_indexing_and_continues_with_other_rows() {
    using var directory = TempDirectory.Create();
    string changed = Path.Combine(directory.Path, "changed.log");
    string safe = Path.Combine(directory.Path, "safe.bin");
    File.WriteAllText(changed, "old");
    File.WriteAllText(safe, "safe");
    LogIndexEntry changedEntry = EntryFor(changed, directory.Path);
    LogIndexEntry safeEntry = EntryFor(safe, directory.Path);
    File.AppendAllText(changed, "-new");

    LogIndexDeleteResult result = await new LogIndexService(new FixedThumbnailRenderer())
        .DeleteAsync(directory.Path, [changedEntry, safeEntry], CancellationToken.None);

    Assert.True(File.Exists(changed));
    Assert.False(File.Exists(safe));
    Assert.Single(result.DeletedLogs);
    Assert.Single(result.Errors);
    Assert.Contains("changed", result.Errors[0], StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public async Task Delete_refuses_rlog_that_appeared_after_indexing() {
    using var directory = TempDirectory.Create();
    string source = Path.Combine(directory.Path, "flight.tlog");
    File.WriteAllText(source, "source");
    LogIndexEntry entry = EntryFor(source, directory.Path);
    string rlog = Path.ChangeExtension(source, ".rlog");
    File.WriteAllText(rlog, "new companion");

    LogIndexDeleteResult result = await new LogIndexService(new FixedThumbnailRenderer())
        .DeleteAsync(directory.Path, [entry], CancellationToken.None);

    Assert.Empty(result.DeletedLogs);
    Assert.Single(result.Errors);
    Assert.True(File.Exists(source));
    Assert.True(File.Exists(rlog));
  }

  [Fact]
  public async Task Delete_refuses_linked_companion_before_removing_the_source() {
    if (OperatingSystem.IsWindows()) {
      return;
    }
    using var directory = TempDirectory.Create();
    string source = Path.Combine(directory.Path, "flight.tlog");
    string outside = Path.Combine(directory.Path, "outside.jpg");
    File.WriteAllText(source, "source");
    File.WriteAllText(outside, "outside");
    File.CreateSymbolicLink(source + ".jpg", outside);
    LogIndexEntry entry = EntryFor(source, directory.Path);

    LogIndexDeleteResult result = await new LogIndexService(new FixedThumbnailRenderer())
        .DeleteAsync(directory.Path, [entry], CancellationToken.None);

    Assert.Empty(result.DeletedLogs);
    Assert.Single(result.Errors);
    Assert.True(File.Exists(source));
    Assert.Equal("outside", File.ReadAllText(outside));
  }

  [AvaloniaFact]
  public async Task ViewModel_aggregates_selection_and_deletes_only_after_dangerous_confirmation() {
    using var directory = TempDirectory.Create();
    LogIndexEntry first = SyntheticEntry(Path.Combine(directory.Path, "one.log"), directory.Path,
        TimeSpan.FromMinutes(2), 1200);
    LogIndexEntry second = SyntheticEntry(Path.Combine(directory.Path, "two.log"), directory.Path,
        TimeSpan.FromMinutes(3), 800);
    var service = new FakeIndexService([first, second]);
    int confirmations = 0;
    await using var viewModel = new LogIndexViewModel(directory.Path, service,
        (_, _, _) => {
          confirmations++;
          return Task.FromResult(true);
        });

    await viewModel.RefreshAsync();
    viewModel.UpdateSelection(viewModel.Rows);
    Assert.Contains("00:05:00", viewModel.SelectedSummary);
    await viewModel.DeleteSelectedAsync(viewModel.Rows.ToArray());

    Assert.Equal(1, confirmations);
    Assert.Equal(2, service.DeleteEntries.Count);
    Assert.Empty(viewModel.Rows);
  }

  [AvaloniaFact]
  public void Duration_formatter_handles_multi_day_archives() {
    Assert.Equal("1.02:03:04", LogIndexRow.FormatDuration(
        TimeSpan.FromDays(1) + TimeSpan.FromHours(2) +
        TimeSpan.FromMinutes(3) + TimeSpan.FromSeconds(4)));
  }

  [AvaloniaFact]
  public void Window_exposes_all_upstream_index_columns_and_extended_selection() {
    using var viewModel = new LogIndexViewModel(
        Path.GetTempPath(), new FakeIndexService([]), (_, _, _) => Task.FromResult(false));
    var window = new LogIndexWindow(viewModel);
    var grid = window.FindControl<DataGrid>("LogGrid");

    Assert.NotNull(grid);
    Assert.Equal(DataGridSelectionMode.Extended, grid!.SelectionMode);
    string[] headers = grid.Columns.Select(column => column.Header?.ToString() ?? "").ToArray();
    Assert.Equal(new[] {
      "Map", "Date", "Directory", "Frame", "Aircraft / sysid", "Duration", "Name", "Size",
      "Home", "Time in air", "Distance", "CAM", "Read warning",
    }, headers);
  }

  private static LogIndexEntry EntryFor(string source, string root) {
    var info = new FileInfo(source);
    var rlog = new FileInfo(Path.ChangeExtension(source, ".rlog"));
    LogFileStamp? paired = rlog.Exists
        ? new LogFileStamp(rlog.Length, rlog.LastWriteTimeUtc)
        : null;
    return new LogIndexEntry(source, root, info.Length, info.LastWriteTimeUtc,
        DateTime.MinValue, "Unknown", 0, TimeSpan.Zero, null, TimeSpan.Zero,
        0, 0, FixedThumbnailRenderer.Jpeg, null, paired);
  }

  private static LogIndexEntry SyntheticEntry(
      string source, string root, TimeSpan air, double distance) => new(
      source, root, 10, DateTime.UtcNow, DateTime.UtcNow, "QUADROTOR", 1,
      TimeSpan.FromMinutes(10), new LogHome(35, 33, 100), air, distance, 0,
      FixedThumbnailRenderer.Jpeg, null);

  private static void WriteTlogPacket(
      Stream destination, DateTime time, MAVLink.MAVLINK_MSG_ID id, object payload, byte system) {
    ulong microseconds = (ulong)((time.ToUniversalTime() - DateTime.UnixEpoch).Ticks / 10);
    byte[] stamp = BitConverter.GetBytes(microseconds);
    if (BitConverter.IsLittleEndian) {
      Array.Reverse(stamp);
    }
    destination.Write(stamp);
    var parser = new MAVLink.MavlinkParse();
    destination.Write(parser.GenerateMAVLinkPacket20(id, payload, false, system, 1));
  }

  private sealed class FixedThumbnailRenderer : ILogThumbnailRenderer {
    internal static readonly byte[] Jpeg = LogThumbnailRenderer.RenderFallback([], "test");

    public Task<byte[]> GetOrCreateAsync(string sourcePath,
        IReadOnlyList<(double Lat, double Lng)> track, CancellationToken cancellationToken) =>
        Task.FromResult(Jpeg);
  }

  private sealed class FakeIndexService(IReadOnlyList<LogIndexEntry> entries) : ILogIndexService {
    internal IReadOnlyList<LogIndexEntry> DeleteEntries { get; private set; } = [];

    public Task<IReadOnlyList<LogIndexEntry>> ScanAsync(string root,
        IProgress<LogIndexProgress>? progress, CancellationToken cancellationToken) =>
        Task.FromResult(entries);

    public Task<LogIndexDeleteResult> DeleteAsync(string root,
        IReadOnlyList<LogIndexEntry> selected, CancellationToken cancellationToken) {
      DeleteEntries = selected;
      return Task.FromResult(new LogIndexDeleteResult(
          selected.Select(entry => entry.FullPath).ToArray(), []));
    }
  }

  private sealed class TempDirectory : IDisposable {
    internal string Path { get; }

    private TempDirectory(string path) => Path = path;

    internal static TempDirectory Create() {
      string path = System.IO.Path.Combine(
          System.IO.Path.GetTempPath(), "mp-log-index-" + Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(path);
      return new TempDirectory(path);
    }

    public void Dispose() {
      try {
        Directory.Delete(Path, recursive: true);
      } catch {
      }
    }
  }
}

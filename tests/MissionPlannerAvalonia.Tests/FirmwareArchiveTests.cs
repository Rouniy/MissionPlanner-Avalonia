using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Xml.Linq;
using MissionPlannerAvalonia.Services;
using MissionPlannerAvalonia.ViewModels.GCSViews.ConfigurationView;

namespace MissionPlannerAvalonia.Tests;

public class FirmwareArchiveTests {
  [Fact]
  public async Task Downloads_official_manifest_with_fallback_deduplication_and_bounded_parallelism() {
    using var root = new TempDirectory();
    string destination = Path.Combine(root.Path, "archive");
    Uri primary = new("https://primary.example/firmware2.xml");
    Uri fallback = new("https://fallback.example/firmware2.xml");
    string[] firmwareUris = Enumerable.Range(0, 8)
        .Select(index => $"https://cdn.example/vehicle/firmware-{index}.apj")
        .ToArray();
    string manifest = "<options><Firmware><name>Copter</name>"
        + string.Join("", firmwareUris.Select((uri, index) => $"<url{index}>{uri}</url{index}>"))
        + $"<urlDuplicate>{firmwareUris[0]}</urlDuplicate><urlEmpty />"
        + "</Firmware></options>";
    var payloads = firmwareUris.ToDictionary(
        uri => uri, uri => Encoding.UTF8.GetBytes("payload:" + uri));
    var handler = new ArchiveHandler(async (request, token) => {
      string uri = request.RequestUri!.AbsoluteUri;
      if (uri == primary.AbsoluteUri) {
        return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
      }
      if (uri == fallback.AbsoluteUri) {
        return Xml(manifest);
      }
      await Task.Delay(30, token);
      return Bytes(payloads[uri]);
    });
    using var http = new HttpClient(handler);
    var progress = new ConcurrentQueue<FirmwareArchiveProgress>();
    var service = new FirmwareArchiveService(http);

    FirmwareArchiveResult result = await service.DownloadAsync(
        new[] { primary, fallback }, destination,
        new InlineProgress<FirmwareArchiveProgress>(progress.Enqueue), CancellationToken.None);

    Assert.Equal(destination, result.Directory);
    Assert.Equal(fallback, result.ManifestSource);
    Assert.Equal(firmwareUris.Length, result.FileCount);
    Assert.Equal(0, result.FailedFiles);
    Assert.Equal(payloads.Values.Sum(value => value.Length), result.BytesDownloaded);
    Assert.InRange(handler.MaximumActiveFirmwareRequests, 2,
        FirmwareArchiveService.MaxParallelDownloads);
    Assert.Equal(1, handler.Requests.Count(uri => uri == firmwareUris[0]));
    Assert.Equal(firmwareUris.Length, progress.Last().Completed);
    Assert.Equal(firmwareUris.Length, progress.Last().Total);

    XDocument localManifest = XDocument.Load(Path.Combine(destination, "firmware2.xml"));
    string[] localReferences = localManifest.Descendants()
        .Where(element => element.Name.LocalName.StartsWith("url", StringComparison.OrdinalIgnoreCase))
        .Select(element => element.Value)
        .Where(value => value.Length > 0)
        .ToArray();
    Assert.Equal(firmwareUris.Length + 1, localReferences.Length);
    Assert.All(localReferences, relative => {
      Assert.DoesNotContain("https://", relative, StringComparison.OrdinalIgnoreCase);
      string fullPath = Path.GetFullPath(Path.Combine(destination,
          relative.Replace('/', Path.DirectorySeparatorChar)));
      Assert.StartsWith(destination + Path.DirectorySeparatorChar, fullPath,
          StringComparison.Ordinal);
      Assert.True(File.Exists(fullPath));
    });
    string[] checksums = File.ReadAllLines(Path.Combine(destination, "checksums.sha256"));
    Assert.Equal(firmwareUris.Length, checksums.Length);
    Assert.All(checksums, line => Assert.Matches("^[0-9a-f]{64}  files/", line));
    Assert.Contains("Unavailable: 0",
        File.ReadAllText(Path.Combine(destination, "archive-report.txt")));
    Assert.Empty(Directory.GetDirectories(root.Path, "*.partial-*"));
  }

  [Fact]
  public async Task Legacy_http_urls_try_https_then_fallback_and_record_partial_failures() {
    using var root = new TempDirectory();
    string destination = Path.Combine(root.Path, "legacy");
    var manifest = new Uri("https://manifest.example/firmware2.xml");
    const string upgraded = "http://legacy.example/vehicle/secure.apj";
    const string fallback = "http://legacy.example/vehicle/fallback.apj";
    const string missing = "https://legacy.example/vehicle/missing.apj";
    string xml = $"<options><Firmware><url>{upgraded}</url>"
        + $"<url2>{fallback}</url2><url3>{missing}</url3></Firmware></options>";
    var handler = new ArchiveHandler((request, _) => {
      string uri = request.RequestUri!.AbsoluteUri;
      return Task.FromResult(uri switch {
        "https://manifest.example/firmware2.xml" => Xml(xml),
        "https://legacy.example/vehicle/secure.apj" => Bytes(new byte[] { 1, 2, 3 }),
        "https://legacy.example/vehicle/fallback.apj" =>
          new HttpResponseMessage(HttpStatusCode.BadGateway),
        "http://legacy.example/vehicle/fallback.apj" => Bytes(new byte[] { 4, 5 }),
        "https://legacy.example/vehicle/missing.apj" =>
          new HttpResponseMessage(HttpStatusCode.NotFound),
        _ => new HttpResponseMessage(HttpStatusCode.NotFound),
      });
    });
    using var http = new HttpClient(handler);
    var service = new FirmwareArchiveService(http);

    FirmwareArchiveResult result = await service.DownloadAsync(
        new[] { manifest }, destination, null, CancellationToken.None);

    Assert.Equal(2, result.FileCount);
    Assert.Equal(1, result.FailedFiles);
    Assert.Equal(5, result.BytesDownloaded);
    Assert.Contains("https://legacy.example/vehicle/secure.apj", handler.Requests);
    Assert.DoesNotContain(upgraded, handler.Requests);
    Assert.Contains("https://legacy.example/vehicle/fallback.apj", handler.Requests);
    Assert.Contains(fallback, handler.Requests);
    string report = File.ReadAllText(Path.Combine(destination, "archive-report.txt"));
    Assert.Contains("Unavailable: 1", report);
    Assert.Contains(missing, report);
    XDocument local = XDocument.Load(Path.Combine(destination, "firmware2.xml"));
    Assert.Equal(missing, local.Descendants("url3").Single().Value);
    Assert.StartsWith("files/", local.Descendants("url").Single().Value);
    Assert.StartsWith("files/", local.Descendants("url2").Single().Value);
  }

  [Fact]
  public async Task Cancellation_removes_owned_staging_directory_and_never_publishes_archive() {
    using var root = new TempDirectory();
    string destination = Path.Combine(root.Path, "cancelled");
    var manifest = new Uri("https://manifest.example/firmware2.xml");
    var firmware = new Uri("https://cdn.example/vehicle/copter.apj");
    var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var handler = new ArchiveHandler(async (request, token) => {
      if (request.RequestUri == manifest) {
        return Xml($"<options><Firmware><url>{firmware}</url></Firmware></options>");
      }
      started.TrySetResult();
      await Task.Delay(Timeout.InfiniteTimeSpan, token);
      throw new InvalidOperationException("unreachable");
    });
    using var http = new HttpClient(handler);
    var service = new FirmwareArchiveService(http);
    using var cancellation = new CancellationTokenSource();

    Task<FirmwareArchiveResult> download = service.DownloadAsync(
        new[] { manifest }, destination, null, cancellation.Token);
    await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
    cancellation.Cancel();

    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
    Assert.False(Directory.Exists(destination));
    Assert.Empty(Directory.GetDirectories(root.Path, "*.partial-*"));
  }

  [Fact]
  public async Task All_failed_or_oversized_downloads_remove_staging_and_report_failure() {
    using var root = new TempDirectory();
    string destination = Path.Combine(root.Path, "oversized");
    var manifest = new Uri("https://manifest.example/firmware2.xml");
    var firmware = new Uri("http://cdn.example/vehicle/huge.apj");
    var handler = new ArchiveHandler((request, _) => {
      if (request.RequestUri == manifest) {
        return Task.FromResult(Xml(
            $"<options><Firmware><url>{firmware}</url></Firmware></options>"));
      }
      HttpResponseMessage response = Bytes(new byte[] { 1 });
      response.Content.Headers.ContentLength = FirmwareArchiveService.MaxFirmwareBytes + 1;
      return Task.FromResult(response);
    });
    using var http = new HttpClient(handler);
    var service = new FirmwareArchiveService(http);

    await Assert.ThrowsAsync<HttpRequestException>(() => service.DownloadAsync(
        new[] { manifest }, destination, null, CancellationToken.None));

    Assert.Contains("https://cdn.example/vehicle/huge.apj", handler.Requests);
    Assert.DoesNotContain(firmware.AbsoluteUri, handler.Requests);
    Assert.False(Directory.Exists(destination));
    Assert.Empty(Directory.GetDirectories(root.Path, "*.partial-*"));
  }

  [Fact]
  public async Task Existing_destination_is_rejected_before_network_or_file_changes() {
    using var root = new TempDirectory();
    string destination = Path.Combine(root.Path, "existing");
    Directory.CreateDirectory(destination);
    string marker = Path.Combine(destination, "keep.txt");
    await File.WriteAllTextAsync(marker, "owned by the operator");
    var handler = new ArchiveHandler((_, _) =>
        throw new InvalidOperationException("Network must not be touched."));
    using var http = new HttpClient(handler);
    var service = new FirmwareArchiveService(http);

    await Assert.ThrowsAsync<IOException>(() => service.DownloadAsync(
        new[] { new Uri("https://manifest.example/firmware2.xml") },
        destination, null, CancellationToken.None));

    Assert.Equal("owned by the operator", await File.ReadAllTextAsync(marker));
    Assert.Empty(handler.Requests);
  }

  [Theory]
  [InlineData("<!DOCTYPE options [<!ENTITY xxe SYSTEM 'file:///etc/passwd'>]><options>&xxe;</options>")]
  [InlineData("<options><Firmware><url>ftp://unsupported.example/fw.apj</url></Firmware></options>")]
  [InlineData("<options><Firmware><url>https://user:password@example/fw.apj</url></Firmware></options>")]
  [InlineData("<options><Firmware><url>../relative.apj</url></Firmware></options>")]
  public async Task Unsafe_or_invalid_manifests_are_rejected_without_creating_output(string xml) {
    using var root = new TempDirectory();
    string destination = Path.Combine(root.Path, "invalid");
    var manifest = new Uri("https://manifest.example/firmware2.xml");
    using var http = new HttpClient(new ArchiveHandler((_, _) => Task.FromResult(Xml(xml))));
    var service = new FirmwareArchiveService(http);

    await Assert.ThrowsAnyAsync<Exception>(() => service.DownloadAsync(
        new[] { manifest }, destination, null, CancellationToken.None));

    Assert.False(Directory.Exists(destination));
    Assert.Empty(Directory.GetDirectories(root.Path, "*.partial-*"));
  }

  [Fact]
  public void Archive_paths_are_unique_bounded_and_cannot_escape_the_files_tree() {
    string first = FirmwareArchiveService.ArchiveRelativePath(
        new Uri("https://cdn.example/a/%2E%2E/%2Fescape.apj?version=one"));
    string second = FirmwareArchiveService.ArchiveRelativePath(
        new Uri("https://cdn.example/b/%2Fescape.apj?version=two"));

    Assert.NotEqual(first, second);
    Assert.StartsWith(Path.Combine("files", "cdn.example") + Path.DirectorySeparatorChar, first);
    Assert.DoesNotContain("..", first, StringComparison.Ordinal);
    Assert.DoesNotContain(':', first);
    Assert.All(first.Split(Path.DirectorySeparatorChar), segment => Assert.InRange(segment.Length, 1, 109));
  }

  [Fact]
  public void Developer_tools_expose_download_cancel_and_collision_free_archive_names() {
    using var root = new TempDirectory();
    DateTime stamp = new(2026, 8, 23, 10, 11, 12, DateTimeKind.Utc);
    string first = ConfigDeveloperToolsViewModel.NextFirmwareArchiveDirectory(root.Path, stamp);
    Directory.CreateDirectory(first);
    string second = ConfigDeveloperToolsViewModel.NextFirmwareArchiveDirectory(root.Path, stamp);
    using var viewModel = new ConfigDeveloperToolsViewModel();

    Assert.EndsWith("MissionPlanner-Firmware-Archive-20260823-101112", first,
        StringComparison.Ordinal);
    Assert.EndsWith("MissionPlanner-Firmware-Archive-20260823-101112-2", second,
        StringComparison.Ordinal);
    Assert.Contains(viewModel.Actions, action => action.Label == "Download Firmware Archive");
    Assert.Contains(viewModel.Actions, action => action.Label == "Cancel Firmware Archive");
  }

  private static HttpResponseMessage Xml(string value) => new(HttpStatusCode.OK) {
    Content = new StringContent(value, Encoding.UTF8, "application/xml"),
  };

  private static HttpResponseMessage Bytes(byte[] value) => new(HttpStatusCode.OK) {
    Content = new ByteArrayContent(value),
  };

  private sealed class ArchiveHandler(
      Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response)
      : HttpMessageHandler {
    private int _activeFirmwareRequests;
    private int _maximumActiveFirmwareRequests;

    internal ConcurrentQueue<string> Requests { get; } = new();
    internal int MaximumActiveFirmwareRequests => Volatile.Read(ref _maximumActiveFirmwareRequests);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken) {
      string uri = request.RequestUri!.AbsoluteUri;
      Requests.Enqueue(uri);
      bool firmware = !uri.EndsWith("firmware2.xml", StringComparison.Ordinal);
      if (firmware) {
        int active = Interlocked.Increment(ref _activeFirmwareRequests);
        int current;
        while (active > (current = Volatile.Read(ref _maximumActiveFirmwareRequests))) {
          if (Interlocked.CompareExchange(
                  ref _maximumActiveFirmwareRequests, active, current) == current) {
            break;
          }
        }
      }
      try {
        return await response(request, cancellationToken);
      } finally {
        if (firmware) {
          Interlocked.Decrement(ref _activeFirmwareRequests);
        }
      }
    }
  }

  private sealed class InlineProgress<T>(Action<T> report) : IProgress<T> {
    public void Report(T value) => report(value);
  }

  private sealed class TempDirectory : IDisposable {
    internal TempDirectory() {
      Path = System.IO.Path.Combine(
          System.IO.Path.GetTempPath(), "mp-firmware-archive-" + Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(Path);
    }

    internal string Path { get; }

    public void Dispose() {
      if (Directory.Exists(Path)) {
        Directory.Delete(Path, recursive: true);
      }
    }
  }
}

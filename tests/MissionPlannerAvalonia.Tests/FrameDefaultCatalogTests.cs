using System.Net;
using System.Net.Http;
using System.Text;
using Avalonia.Headless.XUnit;
using MissionPlannerAvalonia.Services;
using MissionPlannerAvalonia.ViewModels;
using MissionPlannerAvalonia.ViewModels.GCSViews.ConfigurationView;
using MissionPlannerAvalonia.Views.GCSViews.ConfigurationView;

namespace MissionPlannerAvalonia.Tests;

public sealed class FrameDefaultCatalogTests {
  [Fact]
  public async Task Catalog_recurses_below_the_official_root_and_downloads_validated_param_files() {
    var handler = new CatalogHandler(request => request.RequestUri!.AbsoluteUri switch {
      "https://api.github.com/repos/ArduPilot/ardupilot/contents/Tools/Frame_params" => Json("""
          [
            {"name":"Copter.param","path":"Tools/Frame_params/Copter.param","type":"file"},
            {"name":"README.md","path":"Tools/Frame_params/README.md","type":"file"},
            {"name":"QuadPlanes","path":"Tools/Frame_params/QuadPlanes","type":"dir"}
          ]
          """),
      "https://api.github.com/repos/ArduPilot/ardupilot/contents/Tools/Frame_params/QuadPlanes" => Json("""
          [
            {"name":"Tailsitter.param","path":"Tools/Frame_params/QuadPlanes/Tailsitter.param","type":"file"}
          ]
          """),
      "https://raw.githubusercontent.com/ArduPilot/ardupilot/master/Tools/Frame_params/QuadPlanes/Tailsitter.param" =>
          new HttpResponseMessage(HttpStatusCode.OK) {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes("Q_FRAME_CLASS,10\n")),
          },
      _ => new HttpResponseMessage(HttpStatusCode.NotFound),
    });
    using var http = new HttpClient(handler);
    var service = new FrameDefaultCatalogService(http);

    IReadOnlyList<FrameDefaultCatalogItem> files = await service.ListAsync(CancellationToken.None);
    IReadOnlyList<FrameDefaultCatalogItem> cached = await service.ListAsync(CancellationToken.None);
    byte[] downloaded = await service.DownloadAsync(
        "Tools/Frame_params/QuadPlanes/Tailsitter.param", CancellationToken.None);

    Assert.Equal(2, files.Count);
    Assert.Same(files, cached);
    Assert.Equal("Tools/Frame_params/Copter.param", files[0].Path);
    Assert.Equal("Tools/Frame_params/QuadPlanes/Tailsitter.param", files[1].Path);
    Assert.Equal("Q_FRAME_CLASS,10\n", Encoding.UTF8.GetString(downloaded));
    Assert.Equal(3, handler.UserAgents.Count);
    Assert.All(handler.UserAgents, value => Assert.Contains("MissionPlanner-Avalonia", value));
  }

  [Theory]
  [InlineData("../secret.param")]
  [InlineData("Tools/Frame_params/../secret.param")]
  [InlineData("Tools/Other/secret.param")]
  [InlineData("/Tools/Frame_params/secret.param")]
  [InlineData("Tools\\Frame_params\\secret.param")]
  [InlineData("Tools/Frame_params/secret.txt")]
  public void Catalog_rejects_paths_outside_the_official_param_tree(string path) {
    Assert.Throws<InvalidDataException>(() => FrameDefaultCatalogService.NormalizeParamPath(path));
  }

  [Fact]
  public void Cache_path_preserves_subdirectories_and_stays_below_the_cache_root() {
    string root = Path.Combine(Path.GetTempPath(), "mp-frame-default-tests");

    string path = FrameDefaultCatalogService.GetCachePath(
        root, "Tools/Frame_params/QuadPlanes/Tailsitter.param");

    Assert.Equal(Path.GetFullPath(Path.Combine(root, "frame-defaults", "Tools", "Frame_params",
        "QuadPlanes", "Tailsitter.param")), path);
  }

  [Fact]
  public async Task Catalog_request_honours_cancellation() {
    var handler = new CatalogHandler(async (_, cancellationToken) => {
      await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
      return new HttpResponseMessage(HttpStatusCode.OK);
    });
    using var http = new HttpClient(handler);
    var service = new FrameDefaultCatalogService(http);
    using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

    await Assert.ThrowsAnyAsync<OperationCanceledException>(
        () => service.ListAsync(cancellation.Token));
  }

  [AvaloniaFact]
  public void Dedicated_default_settings_view_constructs_with_the_safe_parameter_workflow() {
    using var viewModel = new ConfigDefaultSettingsViewModel();

    var view = new ConfigDefaultSettingsView { DataContext = viewModel };

    Assert.Same(viewModel, view.DataContext);

    using var setup = new SetupViewModel();
    BackstagePage page = Assert.Single(setup.Pages, candidate =>
        candidate.Header == "Default Settings");
    Assert.True(page.RequiresConnection);
  }

  private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) {
    Content = new StringContent(value, Encoding.UTF8, "application/json"),
  };

  private sealed class CatalogHandler : HttpMessageHandler {
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _response;

    internal CatalogHandler(Func<HttpRequestMessage, HttpResponseMessage> response)
        : this((request, _) => Task.FromResult(response(request))) { }

    internal CatalogHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) =>
        _response = response;

    internal List<string> UserAgents { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken) {
      UserAgents.Add(request.Headers.UserAgent.ToString());
      return _response(request, cancellationToken);
    }
  }
}

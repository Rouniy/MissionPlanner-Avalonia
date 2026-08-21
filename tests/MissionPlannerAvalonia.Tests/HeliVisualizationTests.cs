using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using MissionPlannerAvalonia.Controls;
using MissionPlannerAvalonia.Services;
using MissionPlannerAvalonia.ViewModels.GCSViews.ConfigurationView;
using MissionPlannerAvalonia.Views.GCSViews.ConfigurationView;

namespace MissionPlannerAvalonia.Tests;

public class HeliVisualizationTests {
  [Fact]
  public void Stabilize_curve_uses_the_four_official_control_points() {
    var curve = HeliVisualization.BuildStabilizeCurve(110, 420, 610, 930);

    Assert.Equal([
      new HeliCurvePoint(0, 110),
      new HeliCurvePoint(40, 420),
      new HeliCurvePoint(60, 610),
      new HeliCurvePoint(100, 930),
    ], curve);
  }

  [Theory]
  [InlineData(0, 25, 250)]
  [InlineData(0, 50, 500)]
  [InlineData(0, 75, 750)]
  [InlineData(1, 25, 437.5)]
  [InlineData(1, 50, 500)]
  [InlineData(1, 75, 562.5)]
  public void Acro_curve_matches_upstream_expo_formula(
      double expo, int input, double expected) {
    var curve = HeliVisualization.BuildAcroCurve(expo);

    Assert.Equal(101, curve.Count);
    Assert.Equal(expected, curve[input].Output, 8);
  }

  [Theory]
  [InlineData(1000, 1000, 2000, 0)]
  [InlineData(1500, 1000, 2000, 50)]
  [InlineData(2000, 1000, 2000, 100)]
  [InlineData(2400, 1000, 2000, 100)]
  [InlineData(1500, 1000, 1000, 0)]
  public void Collective_cursor_maps_pwm_to_graph_percent(
      double pwm, double minimum, double maximum, double expected) {
    Assert.Equal(expected,
        HeliVisualization.MapCollectiveCursor(pwm, minimum, maximum), 8);
  }

  [Fact]
  public void Servo_range_is_only_captured_in_manual_mode_and_with_valid_pwm() {
    var empty = new HeliInputRange(2200, 800);

    Assert.Equal(empty, HeliVisualization.CaptureRange(empty, 1200, false));
    Assert.Equal(empty, HeliVisualization.CaptureRange(empty, 700, true));
    var first = HeliVisualization.CaptureRange(empty, 1500, true);
    var expanded = HeliVisualization.CaptureRange(first, 1900, true);
    expanded = HeliVisualization.CaptureRange(expanded, 1100, true);

    Assert.True(expanded.HasSamples);
    Assert.Equal(1100, expanded.Minimum);
    Assert.Equal(1900, expanded.Maximum);
  }

  [AvaloniaFact]
  public void Heli_collective_plot_and_page_render_headlessly() {
    var plot = new HeliCollectivePlot {
      StabilizeCurve = HeliVisualization.BuildStabilizeCurve(0, 400, 600, 1000),
      AcroCurve = HeliVisualization.BuildAcroCurve(0.35),
      CursorPercent = 42,
    };
    plot.Measure(new Size(640, 320));
    plot.Arrange(new Rect(0, 0, 640, 320));
    using var target = new RenderTargetBitmap(new PixelSize(640, 320));
    target.Render(plot);

    using var viewModel = new ConfigTradHeliViewModel();
    var view = new ConfigTradHeliView { DataContext = viewModel };
    view.Measure(new Size(900, 760));
    view.Arrange(new Rect(0, 0, 900, 760));
    using var pageTarget = new RenderTargetBitmap(new PixelSize(900, 760));
    pageTarget.Render(view);
    Assert.NotNull(view.Content);
  }
}

using MissionPlannerAvalonia.Services;
using MissionPlannerAvalonia.ViewModels;

namespace MissionPlannerAvalonia.Tests;

public class DataFlashExpressionEvaluatorTests {
  [Fact]
  public void Evaluates_upstream_math_and_angle_functions() {
    using var log = TestLog.Create(
        "FMT, 130, 24, ATT, Qff, TimeUS,Roll,Pitch",
        "ATT, 1000000, 3, 4",
        "ATT, 2000000, -10, 2");

    var magnitude = DataFlashExpressionEvaluator.Evaluate(
        log.Path, "sqrt(ATT.Roll**2+ATT.Pitch**2)");
    var angle = DataFlashExpressionEvaluator.Evaluate(
        log.Path, "degrees(atan2(ATT.Roll,ATT.Pitch))");
    var wrapped = DataFlashExpressionEvaluator.Evaluate(log.Path, "wrap_360(ATT.Roll)");

    Assert.Equal(5, magnitude[0].Value, 6);
    Assert.Equal(Math.Atan2(3, 4) * 180 / Math.PI, angle[0].Value, 6);
    Assert.Equal(350, wrapped[1].Value, 6);
  }

  [Fact]
  public void Evaluates_lowpass_and_delta_with_per_expression_state() {
    using var log = TestLog.Create(
        "FMT, 130, 20, ATT, Qf, TimeUS,Roll",
        "ATT, 1000000, 10",
        "ATT, 2000000, 14");

    var lowpass = DataFlashExpressionEvaluator.Evaluate(
        log.Path, "lowpass(ATT.Roll,'roll',0.5)");
    var delta = DataFlashExpressionEvaluator.Evaluate(
        log.Path, "delta(ATT.Roll,'roll',ATT.TimeUS)");

    Assert.Equal(new[] { 10d, 12d }, lowpass.Select(point => point.Value).ToArray());
    Assert.Equal(10, delta[0].Value, 6);
    Assert.Equal(4, delta[1].Value, 6);
  }

  [Fact]
  public void Mixed_message_expression_uses_latest_chronological_values() {
    using var log = TestLog.Create(
        "FMT, 130, 20, ATT, Qf, TimeUS,Roll",
        "FMT, 131, 20, GPS, Qf, TimeUS,VZ",
        "ATT, 1000000, 10",
        "GPS, 1500000, 2",
        "ATT, 2000000, 12",
        "GPS, 2500000, 3");

    var points = DataFlashExpressionEvaluator.Evaluate(log.Path, "ATT.Roll-GPS.VZ");

    Assert.Equal(new[] { 1.5, 2.0, 2.5 }, points.Select(point => point.TimeSeconds).ToArray());
    Assert.Equal(new[] { 8d, 10d, 9d }, points.Select(point => point.Value).ToArray());
  }

  [Fact]
  public void Gps_velocity_special_expression_routes_to_upstream_dflogscript() {
    using var log = TestLog.Create(
        "FMT, 131, 28, GPS, Qfff, TimeUS,Spd,GCrs,VZ",
        "GPS, 1000000, 10, 0, -1",
        "GPS, 2000000, 12, 0, -1");

    var points = DataFlashExpressionEvaluator.Evaluate(
        log.Path, "delta(gps_velocity_df(GPS).x,'x',GPS.TimeUS)");

    Assert.Equal(2, points.Count);
    Assert.All(points, point => Assert.True(double.IsFinite(point.Value)));
  }

  [Fact]
  public void Special_expression_compatibility_requires_special_and_regular_message_types() {
    var formats = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase) {
      ["GPS"] = ["TimeUS"],
    };

    Assert.False(DataFlashExpressionEvaluator.CanEvaluate(
        "earth_accel_df(IMU,ATT).x+GPS.TimeUS", formats));

    formats["IMU"] = ["AccX", "AccY", "AccZ"];
    formats["ATT"] = ["Roll", "Pitch", "Yaw"];
    Assert.True(DataFlashExpressionEvaluator.CanEvaluate(
        "earth_accel_df(IMU,ATT).x+GPS.TimeUS", formats));
  }

  [Fact]
  public async Task Preset_selects_first_alternative_that_matches_loaded_formats() {
    using var log = TestLog.Create(
        "FMT, 131, 20, GPS, Qf, TimeUS,Spd",
        "GPS, 1000000, 12");
    var preset = Assert.Single(GraphPresets.Parse("""
      <graphs><graph name="Speed">
        <expression>VFR_HUD.groundspeed</expression>
        <expression>GPS.Spd GPS.Missing</expression>
      </graph></graphs>
      """));
    var vm = new LogBrowseViewModel();
    await vm.LoadFileAsync(log.Path);

    var curves = vm.ResolvePresetCurves(preset);

    Assert.Equal("GPS.Spd", Assert.Single(curves).Expression);
  }

  [Fact]
  public async Task Overlay_keeps_textual_mode_name_instead_of_requiring_a_number() {
    using var log = TestLog.Create(
        "FMT, 132, 20, MODE, QM, TimeUS,Mode",
        "MODE, 1000000, AUTO");
    var vm = new LogBrowseViewModel();
    await vm.LoadFileAsync(log.Path);

    var marker = Assert.Single(vm.ReadOverlay("MODE", "Mode"));

    Assert.Equal(1, marker.x, 6);
    Assert.Equal("MODE AUTO", marker.label);
  }

  [Theory]
  [InlineData("ArduTracker", 10, "AUTO")]
  [InlineData("", 10, null)]
  [InlineData("NotAFirmware", 10, null)]
  public void Flight_mode_resolver_matches_upstream_firmware_names(
      string firmware, int mode, string? expected) {
    Assert.Equal(expected, FlightModeNames.Resolve(firmware, mode));
  }

  private sealed class TestLog : IDisposable {
    private readonly string _root;
    internal string Path { get; }

    private TestLog(string root, string path) {
      _root = root;
      Path = path;
    }

    internal static TestLog Create(params string[] lines) {
      string root = System.IO.Path.Combine(
          System.IO.Path.GetTempPath(), "mp-expression-" + Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(root);
      string path = System.IO.Path.Combine(root, "test.log");
      File.WriteAllText(path, string.Join('\n', new[] {
          "FMT, 128, 89, FMT, BBnNZ, Type,Length,Name,Format,Columns",
        }.Concat(lines)) + "\n");
      return new TestLog(root, path);
    }

    public void Dispose() {
      try {
        Directory.Delete(_root, recursive: true);
      } catch {
      }
    }
  }
}

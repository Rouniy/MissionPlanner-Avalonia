using MissionPlannerAvalonia.Views;

namespace MissionPlannerAvalonia.Tests;

public class MavlinkGraphTests {
  [Fact]
  public void Extracts_scalar_numeric_field() {
    bool read = MavlinkGraphSampleExtractor.TryRead(
        new TestMessage { Scalar = 42 }, nameof(TestMessage.Scalar), out var values);

    Assert.True(read);
    Assert.Equal([42d], values);
  }

  [Fact]
  public void Extracts_numeric_array_as_individual_series_values() {
    bool read = MavlinkGraphSampleExtractor.TryRead(
        new TestMessage { Vector = [1.25f, -2.5f, 3] },
        nameof(TestMessage.Vector), out var values);

    Assert.True(read);
    Assert.Equal([1.25d, -2.5d, 3d], values);
  }

  [Fact]
  public void Rejects_text_missing_and_non_finite_fields() {
    var message = new TestMessage { Text = "AUTO", NonFinite = double.NaN };

    Assert.False(MavlinkGraphSampleExtractor.TryRead(
        message, nameof(TestMessage.Text), out _));
    Assert.False(MavlinkGraphSampleExtractor.TryRead(
        message, nameof(TestMessage.NonFinite), out _));
    Assert.False(MavlinkGraphSampleExtractor.TryRead(message, "Missing", out _));
  }

  [Fact]
  public void Identifies_only_supported_graph_field_types() {
    Assert.True(MavlinkGraphSampleExtractor.IsSupportedType(typeof(float)));
    Assert.True(MavlinkGraphSampleExtractor.IsSupportedType(typeof(ushort[])));
    Assert.True(MavlinkGraphSampleExtractor.IsSupportedType(typeof(TestMode)));
    Assert.False(MavlinkGraphSampleExtractor.IsSupportedType(typeof(string)));
    Assert.False(MavlinkGraphSampleExtractor.IsSupportedType(typeof(DateTime)));
  }

  private sealed class TestMessage {
    public int Scalar;
    public float[] Vector = [];
    public string Text = "";
    public double NonFinite;
  }

  private enum TestMode {
    Manual,
    Auto,
  }
}

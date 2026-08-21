using MissionPlannerAvalonia.Controls;

namespace MissionPlannerAvalonia.Tests;

public class CameraFeedbackProjectionTests {
  [Fact]
  public void Projection_rejects_invalid_altitude_or_field_of_view() {
    var point = Point(0);

    Assert.Empty(CameraFeedbackProjection.Project(point, 63, 43));

    point.alt_msl = 120;
    Assert.Empty(CameraFeedbackProjection.Project(point, 0, 43));
    Assert.Empty(CameraFeedbackProjection.Project(point, 63, 180));
  }

  [Fact]
  public void Nadir_projection_returns_four_finite_ground_corners() {
    var corners = CameraFeedbackProjection.Project(Point(1000), 63, 43);

    Assert.Equal(4, corners.Count);
    Assert.All(corners, corner => {
      Assert.True(double.IsFinite(corner.Lat));
      Assert.True(double.IsFinite(corner.Lng));
      Assert.NotEqual((47.397742, 8.545594), corner);
    });
  }

  [Fact]
  public void Overlap_uses_unique_photos_and_excludes_roll_at_or_above_25_degrees() {
    var points = new[] {
      Feedback(1, 0),
      Feedback(1, 0),
      Feedback(2, 25),
      Feedback(3, -25),
      Feedback(4, 24.99f),
    };
    int projections = 0;

    var footprints = CameraOverlapProjection.BuildFootprints(points, 63, 43,
        projector: (_, _, _) => {
          projections++;
          return new[] {
            (47.0, 8.0),
            (47.0, 8.0001),
            (47.0001, 8.0001),
            (47.0001, 8.0),
          };
        });

    Assert.Equal(2, footprints.Count);
    Assert.Equal(2, projections);
  }

  [Theory]
  [InlineData(1, 0, 0, true, true)]
  [InlineData(1, 1, 4, false, true)]
  [InlineData(1, 1, 4, true, true)]
  [InlineData(1, 1, 0, true, false)]
  [InlineData(1, 0, 0, false, false)]
  public void Gimbal_target_gate_preserves_upstream_boolean_precedence(
      float tilt, float roll, float type, bool hasPan, bool expected) {
    Assert.Equal(expected,
        GimbalTargetProjection.ShouldProject(tilt, roll, type, hasPan));
  }

  [Fact]
  public void Gimbal_target_gate_requires_the_three_outer_parameters() {
    Assert.False(GimbalTargetProjection.ShouldProject(null, 0, 4, true));
    Assert.False(GimbalTargetProjection.ShouldProject(1, null, 4, true));
    Assert.False(GimbalTargetProjection.ShouldProject(1, 0, null, true));
  }

  private static MAVLink.mavlink_camera_feedback_t Point(float altitude) => new() {
    lat = (int)(47.397742 * 1e7),
    lng = (int)(8.545594 * 1e7),
    alt_msl = altitude,
    roll = 0,
    pitch = 0,
    yaw = 90,
    img_idx = 42,
  };

  private static MAVLink.mavlink_camera_feedback_t Feedback(ulong time, float roll) => new() {
    time_usec = time,
    lat = (int)(47.397742 * 1e7),
    lng = (int)(8.545594 * 1e7),
    alt_msl = 100,
    roll = roll,
    pitch = 0,
    yaw = 90,
  };
}

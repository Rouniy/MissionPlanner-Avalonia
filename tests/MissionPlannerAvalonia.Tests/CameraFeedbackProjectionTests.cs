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

  private static MAVLink.mavlink_camera_feedback_t Point(float altitude) => new() {
    lat = (int)(47.397742 * 1e7),
    lng = (int)(8.545594 * 1e7),
    alt_msl = altitude,
    roll = 0,
    pitch = 0,
    yaw = 90,
    img_idx = 42,
  };
}

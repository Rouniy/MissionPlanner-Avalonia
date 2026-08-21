using Avalonia.Headless.XUnit;
using Mapsui;
using Mapsui.Styles;
using MissionPlanner.Grid;
using MissionPlanner.Utilities;
using MissionPlannerAvalonia.Controls;
using MissionPlannerAvalonia.ViewModels;

namespace MissionPlannerAvalonia.Tests;

public class SurveyGridPreviewTests {
  [Fact]
  public void Builder_preserves_upstream_shared_numbering_route_and_footprint_color() {
    var calls = new List<(PointLatLngAlt Camera, double Yaw, double Hfov, double Vfov)>();
    SurveyGridPreviewState state = State(
        GridPoints(), showInternals: true, showFootprints: true);

    SurveyGridPreviewGeometry geometry = SurveyGridPreviewBuilder.Build(state,
        (camera, yaw, hfov, vfov) => {
          calls.Add((camera, yaw, hfov, vfov));
          return Square(camera.Lat, camera.Lng, 0.00005);
        });

    Assert.Equal(new[] { "1", "2", "4" }, geometry.Markers.Select(marker => marker.Label));
    Assert.Equal(new[] { false, true, false },
        geometry.Markers.Select(marker => marker.IsPhoto));
    Assert.Equal(3, geometry.Route.Count);
    SurveyPreviewFootprint footprint = Assert.Single(geometry.Footprints);
    Assert.Equal(3, footprint.Index);
    Assert.Equal(new Color(235, 241, 223), footprint.Color);
    var call = Assert.Single(calls);
    Assert.Equal(120, call.Camera.Alt, 8);
    Assert.Equal(42, call.Yaw, 8);
    Assert.Equal(60, call.Hfov, 8);
    Assert.Equal(45, call.Vfov, 8);
  }

  [Fact]
  public void Hidden_internals_are_omitted_from_route_but_footprints_still_consume_numbers() {
    SurveyGridPreviewGeometry geometry = SurveyGridPreviewBuilder.Build(
        State(GridPoints(), showInternals: false, showFootprints: true),
        (camera, _, _, _) => Square(camera.Lat, camera.Lng, 0.00005));

    Assert.Equal(new[] { "1", "3" }, geometry.Markers.Select(marker => marker.Label));
    Assert.Equal(2, geometry.Route.Count);
    Assert.Equal("S", geometry.Route[0].Tag);
    Assert.Equal("E", geometry.Route[1].Tag);
    Assert.Equal(2, Assert.Single(geometry.Footprints).Index);
  }

  [Fact]
  public void Footprint_bearing_uses_heading_and_sideways_offset_then_corridor_segment() {
    double yaw = double.NaN;
    SurveyGridPreviewState headingState = State(
        GridPoints(), showInternals: false, showFootprints: true,
        cameraFacingForward: false, holdHeading: true, heading: 15);
    SurveyGridPreviewBuilder.Build(headingState, (camera, value, _, _) => {
      yaw = value;
      return Square(camera.Lat, camera.Lng, 0.00005);
    });
    Assert.Equal(105, yaw, 8);

    IReadOnlyList<PointLatLngAlt> points = GridPoints();
    double expected = points[0].GetBearing(points[1]) + 90;
    SurveyGridPreviewState corridorState = headingState with { Corridor = true };
    SurveyGridPreviewBuilder.Build(corridorState, (camera, value, _, _) => {
      yaw = value;
      return Square(camera.Lat, camera.Lng, 0.00005);
    });
    Assert.Equal(expected, yaw, 8);
  }

  [Fact]
  public void Coverage_counts_overlaps_uses_upstream_palette_and_one_degree_guard() {
    IReadOnlyList<PointLatLngAlt> square = Square(1, 2, 0.0001);
    var footprints = new[] {
      new SurveyPreviewFootprint(square, Color.Red, 1),
      new SurveyPreviewFootprint(square, Color.Blue, 2),
    };

    IReadOnlyList<SurveyCoveragePoint> coverage =
        SurveyGridPreviewBuilder.BuildCoverage(footprints);

    Assert.NotEmpty(coverage);
    Assert.All(coverage, point => Assert.Equal(2, point.Count));
    Assert.Equal(new Color(0, 0, 255, 140), SurveyGridPreviewBuilder.CoverageColor(2));
    Assert.Equal(new Color(139, 0, 0, 140), SurveyGridPreviewBuilder.CoverageColor(99));

    var oversized = new[] {
      new SurveyPreviewFootprint(new[] {
        new PointLatLngAlt(0, 0),
        new PointLatLngAlt(0, 1.1),
        new PointLatLngAlt(1.1, 1.1),
        new PointLatLngAlt(1.1, 0),
      }, Color.Red, 1),
    };
    Assert.Empty(SurveyGridPreviewBuilder.BuildCoverage(oversized));
  }

  [Fact]
  public void View_model_publishes_fit_policy_and_updates_dragged_boundary() {
    var polygon = Polygon();
    var vm = new GridUIViewModel(polygon, new PointLatLngAlt(-35.363, 149.165, 580), _ => 500) {
      SelectedCamera = "",
    };
    var changes = new List<(SurveyGridPreviewState State, bool Fit)>();
    vm.PreviewChanged += (state, fit) => changes.Add((state, fit));

    vm.Distance += 1;
    Assert.True(changes[^1].Fit);

    vm.Angle += 1;
    Assert.False(changes[^1].Fit);

    vm.ShowGrid = false;
    Assert.False(changes[^1].Fit);
    Assert.False(changes[^1].State.ShowGrid);

    vm.MoveBoundaryPoint(1, -35.3635, 149.1665);
    Assert.False(changes[^1].Fit);
    Assert.Equal(-35.3635, changes[^1].State.Boundary[1].Lat, 8);
    Assert.Equal(149.1665, changes[^1].State.Boundary[1].Lng, 8);
  }

  [Fact]
  public void View_model_defaults_match_upstream_and_point_start_selects_boundary_vertex() {
    var polygon = Polygon();
    var vm = new GridUIViewModel(polygon, new PointLatLngAlt(-35.363, 149.165, 580), _ => 500) {
      SelectedCamera = "",
    };

    Assert.True(vm.ShowBoundary);
    Assert.True(vm.ShowGrid);
    Assert.True(vm.ShowMarkers);
    Assert.False(vm.OptimizeForDistance);

    vm.StartPointNumber = 2;
    vm.StartFrom = Grid.StartPosition.Point.ToString();

    Assert.Equal(polygon[1].Lat, Grid.StartPointLatLngAlt.Lat, 8);
    Assert.Equal(polygon[1].Lng, Grid.StartPointLatLngAlt.Lng, 8);
  }

  [AvaloniaFact]
  public void Preview_map_keeps_dedicated_layers_in_render_order() {
    var map = new SurveyGridPreviewMap();

    Assert.Equal(new[] {
      "Survey boundary",
      "Survey footprints",
      "Survey route",
      "Survey markers",
      "Survey overlap",
    }, map.Map.Layers.Skip(1).Select(layer => layer.Name));
  }

  private static SurveyGridPreviewState State(
      IReadOnlyList<PointLatLngAlt> grid,
      bool showInternals,
      bool showFootprints,
      bool cameraFacingForward = true,
      bool holdHeading = false,
      double heading = 0) => new(
      Polygon(), grid, new PointLatLngAlt(-35.363, 149.165, 20),
      Corridor: false,
      ShowBoundary: true,
      ShowGrid: true,
      ShowMarkers: true,
      ShowInternals: showInternals,
      ShowFootprints: showFootprints,
      Angle: 42,
      CameraFacingForward: cameraFacingForward,
      HoldHeading: holdHeading,
      Heading: heading,
      HorizontalFovDegrees: 60,
      VerticalFovDegrees: 45);

  private static IReadOnlyList<PointLatLngAlt> GridPoints() => new[] {
    new PointLatLngAlt(-35.3640, 149.1640, 100, "S"),
    new PointLatLngAlt(-35.3635, 149.1645, 100, "M"),
    new PointLatLngAlt(-35.3630, 149.1650, 100, "E"),
  };

  private static List<PointLatLngAlt> Polygon() => new() {
    new PointLatLngAlt(-35.364, 149.164),
    new PointLatLngAlt(-35.364, 149.166),
    new PointLatLngAlt(-35.362, 149.166),
    new PointLatLngAlt(-35.362, 149.164),
  };

  private static IReadOnlyList<PointLatLngAlt> Square(double lat, double lng, double radius) =>
      new[] {
        new PointLatLngAlt(lat - radius, lng - radius),
        new PointLatLngAlt(lat - radius, lng + radius),
        new PointLatLngAlt(lat + radius, lng + radius),
        new PointLatLngAlt(lat + radius, lng - radius),
      };
}

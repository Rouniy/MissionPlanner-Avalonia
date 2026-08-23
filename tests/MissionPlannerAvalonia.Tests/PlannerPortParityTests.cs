using System.IO.Compression;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using MissionPlanner.ArduPilot;
using MissionPlanner.Utilities;
using MissionPlannerAvalonia.Controls;
using MissionPlannerAvalonia.Services;
using MissionPlannerAvalonia.ViewModels;
using MissionPlannerAvalonia.Views;
using AvaloniaGrid = Avalonia.Controls.Grid;
using AvaloniaRect = Avalonia.Rect;

namespace MissionPlannerAvalonia.Tests;

public class PlannerPortParityTests {
  [AvaloniaFact]
  public void Legacy_plugin_mission_calls_preserve_official_coordinate_and_parameter_order() {
    using var vm = new FlightPlannerViewModel {
      VerifyHeight = false,
      DefaultFrame = "Terrain",
    };
    object tag = new();

    int added = vm.AddPluginCommand(MAVLink.MAV_CMD.WAYPOINT,
        1, 2, 3, 4, 33.25, 44.5, 120, tag);
    vm.InsertPluginCommand(0, MAVLink.MAV_CMD.DO_SET_SERVO,
        9, 1500, 0, 0, 0, 0, 0);

    Assert.Equal(0, added);
    Assert.Equal(2, vm.Waypoints.Count);
    Assert.Equal(new[] { 0, 1 }, vm.Waypoints.Select(row => row.Seq));
    WpRow inserted = vm.Waypoints[0];
    Assert.Equal((ushort)MAVLink.MAV_CMD.DO_SET_SERVO, inserted.Command);
    Assert.Equal((9d, 1500d), (inserted.P1, inserted.P2));
    WpRow waypoint = vm.Waypoints[1];
    Assert.Equal((44.5d, 33.25d, 120d), (waypoint.Lat, waypoint.Lng, waypoint.Alt));
    Assert.Equal((1d, 2d, 3d, 4d), (waypoint.P1, waypoint.P2, waypoint.P3, waypoint.P4));
    Assert.Equal((byte)MAVLink.MAV_FRAME.GLOBAL_TERRAIN_ALT, waypoint.Frame);
    Assert.Same(tag, waypoint.Tag);
    vm.MissionType = "Rally";
    vm.MissionType = "Mission";
    Assert.Same(tag, vm.Waypoints[1].Tag);
  }

  [Theory]
  [InlineData(MAVLink.MAV_MISSION_TYPE.MISSION, "@MISSION/mission.dat")]
  [InlineData(MAVLink.MAV_MISSION_TYPE.FENCE, "@MISSION/fence.dat")]
  [InlineData(MAVLink.MAV_MISSION_TYPE.RALLY, "@MISSION/rally.dat")]
  public void Mission_ftp_paths_match_ArduPilot_filesystem(
      MAVLink.MAV_MISSION_TYPE type, string expected) {
    Assert.Equal(expected, FlightPlannerViewModel.MissionFtpPath(type));
  }

  [Fact]
  public void Mission_ftp_payload_round_trips_home_rows_and_targets() {
    var rows = new[] {
      new WpRow {
        Command = (ushort)MAVLink.MAV_CMD.WAYPOINT,
        Frame = (byte)MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT,
        Lat = -35.2,
        Lng = 149.1,
        Alt = 75,
      },
    };

    byte[] payload = FlightPlannerViewModel.BuildMissionFtpPayload(
        MAVLink.MAV_MISSION_TYPE.MISSION, rows, -35.3, 149.2, 600, 42, 7);
    var unpacked = missionpck.unpack(payload);

    Assert.Equal(MAVLink.MAV_MISSION_TYPE.MISSION, unpacked.type);
    Assert.Equal((ushort)0, unpacked.start);
    Assert.Equal(2, unpacked.wps.Count);
    Assert.Equal((ushort)0, unpacked.wps[0].seq);
    Assert.Equal((ushort)1, unpacked.wps[1].seq);
    Assert.Equal((byte)42, unpacked.wps[1].target_system);
    Assert.Equal((byte)7, unpacked.wps[1].target_component);
    Assert.Equal((int)(-35.2 * 1e7), unpacked.wps[1].x);
  }

  [Fact]
  public void Mission_ftp_fence_payload_forces_global_frame() {
    var rows = new[] {
      new WpRow {
        Command = (ushort)MAVLink.MAV_CMD.FENCE_RETURN_POINT,
        Frame = (byte)MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT,
        Lat = 1,
        Lng = 2,
      },
    };

    var unpacked = missionpck.unpack(FlightPlannerViewModel.BuildMissionFtpPayload(
        MAVLink.MAV_MISSION_TYPE.FENCE, rows, 0, 0, 0, 1, 1));

    Assert.Single(unpacked.wps);
    Assert.Equal((byte)MAVLink.MAV_FRAME.GLOBAL, unpacked.wps[0].frame);
    Assert.Equal((byte)MAVLink.MAV_MISSION_TYPE.FENCE, unpacked.wps[0].mission_type);
  }

  [AvaloniaFact]
  public void Rally_shortcut_adds_a_relative_point_without_losing_the_active_mission() {
    using var vm = new FlightPlannerViewModel { VerifyHeight = false };
    vm.AddWaypointAt(40, 28);

    vm.AddRallyPointAt(41, 29, 120);

    Assert.Equal("Rally", vm.MissionType);
    var rally = Assert.Single(vm.Waypoints);
    Assert.Equal((ushort)MAVLink.MAV_CMD.RALLY_POINT, rally.Command);
    Assert.Equal((byte)MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT, rally.Frame);
    Assert.Equal((41d, 29d, 120d), (rally.Lat, rally.Lng, rally.Alt));
    vm.MissionType = "Mission";
    Assert.Single(vm.Waypoints);
    Assert.Equal((ushort)MAVLink.MAV_CMD.WAYPOINT, vm.Waypoints[0].Command);
  }

  [AvaloniaFact]
  public async Task Clearing_rally_offline_is_undoable_and_preserves_the_mission_store() {
    using var vm = new FlightPlannerViewModel { VerifyHeight = false };
    vm.AddWaypointAt(40, 28);
    vm.AddRallyPointAt(41, 29, 120);

    await vm.ClearRallyPointsAsync();

    Assert.Empty(vm.Waypoints);
    Assert.Contains("local rally", vm.Status, StringComparison.OrdinalIgnoreCase);
    vm.UndoCommand.Execute(null);
    Assert.Single(vm.Waypoints);
    vm.MissionType = "Mission";
    Assert.Single(vm.Waypoints);
    Assert.Equal(40, vm.Waypoints[0].Lat);
  }

  [AvaloniaFact]
  public async Task Failed_vehicle_rally_clear_keeps_the_local_points() {
    using var vm = new FlightPlannerViewModel { VerifyHeight = false };
    vm.AddRallyPointAt(41, 29, 120);

    await vm.ClearRallyPointsAsync(true, () => Task.FromResult(false));

    Assert.Single(vm.Waypoints);
    Assert.Contains("not cleared", vm.Status, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("local list was kept", vm.Status, StringComparison.OrdinalIgnoreCase);
  }

  [AvaloniaFact]
  public async Task Successful_vehicle_rally_clear_is_undoable() {
    using var vm = new FlightPlannerViewModel { VerifyHeight = false };
    vm.AddRallyPointAt(41, 29, 120);

    await vm.ClearRallyPointsAsync(true, () => Task.FromResult(true));

    Assert.Empty(vm.Waypoints);
    Assert.Equal("Cleared rally points on the vehicle.", vm.Status);
    vm.UndoCommand.Execute(null);
    Assert.Single(vm.Waypoints);
  }

  [AvaloniaFact]
  public async Task Concurrent_vehicle_rally_clear_does_not_start_a_second_transfer() {
    using var vm = new FlightPlannerViewModel { VerifyHeight = false };
    vm.AddRallyPointAt(41, 29, 120);
    var releaseFirst = new TaskCompletionSource<bool>(
        TaskCreationOptions.RunContinuationsAsynchronously);
    int transfers = 0;
    Task first = vm.ClearRallyPointsAsync(true, () => {
      transfers++;
      return releaseFirst.Task;
    });

    await vm.ClearRallyPointsAsync(true, () => {
      transfers++;
      return Task.FromResult(true);
    });

    Assert.Equal(1, transfers);
    Assert.Contains("already in progress", vm.Status, StringComparison.OrdinalIgnoreCase);
    Assert.Single(vm.Waypoints);
    releaseFirst.SetResult(true);
    await first;
    Assert.Empty(vm.Waypoints);
  }

  [Theory]
  [InlineData(null, false)]
  [InlineData("not-an-altitude", false)]
  [InlineData("NaN", false)]
  [InlineData("123.5", true)]
  public void Rally_altitude_prompt_rejects_cancel_and_invalid_values(string? text, bool expected) {
    Assert.Equal(expected, FlightPlannerView.TryParseRallyAltitude(text, out _));
  }

  [AvaloniaFact]
  [Obsolete]
  public void Planner_map_exposes_the_six_official_rally_shortcuts() {
    var view = new FlightPlannerView();
    var map = Assert.IsType<FlightPlannerMap>(view.FindControl<FlightPlannerMap>("Map"));
    var menu = Assert.IsType<ContextMenu>(map.ContextMenu);
    var rally = Assert.Single(menu.Items.OfType<MenuItem>(), item => Equals(item.Header, "Rally Points"));

    Assert.Equal(new[] {
      "Set Rally Point", "Download", "Upload", "Clear Rally Points",
      "Save Rally to File", "Load Rally from File",
    }, rally.Items.OfType<MenuItem>().Select(item => item.Header?.ToString()));
  }

  [AvaloniaFact]
  [Obsolete]
  public void Planner_switch_docking_recreates_both_official_panel_arrangements() {
    var view = new FlightPlannerView();
    var layout = Assert.IsType<AvaloniaGrid>(view.FindControl<AvaloniaGrid>("PlannerLayoutGrid"));
    var map = Assert.IsType<FlightPlannerMap>(view.FindControl<FlightPlannerMap>("Map"));
    var waypoints = Assert.IsType<AvaloniaGrid>(view.FindControl<AvaloniaGrid>("WaypointPanel"));
    var actions = Assert.IsType<Border>(view.FindControl<Border>("ActionPanel"));
    var horizontal = Assert.IsType<GridSplitter>(
        view.FindControl<GridSplitter>("HorizontalDockSplitter"));
    var vertical = Assert.IsType<GridSplitter>(
        view.FindControl<GridSplitter>("VerticalDockSplitter"));
    var actionItems = Assert.IsType<StackPanel>(view.FindControl<StackPanel>("ActionItemsPanel"));
    var actionScroller = Assert.IsType<ScrollViewer>(view.FindControl<ScrollViewer>("ActionScroller"));

    view.ApplyDockingLayout(false, persist: false);
    view.Measure(new Size(1100, 640));
    view.Arrange(new AvaloniaRect(0, 0, 1100, 640));
    Assert.False(view.IsActionDockedBottom);
    Assert.Equal(GridUnitType.Pixel, layout.ColumnDefinitions[2].Width.GridUnitType);
    Assert.Equal(168, layout.ColumnDefinitions[2].Width.Value);
    Assert.Equal((2, 0, 1, 1),
        (AvaloniaGrid.GetRow(waypoints), AvaloniaGrid.GetColumn(waypoints),
            AvaloniaGrid.GetRowSpan(waypoints), AvaloniaGrid.GetColumnSpan(waypoints)));
    Assert.Equal((0, 2, 3, 1),
        (AvaloniaGrid.GetRow(actions), AvaloniaGrid.GetColumn(actions),
            AvaloniaGrid.GetRowSpan(actions), AvaloniaGrid.GetColumnSpan(actions)));
    Assert.Equal(3, AvaloniaGrid.GetRowSpan(vertical));
    Assert.Equal(1, AvaloniaGrid.GetColumnSpan(horizontal));
    Assert.Equal(Orientation.Vertical, actionItems.Orientation);
    Assert.Equal(ScrollBarVisibility.Auto, actionScroller.VerticalScrollBarVisibility);

    view.ApplyDockingLayout(true, persist: false);
    view.Measure(new Size(1100, 640));
    view.Arrange(new AvaloniaRect(0, 0, 1100, 640));
    Assert.True(view.IsActionDockedBottom);
    Assert.Equal(GridUnitType.Star, layout.ColumnDefinitions[2].Width.GridUnitType);
    Assert.Equal(120, layout.RowDefinitions[2].Height.Value);
    Assert.Equal((0, 2, 1, 1),
        (AvaloniaGrid.GetRow(waypoints), AvaloniaGrid.GetColumn(waypoints),
            AvaloniaGrid.GetRowSpan(waypoints), AvaloniaGrid.GetColumnSpan(waypoints)));
    Assert.Equal((2, 0, 1, 3),
        (AvaloniaGrid.GetRow(actions), AvaloniaGrid.GetColumn(actions),
            AvaloniaGrid.GetRowSpan(actions), AvaloniaGrid.GetColumnSpan(actions)));
    Assert.Equal(1, AvaloniaGrid.GetRowSpan(vertical));
    Assert.Equal(3, AvaloniaGrid.GetColumnSpan(horizontal));
    Assert.Equal(Orientation.Horizontal, actionItems.Orientation);
    Assert.Equal(ScrollBarVisibility.Auto, actionScroller.HorizontalScrollBarVisibility);

    var menu = Assert.IsType<ContextMenu>(map.ContextMenu);
    Assert.Single(menu.Items.OfType<MenuItem>(), item => Equals(item.Header, "Switch Docking"));
  }

  [AvaloniaFact]
  [Obsolete]
  public void Planner_switch_docking_persists_the_upstream_setting_values() {
    string? saved = Settings.Instance["FP_docking"];
    try {
      var view = new FlightPlannerView();
      view.ApplyDockingLayout(false, persist: false);

      view.SwitchDocking();
      Assert.Equal("Bottom", Settings.Instance["FP_docking"]);
      Assert.True(new FlightPlannerView().IsActionDockedBottom);

      view.SwitchDocking();
      Assert.Equal("Right", Settings.Instance["FP_docking"]);
      Assert.False(new FlightPlannerView().IsActionDockedBottom);
    } finally {
      Settings.Instance["FP_docking"] = saved;
    }
  }

  [AvaloniaFact]
  [Obsolete]
  public void Flight_data_main_splitter_is_draggable_and_persists_official_distance() {
    string? saved = Settings.Instance["FlightSplitter"];
    try {
      Settings.Instance["FlightSplitter"] = "360";
      var view = new FlightDataView();
      var layout = Assert.IsType<AvaloniaGrid>(
          view.FindControl<AvaloniaGrid>("FlightDataLayoutGrid"));
      var splitter = Assert.IsType<GridSplitter>(
          view.FindControl<GridSplitter>("MainFlightSplitter"));

      Assert.True(splitter.IsEnabled);
      Assert.Equal(GridResizeDirection.Columns, splitter.ResizeDirection);
      Assert.Equal(GridResizeBehavior.PreviousAndNext, splitter.ResizeBehavior);
      Assert.Equal(GridUnitType.Pixel, layout.ColumnDefinitions[0].Width.GridUnitType);
      Assert.Equal(360, layout.ColumnDefinitions[0].Width.Value);

      view.Measure(new Size(1200, 700));
      view.Arrange(new AvaloniaRect(0, 0, 1200, 700));
      view.ApplyMainSplitterDistance(300);
      view.Measure(new Size(1200, 700));
      view.Arrange(new AvaloniaRect(0, 0, 1200, 700));

      Assert.Equal(300, layout.ColumnDefinitions[0].Width.Value);
      Assert.Equal("300", Settings.Instance["FlightSplitter"]);
      Assert.Equal(GridUnitType.Star, layout.ColumnDefinitions[2].Width.GridUnitType);
    } finally {
      Settings.Instance["FlightSplitter"] = saved;
    }
  }

  [AvaloniaFact]
  [Obsolete]
  public void Flight_data_hud_and_quick_panels_detach_and_return_without_recreation() {
    var view = new FlightDataView();
    var vm = new FlightDataViewModel();
    try {
      view.DataContext = vm;
      var hudHost = Assert.IsType<ContentControl>(view.FindControl<ContentControl>("HudHost"));
      var quickHost = Assert.IsType<ContentControl>(view.FindControl<ContentControl>("QuickHost"));
      var hud = Assert.IsType<HudControl>(view.FindControl<HudControl>("Hud"));
      var quick = Assert.IsType<ItemsControl>(view.FindControl<ItemsControl>("QuickGrid"));
      var hudMenu = Assert.IsType<MenuItem>(view.FindControl<MenuItem>("DetachHudMenuItem"));
      var quickMenu = Assert.IsType<MenuItem>(view.FindControl<MenuItem>("DetachQuickMenuItem"));

      var hudWindow = Assert.IsType<Window>(view.DetachHud(showWindow: false));
      var quickWindow = Assert.IsType<Window>(view.DetachQuick(showWindow: false));

      Assert.True(view.IsHudDetached);
      Assert.True(view.IsQuickDetached);
      Assert.Same(hud, hudWindow.Content);
      Assert.Same(quick, quickWindow.Content);
      Assert.IsType<Button>(hudHost.Content);
      Assert.IsType<Button>(quickHost.Content);
      Assert.Equal("Dock HUD", hudMenu.Header);
      Assert.Equal("Dock Quick", quickMenu.Header);

      view.RestoreDetachedPanels();

      Assert.False(view.IsHudDetached);
      Assert.False(view.IsQuickDetached);
      Assert.Same(hud, hudHost.Content);
      Assert.Same(quick, quickHost.Content);
      Assert.Null(hudWindow.Content);
      Assert.Null(quickWindow.Content);
      Assert.Equal("Undock HUD", hudMenu.Header);
      Assert.Equal("Undock Quick", quickMenu.Header);
    } finally {
      view.RestoreDetachedPanels();
      vm.Dispose();
    }
  }

  [AvaloniaFact]
  [Obsolete]
  public void Closing_a_detached_flight_data_window_redocks_its_live_panel() {
    var view = new FlightDataView();
    var owner = new Window { Content = view };
    owner.Show();
    try {
      var hudHost = Assert.IsType<ContentControl>(view.FindControl<ContentControl>("HudHost"));
      var hud = Assert.IsType<HudControl>(view.FindControl<HudControl>("Hud"));
      var detached = Assert.IsType<Window>(view.DetachHud());

      Assert.True(detached.IsVisible);
      Assert.True(view.IsHudDetached);
      detached.Close();

      Assert.False(view.IsHudDetached);
      Assert.Same(hud, hudHost.Content);
    } finally {
      view.RestoreDetachedPanels();
      owner.Close();
    }
  }

  [AvaloniaFact]
  [Obsolete]
  public void Flight_data_gimbal_video_layouts_move_one_live_panel_without_recreation() {
    var view = new FlightDataView();
    var panel = new Border();
    var popup = new Window();
    var fullHost = Assert.IsType<ContentControl>(
        view.FindControl<ContentControl>("GimbalVideoFullHost"));
    var miniLayout = Assert.IsType<AvaloniaGrid>(
        view.FindControl<AvaloniaGrid>("GimbalVideoMiniLayout"));
    var miniHost = Assert.IsType<ContentControl>(
        view.FindControl<ContentControl>("GimbalVideoMiniHost"));
    var miniMapHost = Assert.IsType<ContentControl>(
        view.FindControl<ContentControl>("GimbalMiniMapHost"));
    var map = Assert.IsType<MapView>(view.FindControl<MapView>("FdMap"));
    var mapLayout = Assert.IsType<AvaloniaGrid>(
        view.FindControl<AvaloniaGrid>("MapVideoLayout"));
    try {
      view.PlaceGimbalVideoPanel(
          panel, GimbalVideoPresentation.FullSized, popup, showWindow: false);

      Assert.Same(panel, fullHost.Content);
      Assert.True(fullHost.IsVisible);
      Assert.True(miniLayout.IsVisible);
      Assert.Same(map, miniMapHost.Content);
      Assert.True(miniMapHost.IsVisible);
      Assert.Equal(GimbalVideoPresentation.FullSized, view.CurrentGimbalVideoPresentation);

      view.PlaceGimbalVideoPanel(
          panel, GimbalVideoPresentation.Mini, popup, showWindow: false);

      Assert.Null(fullHost.Content);
      Assert.False(fullHost.IsVisible);
      Assert.Same(panel, miniHost.Content);
      Assert.True(miniLayout.IsVisible);
      Assert.Null(miniMapHost.Content);
      Assert.Contains(map, mapLayout.Children);
      Assert.Equal(GimbalVideoPresentation.Mini, view.CurrentGimbalVideoPresentation);

      view.PlaceGimbalVideoPanel(
          panel, GimbalVideoPresentation.PopOut, popup, showWindow: false);

      Assert.Null(fullHost.Content);
      Assert.Null(miniHost.Content);
      Assert.False(miniLayout.IsVisible);
      Assert.Null(miniMapHost.Content);
      Assert.Contains(map, mapLayout.Children);
      Assert.Same(panel, popup.Content);
      Assert.Equal(GimbalVideoPresentation.PopOut, view.CurrentGimbalVideoPresentation);
    } finally {
      view.CloseGimbalVideo();
    }

    Assert.Null(fullHost.Content);
    Assert.Null(miniHost.Content);
    Assert.Null(miniMapHost.Content);
    Assert.Contains(map, mapLayout.Children);
    Assert.Null(view.CurrentGimbalVideoPresentation);
  }

  [AvaloniaFact]
  [Obsolete]
  public void Closing_popout_clears_the_gimbal_video_presentation() {
    var view = new FlightDataView();
    var owner = new Window { Content = view };
    var panel = new Border();
    var popup = new Window();
    owner.Show();
    try {
      view.PlaceGimbalVideoPanel(
          panel, GimbalVideoPresentation.PopOut, popup, showWindow: true);

      Assert.True(popup.IsVisible);
      Assert.Same(panel, popup.Content);
      popup.Close();

      Assert.Null(view.CurrentGimbalVideoPresentation);
      Assert.Null(view.FindControl<ContentControl>("GimbalVideoFullHost")?.Content);
      Assert.Null(view.FindControl<ContentControl>("GimbalVideoMiniHost")?.Content);
    } finally {
      view.CloseGimbalVideo();
      owner.Close();
    }
  }

  [Theory]
  [InlineData("50S", -50)]
  [InlineData("11N", 11)]
  [InlineData("-55", -55)]
  [InlineData(" 33 n ", 33)]
  public void Parses_upstream_style_utm_zones(string text, int expected) {
    Assert.True(Geo.TryParseUtmZone(text, out int zone));
    Assert.Equal(expected, zone);
  }

  [Theory]
  [InlineData("")]
  [InlineData("0N")]
  [InlineData("61S")]
  [InlineData("north")]
  public void Rejects_invalid_utm_zones(string text) {
    Assert.False(Geo.TryParseUtmZone(text, out _));
  }

  [Fact]
  public void Offsets_a_drawn_polygon_in_projected_metres() {
    var polygon = new[] {
      new PointLatLngAlt(-35.364, 149.164),
      new PointLatLngAlt(-35.364, 149.166),
      new PointLatLngAlt(-35.362, 149.166),
      new PointLatLngAlt(-35.362, 149.164),
    };

    var expanded = PlannerGeometry.OffsetPolygon(polygon, 50);

    Assert.True(expanded.Count >= 3);
    var center = new PointLatLngAlt(-35.363, 149.165);
    Assert.True(expanded.Max(point => point.GetDistance(center)) >
        polygon.Max(point => point.GetDistance(center)) + 35);
  }

  [Fact]
  public void Reads_kml_route_and_poi_without_winforms() {
    const string xml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <kml xmlns="http://www.opengis.net/kml/2.2"><Document>
          <Placemark><name>Camera</name><Point><coordinates>149.1,-35.1,42</coordinates></Point></Placemark>
          <Placemark><LineString><coordinates>149.2,-35.2,50 149.3,-35.3,60</coordinates></LineString></Placemark>
        </Document></kml>
        """;
    using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));

    KmlMissionContent content = KmlMissionReader.Parse(stream);

    Assert.Equal(2, content.Route.Count);
    Assert.Equal(50, content.Route[0].Alt);
    Assert.Equal("Camera", Assert.Single(content.Pois).Name);
    Assert.Equal(3, content.Overlay.Count);
  }

  [Fact]
  public void Reads_a_kml_document_from_a_kmz_archive() {
    string path = Path.Combine(Path.GetTempPath(), $"mp_kmz_{Guid.NewGuid():N}.kmz");
    try {
      using (var archive = ZipFile.Open(path, ZipArchiveMode.Create)) {
        using var writer = new StreamWriter(archive.CreateEntry("doc.kml").Open());
        writer.Write("""
            <kml xmlns="http://www.opengis.net/kml/2.2"><Placemark><LineString>
            <coordinates>28.1,40.1,25 28.2,40.2,30</coordinates>
            </LineString></Placemark></kml>
            """);
      }

      KmlMissionContent content = KmlMissionReader.Read(path);

      Assert.Equal(2, content.Route.Count);
      Assert.Equal(30, content.Route[1].Alt);
    } finally {
      File.Delete(path);
    }
  }

  [Fact]
  public void Default_altitude_frame_round_trips_and_absolute_rows_are_detected() {
    byte absolute = FlightPlannerViewModel.FrameId("Absolute");

    Assert.Equal((byte)MAVLink.MAV_FRAME.GLOBAL, absolute);
    Assert.Equal("Absolute", FlightPlannerViewModel.FrameName(absolute));
    Assert.True(FlightPlannerViewModel.ContainsAbsoluteAltitude(
        new[] { new WpRow { Frame = absolute } }));
    Assert.False(FlightPlannerViewModel.ContainsAbsoluteAltitude(
        new[] { new WpRow { FrameName = "Terrain" } }));
  }

  [Theory]
  [InlineData(3)]
  [InlineData(12)]
  [InlineData(20)]
  public void Flight_data_map_zoom_persistence_round_trips(int level) {
    double resolution = 156543.03392804097 / Math.Pow(2, level);

    Assert.Equal(level, MapView.ZoomLevelForResolution(resolution), 8);
  }

  [Fact]
  public void Bing_selector_uses_a_quadkey_tile_source_instead_of_esri() {
    string url = MapTileSourceFactory.UrlTemplateFor("BingSatelliteMap");

    Assert.Contains("virtualearth.net", url, StringComparison.Ordinal);
    Assert.Contains("{quadkey}", url, StringComparison.Ordinal);
    Assert.DoesNotContain("arcgisonline", url, StringComparison.Ordinal);
  }

  [Fact]
  public void Modify_alt_matches_upstream_delta_and_multiplier_semantics() {
    var rows = new[] { new WpRow { Alt = 10 }, new WpRow { Alt = 20 } };

    Assert.True(FlightPlannerViewModel.TryModifyAltitudes(rows, "20", 2));
    Assert.Equal(new[] { 20d, 30d }, rows.Select(row => row.Alt));
    Assert.True(FlightPlannerViewModel.TryModifyAltitudes(rows, "*2", 2));
    Assert.Equal(new[] { 40d, 60d }, rows.Select(row => row.Alt));
    Assert.False(FlightPlannerViewModel.TryModifyAltitudes(rows, "bad", 2));
  }

  [Fact]
  public void Planner_altitudes_convert_only_at_the_display_boundary() {
    Assert.Equal(328.084, FlightPlannerViewModel.ToDisplayAltitude(100, 3.28084), 5);
    Assert.Equal(100, FlightPlannerViewModel.FromDisplayAltitude(328.084, 3.28084), 5);
  }

  [Fact]
  [Obsolete]
  public void Planner_route_excludes_roi_and_radius_display_units_convert_to_metres() {
    Assert.True(MissionRoute.IsNavigation((ushort)MAVLink.MAV_CMD.WAYPOINT));
    Assert.True(MissionRoute.IsNavigation((ushort)MAVLink.MAV_CMD.SPLINE_WAYPOINT));
    Assert.False(MissionRoute.IsNavigation((ushort)MAVLink.MAV_CMD.ROI));
    Assert.False(MissionRoute.IsNavigation((ushort)MAVLink.MAV_CMD.DO_SET_ROI));
    Assert.Equal(100, FlightPlannerMap.RadiusInMeters(328.084, 3.28084), 5);
  }

  [Fact]
  public void Auto_wp_text_uses_a_cross_platform_vector_path() {
    IReadOnlyList<(double X, double Y)> points = PlannerTextGeometry.Create("MP", 5, 25);

    Assert.True(points.Count > 10);
    Assert.True(points.Max(point => point.X) - points.Min(point => point.X) > 1);
    Assert.True(points.Max(point => point.Y) - points.Min(point => point.Y) > 1);
  }

  [Fact]
  public void Tile_prefetch_enumerates_visible_area_and_a_route_corridor() {
    var (x, y) = Mapsui.Projections.SphericalMercator.FromLonLat(149.16, -35.36);
    var area = new BruTile.Extent(x - 500, y - 500, x + 500, y + 500);

    IReadOnlyList<BruTile.TileInfo> areaTiles = MapTileSourceFactory.AreaTiles(area, 12, 13);
    IReadOnlyList<BruTile.TileInfo> pathTiles = MapTileSourceFactory.PathTiles(
        new[] { (-35.36, 149.16), (-35.30, 149.25) }, 12, 13);

    Assert.NotEmpty(areaTiles);
    Assert.NotEmpty(pathTiles);
    Assert.Equal(areaTiles.Count, areaTiles.Select(tile => tile.Index).Distinct().Count());
    Assert.Equal(pathTiles.Count, pathTiles.Select(tile => tile.Index).Distinct().Count());
  }

  [Fact]
  public void Survey_uses_the_drawn_polygon_before_mission_waypoints() {
    var vm = new FlightPlannerViewModel { DefaultAlt = 75 };
    vm.Waypoints.Add(new WpRow {
      Command = (ushort)MAVLink.MAV_CMD.WAYPOINT,
      Lat = 10,
      Lng = 20,
    });
    vm.Waypoints.Add(new WpRow {
      Command = (ushort)MAVLink.MAV_CMD.WAYPOINT,
      Lat = 11,
      Lng = 21,
    });
    vm.Waypoints.Add(new WpRow {
      Command = (ushort)MAVLink.MAV_CMD.WAYPOINT,
      Lat = 12,
      Lng = 22,
    });
    vm.AddPolygonPoint(-35.36, 149.16);
    vm.AddPolygonPoint(-35.36, 149.17);
    vm.AddPolygonPoint(-35.35, 149.17);

    var area = Assert.IsType<ValueTuple<List<PointLatLngAlt>, PointLatLngAlt>>(
        vm.BuildSurveyArea());

    Assert.Equal(3, area.Item1.Count);
    Assert.Equal(-35.36, area.Item1[0].Lat, 6);
    Assert.Equal(149.16, area.Item1[0].Lng, 6);
    Assert.All(area.Item1, point => Assert.Equal(75, point.Alt));
  }

  [Fact]
  public void Survey_keeps_mission_outline_as_a_compatibility_fallback() {
    var vm = new FlightPlannerViewModel { DefaultAlt = 60 };
    vm.Waypoints.Add(new WpRow {
      Command = (ushort)MAVLink.MAV_CMD.WAYPOINT,
      Lat = 1,
      Lng = 2,
    });
    vm.Waypoints.Add(new WpRow {
      Command = (ushort)MAVLink.MAV_CMD.WAYPOINT,
      Lat = 3,
      Lng = 4,
    });
    vm.Waypoints.Add(new WpRow {
      Command = (ushort)MAVLink.MAV_CMD.WAYPOINT,
      Lat = 5,
      Lng = 6,
    });

    var area = Assert.IsType<ValueTuple<List<PointLatLngAlt>, PointLatLngAlt>>(
        vm.BuildSurveyArea());

    Assert.Equal(new[] { 1d, 3d, 5d }, area.Item1.Select(point => point.Lat));
    Assert.All(area.Item1, point => Assert.Equal(60, point.Alt));
  }

  [Fact]
  public void Legacy_fence_transfer_keeps_return_point_and_closes_polygon() {
    var rows = new[] {
      new WpRow { Command = (ushort)MAVLink.MAV_CMD.FENCE_RETURN_POINT, Lat = 1, Lng = 2 },
      new WpRow { Command = (ushort)MAVLink.MAV_CMD.FENCE_POLYGON_VERTEX_INCLUSION, Lat = 3, Lng = 4 },
      new WpRow { Command = (ushort)MAVLink.MAV_CMD.FENCE_POLYGON_VERTEX_INCLUSION, Lat = 5, Lng = 6 },
      new WpRow { Command = (ushort)MAVLink.MAV_CMD.FENCE_POLYGON_VERTEX_INCLUSION, Lat = 7, Lng = 8 },
    };

    var transfer = FlightPlannerViewModel.BuildLegacyFenceTransfer(rows);

    Assert.Equal(5, transfer.Count);
    Assert.Equal((1d, 2d), (transfer[0].Lat, transfer[0].Lng));
    Assert.Equal((3d, 4d), (transfer[1].Lat, transfer[1].Lng));
    Assert.Equal((3d, 4d), (transfer[^1].Lat, transfer[^1].Lng));
  }

  [Fact]
  public void Legacy_fence_rejects_geometry_it_cannot_represent() {
    var rows = new[] {
      new WpRow { Command = (ushort)MAVLink.MAV_CMD.FENCE_RETURN_POINT },
      new WpRow { Command = (ushort)MAVLink.MAV_CMD.FENCE_POLYGON_VERTEX_INCLUSION },
      new WpRow { Command = (ushort)MAVLink.MAV_CMD.FENCE_POLYGON_VERTEX_INCLUSION },
      new WpRow { Command = (ushort)MAVLink.MAV_CMD.FENCE_POLYGON_VERTEX_INCLUSION },
      new WpRow { Command = (ushort)MAVLink.MAV_CMD.FENCE_CIRCLE_EXCLUSION },
    };

    Assert.Throws<InvalidOperationException>(() =>
        FlightPlannerViewModel.BuildLegacyFenceTransfer(rows));
  }
}

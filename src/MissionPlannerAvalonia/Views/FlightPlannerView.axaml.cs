using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MissionPlannerAvalonia.ViewModels;

namespace MissionPlannerAvalonia.Views;

public partial class FlightPlannerView : UserControl {
  private FlightPlannerViewModel? _wired;
  private bool _polygonDrawMode;
  private readonly Services.NoFlyOverlayCoordinator _noFlyOverlay;
  private bool _tilePrefetchRunning;
  private bool _actionDockBottom;

  private const int _pColIndex = 2;
  private const string _dockingSetting = "FP_docking";

  [Obsolete]
  public FlightPlannerView() {
    InitializeComponent();
    ApplyDockingLayout(
        MissionPlanner.Utilities.Settings.Instance[_dockingSetting] == "Bottom",
        persist: false);
    Map.WaypointDragMoved += OnWaypointDragged;
    Map.WaypointDragCommitted += OnWaypointDragged;
    Map.MapClicked += OnMapClicked;
    Map.MidpointInsertRequested += (afterSeq, lat, lng) =>
        Vm?.InsertWaypointAfterSeq(afterSeq, lat, lng);
    Map.ContextMenu = BuildMapMenu();
    KeyDown += OnPlannerKeyDown;
    DataContextChanged += (_, _) => WireViewModel();
    WireViewModel();
    _noFlyOverlay = new Services.NoFlyOverlayCoordinator(
        Map.SetNoFlyLayer,
        status => {
          if (Vm != null) {
            Vm.Status = status;
          }
        });
    AttachedToVisualTree += (_, _) => {
      _noFlyOverlay.Activate();
    };
    DetachedFromVisualTree += (_, _) => {
      _noFlyOverlay.Deactivate();
      Vm?.SavePlannerSettings();
    };
  }

  internal bool IsActionDockedBottom => _actionDockBottom;

  internal void SwitchDocking() => ApplyDockingLayout(!_actionDockBottom, persist: true);

  internal void ApplyDockingLayout(bool actionBottom, bool persist) {
    _actionDockBottom = actionBottom;

    var columns = PlannerLayoutGrid.ColumnDefinitions;
    columns[0].Width = new GridLength(1, GridUnitType.Star);
    columns[0].MinWidth = 180;
    columns[1].Width = new GridLength(4);
    columns[1].MinWidth = 4;
    columns[2].Width = actionBottom
        ? new GridLength(1, GridUnitType.Star)
        : new GridLength(168);
    columns[2].MinWidth = actionBottom ? 180 : 120;

    var rows = PlannerLayoutGrid.RowDefinitions;
    rows[0].Height = new GridLength(1, GridUnitType.Star);
    rows[0].MinHeight = 140;
    rows[1].Height = new GridLength(4);
    rows[1].MinHeight = 4;
    rows[2].Height = new GridLength(actionBottom ? 120 : 210);
    rows[2].MinHeight = actionBottom ? 80 : 120;

    static void Place(Control control, int row, int column,
        int rowSpan = 1, int columnSpan = 1) {
      Grid.SetRow(control, row);
      Grid.SetColumn(control, column);
      Grid.SetRowSpan(control, rowSpan);
      Grid.SetColumnSpan(control, columnSpan);
    }

    Place(Map, 0, 0);
    if (actionBottom) {
      Place(WaypointPanel, 0, 2);
      Place(VerticalDockSplitter, 0, 1);
      Place(HorizontalDockSplitter, 1, 0, columnSpan: 3);
      Place(ActionPanel, 2, 0, columnSpan: 3);
    } else {
      Place(WaypointPanel, 2, 0);
      Place(HorizontalDockSplitter, 1, 0);
      Place(VerticalDockSplitter, 0, 1, rowSpan: 3);
      Place(ActionPanel, 0, 2, rowSpan: 3);
    }

    ActionItemsPanel.Orientation = actionBottom ? Orientation.Horizontal : Orientation.Vertical;
    ActionScroller.HorizontalScrollBarVisibility = actionBottom
        ? Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        : Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled;
    ActionScroller.VerticalScrollBarVisibility = actionBottom
        ? Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
        : Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;

    PlannerLayoutGrid.InvalidateMeasure();
    Map.InvalidateMeasure();
    WpGrid.InvalidateMeasure();
    if (persist) {
      MissionPlanner.Utilities.Settings.Instance[_dockingSetting] =
          actionBottom ? "Bottom" : "Right";
    }
  }

  private void OnMapClicked(double lat, double lng) {
    if (Vm == null) {
      return;
    }
    if (_polygonDrawMode) {
      Vm.AddPolygonPoint(lat, lng);
    } else {
      Vm.AddWaypointAt(lat, lng);
    }
  }

  private static readonly string[] _defaultParamLabels = { "P1", "P2", "P3", "P4", "Lat", "Lon", "Alt" };
  [Obsolete]
  private static readonly Dictionary<MAVLink.MAV_CMD, string[]> _paramLabels = new() {
    [MAVLink.MAV_CMD.WAYPOINT] = new[] { "Delay", "—", "—", "Yaw" },
    [MAVLink.MAV_CMD.SPLINE_WAYPOINT] = new[] { "Delay", "—", "—", "—" },
    [MAVLink.MAV_CMD.LOITER_UNLIM] = new[] { "—", "—", "Radius", "Yaw" },
    [MAVLink.MAV_CMD.LOITER_TURNS] = new[] { "Turns", "—", "Radius", "—" },
    [MAVLink.MAV_CMD.LOITER_TIME] = new[] { "Time", "—", "Radius", "—" },
    [MAVLink.MAV_CMD.RETURN_TO_LAUNCH] = new[] { "—", "—", "—", "—" },
    [MAVLink.MAV_CMD.LAND] = new[] { "Abort", "—", "—", "Yaw" },
    [MAVLink.MAV_CMD.TAKEOFF] = new[] { "—", "—", "—", "Yaw" },
    [MAVLink.MAV_CMD.DO_JUMP] = new[] { "WP#", "Repeat", "—", "—" },
    [MAVLink.MAV_CMD.DO_CHANGE_SPEED] = new[] { "Type", "Speed", "Throttle", "—" },
    [MAVLink.MAV_CMD.DO_SET_ROI] = new[] { "—", "—", "—", "—" },
    [MAVLink.MAV_CMD.DO_DIGICAM_CONTROL] = new[] { "Shoot", "—", "—", "—" },
    [MAVLink.MAV_CMD.DO_SET_SERVO] = new[] { "Ch", "PWM", "—", "—" },
    [MAVLink.MAV_CMD.DO_SET_RELAY] = new[] { "Relay", "On/Off", "—", "—" },
    [MAVLink.MAV_CMD.CONDITION_DELAY] = new[] { "Time", "—", "—", "—" },
  };

  [Obsolete]
  private void OnWpSelectionChanged(object? sender, SelectionChangedEventArgs e) {
    if (sender is not DataGrid grid || grid.SelectedItem is not WpRow row) {
      return;
    }
    var cmd = (MAVLink.MAV_CMD)row.Command;
    Services.MavCmdInfo.EnsureLoaded(Services.MavCmdInfo.CurrentSubtree());
    var xml = Services.MavCmdInfo.Get(cmd.ToString());
    var labels = Services.MissionCommandCatalog.GetLabels(row.Command)
        ?? xml
        ?? (_paramLabels.TryGetValue(cmd, out var l)
            ? l.Concat(_defaultParamLabels.Skip(4)).ToArray()
            : _defaultParamLabels);
    for (int i = 0; i < 7; i++) {
      int col = _pColIndex + i;
      if (col < grid.Columns.Count) {
        string name = i < labels.Length && !string.IsNullOrEmpty(labels[i]) ? labels[i] : _defaultParamLabels[i];
        grid.Columns[col].Header = string.IsNullOrEmpty(name) ? "—" : name;
      }
    }
  }

  [Obsolete]
  private ContextMenu BuildMapMenu() {
    MenuItem Item(string header, Action<FlightPlannerViewModel, double, double> action) {
      var mi = new MenuItem { Header = header };
      mi.Click += (_, _) => {
        var (lat, lng) = Map.LastClickLatLng;
        if (Vm != null) {
          action(Vm, lat, lng);
        }
      };
      return mi;
    }
    var menu = new ContextMenu();
    menu.Items.Add(Item("Insert Point", (vm, lat, lng) => vm.InsertWaypointAt(lat, lng)));
    menu.Items.Add(Item("Delete Point", (vm, lat, lng) => vm.DeleteNearest(lat, lng)));
    menu.Items.Add(Item("Set Home Here", (vm, lat, lng) => vm.SetHome(lat, lng)));
    menu.Items.Add(Item("Measure Distance", (vm, lat, lng) => _ = vm.MeasureClick(lat, lng)));
    var zoomto = new MenuItem { Header = "Zoom To" };
    var zh = new MenuItem { Header = "Home" };
    zh.Click += (_, _) => Map.ZoomToHome();
    var zm = new MenuItem { Header = "Mission" };
    zm.Click += (_, _) => Map.ZoomToMission();
    var zv = new MenuItem { Header = "Vehicle" };
    zv.Click += (_, _) => Map.ZoomToVehicle();
    var zs = new MenuItem { Header = "Search Place…" };
    zs.Click += (_, _) => _ = SearchPlaceAsync();
    zoomto.Items.Add(zh);
    zoomto.Items.Add(zm);
    zoomto.Items.Add(zv);
    zoomto.Items.Add(zs);
    menu.Items.Add(zoomto);
    var rotate = new MenuItem { Header = "Rotate" };
    var rcw = new MenuItem { Header = "Clockwise 15°" };
    rcw.Click += (_, _) => Map.RotateBy(15);
    var rccw = new MenuItem { Header = "Counter-clockwise 15°" };
    rccw.Click += (_, _) => Map.RotateBy(-15);
    var rreset = new MenuItem { Header = "Reset (North Up)" };
    rreset.Click += (_, _) => Map.ResetRotation();
    var rset = new MenuItem { Header = "Set Heading…" };
    rset.Click += (_, _) => _ = SetMapHeadingAsync();
    rotate.Items.Add(rcw);
    rotate.Items.Add(rccw);
    rotate.Items.Add(rreset);
    rotate.Items.Add(rset);
    menu.Items.Add(rotate);
    menu.Items.Add(Item("Prefetch Visible Area…",
        (_, _, _) => _ = PrefetchMapTilesAsync(pathOnly: false)));
    menu.Items.Add(Item("Prefetch WP Path…",
        (_, _, _) => _ = PrefetchMapTilesAsync(pathOnly: true)));
    menu.Items.Add(Item("Enter UTM Coordinate…",
        (vm, lat, lng) => _ = vm.AddWaypointFromUtmAsync(lat, lng)));
    var trackerHome = Item("Set Tracker Home…",
        (vm, lat, lng) => _ = vm.SetTrackerHomeAsync(lat, lng));
    menu.Items.Add(trackerHome);

    var missionOnly = new List<Control>();
    void AddMissionOnly(Control c) {
      missionOnly.Add(c);
      menu.Items.Add(c);
    }

    var fenceOnly = new List<Control>();
    void AddFenceOnly(Control c) {
      fenceOnly.Add(c);
      menu.Items.Add(c);
    }
    AddFenceOnly(Item("Set Return Location", (vm, lat, lng) => vm.SetFenceReturn(lat, lng)));
    var fenceGeometry = new MenuItem { Header = "Fence Geometry" };
    fenceGeometry.Items.Add(Item("Inclusion Polygon from Drawn Polygon",
        (vm, _, _) => vm.AddDrawnPolygonToFence(true)));
    fenceGeometry.Items.Add(Item("Exclusion Polygon from Drawn Polygon",
        (vm, _, _) => vm.AddDrawnPolygonToFence(false)));
    fenceGeometry.Items.Add(Item("Inclusion Circle Here",
        (vm, lat, lng) => _ = vm.AddFenceCircle(lat, lng, true)));
    fenceGeometry.Items.Add(Item("Exclusion Circle Here",
        (vm, lat, lng) => _ = vm.AddFenceCircle(lat, lng, false)));
    AddFenceOnly(fenceGeometry);
    var rallyPoints = new MenuItem { Header = "Rally Points" };
    rallyPoints.Items.Add(Item("Set Rally Point",
        (vm, lat, lng) => _ = SetRallyPointAsync(vm, lat, lng)));
    rallyPoints.Items.Add(Item("Download", (vm, _, _) => {
      vm.MissionType = "Rally";
      vm.ReadWaypointsCommand.Execute(null);
    }));
    rallyPoints.Items.Add(Item("Upload", (vm, _, _) => {
      vm.MissionType = "Rally";
      vm.WriteWaypointsCommand.Execute(null);
    }));
    rallyPoints.Items.Add(Item("Clear Rally Points",
        (vm, _, _) => _ = vm.ClearRallyPointsAsync()));
    rallyPoints.Items.Add(Item("Save Rally to File",
        (_, _, _) => _ = SaveRallyFile()));
    rallyPoints.Items.Add(Item("Load Rally from File",
        (_, _, _) => _ = PickAndLoadRallyFile()));
    menu.Items.Add(rallyPoints);
    AddMissionOnly(new Separator());
    AddMissionOnly(Item("Insert at Current Position", (vm, _, _) => vm.InsertAtCurrentPosition()));
    AddMissionOnly(Item("Insert Spline WP", (vm, lat, lng) => vm.AddSplineWp(lat, lng)));
    AddMissionOnly(Item("Takeoff", (vm, lat, lng) => _ = vm.AddTakeoff(lat, lng)));
    AddMissionOnly(Item("Land", (vm, lat, lng) => vm.AddLand(lat, lng)));
    AddMissionOnly(Item("RTL", (vm, _, _) => vm.AddRtl()));
    AddMissionOnly(Item("DO_SET_ROI", (vm, lat, lng) => vm.AddRoi(lat, lng)));
    var loiter = new MenuItem { Header = "Loiter" };
    loiter.Items.Add(Item("Forever", (vm, lat, lng) => vm.AddLoiterForever(lat, lng)));
    loiter.Items.Add(Item("Time", (vm, lat, lng) => _ = vm.AddLoiterTime(lat, lng)));
    loiter.Items.Add(Item("Circles", (vm, lat, lng) => _ = vm.AddLoiterCircles(lat, lng)));
    AddMissionOnly(loiter);
    AddMissionOnly(Item("Jump", (vm, _, _) => _ = vm.AddJump()));
    AddMissionOnly(Item("Jump to Start", (vm, _, _) => _ = vm.AddJumpStart()));
    var autowp = new MenuItem { Header = "Auto WP" };
    autowp.Items.Add(Item("Survey (Grid)", (vm, _, _) => OpenSurveyGrid(vm)));
    autowp.Items.Add(Item("Area", (vm, _, _) => vm.PolygonArea()));
    autowp.Items.Add(Item("Circle", (vm, lat, lng) => _ = vm.CreateWpCircle(lat, lng)));
    var splineCircle = Item("Spline Circle", (vm, lat, lng) => _ = vm.CreateSplineCircle(lat, lng));
    var circleSurvey = Item("Circle Survey", (vm, lat, lng) => _ = vm.CreateCircleSurvey(lat, lng));
    var textAutoWp = Item("Text", (vm, lat, lng) => _ = vm.CreateTextWaypoints(lat, lng));
    autowp.Items.Add(splineCircle);
    autowp.Items.Add(circleSurvey);
    autowp.Items.Add(textAutoWp);
    AddMissionOnly(autowp);
    AddMissionOnly(Item("Elevation Graph", (_, _, _) => ShowElevationGraph()));
    menu.Items.Add(new Separator());
    menu.Items.Add(Item("Clear", (vm, _, _) => vm.ClearMissionCommand.Execute(null)));
    AddMissionOnly(Item("Reverse WPs", (vm, _, _) => vm.ReverseWaypointsCommand.Execute(null)));
    AddMissionOnly(Item("Modify Alt", (vm, _, _) => _ = vm.ModifyAllAlt()));
    menu.Items.Add(new Separator());
    var poi = new MenuItem { Header = "POI" };
    poi.Items.Add(Item("Add POI", (vm, lat, lng) => _ = vm.AddPoi(lat, lng)));
    poi.Items.Add(Item("Edit POI", (vm, lat, lng) => _ = vm.EditNearestPoi(lat, lng)));
    poi.Items.Add(Item("Delete POI", (vm, lat, lng) => vm.DeleteNearestPoi(lat, lng)));
    poi.Items.Add(Item("POI at Coords", (vm, _, _) => _ = vm.AddPoiAtCoords()));
    poi.Items.Add(Item("Clear POIs", (vm, _, _) => vm.ClearPois()));
    menu.Items.Add(poi);
    var switchDocking = new MenuItem { Header = "Switch Docking" };
    switchDocking.Click += (_, _) => SwitchDocking();
    menu.Items.Add(switchDocking);
    menu.Opening += (_, _) => {
      var profile = Services.DisplayViewService.Current;
      bool mission = Vm?.MissionType is null or "Mission";
      foreach (var c in missionOnly) {
        c.IsVisible = mission;
      }
      foreach (var c in fenceOnly) {
        c.IsVisible = profile.displayGeoFenceMenu && Vm?.MissionType == "Fence";
      }
      trackerHome.IsVisible = profile.displayTrackerHomeMenu;
      splineCircle.IsVisible = profile.displaySplineCircleAutoWp;
      circleSurvey.IsVisible = profile.displayCircleSurveyAutoWp;
      textAutoWp.IsVisible = profile.displayTextAutoWp;
      poi.IsVisible = profile.displayPoiMenu;
    };
    menu.Items.Add(new Separator());
    var nofly = new MenuItem { Header = "Load NoFly Overlay…" };
    nofly.Click += OnLoadNoFly;
    menu.Items.Add(nofly);
    var noflyClear = new MenuItem { Header = "Clear NoFly Overlay" };
    noflyClear.Click += (_, _) => Map.SetNoFlyLayer(null);
    menu.Items.Add(noflyClear);
    var mapOverlay = new MenuItem { Header = "Load Map Overlay (KML/SHP/DXF)…" };
    mapOverlay.Click += OnLoadMapOverlay;
    menu.Items.Add(mapOverlay);
    menu.Items.Add(new Separator());
    var poly = new MenuItem { Header = "Polygon" };
    var draw = new MenuItem { Header = "Draw" };
    draw.Click += (_, _) => {
      _polygonDrawMode = !_polygonDrawMode;
      draw.Header = _polygonDrawMode ? "Draw (on)" : "Draw";
      if (Vm != null) {
        Vm.Status = _polygonDrawMode
            ? "Polygon draw: click the map to add vertices."
            : "Polygon draw off.";
      }
    };
    poly.Items.Add(draw);
    poly.Items.Add(Item("Clear", (vm, _, _) => vm.ClearPolygon()));
    poly.Items.Add(Item("From Current Waypoints", (vm, _, _) => vm.BuildPolygonFromWaypoints()));
    poly.Items.Add(Item("Offset…", (vm, _, _) => _ = vm.OffsetDrawnPolygonAsync()));
    poly.Items.Add(Item("Area", (vm, _, _) => vm.PolygonArea()));
    var loadPolygon = new MenuItem { Header = "Load .poly…" };
    loadPolygon.Click += OnLoadPolygon;
    poly.Items.Add(loadPolygon);
    var loadShpPolygon = new MenuItem { Header = "From SHP…" };
    loadShpPolygon.Click += OnLoadShapefilePolygon;
    poly.Items.Add(loadShpPolygon);
    var savePolygon = new MenuItem { Header = "Save .poly…" };
    savePolygon.Click += OnSavePolygon;
    poly.Items.Add(savePolygon);
    menu.Items.Add(poly);
    return menu;
  }

  private void OnZoomSliderChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e) {
    Map?.SetZoomLevel(e.NewValue);
  }

  private void ShowElevationGraph() {
    if (Vm?.BuildElevationProfile() is not { } p) {
      if (Vm != null) {
        Vm.Status = "Need at least 2 waypoints for elevation graph.";
      }
      return;
    }
    var win = new ElevationGraphWindow(p.Dist, p.Terrain, p.Planned,
        MissionPlanner.CurrentState.AltUnit, MissionPlanner.CurrentState.DistanceUnit);
    if (TopLevel.GetTopLevel(this) is Window owner) {
      win.Show(owner);
    } else {
      win.Show();
    }
  }

  private static readonly FilePickerFileType _noFlyType = new("NoFly KML/KMZ") {
    Patterns = new[] { "*.kml", "*.kmz" },
  };

  private async void OnLoadNoFly(object? sender, RoutedEventArgs e) {
    var top = TopLevel.GetTopLevel(this);
    if (top == null) {
      return;
    }

    var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions {
      Title = "Load NoFly Overlay",
      AllowMultiple = false,
      FileTypeFilter = new[] { _noFlyType },
    });
    var path = files.FirstOrDefault()?.TryGetLocalPath();
    if (path == null) {
      return;
    }

    var layer = Services.NoFlyOverlay.BuildLayer(path);
    Map.SetNoFlyLayer(layer);
    if (Vm != null) {
      Vm.Status = layer == null ? "No NoFly polygons found." : "NoFly overlay loaded.";
    }
  }

  private FlightPlannerViewModel? Vm => DataContext as FlightPlannerViewModel;

  private static readonly FilePickerFileType _wpType = new("Mission Planner files") {
    Patterns = new[] {
      "*.waypoints", "*.txt", "*.mission", "*.plan", "*.fen", "*.ral", "*.poly", "*.kml", "*.kmz",
      "*.shp",
    },
  };

  private static readonly FilePickerFileType _jsonMissionType = new("Mission/QGC JSON") {
    Patterns = new[] { "*.mission", "*.plan" },
  };

  private static readonly FilePickerFileType _fenceType = new("Legacy fence") {
    Patterns = new[] { "*.fen" },
  };

  private static readonly FilePickerFileType _rallyType = new("Legacy rally") {
    Patterns = new[] { "*.ral" },
  };

  private static readonly FilePickerFileType _polygonType = new("Polygon") {
    Patterns = new[] { "*.poly" },
  };

  private static readonly FilePickerFileType _shapefileType = new("ESRI Shapefile") {
    Patterns = new[] { "*.shp" },
  };

  private static readonly FilePickerFileType _mapOverlayType = new("Map overlays") {
    Patterns = new[] { "*.kml", "*.kmz", "*.shp", "*.dxf", "*.gpkg" },
  };

  private void WireViewModel() {
    if (ReferenceEquals(_wired, Vm)) {
      return;
    }

    if (_wired != null) {
      _wired.WaypointsChanged -= OnWaypointsChanged;
      _wired.PropertyChanged -= OnVmPropertyChanged;
      _wired.PoiChanged -= OnPoiChanged;
      _wired.DrawnPolygonChanged -= OnDrawnPolygonChanged;
    }

    _wired = Vm;
    if (_wired == null) {
      return;
    }

    _wired.WaypointsChanged += OnWaypointsChanged;
    _wired.PropertyChanged += OnVmPropertyChanged;
    _wired.PoiChanged += OnPoiChanged;
    _wired.DrawnPolygonChanged += OnDrawnPolygonChanged;
    OnWaypointsChanged();
    OnPoiChanged();
    OnDrawnPolygonChanged();
    Map.SetGraticuleVisible(_wired.ShowGrid);
    Map.SetMapType(_wired.MapType);
    Map.SetHome(_wired.HomeLat, _wired.HomeLng, _wired.HomeAlt);
  }

  private void OnPoiChanged() =>
      Map.ShowPois(Services.PoiStore.All.Select(p => (p.Lat, p.Lng, p.Name)).ToList());

  private void OnDrawnPolygonChanged() =>
      Map.ShowDrawnPolygon(Vm == null
          ? new List<(double, double)>()
          : Vm.DrawnPolygon.Select(p => (p.Lat, p.Lng)).ToList());

  private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e) {
    if (Vm == null) {
      return;
    }

    if (e.PropertyName == nameof(FlightPlannerViewModel.ShowGrid)) {
      Map.SetGraticuleVisible(Vm.ShowGrid);
    } else if (e.PropertyName == nameof(FlightPlannerViewModel.MapType)) {
      string normalized = Services.MapTileSourceFactory.NormalizeMapType(Vm.MapType);
      if (!string.Equals(normalized, Vm.MapType, StringComparison.Ordinal)) {
        Vm.Status = $"Configure {Vm.MapType} before selecting it.";
        Vm.MapType = normalized;
        return;
      }
      Map.SetMapType(Vm.MapType);
    } else if (e.PropertyName == nameof(FlightPlannerViewModel.HomeLat)
               || e.PropertyName == nameof(FlightPlannerViewModel.HomeLng)
               || e.PropertyName == nameof(FlightPlannerViewModel.HomeAlt)) {
      Map.SetHome(Vm.HomeLat, Vm.HomeLng, Vm.HomeAlt);
    } else if (e.PropertyName == nameof(FlightPlannerViewModel.MissionType)) {
      Map.SetRenderMode(Vm.MissionType);
    } else if (e.PropertyName == nameof(FlightPlannerViewModel.WpRadius)
               || e.PropertyName == nameof(FlightPlannerViewModel.LoiterRadius)) {
      OnWaypointsChanged();
    }
  }

  private void OnWaypointsChanged() {
    if (Vm == null) {
      return;
    }

    Map.SetWaypoints(
        Vm.Waypoints.Select(w => (w.Seq, w.Lat, w.Lng, w.Command, w.P1, w.P2, w.P3, w.P4)).ToList(),
        Vm.WpRadius, Vm.LoiterRadius);
  }

  private void OnWaypointDragged(int seq, double lat, double lng) => Vm?.MoveWaypoint(seq, lat, lng);

  private async void OnLoadFile(object? sender, RoutedEventArgs e) {
    await PickAndLoadFile(false);
  }

  private async void OnLoadAndAppendFile(object? sender, RoutedEventArgs e) {
    await PickAndLoadFile(true);
  }

  private static async Task SetRallyPointAsync(FlightPlannerViewModel vm, double lat, double lng) {
    string? input = await Services.Dialogs.InputBox(
        "Altitude", $"Altitude ({MissionPlanner.CurrentState.AltUnit})",
        vm.DefaultAltDisplay.ToString("0.##", CultureInfo.CurrentCulture));
    if (input == null) {
      return;
    }
    if (!TryParseRallyAltitude(input, out double displayAltitude)) {
      await Services.Dialogs.Alert("Rally Point", "Invalid altitude.");
      return;
    }

    vm.AddRallyPointAt(lat, lng, FlightPlannerViewModel.FromDisplayAltitude(displayAltitude));
  }

  internal static bool TryParseRallyAltitude(string? input, out double displayAltitude) {
    const NumberStyles styles = NumberStyles.Float;
    bool parsed = double.TryParse(input, styles, CultureInfo.CurrentCulture, out displayAltitude)
                  || double.TryParse(input, styles, CultureInfo.InvariantCulture, out displayAltitude);
    return parsed && double.IsFinite(displayAltitude);
  }

  private async Task PickAndLoadRallyFile() {
    var top = TopLevel.GetTopLevel(this);
    if (top is null || Vm is null) {
      return;
    }

    var files = await top.StorageProvider.OpenFilePickerAsync(
        new FilePickerOpenOptions {
          Title = "Load Rally from File",
          AllowMultiple = false,
          FileTypeFilter = new[] { _rallyType },
        });
    var file = files.FirstOrDefault();
    if (file?.TryGetLocalPath() is { } path) {
      await Vm.LoadFileAsync(path);
    }
  }

  private async Task SaveRallyFile() {
    var top = TopLevel.GetTopLevel(this);
    if (top is null || Vm is null) {
      return;
    }

    var file = await top.StorageProvider.SaveFilePickerAsync(
        new FilePickerSaveOptions {
          Title = "Save Rally to File",
          DefaultExtension = "ral",
          SuggestedFileName = "rally.ral",
          FileTypeChoices = new[] { _rallyType },
        });
    if (file?.TryGetLocalPath() is { } path) {
      await Vm.SaveFileAsync(path);
    }
  }

  private async Task PickAndLoadFile(bool append) {
    var top = TopLevel.GetTopLevel(this);
    if (top is null || Vm is null) {
      return;
    }

    var files = await top.StorageProvider.OpenFilePickerAsync(
        new FilePickerOpenOptions {
          Title = append ? "Load and append" : "Load Mission/Fence/Rally/Polygon",
          AllowMultiple = false,
          FileTypeFilter = new[] { _wpType },
        }
    );
    var file = files.FirstOrDefault();
    if (file?.TryGetLocalPath() is { } path) {
      await Vm.LoadFileAsync(path, append);
    }
  }

  private async void OnSaveFile(object? sender, RoutedEventArgs e) {
    await SaveFile();
  }

  private async Task SaveFile() {
    var top = TopLevel.GetTopLevel(this);
    if (top is null || Vm is null) {
      return;
    }

    string defaultExtension = Vm.MissionType switch {
      "Fence" => "fen",
      "Rally" => "ral",
      _ => "waypoints",
    };
    string suggestedName = Vm.MissionType.ToLowerInvariant() + "." + defaultExtension;
    var file = await top.StorageProvider.SaveFilePickerAsync(
        new FilePickerSaveOptions {
          Title = "Save Mission/Fence/Rally/Polygon",
          DefaultExtension = defaultExtension,
          SuggestedFileName = suggestedName,
          FileTypeChoices = new[] { _wpType, _jsonMissionType, _fenceType, _rallyType, _polygonType },
        }
    );
    if (file?.TryGetLocalPath() is { } path) {
      await Vm.SaveFileAsync(path);
    }
  }

  private async void OnPlannerKeyDown(object? sender, KeyEventArgs e) {
    if (Vm == null || !e.KeyModifiers.HasFlag(KeyModifiers.Control)) {
      return;
    }

    bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
    switch (e.Key) {
      case Key.Z when !shift:
        e.Handled = true;
        Vm.UndoCommand.Execute(null);
        break;
      case Key.O when !shift:
        e.Handled = true;
        await PickAndLoadFile(false);
        break;
      case Key.S when !shift:
        e.Handled = true;
        await SaveFile();
        break;
      case Key.F when shift:
        e.Handled = true;
        Vm.WriteWaypointsFastCommand.Execute(null);
        break;
      case Key.W when shift:
        e.Handled = true;
        Vm.WriteWaypointsCommand.Execute(null);
        break;
      case Key.R when shift:
        e.Handled = true;
        Vm.ReadWaypointsCommand.Execute(null);
        break;
    }
  }

  private async void OnLoadPolygon(object? sender, RoutedEventArgs e) {
    var top = TopLevel.GetTopLevel(this);
    if (top is null || Vm is null) {
      return;
    }
    var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions {
      Title = "Load Polygon",
      AllowMultiple = false,
      FileTypeFilter = new[] { _polygonType },
    });
    if (files.FirstOrDefault()?.TryGetLocalPath() is { } path) {
      await Vm.LoadFileAsync(path);
    }
  }

  private async void OnSavePolygon(object? sender, RoutedEventArgs e) {
    var top = TopLevel.GetTopLevel(this);
    if (top is null || Vm is null) {
      return;
    }
    var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions {
      Title = "Save Polygon",
      DefaultExtension = "poly",
      SuggestedFileName = "polygon.poly",
      FileTypeChoices = new[] { _polygonType },
    });
    if (file?.TryGetLocalPath() is { } path) {
      await Vm.SaveFileAsync(path);
    }
  }

  private async void OnLoadShapefilePolygon(object? sender, RoutedEventArgs e) {
    var top = TopLevel.GetTopLevel(this);
    if (top is null || Vm is null) {
      return;
    }
    var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions {
      Title = "Load Polygon from SHP",
      AllowMultiple = false,
      FileTypeFilter = new[] { _shapefileType },
    });
    if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path) {
      return;
    }
    try {
      Services.ShapefilePolygonImport import = await Task.Run(() =>
          Services.ShapefileImportService.ReadPolygon(path));
      if (import.Points.Count < 3) {
        Vm.Status = "No polygon with at least three vertices found in the shapefile.";
        return;
      }
      Vm.ReplaceDrawnPolygon(import.Points.Select(point =>
          new MissionPlanner.Utilities.PointLatLngAlt(point.Lat, point.Lng, point.Alt)).ToList());
      string projection = import.ProjectionName == null
          ? "raw longitude/latitude"
          : import.ProjectionName;
      Vm.Status = $"Loaded SHP polygon with {import.Points.Count} vertices from " +
          $"{import.FeatureCount} feature(s) ({projection}).";
    } catch (Exception ex) {
      Vm.Status = "SHP polygon load failed: " + ex.Message;
    }
  }

  private void OnViewKml(object? sender, RoutedEventArgs e) {
    if (Vm != null) {
      Vm.Status = Vm.GenerateMissionKmlAndOpen();
    }
  }

  private async void OnLoadMapOverlay(object? sender, RoutedEventArgs e) {
    var top = TopLevel.GetTopLevel(this);
    if (top is null) {
      return;
    }

    var files = await top.StorageProvider.OpenFilePickerAsync(
        new FilePickerOpenOptions {
          Title = "Load Map Overlay",
          AllowMultiple = false,
          FileTypeFilter = new[] { _mapOverlayType },
        }
    );
    if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path) {
      return;
    }

    try {
      string extension = Path.GetExtension(path).ToLowerInvariant();
      int? signedUtmZone = null;
      if (extension == ".dxf") {
        string? zoneText = await PromptAsync("DXF coordinate system",
            "Signed UTM zone (1..60 north, -1..-60 south); leave blank for longitude/latitude",
            "");
        if (zoneText == null) {
          return;
        }
        if (!string.IsNullOrWhiteSpace(zoneText)) {
          if (!int.TryParse(zoneText, NumberStyles.Integer, CultureInfo.InvariantCulture,
                  out int zone)) {
            if (Vm != null) {
              Vm.Status = "DXF load failed: UTM zone must be an integer.";
            }
            return;
          }
          signedUtmZone = zone;
        }
      }

      // Match upstream clear-first parsing, but preserve the current overlay when
      // the port-specific DXF coordinate-system prompt is cancelled or invalid.
      Map.ShowMapOverlay(Services.ImportedMapOverlay.Empty);
      Services.ImportedOverlayStore.ClearFlightData();
      Services.ImportedMapOverlay overlay = await Task.Run(() => extension switch {
        ".dxf" => Services.DxfOverlayReader.Read(path, signedUtmZone),
        ".shp" => Services.ShapefileImportService.ReadOverlay(path),
        ".gpkg" => Services.GeoPackageOverlayReader.Read(path),
        ".kml" or ".kmz" => Services.KmlMissionReader.ReadOverlay(path),
        _ => throw new InvalidDataException("Unsupported map overlay format."),
      });
      if (!overlay.HasContent) {
        if (Vm != null) {
          Vm.Status = "No supported geometry found in the map overlay.";
        }
        return;
      }
      Map.ShowMapOverlay(overlay);
      Map.ZoomToMapOverlay();
      bool copiedToFlightData = false;
      if (extension == ".gpkg") {
        // Official Mission Planner adds GeoPackage points, lines and polygons to both
        // Flight Planner and Flight Data without a second prompt.
        Services.ImportedOverlayStore.CopyVectorGeometryToFlightData(overlay);
        copiedToFlightData = true;
      } else if (extension is ".kml" or ".kmz"
          && await Services.Dialogs.Confirm("Map Overlay",
              "Do you want to load this into the Flight Data screen?")) {
        // Official Mission Planner copies KML polygons/routes, but not point markers or
        // GroundOverlay images, into its separate Flight Data overlay.
        Services.ImportedOverlayStore.CopyRoutesToFlightData(overlay);
        copiedToFlightData = true;
      }
      if (Vm != null) {
        Vm.Status = $"{extension.TrimStart('.').ToUpperInvariant()} overlay loaded " +
            $"({overlay.Routes.Count} route(s), {overlay.Markers.Count} marker(s), " +
            $"{overlay.Rasters.Count} raster(s), {overlay.PointCount} point(s))" +
            (copiedToFlightData ? "; routes shown on Flight Data." : ".");
      }
    } catch (Exception ex) {
      if (Vm != null) {
        Vm.Status = "Map overlay load failed: " + ex.Message;
      }
    }
  }

  private async Task SearchPlaceAsync() {
    string? place = await PromptAsync(
        "Zoom To", "Location", "Perth Airport, Australia");
    if (string.IsNullOrWhiteSpace(place)) {
      return;
    }

    try {
      GMap.NET.GeoCoderStatusCode status = GMap.NET.GeoCoderStatusCode.Unknow;
      GMap.NET.PointLatLng? point = await Task.Run(() =>
          GMap.NET.MapProviders.GMapProviders.OpenStreetMap.GetPoint(place, out status));
      if (point is { } location && status == GMap.NET.GeoCoderStatusCode.G_GEO_SUCCESS) {
        Map.CenterOnAndZoom(location.Lat, location.Lng, 15);
        ZoomSlider.Value = 15;
        if (Vm != null) {
          Vm.Status = "Map centred on " + place;
        }
        return;
      }
      if (Vm != null) {
        Vm.Status = $"Location not found ({status}): {place}";
      }
    } catch (Exception ex) {
      if (Vm != null) {
        Vm.Status = "Location search failed: " + ex.Message;
      }
    }
  }

  private async Task SetMapHeadingAsync() {
    string? input = await PromptAsync(
        "Rotate map", "New up heading (degrees)",
        Map.Rotation.ToString("0.##", CultureInfo.InvariantCulture));
    if (double.TryParse(input, NumberStyles.Any, CultureInfo.InvariantCulture,
            out double heading) && double.IsFinite(heading)) {
      Map.RotateTo(heading);
    } else if (input != null && Vm != null) {
      Vm.Status = "Invalid map heading.";
    }
  }

  private async Task PrefetchMapTilesAsync(bool pathOnly) {
    if (Vm == null) {
      return;
    }
    if (_tilePrefetchRunning) {
      Vm.Status = "Tile prefetch is already running.";
      return;
    }
    int current = Math.Clamp(Map.CurrentZoomLevel, 1, 21);
    string? minimumInput = await PromptAsync(
        "Tile prefetch", "Minimum zoom (1-21)", current.ToString(CultureInfo.InvariantCulture));
    if (minimumInput == null) {
      return;
    }
    string? maximumInput = await PromptAsync(
        "Tile prefetch", "Maximum zoom (1-21)",
        Math.Min(current + 2, 21).ToString(CultureInfo.InvariantCulture));
    if (maximumInput == null) {
      return;
    }
    if (!int.TryParse(minimumInput, NumberStyles.Integer, CultureInfo.InvariantCulture,
            out int minimum)
        || !int.TryParse(maximumInput, NumberStyles.Integer, CultureInfo.InvariantCulture,
            out int maximum)
        || minimum is < 1 or > 21 || maximum is < 1 or > 21 || minimum > maximum) {
      Vm.Status = "Invalid tile-prefetch zoom range.";
      return;
    }

    IReadOnlyList<BruTile.TileInfo> tiles = pathOnly
        ? Services.MapTileSourceFactory.PathTiles(
            Vm.Waypoints
                .Where(row => Services.MissionRoute.IsNavigation(row.Command)
                              && (row.Lat != 0 || row.Lng != 0))
                .Select(row => (row.Lat, row.Lng)).ToList(), minimum, maximum)
        : Services.MapTileSourceFactory.AreaTiles(Map.VisibleTileExtent, minimum, maximum);
    if (tiles.Count == 0) {
      Vm.Status = pathOnly ? "No waypoint path to prefetch." : "The visible map area is empty.";
      return;
    }
    if (tiles.Count > 20000) {
      Vm.Status = $"Tile prefetch has {tiles.Count} tiles; reduce the area or zoom range.";
      return;
    }
    if (tiles.Count > 2000 && !await Services.Dialogs.Confirm(
          "Tile prefetch", $"Download and cache {tiles.Count} map tiles?")) {
      Vm.Status = "Tile prefetch cancelled.";
      return;
    }

    try {
      _tilePrefetchRunning = true;
      var progress = new Progress<(int Done, int Total)>(value =>
          Vm.Status = $"Prefetching map tiles: {value.Done}/{value.Total}…");
      Services.MapPrefetchResult result = await Services.MapTileSourceFactory.PrefetchAsync(
          Vm.MapType, tiles, progress);
      Vm.Status = $"Tile prefetch complete: {result.Downloaded}/{result.Total} cached"
          + (result.Failed > 0 ? $", {result.Failed} failed." : ".");
    } catch (Exception ex) {
      Vm.Status = "Tile prefetch failed: " + ex.Message;
    } finally {
      _tilePrefetchRunning = false;
    }
  }

  private async void OnInjectCustomMap(object? sender, RoutedEventArgs e) {
    var url = await PromptAsync("Inject Custom Map",
        "Tile URL template (use {x} {y} {z}):",
        "https://tile.openstreetmap.org/{z}/{x}/{y}.png");
    if (!string.IsNullOrWhiteSpace(url)) {
      Map.SetCustomTileSource(url);
      if (Vm != null) {
        Vm.Status = "Custom tile source applied.";
      }
    }
  }

  private async void OnConfigureWms(object? sender, RoutedEventArgs e) {
    if (Vm == null) {
      return;
    }
    string initial = MissionPlanner.Utilities.Settings.Instance["WMSserver"] ?? "";
    string? server = await Services.Dialogs.InputBox(
        "WMS Server", "Enter the WMS server URL", initial);
    if (server == null) {
      return;
    }
    try {
      Vm.Status = "Reading WMS capabilities…";
      Services.WmsDiscovery discovery = await Services.OgcMapProvider.DiscoverWmsAsync(server);
      string? selectedLayer = MissionPlanner.Utilities.Settings.Instance["WMSLayer"];
      int selected = Math.Max(0, discovery.Layers.ToList()
          .FindIndex(layer => string.Equals(layer.Name, selectedLayer, StringComparison.Ordinal)));
      int? choice = await Services.Dialogs.Select(
          "WMS Server", "Select a PNG map layer", discovery.Layers
              .Select(layer => $"{layer.Title} — {layer.Name} ({layer.Crs})").ToArray(), selected);
      if (choice == null) {
        Vm.Status = "WMS configuration cancelled.";
        return;
      }
      Services.OgcMapProvider.SaveWms(discovery, discovery.Layers[choice.Value]);
      ActivateOgcMap(Services.OgcMapProvider.WmsMapType);
      Vm.Status = $"WMS layer active: {discovery.Layers[choice.Value].Title}";
    } catch (Exception ex) {
      Vm.Status = "WMS configuration failed: " + ex.Message;
      await Services.Dialogs.Alert("WMS Server", ex.Message);
    }
  }

  private async void OnConfigureWmts(object? sender, RoutedEventArgs e) {
    if (Vm == null) {
      return;
    }
    string initial = MissionPlanner.Utilities.Settings.Instance["WMSTserver"]
        ?? "https://maps.wien.gv.at/basemap/1.0.0/WMTSCapabilities.xml";
    string? server = await Services.Dialogs.InputBox(
        "WMTS Server", "Enter the WMTS capabilities URL", initial);
    if (server == null) {
      return;
    }
    try {
      Vm.Status = "Reading WMTS capabilities…";
      Services.WmtsDiscovery discovery = await Services.OgcMapProvider.DiscoverWmtsAsync(server);
      int selectedSource = int.TryParse(
          MissionPlanner.Utilities.Settings.Instance["WMSTLayer"],
          NumberStyles.None, CultureInfo.InvariantCulture, out int stored) ? stored : -1;
      int selected = Math.Max(0, discovery.Layers.ToList()
          .FindIndex(layer => layer.SourceIndex == selectedSource));
      int? choice = await Services.Dialogs.Select(
          "WMTS Server", "Select a Web Mercator map layer",
          discovery.Layers.Select(layer => layer.DisplayName).ToArray(), selected);
      if (choice == null) {
        Vm.Status = "WMTS configuration cancelled.";
        return;
      }
      Services.WmtsLayerChoice layer = discovery.Layers[choice.Value];
      Services.OgcMapProvider.SaveWmts(discovery, layer);
      ActivateOgcMap(Services.OgcMapProvider.WmtsMapType);
      Vm.Status = $"WMTS layer active: {layer.DisplayName}";
    } catch (Exception ex) {
      Vm.Status = "WMTS configuration failed: " + ex.Message;
      await Services.Dialogs.Alert("WMTS Server", ex.Message);
    }
  }

  private void ActivateOgcMap(string mapType) {
    if (Vm == null) {
      return;
    }
    string previous = Services.MapTileSourceFactory.CurrentMapType;
    Vm.MapType = mapType;
    Map.SetMapType(mapType);
    if (string.Equals(previous, mapType, StringComparison.Ordinal)) {
      Services.MapTileSourceFactory.RefreshMapType(mapType);
    }
  }

  private void OnSurveyGrid(object? sender, RoutedEventArgs e) {
    if (Vm == null) {
      return;
    }

    OpenSurveyGrid(Vm);
  }

  private static void OpenSurveyGrid(FlightPlannerViewModel vm) {
    if (vm.BuildSurveyArea() is not { } area) {
      vm.Status = "Draw at least 3 polygon points to outline the survey area.";
      return;
    }

    GridUIWindow.OpenForPolygon(area.polygon, area.home,
        plan => vm.Status = vm.AppendSurveyPlan(plan),
        vm.ReplaceDrawnPolygon);
  }

  private Window? OwnerWindow => TopLevel.GetTopLevel(this) as Window;

  private async Task<string?> PromptAsync(string title, string label, string initial) {
    var owner = OwnerWindow;
    if (owner == null) {
      return null;
    }

    var box = new TextBox { Text = initial };
    var ok = new Button { Content = "OK", IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right };
    var dlg = new Window {
      Title = title,
      Width = 460,
      SizeToContent = SizeToContent.Height,
      WindowStartupLocation = WindowStartupLocation.CenterOwner,
      Content = new StackPanel {
        Margin = new Avalonia.Thickness(12),
        Spacing = 8,
        Children = {
          new TextBlock { Text = label },
          box,
          ok,
        },
      },
    };
    string? answer = null;
    ok.Click += (_, _) => {
      answer = box.Text;
      dlg.Close();
    };
    await dlg.ShowDialog(owner);
    return answer;
  }

}

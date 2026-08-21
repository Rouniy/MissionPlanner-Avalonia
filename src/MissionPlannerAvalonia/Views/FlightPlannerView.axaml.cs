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
  private int _noFlyLoadVersion;
  private bool _tilePrefetchRunning;

  private const int _pColIndex = 2;

  [Obsolete]
  public FlightPlannerView() {
    InitializeComponent();
    Map.WaypointDragMoved += OnWaypointDragged;
    Map.WaypointDragCommitted += OnWaypointDragged;
    Map.MapClicked += OnMapClicked;
    Map.MidpointInsertRequested += (afterSeq, lat, lng) =>
        Vm?.InsertWaypointAfterSeq(afterSeq, lat, lng);
    Map.ContextMenu = BuildMapMenu();
    KeyDown += OnPlannerKeyDown;
    DataContextChanged += (_, _) => WireViewModel();
    WireViewModel();
    LoadAutoNoFly();
    AttachedToVisualTree += (_, _) =>
        Services.NoFlyOverlay.VisibilityChanged += OnNoFlyVisibilityChanged;
    DetachedFromVisualTree += (_, _) => {
      Services.NoFlyOverlay.VisibilityChanged -= OnNoFlyVisibilityChanged;
      Vm?.SavePlannerSettings();
    };
  }

  private async void LoadAutoNoFly() {
    int version = ++_noFlyLoadVersion;
    if (!MissionPlanner.Utilities.Settings.Instance.GetBoolean("ShowNoFly", false)) {
      Map.SetNoFlyLayer(null);
      return;
    }
    try {
      var layer = await Task.Run(() =>
          Services.NoFlyOverlay.BuildLayerFromDirectory(Services.NoFlyOverlay.DefaultDirectory));
      if (version != _noFlyLoadVersion ||
          !MissionPlanner.Utilities.Settings.Instance.GetBoolean("ShowNoFly", false)) {
        return;
      }
      Map.SetNoFlyLayer(layer);
      if (layer != null) {
        if (Vm != null) {
          Vm.Status = "NoFly overlay loaded from " + Services.NoFlyOverlay.DefaultDirectory;
        }
      }
    } catch {
    }
  }

  private void OnNoFlyVisibilityChanged() => Dispatcher.UIThread.Post(LoadAutoNoFly);

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
    var kmlOverlay = new MenuItem { Header = "Load KML Overlay…" };
    kmlOverlay.Click += OnLoadKmlOverlay;
    menu.Items.Add(kmlOverlay);
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

  private static readonly FilePickerFileType _kmlType = new("KML/KMZ") {
    Patterns = new[] { "*.kml", "*.kmz" },
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

  private void OnMapTypeChanged(object? sender, SelectionChangedEventArgs e) {
    if (Vm != null) {
      Map.SetMapType(Vm.MapType);
    }
  }

  private async void OnLoadFile(object? sender, RoutedEventArgs e) {
    await PickAndLoadFile(false);
  }

  private async void OnLoadAndAppendFile(object? sender, RoutedEventArgs e) {
    await PickAndLoadFile(true);
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

  private void OnViewKml(object? sender, RoutedEventArgs e) {
    if (Vm != null) {
      Vm.Status = Vm.GenerateMissionKmlAndOpen();
    }
  }

  private async void OnLoadKmlOverlay(object? sender, RoutedEventArgs e) {
    var top = TopLevel.GetTopLevel(this);
    if (top is null) {
      return;
    }

    var files = await top.StorageProvider.OpenFilePickerAsync(
        new FilePickerOpenOptions {
          Title = "Load KML Overlay",
          AllowMultiple = false,
          FileTypeFilter = new[] { _kmlType },
        }
    );
    if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path) {
      return;
    }

    try {
      var track = Services.KmlMissionReader.Read(path).Overlay
          .Select(point => (point.Lat, point.Lng)).ToList();
      if (track.Count >= 2) {
        Map.ShowKmlTrack(track);
        if (Vm != null) {
          Vm.Status = $"KML/KMZ overlay loaded ({track.Count} point(s)).";
        }
      } else if (Vm != null) {
        Vm.Status = "No line or polygon geometry found in the KML/KMZ file.";
      }
    } catch (Exception ex) {
      if (Vm != null) {
        Vm.Status = "KML load failed: " + ex.Message;
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

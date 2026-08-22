using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using MissionPlanner.Utilities;
using MissionPlannerAvalonia.Controls;
using MissionPlannerAvalonia.Services;
using MissionPlannerAvalonia.ViewModels;

namespace MissionPlannerAvalonia.Views;

public partial class FlightDataView : UserControl {
  [Obsolete]
  public FlightDataView() {
    InitializeComponent();
    _flightDataLayout = this.FindControl<Avalonia.Controls.Grid>("FlightDataLayoutGrid");
    RestoreMainSplitterDistance();
    _hudHost = this.FindControl<ContentControl>("HudHost");
    _hud = this.FindControl<HudControl>("Hud");
    if (_hud != null) {
      _hud.IndicatorClicked += OnHudIndicatorClicked;
      _hud.CustomPaint += OnPluginHudPaint;
      _hud.DoubleTapped += OnHudDoubleTapped;
    }
    _quickHost = this.FindControl<ContentControl>("QuickHost");
    _quickGrid = this.FindControl<ItemsControl>("QuickGrid");
    _detachHudMenuItem = this.FindControl<MenuItem>("DetachHudMenuItem");
    _detachQuickMenuItem = this.FindControl<MenuItem>("DetachQuickMenuItem");
    _recordHudMenuItem = this.FindControl<MenuItem>("RecordHudMenuItem");
    _stopHudRecordingMenuItem = this.FindControl<MenuItem>("StopHudRecordingMenuItem");
    _hudCaptureTimer = new DispatcherTimer {
      Interval = TimeSpan.FromMilliseconds(1000.0 / HudFrameRecorder.DefaultFramesPerSecond),
    };
    _hudCaptureTimer.Tick += OnHudCaptureTick;
    UpdateHudRecordingMenu(recording: false);
    _mapVideoLayout = this.FindControl<Avalonia.Controls.Grid>("MapVideoLayout");
    _fdMap = this.FindControl<MapView>("FdMap");
    _gimbalVideoFullHost = this.FindControl<ContentControl>("GimbalVideoFullHost");
    _gimbalVideoMiniLayout = this.FindControl<Avalonia.Controls.Grid>("GimbalVideoMiniLayout");
    _gimbalVideoMiniHost = this.FindControl<ContentControl>("GimbalVideoMiniHost");
    _gimbalMiniMapHost = this.FindControl<ContentControl>("GimbalMiniMapHost");
    _tuningPlot = this.FindControl<LivePlot>("TuningPlot");
    if (_fdMap != null) {
      _fdMap.ContextMenu = BuildMapMenu(_fdMap);

      _fdMap.CursorMoved += (lat, lng) => {
        if (DataContext is FlightDataViewModel vm) {
          vm.CursorLat = lat;
          vm.CursorLng = lng;
        }
      };
      BindMap();
      DataContextChanged += (_, _) => BindMap();
    }
    _fdTabs = this.FindControl<TabControl>("FdTabs");
    if (_fdTabs != null) {
      _defaultTabPanel = _fdTabs.ItemsPanel;
      _fdTabs.ContextMenu = BuildTabMenu(_fdTabs);
      ApplyTabSettings(_fdTabs);
    }
    AttachedToVisualTree += (_, _) => {
      if (_displayViewSubscribed) {
        return;
      }
      _displayViewSubscribed = true;
      Services.DisplayViewService.Changed += OnDisplayViewChanged;
      if (_fdTabs != null) {
        ApplyTabSettings(_fdTabs);
      }
      BindGimbalVideoPresenter();
    };
    DetachedFromVisualTree += (_, _) => {
      _displayViewSubscribed = false;
      Services.DisplayViewService.Changed -= OnDisplayViewChanged;
      UnsubscribeGimbalVideoPresenter();
      CloseGimbalVideo();
      RestoreDetachedPanels();
      _ = StopHudRecordingAsync(showResult: false);
    };
    DataContextChanged += (_, _) => {
      SyncDetachedWindowDataContexts();
      BindGimbalVideoPresenter();
    };
    ApplyGaugeSettings();
  }

  private bool _displayViewSubscribed;

  private static void OnPluginHudPaint(HudControl hud, Avalonia.Media.DrawingContext context) {
    double scaling = TopLevel.GetTopLevel(hud)?.RenderScaling ?? 1;
    PluginService.DrawHud(context, hud.Bounds, scaling);
  }

  private void OnDisplayViewChanged(object? sender, EventArgs e) {
    Avalonia.Threading.Dispatcher.UIThread.Post(() => {
      if (_fdTabs != null) {
        ApplyTabSettings(_fdTabs);
      }
    });
  }

  private static readonly Dictionary<string, string> _gaugeKeys = new() {
    ["GVsi"] = "GaugeVSI",
    ["GSpeed"] = "GaugeSpeed",
    ["GAlt"] = "GaugeAlt",
  };

  private void ApplyGaugeSettings() {
    foreach (var (name, key) in _gaugeKeys) {
      var g = this.FindControl<Gauge>(name);
      if (g == null) {
        continue;
      }
      if (TryGetDouble(key + "MIN", out var mn)) {
        g.Min = mn;
      }
      if (TryGetDouble(key + "MAX", out var mx)) {
        g.Max = mx;
      }

      double span = g.Max - g.Min;
      g.Ranges = [
        new() { Start = g.Min, End = g.Min + span * 0.75,
                Color = new Avalonia.Media.SolidColorBrush(
                    Avalonia.Media.Color.FromArgb(120, 0, 200, 0)) },
        new() { Start = g.Min + span * 0.75, End = g.Max,
                Color = new Avalonia.Media.SolidColorBrush(
                    Avalonia.Media.Color.FromArgb(150, 220, 40, 40)) },
      ];
    }
  }

  private static bool TryGetDouble(string key, out double value) {
    value = 0;
    return Settings.Instance.ContainsKey(key)
        && double.TryParse(Settings.Instance[key], NumberStyles.Any, CultureInfo.InvariantCulture,
            out value);
  }

  private async void OnGaugeDoubleTapped(object? sender, TappedEventArgs e) {
    if (sender is not Gauge g || g.Name == null || !_gaugeKeys.TryGetValue(g.Name, out var key)) {
      return;
    }
    var minStr = await Services.Dialogs.InputBox("Set Min", "Enter Min value",
        g.Min.ToString(CultureInfo.InvariantCulture));
    if (minStr != null
        && double.TryParse(minStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var mn)) {
      g.Min = mn;
      Settings.Instance[key + "MIN"] = mn.ToString(CultureInfo.InvariantCulture);
    }
    var maxStr = await Services.Dialogs.InputBox("Set Max", "Enter Max value",
        g.Max.ToString(CultureInfo.InvariantCulture));
    if (maxStr != null
        && double.TryParse(maxStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var mx)) {
      g.Max = mx;
      Settings.Instance[key + "MAX"] = mx.ToString(CultureInfo.InvariantCulture);
    }
    ApplyGaugeSettings();
  }

  private async void OnQuickViewDoubleTapped(object? sender, TappedEventArgs e) {
    if (DataContext is not FlightDataViewModel { QuickViewEditable: true } vm) {
      return;
    }

    if ((e.Source as Control)?.DataContext is not QuickItem item) {
      return;
    }
    if (TopLevel.GetTopLevel(this) is not Window owner) {
      return;
    }
    var fields = vm.QuickFieldList();
    var list = new ListBox {
      ItemsSource = fields.ConvertAll(f => f.desc),
      Height = 480,
      MinWidth = 360,
    };
    int cur = fields.FindIndex(f => f.name == item.Field);
    if (cur >= 0) {
      list.SelectedIndex = cur;
    }
    var ok = new Button { Content = "OK", HorizontalAlignment = HorizontalAlignment.Right };
    var dlg = new Window {
      Title = "Display This",
      Width = 420,
      Height = 560,
      WindowStartupLocation = WindowStartupLocation.CenterOwner,
      Content = new StackPanel {
        Margin = new Avalonia.Thickness(10),
        Spacing = 8,
        Children = { new ScrollViewer { Content = list, Height = 480 }, ok },
      },
    };
    ok.Click += (_, _) => dlg.Close(true);
    list.DoubleTapped += (_, _) => dlg.Close(true);
    if (await dlg.ShowDialog<bool>(owner) && list.SelectedIndex >= 0) {
      vm.SetQuickField(item, fields[list.SelectedIndex].name);
    }
  }

  private readonly MapView? _fdMap;
  private readonly Avalonia.Controls.Grid? _mapVideoLayout;
  private readonly LivePlot? _tuningPlot;
  private readonly TabControl? _fdTabs;
  private readonly Avalonia.Controls.Grid? _flightDataLayout;
  private readonly ContentControl? _hudHost;
  private readonly HudControl? _hud;
  private readonly ContentControl? _quickHost;
  private readonly ItemsControl? _quickGrid;
  private readonly ContentControl? _gimbalVideoFullHost;
  private readonly Avalonia.Controls.Grid? _gimbalVideoMiniLayout;
  private readonly ContentControl? _gimbalVideoMiniHost;
  private readonly ContentControl? _gimbalMiniMapHost;
  private readonly MenuItem? _detachHudMenuItem;
  private readonly MenuItem? _detachQuickMenuItem;
  private readonly MenuItem? _recordHudMenuItem;
  private readonly MenuItem? _stopHudRecordingMenuItem;
  private readonly DispatcherTimer _hudCaptureTimer;
  private HudFrameRecorder? _hudRecorder;
  private FlightDataViewModel? _mapVm;
  private FlightDataViewModel? _gimbalVideoVm;
  private Window? _hudWindow;
  private Window? _quickWindow;
  private Window? _gimbalVideoWindow;
  private Control? _gimbalVideoPanel;
  private bool _gimbalVideoPresenterSubscribed;
  private bool _gimbalShowMiniMap = true;
  private GimbalVideoPresentation? _gimbalVideoPresentation;

  internal bool IsHudDetached => _hudWindow != null;
  internal bool IsQuickDetached => _quickWindow != null;
  internal GimbalVideoPresentation? CurrentGimbalVideoPresentation =>
      _gimbalVideoPresentation;

  private void OnHudDoubleTapped(object? sender, TappedEventArgs e) {
    if (_hudWindow == null) {
      DetachHud();
      e.Handled = true;
    }
  }

  private void OnToggleHudDetached(object? sender, RoutedEventArgs e) {
    if (_hudWindow == null) {
      DetachHud();
    } else {
      RestoreHud();
    }
  }

  private void OnToggleQuickDetached(object? sender, RoutedEventArgs e) {
    if (_quickWindow == null) {
      DetachQuick();
    } else {
      RestoreQuick();
    }
  }

  internal Window? DetachHud(bool showWindow = true) {
    if (_hudWindow != null) {
      if (showWindow && _hudWindow.IsVisible) {
        _hudWindow.Activate();
      }
      return _hudWindow;
    }
    if (_hudHost == null || _hud == null || !ReferenceEquals(_hudHost.Content, _hud)) {
      return null;
    }

    _hudWindow = CreateDetachedWindow(
        _hudHost, _hud, "Flight Data — HUD", 720, 480, RestoreHud);
    UpdateDetachedMenuHeaders();
    if (showWindow && !TryShowDetachedWindow(_hudWindow, RestoreHud)) {
      return null;
    }
    return _hudWindow;
  }

  internal Window? DetachQuick(bool showWindow = true) {
    if (_quickWindow != null) {
      if (showWindow && _quickWindow.IsVisible) {
        _quickWindow.Activate();
      }
      return _quickWindow;
    }
    if (_quickHost == null || _quickGrid == null
        || !ReferenceEquals(_quickHost.Content, _quickGrid)) {
      return null;
    }

    _quickWindow = CreateDetachedWindow(
        _quickHost, _quickGrid, "Flight Data — Quick", 340, 500, RestoreQuick);
    UpdateDetachedMenuHeaders();
    if (showWindow && !TryShowDetachedWindow(_quickWindow, RestoreQuick)) {
      return null;
    }
    return _quickWindow;
  }

  private Window CreateDetachedWindow(
      ContentControl host,
      Control panel,
      string title,
      double width,
      double height,
      Action restore) {
    var restoreButton = new Button {
      Content = title + " is open in a separate window. Click to dock it again.",
      HorizontalAlignment = HorizontalAlignment.Center,
      VerticalAlignment = VerticalAlignment.Center,
      Margin = new Thickness(16),
    };
    restoreButton.Click += (_, _) => restore();

    // Clear the original logical parent before assigning the same live control to a Window.
    host.Content = restoreButton;
    var window = new Window {
      Title = title,
      Width = width,
      Height = height,
      MinWidth = 260,
      MinHeight = 220,
      DataContext = DataContext,
      Content = panel,
      Background = title.EndsWith("HUD", StringComparison.Ordinal) ? Avalonia.Media.Brushes.Black : null,
      WindowStartupLocation = WindowStartupLocation.CenterOwner,
    };
    window.Closed += (_, _) => restore();
    return window;
  }

  private bool TryShowDetachedWindow(Window window, Action restore) {
    try {
      if (TopLevel.GetTopLevel(this) is Window owner) {
        window.Show(owner);
      } else {
        window.Show();
      }
      return true;
    } catch (Exception ex) {
      restore();
      _ = Services.Dialogs.Alert("Flight Data panel", "Could not open a separate window: " + ex.Message);
      return false;
    }
  }

  private void RestoreHud() {
    Window? window = _hudWindow;
    _hudWindow = null;
    if (window != null) {
      window.Content = null;
    }
    if (_hudHost != null && _hud != null) {
      _hudHost.Content = _hud;
    }
    if (window?.IsVisible == true) {
      window.Close();
    }
    UpdateDetachedMenuHeaders();
  }

  private void RestoreQuick() {
    Window? window = _quickWindow;
    _quickWindow = null;
    if (window != null) {
      window.Content = null;
    }
    if (_quickHost != null && _quickGrid != null) {
      _quickHost.Content = _quickGrid;
    }
    if (window?.IsVisible == true) {
      window.Close();
    }
    UpdateDetachedMenuHeaders();
  }

  internal void RestoreDetachedPanels() {
    RestoreHud();
    RestoreQuick();
  }

  private void SyncDetachedWindowDataContexts() {
    if (_hudWindow != null) {
      _hudWindow.DataContext = DataContext;
    }
    if (_quickWindow != null) {
      _quickWindow.DataContext = DataContext;
    }
  }

  private void UpdateDetachedMenuHeaders() {
    if (_detachHudMenuItem != null) {
      _detachHudMenuItem.Header = _hudWindow == null ? "Undock HUD" : "Dock HUD";
    }
    if (_detachQuickMenuItem != null) {
      _detachQuickMenuItem.Header = _quickWindow == null ? "Undock Quick" : "Dock Quick";
    }
  }

  private void BindGimbalVideoPresenter() {
    var next = DataContext as FlightDataViewModel;
    if (!ReferenceEquals(_gimbalVideoVm, next)) {
      UnsubscribeGimbalVideoPresenter();
      CloseGimbalVideo();
      _gimbalVideoVm = next;
    }
    if (_displayViewSubscribed
        && _gimbalVideoVm != null
        && !_gimbalVideoPresenterSubscribed) {
      _gimbalVideoVm.VideoPresentationRequested += OnGimbalVideoPresentationRequested;
      _gimbalVideoPresenterSubscribed = true;
    }
  }

  private void UnsubscribeGimbalVideoPresenter() {
    if (_gimbalVideoVm != null && _gimbalVideoPresenterSubscribed) {
      _gimbalVideoVm.VideoPresentationRequested -= OnGimbalVideoPresentationRequested;
    }
    _gimbalVideoPresenterSubscribed = false;
  }

  private void OnGimbalVideoPresentationRequested(GimbalVideoPresentation presentation) {
    if (!Dispatcher.UIThread.CheckAccess()) {
      Dispatcher.UIThread.Post(() => PresentGimbalVideo(presentation));
      return;
    }
    PresentGimbalVideo(presentation);
  }

  internal Window? PresentGimbalVideo(
      GimbalVideoPresentation presentation,
      bool showWindow = true) {
    FlightDataViewModel? vm = _gimbalVideoVm ?? DataContext as FlightDataViewModel;
    if (vm == null) {
      return null;
    }
    if (!ReferenceEquals(_gimbalVideoVm, vm)) {
      BindGimbalVideoPresenter();
    }

    VideoPopupWindow window = vm.EnsureVideoWindow(showWindow: false);
    Control? panel = _gimbalVideoPanel ?? window.Content as Control;
    if (panel == null) {
      return null;
    }
    panel.ContextMenu ??= BuildGimbalVideoPresentationMenu();
    return PlaceGimbalVideoPanel(panel, presentation, window, showWindow);
  }

  internal Window PlaceGimbalVideoPanel(
      Control panel,
      GimbalVideoPresentation presentation,
      Window? popupWindow = null,
      bool showWindow = false) {
    ArgumentNullException.ThrowIfNull(panel);
    popupWindow ??= new Window {
      Title = "MAVLink Camera / Gimbal Video",
      Width = 900,
      Height = 620,
      Background = Avalonia.Media.Brushes.Black,
      WindowStartupLocation = WindowStartupLocation.CenterOwner,
    };

    if (!ReferenceEquals(_gimbalVideoWindow, popupWindow)) {
      if (_gimbalVideoWindow != null) {
        _gimbalVideoWindow.Closed -= OnGimbalVideoWindowClosed;
      }
      _gimbalVideoWindow = popupWindow;
      _gimbalVideoWindow.Closed += OnGimbalVideoWindowClosed;
    }
    _gimbalVideoPanel = panel;
    DetachGimbalVideoPanel();
    RestoreGimbalMap();

    switch (presentation) {
      case GimbalVideoPresentation.FullSized:
        if (popupWindow.IsVisible) {
          popupWindow.Hide();
        }
        if (_gimbalVideoFullHost != null) {
          _gimbalVideoFullHost.Content = panel;
          _gimbalVideoFullHost.IsVisible = true;
        }
        MoveMapToGimbalMiniHost();
        break;
      case GimbalVideoPresentation.Mini:
        if (popupWindow.IsVisible) {
          popupWindow.Hide();
        }
        if (_gimbalVideoMiniHost != null) {
          _gimbalVideoMiniHost.Content = panel;
          if (_gimbalVideoMiniLayout != null) {
            _gimbalVideoMiniLayout.IsVisible = true;
          }
        }
        break;
      case GimbalVideoPresentation.PopOut:
        popupWindow.Content = panel;
        if (showWindow && !TryShowGimbalVideoWindow(popupWindow)) {
          popupWindow.Content = null;
          if (_gimbalVideoMiniHost != null) {
            _gimbalVideoMiniHost.Content = panel;
            if (_gimbalVideoMiniLayout != null) {
              _gimbalVideoMiniLayout.IsVisible = true;
            }
          }
          presentation = GimbalVideoPresentation.Mini;
        }
        break;
      default:
        throw new ArgumentOutOfRangeException(nameof(presentation), presentation, null);
    }
    _gimbalVideoPresentation = presentation;
    return popupWindow;
  }

  private bool TryShowGimbalVideoWindow(Window window) {
    try {
      if (window.IsVisible) {
        window.Activate();
      } else if (TopLevel.GetTopLevel(this) is Window owner) {
        window.Show(owner);
      } else {
        window.Show();
      }
      return true;
    } catch (Exception ex) {
      _ = Services.Dialogs.Alert(
          "Gimbal Video", "Could not open the video window: " + ex.Message);
      return false;
    }
  }

  private ContextMenu BuildGimbalVideoPresentationMenu() {
    MenuItem Item(string header, Action action) {
      var item = new MenuItem { Header = header };
      item.Click += (_, _) => action();
      return item;
    }
    var showMiniMap = new MenuItem {
      Header = "Show Mini Map",
      ToggleType = MenuItemToggleType.CheckBox,
      IsChecked = _gimbalShowMiniMap,
    };
    showMiniMap.Click += (_, _) => {
      _gimbalShowMiniMap = showMiniMap.IsChecked;
      UpdateGimbalMiniMapVisibility();
    };
    return new ContextMenu {
      Items = {
        showMiniMap,
        Item("Swap with map", () => PresentGimbalVideo(
            _gimbalVideoPresentation == GimbalVideoPresentation.FullSized
                ? GimbalVideoPresentation.Mini
                : GimbalVideoPresentation.FullSized)),
        new Separator(),
        Item("Full Sized", () => PresentGimbalVideo(GimbalVideoPresentation.FullSized)),
        Item("Mini", () => PresentGimbalVideo(GimbalVideoPresentation.Mini)),
        Item("Pop Out", () => PresentGimbalVideo(GimbalVideoPresentation.PopOut)),
        new Separator(),
        Item("Close Video", CloseGimbalVideo),
      },
    };
  }

  private void MoveMapToGimbalMiniHost() {
    if (_fdMap == null || _mapVideoLayout == null || _gimbalMiniMapHost == null) {
      return;
    }
    _mapVideoLayout.Children.Remove(_fdMap);
    _gimbalMiniMapHost.Content = _fdMap;
    _gimbalMiniMapHost.IsVisible = _gimbalShowMiniMap;
    if (_gimbalShowMiniMap && _gimbalVideoMiniLayout != null) {
      _gimbalVideoMiniLayout.IsVisible = true;
    }
  }

  private void UpdateGimbalMiniMapVisibility() {
    bool show = _gimbalVideoPresentation == GimbalVideoPresentation.FullSized
        && _gimbalShowMiniMap
        && _gimbalMiniMapHost?.Content != null;
    if (_gimbalMiniMapHost != null) {
      _gimbalMiniMapHost.IsVisible = show;
    }
    if (_gimbalVideoPresentation == GimbalVideoPresentation.FullSized
        && _gimbalVideoMiniLayout != null) {
      _gimbalVideoMiniLayout.IsVisible = show;
    }
  }

  private void RestoreGimbalMap() {
    if (_gimbalMiniMapHost != null
        && ReferenceEquals(_gimbalMiniMapHost.Content, _fdMap)) {
      _gimbalMiniMapHost.Content = null;
    }
    if (_gimbalMiniMapHost != null) {
      _gimbalMiniMapHost.IsVisible = false;
    }
    if (_fdMap != null
        && _mapVideoLayout != null
        && !_mapVideoLayout.Children.Contains(_fdMap)) {
      _mapVideoLayout.Children.Insert(0, _fdMap);
    }
  }

  private void DetachGimbalVideoPanel() {
    Control? panel = _gimbalVideoPanel;
    if (_gimbalVideoFullHost != null) {
      if (ReferenceEquals(_gimbalVideoFullHost.Content, panel)) {
        _gimbalVideoFullHost.Content = null;
      }
      _gimbalVideoFullHost.IsVisible = false;
    }
    if (_gimbalVideoMiniHost != null
        && ReferenceEquals(_gimbalVideoMiniHost.Content, panel)) {
      _gimbalVideoMiniHost.Content = null;
    }
    if (_gimbalVideoMiniLayout != null) {
      _gimbalVideoMiniLayout.IsVisible = false;
    }
    if (_gimbalVideoWindow != null
        && ReferenceEquals(_gimbalVideoWindow.Content, panel)) {
      _gimbalVideoWindow.Content = null;
    }
  }

  private void OnGimbalVideoWindowClosed(object? sender, EventArgs e) {
    if (sender is not Window window || !ReferenceEquals(window, _gimbalVideoWindow)) {
      return;
    }
    window.Closed -= OnGimbalVideoWindowClosed;
    DetachGimbalVideoPanel();
    RestoreGimbalMap();
    _gimbalVideoWindow = null;
    _gimbalVideoPanel = null;
    _gimbalVideoPresentation = null;
  }

  internal void CloseGimbalVideo() {
    Window? window = _gimbalVideoWindow;
    Control? panel = _gimbalVideoPanel;
    FlightDataViewModel? vm = _gimbalVideoVm;
    if (window != null) {
      window.Closed -= OnGimbalVideoWindowClosed;
    }
    DetachGimbalVideoPanel();
    RestoreGimbalMap();
    _gimbalVideoWindow = null;
    _gimbalVideoPanel = null;
    _gimbalVideoPresentation = null;
    if (window != null && panel != null) {
      window.Content = panel;
    }
    if (vm != null) {
      vm.CloseVideoWindow();
    } else if (window != null) {
      window.Content = null;
      window.Close();
    }
  }

  private async void OnStartHudRecording(object? sender, RoutedEventArgs e) {
    if (_hud == null) {
      await Services.Dialogs.Alert("HUD Recording", "The HUD is not available.");
      return;
    }

    await StopHudRecordingAsync(showResult: false);
    try {
      double scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
      PixelSize size = HudPixelSize(_hud, scaling);
      string output = HudRecordingPath.Create(Settings.Instance.LogDir, DateTime.Now);
      _hudRecorder = new HudFrameRecorder(
          output, size.Width, size.Height, HudFrameRecorder.DefaultFramesPerSecond);
      UpdateHudRecordingMenu(recording: true);
      CaptureHudFrame();
      _hudCaptureTimer.Start();
      await Services.Dialogs.Alert(
          "HUD Recording",
          "HUD recording started. The AVI file is being saved in the log folder:\n\n" + output);
    } catch (Exception ex) {
      await StopHudRecordingAsync(showResult: false);
      await Services.Dialogs.Alert("HUD Recording", "Could not start recording: " + ex.Message);
    }
  }

  private async void OnStopHudRecording(object? sender, RoutedEventArgs e) {
    await StopHudRecordingAsync(showResult: true);
  }

  private void OnHudCaptureTick(object? sender, EventArgs e) {
    HudFrameRecorder? recorder = _hudRecorder;
    if (recorder == null) {
      _hudCaptureTimer.Stop();
      return;
    }
    if (!recorder.IsActive) {
      _ = StopHudRecordingAsync(showResult: true);
      return;
    }
    if (!recorder.CanAcceptFrame) {
      return;
    }

    try {
      CaptureHudFrame();
    } catch (Exception ex) {
      _ = StopHudRecordingAfterCaptureErrorAsync(ex);
    }
  }

  private void CaptureHudFrame() {
    HudFrameRecorder? recorder = _hudRecorder;
    if (recorder == null || _hud == null) {
      return;
    }

    double scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
    PixelSize size = HudPixelSize(_hud, scaling);
    var dpi = new Vector(96 * scaling, 96 * scaling);
    using var target = new RenderTargetBitmap(size, dpi);
    target.Render(_hud);

    if (target.Format != PixelFormats.Bgra8888
        && target.Format != PixelFormats.Rgba8888) {
      using var encoded = new MemoryStream();
      target.Save(encoded);
      int imageLength = checked((int)encoded.Length);
      byte[] image = ArrayPool<byte>.Shared.Rent(imageLength);
      encoded.Position = 0;
      encoded.ReadExactly(image.AsSpan(0, imageLength));
      recorder.SubmitPooledEncodedFrame(image, imageLength, size.Width, size.Height);
      return;
    }
    HudPixelLayout layout = target.Format == PixelFormats.Bgra8888
        ? HudPixelLayout.Bgra8888
        : HudPixelLayout.Rgba8888;

    int stride = checked(size.Width * 4);
    int byteCount = checked(stride * size.Height);
    byte[] pixels = ArrayPool<byte>.Shared.Rent(byteCount);
    try {
      GCHandle pinned = GCHandle.Alloc(pixels, GCHandleType.Pinned);
      try {
        target.CopyPixels(new PixelRect(size), pinned.AddrOfPinnedObject(), byteCount, stride);
      } finally {
        pinned.Free();
      }
    } catch {
      ArrayPool<byte>.Shared.Return(pixels);
      throw;
    }

    // SubmitPooledFrame owns and returns the rented array, including when its queue is full.
    recorder.SubmitPooledFrame(pixels, size.Width, size.Height, stride, layout);
  }

  private static PixelSize HudPixelSize(HudControl hud, double scaling = 1) {
    if (!double.IsFinite(scaling) || scaling <= 0) {
      scaling = 1;
    }
    int width = Math.Clamp((int)Math.Round(hud.Bounds.Width * scaling), 16, 8192);
    int height = Math.Clamp((int)Math.Round(hud.Bounds.Height * scaling), 16, 8192);
    return new PixelSize(width, height);
  }

  private async Task StopHudRecordingAfterCaptureErrorAsync(Exception captureError) {
    HudRecordingResult? result = await StopHudRecordingAsync(showResult: false);
    string detail = result?.Error?.Message ?? captureError.Message;
    await Services.Dialogs.Alert("HUD Recording", "Recording stopped: " + detail);
  }

  private async Task<HudRecordingResult?> StopHudRecordingAsync(bool showResult) {
    _hudCaptureTimer.Stop();
    HudFrameRecorder? recorder = _hudRecorder;
    _hudRecorder = null;
    UpdateHudRecordingMenu(recording: false);
    if (recorder == null) {
      return null;
    }

    HudRecordingResult result = await recorder.StopAsync();
    if (showResult) {
      string message = result.Error == null
          ? $"HUD recording saved ({result.WrittenFrames} frames):\n\n{result.Path}"
          : $"HUD recording stopped with an error: {result.Error.Message}\n\n"
              + $"The partial AVI is at:\n{result.Path}";
      await Services.Dialogs.Alert("HUD Recording", message);
    }
    return result;
  }

  private void UpdateHudRecordingMenu(bool recording) {
    if (_recordHudMenuItem != null) {
      _recordHudMenuItem.Header = recording ? "Recording HUD…" : "Record HUD to AVI";
      _recordHudMenuItem.IsEnabled = !recording;
    }
    if (_stopHudRecordingMenuItem != null) {
      _stopHudRecordingMenuItem.IsEnabled = recording;
    }
  }

  private void OnMainSplitterDragCompleted(object? sender, VectorEventArgs e) {
    if (_flightDataLayout?.ColumnDefinitions.Count >= 3) {
      SaveMainSplitterDistance(_flightDataLayout.ColumnDefinitions[0].ActualWidth);
    }
  }

  private void RestoreMainSplitterDistance() {
    if (!Settings.Instance.ContainsKey("FlightSplitter") ||
        !double.TryParse(Settings.Instance["FlightSplitter"], NumberStyles.Float,
            CultureInfo.InvariantCulture, out double distance)) {
      return;
    }
    ApplyMainSplitterDistance(distance, persist: false);
  }

  internal void ApplyMainSplitterDistance(double distance, bool persist = true) {
    if (_flightDataLayout == null || _flightDataLayout.ColumnDefinitions.Count < 3 ||
        double.IsNaN(distance) || double.IsInfinity(distance)) {
      return;
    }

    ColumnDefinition left = _flightDataLayout.ColumnDefinitions[0];
    ColumnDefinition right = _flightDataLayout.ColumnDefinitions[2];
    double maximum = _flightDataLayout.Bounds.Width > 0
        ? Math.Max(left.MinWidth,
            _flightDataLayout.Bounds.Width - right.MinWidth -
            _flightDataLayout.ColumnDefinitions[1].ActualWidth)
        : 2000;
    distance = Math.Clamp(distance, left.MinWidth, maximum);
    left.Width = new GridLength(distance, GridUnitType.Pixel);
    if (persist) {
      SaveMainSplitterDistance(distance);
    }
  }

  private static void SaveMainSplitterDistance(double distance) {
    if (distance <= 0 || double.IsNaN(distance) || double.IsInfinity(distance)) {
      return;
    }
    Settings.Instance["FlightSplitter"] =
        Math.Round(distance).ToString(CultureInfo.InvariantCulture);
  }

  private void BindMap() {
    if (_fdMap == null || ReferenceEquals(_mapVm, DataContext)) {
      return;
    }
    if (_mapVm != null) {
      _mapVm.TrackClearRequested -= _fdMap.ClearTrack;
      _mapVm.ActionTabShortcutRequested -= SelectVisibleActionTab;
      _mapVm.PropertyChanged -= OnMapVmChanged;
      _mapVm.TuningSampled -= OnTuningSampled;
      _mapVm.TuningFieldsChanged -= OnTuningFieldsChanged;
    }
    _mapVm = DataContext as FlightDataViewModel;
    if (_mapVm != null) {
      _fdMap.AutoPan = _mapVm.AutoPan;
      _mapVm.TrackClearRequested += _fdMap.ClearTrack;
      _mapVm.ActionTabShortcutRequested += SelectVisibleActionTab;
      _mapVm.PropertyChanged += OnMapVmChanged;
      _mapVm.TuningSampled += OnTuningSampled;
      _mapVm.TuningFieldsChanged += OnTuningFieldsChanged;
    }
  }

  private void SelectVisibleActionTab(int ordinal) {
    if (_fdTabs == null || ordinal < 0) {
      return;
    }
    var visibleTabs = TabItemsOf(_fdTabs).Where(tab => tab.IsVisible).ToList();
    if (ordinal < visibleTabs.Count) {
      _fdTabs.SelectedItem = visibleTabs[ordinal];
    }
  }

  private void OnMapVmChanged(object? _, System.ComponentModel.PropertyChangedEventArgs e) {
    if (_fdMap != null && _mapVm != null
        && e.PropertyName == nameof(FlightDataViewModel.AutoPan)) {
      _fdMap.AutoPan = _mapVm.AutoPan;
    }
    if (e.PropertyName == nameof(FlightDataViewModel.Tuning) && _mapVm?.Tuning != true) {
      ResetTuningPlot();
    }
  }

  private readonly Dictionary<string, (List<double> Xs, List<double> Ys)> _tuningBuffers = [];

  private static readonly ScottPlot.Color[] _tuningPalette = [
    ScottPlot.Colors.Yellow, ScottPlot.Colors.Cyan, ScottPlot.Colors.OrangeRed,
    ScottPlot.Colors.LightGreen, ScottPlot.Colors.Magenta, ScottPlot.Colors.DeepSkyBlue,
  ];
  private readonly Dictionary<string, ScottPlot.Color> _tuningColors = [];

  private ScottPlot.Color ColorFor(string label) {
    if (!_tuningColors.TryGetValue(label, out var c)) {
      c = _tuningPalette[_tuningColors.Count % _tuningPalette.Length];
      _tuningColors[label] = c;
    }
    return c;
  }

  private void ResetTuningPlot() {
    _tuningBuffers.Clear();
    _tuningPlot?.ClearAll();
  }

  private void OnTuningFieldsChanged() => ResetTuningPlot();

  private void OnTuningSampled(double t,
      System.Collections.Generic.IReadOnlyDictionary<string, double> sample) {
    if (_tuningPlot == null) {
      return;
    }
    double cutoff = t - FlightDataViewModel.TuningWindowSeconds;
    foreach (var (label, value) in sample) {
      if (!_tuningBuffers.TryGetValue(label, out var buf)) {
        buf = (new List<double>(), new List<double>());
        _tuningBuffers[label] = buf;
      }
      buf.Xs.Add(t);
      buf.Ys.Add(value);

      while (buf.Xs.Count > 0 && buf.Xs[0] < cutoff) {
        buf.Xs.RemoveAt(0);
        buf.Ys.RemoveAt(0);
      }
      _tuningPlot.SetSeries(label, buf.Xs, buf.Ys, ColorFor(label));
    }
  }

  private async void OnTuningPickClick(object? sender, RoutedEventArgs e) {
    if (DataContext is not FlightDataViewModel vm
        || TopLevel.GetTopLevel(this) is not Window owner) {
      return;
    }
    var fields = vm.TuningFieldList();
    var panel = new WrapPanel { Orientation = Orientation.Vertical, MaxHeight = 520 };
    var boxes = new List<CheckBox>();
    foreach (var (name, desc) in fields) {
      var cb = new CheckBox {
        Content = desc,
        Tag = name,
        IsChecked = vm.IsTuningField(name),
        Width = 200,
        FontSize = 11,
      };
      boxes.Add(cb);
      panel.Children.Add(cb);
    }
    var ok = new Button { Content = "OK", HorizontalAlignment = HorizontalAlignment.Right };
    var dlg = new Window {
      Title = "Tuning — pick fields",
      Width = 680,
      Height = 600,
      WindowStartupLocation = WindowStartupLocation.CenterOwner,
      Content = new DockPanel {
        Margin = new Avalonia.Thickness(10),
        Children = {
          ok,
          new ScrollViewer {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = panel,
          },
        },
      },
    };
    DockPanel.SetDock(ok, Dock.Bottom);
    ok.Click += (_, _) => dlg.Close(true);
    if (await dlg.ShowDialog<bool>(owner)) {
      vm.SetTuningFields(boxes.Where(b => b.IsChecked == true).Select(b => (string)b.Tag!));
      vm.Tuning = true;
    }
  }

  private readonly Dictionary<string, Window> _indicatorWindows = [];

  private void OnHudIndicatorClicked(string which) {
    string key = which switch { "ekf" => "ekf", "vibe" => "vibe", _ => "prearm" };
    if (_indicatorWindows.TryGetValue(key, out var existing)) {
      existing.Activate();
      return;
    }

    Window win = key switch {
      "ekf" => new EKFStatusWindow(),
      "vibe" => new VibrationWindow(),
      _ => new PrearmStatusWindow(),
    };
    _indicatorWindows[key] = win;
    win.Closed += (_, _) => _indicatorWindows.Remove(key);
    if (TopLevel.GetTopLevel(this) is Window owner) {
      win.Show(owner);
    } else {
      win.Show();
    }
  }

  [Obsolete]
  private ContextMenu BuildMapMenu(MapView map) {
    FlightDataViewModel? Vm() => DataContext as FlightDataViewModel;
    MenuItem Item(string header, Func<FlightDataViewModel, Task> action) {
      var mi = new MenuItem { Header = header };
      mi.Click += async (_, _) => {
        var vm = Vm();
        if (vm != null) {
          await action(vm);
        }
      };
      return mi;
    }
    var menu = new ContextMenu();
    menu.Items.Add(Item("Fly To Here", vm => vm.FlyToHere(map.LastClickLatLng.Lat, map.LastClickLatLng.Lng)));
    menu.Items.Add(Item("Fly To Here Alt…", vm => vm.SetGuidedAltitude()));
    menu.Items.Add(Item("Fly To Coords", vm => vm.FlyToCoords()));
    menu.Items.Add(new Separator());
    menu.Items.Add(Item("Point Camera Here", vm => vm.PointCameraHere(map.LastClickLatLng.Lat, map.LastClickLatLng.Lng)));
    menu.Items.Add(Item("Point Camera Coords…", vm => vm.PointCameraCoords()));
    menu.Items.Add(Item("Trigger Camera NOW", vm => vm.TriggerCameraNow()));
    var cameraOverlap = new MenuItem {
      Header = "Camera Overlap on/off",
      ToggleType = MenuItemToggleType.CheckBox,
      IsChecked = map.CameraOverlapEnabled,
    };
    cameraOverlap.Click += (_, _) => map.CameraOverlapEnabled = cameraOverlap.IsChecked;
    menu.Items.Add(cameraOverlap);
    menu.Items.Add(new Separator());
    menu.Items.Add(Item("Add POI Here…", vm => vm.AddPoiHere(map.LastClickLatLng.Lat, map.LastClickLatLng.Lng)));
    menu.Items.Add(Item("Add POI at Coords…", vm => vm.AddPoiCoords()));
    menu.Items.Add(Item("Delete Nearest POI…", vm => vm.DeleteNearestPoi(map.LastClickLatLng.Lat, map.LastClickLatLng.Lng)));
    menu.Items.Add(Item("Clear All POIs…", vm => vm.ClearPois()));
    menu.Items.Add(Item("Load POIs…", vm => vm.LoadPois()));
    menu.Items.Add(Item("Save POIs…", vm => vm.SavePois()));
    var clearImportedOverlay = new MenuItem { Header = "Clear Imported Map Overlay" };
    clearImportedOverlay.Click += (_, _) => Services.ImportedOverlayStore.ClearFlightData();
    menu.Items.Add(clearImportedOverlay);
    menu.Items.Add(new Separator());
    menu.Items.Add(Item("Open Flight Planner", vm => vm.OpenFlightPlanner()));
    menu.Items.Add(new Separator());
    menu.Items.Add(Item("Set Home Here", vm => vm.SetHomeHere(map.LastClickLatLng.Lat, map.LastClickLatLng.Lng)));
    menu.Items.Add(Item("Set EKF Origin Here", vm => vm.SetEkfOriginHere(map.LastClickLatLng.Lat, map.LastClickLatLng.Lng)));
    menu.Items.Add(Item("TakeOff", vm => vm.TakeOffHere()));
    menu.Items.Add(Item("Jump To Tag", vm => vm.JumpToTag()));
    var gimbalVideo = new MenuItem { Header = "Gimbal Video" };
    gimbalVideo.Items.Add(Item("Full Sized", vm => {
      vm.RequestVideoPresentation(GimbalVideoPresentation.FullSized);
      return Task.CompletedTask;
    }));
    gimbalVideo.Items.Add(Item("Mini", vm => {
      vm.RequestVideoPresentation(GimbalVideoPresentation.Mini);
      return Task.CompletedTask;
    }));
    gimbalVideo.Items.Add(Item("Pop Out", vm => {
      vm.RequestVideoPresentation(GimbalVideoPresentation.PopOut);
      return Task.CompletedTask;
    }));
    menu.Items.Add(gimbalVideo);
    return menu;
  }

  private readonly ITemplate<Panel?>? _defaultTabPanel;

  private const string _hiddenTabsKey = "tabcontrolactions_avalonia_hidden";

  private static readonly Dictionary<string, string> _upstreamTabHeaders =
      new(StringComparer.OrdinalIgnoreCase) {
        ["tabQuick"] = "Quick",
        ["tabActions"] = "Actions",
        ["tabActionsSimple"] = "Simple Actions",
        ["tabPagemessages"] = "Messages",
        ["tabPagePreFlight"] = "PreFlight",
        ["tabGauges"] = "Gauges",
        ["tabStatus"] = "Status",
        ["tabServo"] = "Servo/Relay",
        ["tabScripts"] = "Scripts",
        ["tabPayload"] = "Payload Control",
        ["tabTLogs"] = "Telemetry Logs",
        ["tablogbrowse"] = "DataFlash Logs",
        ["tabTransponder"] = "Transponder",
        ["tabAuxFunction"] = "Aux Function",
      };

  private static IEnumerable<TabItem> TabItemsOf(TabControl tabs) => tabs.Items.OfType<TabItem>();

  private static string HeaderOf(TabItem ti) => ti.Header?.ToString() ?? "";

  private void ApplyTabSettings(TabControl tabs) {
    var items = TabItemsOf(tabs).ToList();
    var hidden = HiddenTabSet(items.Select(HeaderOf).ToList());
    var profile = Services.DisplayViewService.Current;
    foreach (var ti in items) {
      string header = HeaderOf(ti);
      ti.IsVisible = ProfileAllowsTab(header, profile) && !hidden.Contains(header);
    }
    if (!items.Any(item => item.IsVisible) && items.FirstOrDefault() is { } fallback) {
      // Avalonia otherwise leaves an empty tab host for a malformed all-false custom profile.
      fallback.IsVisible = true;
    }
    if (tabs.SelectedItem is not TabItem { IsVisible: true }
        && items.FirstOrDefault(item => item.IsVisible) is { } selected) {
      tabs.SelectedItem = selected;
    }
    if (TabMultiLineSetting()) {
      SetTabMultiLine(tabs, true);
    }
  }

  internal static bool ProfileAllowsTab(string header, DisplayView profile) => header switch {
    "Quick" => profile.displayQuickTab,
    "Actions" => profile.displayAdvActionsTab,
    "Simple Actions" => profile.displaySimpleActionsTab,
    "Messages" => profile.displayMessagesTab,
    "PreFlight" => profile.displayPreFlightTab,
    "Gauges" => profile.displayGaugesTab,
    "Status" => profile.displayStatusTab,
    "Servo/Relay" => profile.displayServoTab,
    "Scripts" => profile.displayScriptsTab,
    "Payload Control" => profile.displayPayloadTab,
    "Telemetry Logs" => profile.displayTelemetryTab,
    "DataFlash Logs" => profile.displayDataflashTab,
    "Transponder" => profile.displayTransponderTab,
    "Aux Function" => profile.displayAuxFunctionTab,
    _ => true,
  };

  private static HashSet<string> HiddenTabSet(IReadOnlyList<string> headers) {
    bool hasAvaloniaSetting = Settings.Instance.ContainsKey(_hiddenTabsKey);
    string? avaloniaSetting = hasAvaloniaSetting ? Settings.Instance[_hiddenTabsKey] : null;
    string? legacySetting = Settings.Instance.ContainsKey("tabcontrolactions")
        ? Settings.Instance["tabcontrolactions"]
        : null;
    var hidden = ResolveHiddenTabs(headers, avaloniaSetting, legacySetting);

    if (!hasAvaloniaSetting && legacySetting != null) {
      // Early Avalonia builds reused the upstream key but stored hidden display captions in it.
      // Keep a port-specific key going forward and repair the original key to its upstream
      // contract (ordered internal names of visible tabs), preserving settings interoperability.
      SaveTabSettings(headers, hidden);
    }
    return hidden;
  }

  internal static HashSet<string> ResolveHiddenTabs(
      IReadOnlyList<string> headers, string? avaloniaHidden, string? legacySetting) {
    var knownHeaders = new HashSet<string>(headers, StringComparer.OrdinalIgnoreCase);
    if (avaloniaHidden != null) {
      return ParseTabs(avaloniaHidden)
          .Where(knownHeaders.Contains)
          .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    var legacy = ParseTabs(legacySetting).ToList();
    bool upstreamVisibleFormat = legacy.Any(_upstreamTabHeaders.ContainsKey);
    if (!upstreamVisibleFormat) {
      // Compatibility with early Avalonia builds, where this value was a list of hidden headers.
      return legacy.Where(knownHeaders.Contains)
          .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    var visible = legacy
        .Select(name => _upstreamTabHeaders.TryGetValue(name, out var header) ? header : null)
        .Where(header => header != null)
        .Cast<string>()
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    return headers.Where(header => !visible.Contains(header))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
  }

  internal static string EncodeUpstreamVisibleTabs(
      IReadOnlyList<string> headers, IReadOnlySet<string> hidden) {
    var namesByHeader = _upstreamTabHeaders
        .ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.OrdinalIgnoreCase);
    return string.Join(";", headers
        .Where(header => !hidden.Contains(header) && namesByHeader.ContainsKey(header))
        .Select(header => namesByHeader[header]));
  }

  private static IEnumerable<string> ParseTabs(string? value) =>
      (value ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
      .Select(tab => tab.Trim())
      .Where(tab => tab.Length > 0);

  private static void SaveTabSettings(
      IReadOnlyList<string> headers, IReadOnlySet<string> hidden) {
    Settings.Instance[_hiddenTabsKey] = string.Join(";", headers.Where(hidden.Contains));
    Settings.Instance["tabcontrolactions"] = EncodeUpstreamVisibleTabs(headers, hidden);
  }

  private static bool TabMultiLineSetting() =>
      Settings.Instance.ContainsKey("tabcontrolmultiline")
      && bool.TryParse(Settings.Instance["tabcontrolmultiline"], out var b) && b;

  private void SetTabMultiLine(TabControl tabs, bool on) {
    tabs.ItemsPanel = on ? new FuncTemplate<Panel?>(() => new WrapPanel()) : _defaultTabPanel;
  }

  private ContextMenu BuildTabMenu(TabControl tabs) {
    var customize = new MenuItem { Header = "Customize" };
    customize.Click += async (_, _) => await CustomizeTabsAsync(tabs);
    var multiline = new MenuItem {
      Header = "MultiLine",
      ToggleType = MenuItemToggleType.CheckBox,
      IsChecked = TabMultiLineSetting(),
    };
    multiline.Click += (_, _) => {
      SetTabMultiLine(tabs, multiline.IsChecked);
      Settings.Instance["tabcontrolmultiline"] = multiline.IsChecked.ToString();
    };
    var menu = new ContextMenu();
    menu.Items.Add(customize);
    menu.Items.Add(multiline);
    return menu;
  }

  private async Task CustomizeTabsAsync(TabControl tabs) {
    if (TopLevel.GetTopLevel(this) is not Window owner) {
      return;
    }
    var panel = new StackPanel { Spacing = 2, Margin = new Avalonia.Thickness(4) };
    var map = new List<(TabItem Ti, CheckBox Cb)>();
    var items = TabItemsOf(tabs).ToList();
    var manuallyHidden = HiddenTabSet(items.Select(HeaderOf).ToList());
    var profile = Services.DisplayViewService.Current;
    foreach (var ti in items) {
      string header = HeaderOf(ti);
      bool profileAllows = ProfileAllowsTab(header, profile);
      var cb = new CheckBox {
        Content = header,
        IsChecked = !manuallyHidden.Contains(header),
        IsEnabled = profileAllows,
      };
      if (!profileAllows) {
        ToolTip.SetTip(cb, $"Hidden by the {profile.displayName} layout profile");
      }
      map.Add((ti, cb));
      panel.Children.Add(cb);
    }
    var ok = new Button { Content = "OK", HorizontalAlignment = HorizontalAlignment.Right };
    var dlg = new Window {
      Title = "Customize Tabs",
      Width = 280,
      Height = 480,
      WindowStartupLocation = WindowStartupLocation.CenterOwner,
      Content = new DockPanel {
        Margin = new Avalonia.Thickness(10),
        Children = { ok, new ScrollViewer { Content = panel } },
      },
    };
    DockPanel.SetDock(ok, Dock.Bottom);
    ok.Click += (_, _) => dlg.Close(true);
    if (!await dlg.ShowDialog<bool>(owner)) {
      return;
    }
    var hidden = new List<string>();
    foreach (var (ti, cb) in map) {
      bool vis = cb.IsChecked == true;
      if (!vis) {
        hidden.Add(HeaderOf(ti));
      }
    }
    var headers = map.Select(item => HeaderOf(item.Ti)).ToList();
    SaveTabSettings(headers, hidden.ToHashSet(StringComparer.OrdinalIgnoreCase));
    ApplyTabSettings(tabs);
  }
}

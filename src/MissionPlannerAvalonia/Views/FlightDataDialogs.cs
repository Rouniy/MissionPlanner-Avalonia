using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MissionPlannerAvalonia.Controls;
using MissionPlannerAvalonia.ViewModels;

namespace MissionPlannerAvalonia.Views;

public class RawSensorWindow : Window {
  private readonly DispatcherTimer _timer;
  private readonly TextBlock _text = new() {
    FontFamily = new FontFamily("Consolas,Menlo,monospace"),
    FontSize = 13,
    Foreground = Brushes.WhiteSmoke,
    Margin = new Avalonia.Thickness(12),
  };

  public RawSensorWindow() {
    Title = "Raw Sensor View";
    Width = 320;
    Height = 360;
    Background = new SolidColorBrush(Color.Parse("#1F1F20"));
    WindowStartupLocation = WindowStartupLocation.CenterOwner;
    Content = new ScrollViewer { Content = _text };
    _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
    _timer.Tick += (_, _) => Refresh();
    _timer.Start();
    Closed += (_, _) => _timer.Stop();
    Refresh();
  }

  private void Refresh() {
    var cs = AppState.comPort.MAV?.cs;
    if (cs == null) {
      _text.Text = "No vehicle state.";
      return;
    }
    _text.Text =
        $"Accel (mg)\n  X {cs.ax,10:0.0}\n  Y {cs.ay,10:0.0}\n  Z {cs.az,10:0.0}\n\n" +
        $"Gyro (deg/s)\n  X {cs.gx,10:0.000}\n  Y {cs.gy,10:0.000}\n  Z {cs.gz,10:0.000}\n\n" +
        $"Mag\n  X {cs.mx,10:0.0}\n  Y {cs.my,10:0.0}\n  Z {cs.mz,10:0.0}\n\n" +
        $"Baro\n  Press {cs.press_abs,8:0.00} Pa\n  Temp  {cs.press_temp,8} °C\n  Alt   {cs.alt,8:0.00} m";
  }
}

public class MessagesWindow : Window {
  public MessagesWindow() {
    Title = "Messages";
    Width = 520;
    Height = 360;
    Background = new SolidColorBrush(Color.Parse("#1F1F20"));
    WindowStartupLocation = WindowStartupLocation.CenterOwner;
    var box = new TextBox {
      AcceptsReturn = true,
      IsReadOnly = true,
      TextWrapping = TextWrapping.Wrap,
      Background = new SolidColorBrush(Color.Parse("#1F1F20")),
      Foreground = Brushes.WhiteSmoke,
    };
    var cs = AppState.comPort.MAV?.cs;
    try {
      var msgs = cs?.messages?.ToArray() ?? Array.Empty<(DateTime, string)>();
      box.Text = msgs.Length > 0
          ? string.Join(Environment.NewLine,
              msgs.TakeLast(200).Select(m => $"{m.time:HH:mm:ss}  {m.message?.TrimEnd()}"))
          : "No messages received.";
    } catch (InvalidOperationException) {
      box.Text = "No messages received.";
    }
    Content = box;
  }
}

internal enum GimbalVideoPresentation {
  FullSized,
  Mini,
  PopOut,
}

public class VideoPopupWindow : Window {
  public VideoControl Video { get; }
  private readonly GimbalVideoOverlay _overlay;
  private readonly TextBlock _status = new() {
    Foreground = Brushes.WhiteSmoke,
    Margin = new Avalonia.Thickness(6),
    VerticalAlignment = VerticalAlignment.Center,
  };

  public VideoPopupWindow(VideoControl video, FlightDataViewModel viewModel) {
    Video = video;
    DataContext = viewModel;
    Title = "MAVLink Camera / Gimbal Video";
    Width = 900;
    Height = 620;
    Background = Brushes.Black;
    WindowStartupLocation = WindowStartupLocation.CenterOwner;
    var grid = new Grid();
    grid.RowDefinitions = new RowDefinitions("*,Auto,Auto,Auto");

    _overlay = new GimbalVideoOverlay(
        () => Video.VideoAspectRatio,
        viewModel.CurrentGimbalTrackingOverlay);
    _overlay.CommandRequested += command =>
        _ = viewModel.HandleGimbalVideoPointerCommand(command);
    _overlay.KeyStateChanged += viewModel.HandleGimbalVideoKeyState;
    _overlay.InputReleased += viewModel.ReleaseGimbalVideoInput;
    Video.OverlayContent = _overlay;

    Grid.SetRow(video, 0);
    grid.Children.Add(video);

    var commands = new WrapPanel {
      Margin = new Avalonia.Thickness(6, 5, 6, 2),
      HorizontalAlignment = HorizontalAlignment.Center,
    };
    commands.Children.Add(CommandButton("Photo (Alt+F)", viewModel.TakeMavlinkPhotoCommand));
    commands.Children.Add(CommandButton("Camera REC (Alt+R)", viewModel.StartMavlinkCameraRecordingCommand));
    commands.Children.Add(CommandButton("Camera STOP", viewModel.StopMavlinkCameraRecordingCommand));
    commands.Children.Add(CommandButton("Lock / Follow (L)", viewModel.ToggleGimbalYawLockCommand));
    commands.Children.Add(CommandButton("Retract", viewModel.GimbalRetractCommand));
    commands.Children.Add(CommandButton("Neutral (N)", viewModel.GimbalNeutralCommand));
    commands.Children.Add(CommandButton("Point down", viewModel.GimbalPointDownCommand));
    commands.Children.Add(CommandButton("Home (H)", viewModel.GimbalHomeCommand));
    Grid.SetRow(commands, 1);
    grid.Children.Add(commands);

    var settings = new WrapPanel {
      Margin = new Avalonia.Thickness(8, 2, 8, 3),
      HorizontalAlignment = HorizontalAlignment.Center,
      VerticalAlignment = VerticalAlignment.Center,
    };
    settings.Children.Add(NumberSetting(
        "Gimbal ID", 0, 6, 1, "0",
        () => viewModel.GimbalDeviceId,
        value => viewModel.GimbalDeviceId = (int)value));
    settings.Children.Add(NumberSetting(
        "Slow", 0.1m, 360, 0.5m, "0.0",
        () => viewModel.GimbalSlewSlow,
        value => viewModel.GimbalSlewSlow = value));
    settings.Children.Add(NumberSetting(
        "Normal", 0.1m, 360, 0.5m, "0.0",
        () => viewModel.GimbalSlewNormal,
        value => viewModel.GimbalSlewNormal = value));
    settings.Children.Add(NumberSetting(
        "Fast", 0.1m, 360, 1, "0.0",
        () => viewModel.GimbalSlewFast,
        value => viewModel.GimbalSlewFast = value));
    settings.Children.Add(NumberSetting(
        "Zoom", 0.01m, 1, 0.05m, "0.00",
        () => viewModel.GimbalZoomSpeed,
        value => viewModel.GimbalZoomSpeed = value));
    settings.Children.Add(NumberSetting(
        "HFOV", 0.01m, 180, 1, "0.0",
        () => viewModel.GimbalCameraHorizontalFov,
        value => viewModel.GimbalCameraHorizontalFov = value));
    settings.Children.Add(NumberSetting(
        "VFOV", 0.01m, 180, 1, "0.0",
        () => viewModel.GimbalCameraVerticalFov,
        value => viewModel.GimbalCameraVerticalFov = value));
    var reportedFov = new CheckBox {
      Content = "Use reported FOV",
      IsChecked = viewModel.GimbalUseReportedFov,
      Foreground = Brushes.WhiteSmoke,
      Margin = new Avalonia.Thickness(7, 3),
      VerticalAlignment = VerticalAlignment.Center,
    };
    reportedFov.IsCheckedChanged += (_, _) =>
        viewModel.GimbalUseReportedFov = reportedFov.IsChecked == true;
    settings.Children.Add(reportedFov);
    Grid.SetRow(settings, 2);
    grid.Children.Add(settings);

    var combinedStatus = new StackPanel {
      Orientation = Orientation.Vertical,
      Margin = new Avalonia.Thickness(6, 0, 6, 4),
    };
    var interactionStatus = new TextBlock {
      Foreground = new SolidColorBrush(Color.Parse("#FFB74D")),
      TextWrapping = TextWrapping.Wrap,
    };
    interactionStatus.Bind(
        TextBlock.TextProperty,
        new Binding(nameof(FlightDataViewModel.GimbalVideoStatus)));
    combinedStatus.Children.Add(interactionStatus);
    combinedStatus.Children.Add(_status);
    Grid.SetRow(combinedStatus, 3);
    grid.Children.Add(combinedStatus);
    Content = grid;
    Video.StatusChanged += OnVideoStatusChanged;
    Closed += (_, _) => {
      Video.StatusChanged -= OnVideoStatusChanged;
      Video.OverlayContent = null;
      _overlay.Dispose();
      Video.Stop();
    };
    UpdateStatus();
  }

  private void OnVideoStatusChanged(object? sender, EventArgs e) => UpdateStatus();

  public void UpdateStatus() => _status.Text = Video.Status;

  private static Button CommandButton(string text, System.Windows.Input.ICommand command) =>
      new() {
        Content = text,
        Command = command,
        Margin = new Avalonia.Thickness(3),
        Padding = new Avalonia.Thickness(8, 4),
      };

  private static StackPanel NumberSetting(
      string label,
      decimal minimum,
      decimal maximum,
      decimal increment,
      string format,
      Func<double> get,
      Action<double> set) {
    var number = new NumericUpDown {
      Minimum = minimum,
      Maximum = maximum,
      Increment = increment,
      FormatString = format,
      Value = (decimal)get(),
      Width = 72,
    };
    number.ValueChanged += (_, _) => {
      if (number.Value is { } value) {
        set((double)value);
      }
    };
    return new StackPanel {
      Orientation = Orientation.Horizontal,
      Margin = new Avalonia.Thickness(5, 2),
      Spacing = 4,
      VerticalAlignment = VerticalAlignment.Center,
      Children = {
        new TextBlock {
          Text = label,
          Foreground = Brushes.WhiteSmoke,
          VerticalAlignment = VerticalAlignment.Center,
        },
        number,
      },
    };
  }
}

public class EKFStatusWindow : Window {
  private readonly DispatcherTimer _timer;
  private readonly (string label, ProgressBar bar)[] _rows;
  private readonly TextBlock _flags = new() {
    Foreground = Brushes.WhiteSmoke,
    FontSize = 11,
    Margin = new Avalonia.Thickness(0, 8, 0, 0),
  };

  public EKFStatusWindow() {
    Title = "EKF Status";
    Width = 320;
    Height = 320;
    Background = new SolidColorBrush(Color.Parse("#1F1F20"));
    WindowStartupLocation = WindowStartupLocation.CenterOwner;
    _rows = new[] { "Velocity", "Pos Horiz", "Pos Vert", "Compass", "Terrain" }
        .Select(l => (l, new ProgressBar { Maximum = 100, Height = 16 })).ToArray();
    var panel = new StackPanel { Margin = new Avalonia.Thickness(12), Spacing = 6 };
    foreach (var (label, bar) in _rows) {
      panel.Children.Add(new TextBlock { Text = label, Foreground = Brushes.WhiteSmoke, FontSize = 12 });
      panel.Children.Add(bar);
    }
    panel.Children.Add(_flags);
    Content = new ScrollViewer { Content = panel };
    _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
    _timer.Tick += (_, _) => Refresh();
    _timer.Start();
    Closed += (_, _) => _timer.Stop();
    Refresh();
  }

  private void Refresh() {
    var cs = AppState.comPort.MAV?.cs;
    if (cs == null) {
      return;
    }
    var vals = new[] { cs.ekfvelv, cs.ekfposhor, cs.ekfposvert, cs.ekfcompv, cs.ekfteralt };
    for (int i = 0; i < _rows.Length; i++) {
      var v = (int)(vals[i] * 100);
      _rows[i].bar.Value = v;
      _rows[i].bar.Foreground = v > 80 ? Brushes.Red : v > 50 ? Brushes.Orange : Brushes.LimeGreen;
    }
    _flags.Text = "Flags: 0x" + cs.ekfflags.ToString("X");
  }
}

public class VibrationWindow : Window {
  private readonly DispatcherTimer _timer;
  private readonly (string label, ProgressBar bar)[] _rows;
  private readonly TextBlock _clips = new() { Foreground = Brushes.WhiteSmoke, FontSize = 12 };

  public VibrationWindow() {
    Title = "Vibration";
    Width = 300;
    Height = 240;
    Background = new SolidColorBrush(Color.Parse("#1F1F20"));
    WindowStartupLocation = WindowStartupLocation.CenterOwner;
    _rows = new[] { "Vibe X", "Vibe Y", "Vibe Z" }
        .Select(l => (l, new ProgressBar { Maximum = 100, Height = 16 })).ToArray();
    var panel = new StackPanel { Margin = new Avalonia.Thickness(12), Spacing = 6 };
    foreach (var (label, bar) in _rows) {
      panel.Children.Add(new TextBlock { Text = label, Foreground = Brushes.WhiteSmoke, FontSize = 12 });
      panel.Children.Add(bar);
    }
    panel.Children.Add(_clips);
    Content = panel;
    _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
    _timer.Tick += (_, _) => Refresh();
    _timer.Start();
    Closed += (_, _) => _timer.Stop();
    Refresh();
  }

  private void Refresh() {
    var cs = AppState.comPort.MAV?.cs;
    if (cs == null) {
      return;
    }
    var vals = new[] { cs.vibex, cs.vibey, cs.vibez };
    for (int i = 0; i < _rows.Length; i++) {
      var v = (int)vals[i];
      _rows[i].bar.Value = v;
      _rows[i].bar.Foreground = v > 60 ? Brushes.Red : v > 30 ? Brushes.Orange : Brushes.LimeGreen;
    }
    _clips.Text = $"Clipping: {cs.vibeclip0} / {cs.vibeclip1} / {cs.vibeclip2}";
  }
}

public class PrearmStatusWindow : Window {
  private static readonly Color _okColor = Color.Parse("#3FB950");
  private static readonly Color _failColor = Color.Parse("#F85149");
  private static readonly Color _mutedColor = Color.Parse("#6E7681");

  private readonly DispatcherTimer _timer;
  private readonly Border _banner;
  private readonly TextBlock _icon = new() { FontSize = 22, FontWeight = FontWeight.Bold };
  private readonly TextBlock _state = new() { FontSize = 20, FontWeight = FontWeight.SemiBold };
  private readonly TextBlock _summary =
      new() { Foreground = new SolidColorBrush(Color.Parse("#9AA0A6")), FontSize = 12 };
  private readonly ItemsControl _checks = new() { Margin = new Avalonia.Thickness(0, 12, 0, 0) };
  private readonly TextBlock _empty = new() {
    FontSize = 13,
    Foreground = new SolidColorBrush(_mutedColor),
    HorizontalAlignment = HorizontalAlignment.Center,
    Margin = new Avalonia.Thickness(0, 40, 0, 0),
    TextWrapping = TextWrapping.Wrap,
    TextAlignment = TextAlignment.Center,
  };

  public PrearmStatusWindow() {
    Title = "Arming Checks";
    Width = 480;
    Height = 380;
    MinWidth = 360;
    MinHeight = 240;
    Background = new SolidColorBrush(Color.Parse("#1B1B1D"));
    WindowStartupLocation = WindowStartupLocation.CenterOwner;

    var headerText = new StackPanel {
      Spacing = 1,
      VerticalAlignment = VerticalAlignment.Center,
      Children = { _state, _summary },
    };
    _banner = new Border {
      CornerRadius = new Avalonia.CornerRadius(6),
      BorderThickness = new Avalonia.Thickness(1),
      Padding = new Avalonia.Thickness(14, 12),
      Child = new StackPanel {
        Orientation = Orientation.Horizontal,
        Spacing = 12,
        Children = { _icon, headerText },
      },
    };

    var body = new Panel { Children = { _empty, _checks } };
    var list = new ScrollViewer {
      Content = body,
      VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
      Margin = new Avalonia.Thickness(0, 12, 0, 0),
    };

    var root = new DockPanel { Margin = new Avalonia.Thickness(16) };
    DockPanel.SetDock(_banner, Avalonia.Controls.Dock.Top);
    root.Children.Add(_banner);
    root.Children.Add(list);
    Content = root;

    _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
    _timer.Tick += (_, _) => Refresh();
    _timer.Start();
    Closed += (_, _) => _timer.Stop();
    Refresh();
  }

  private void SetBanner(Color accent, string glyph, string state, string summary) {
    _banner.Background = new SolidColorBrush(accent, 0.12);
    _banner.BorderBrush = new SolidColorBrush(accent, 0.55);
    var accentBrush = new SolidColorBrush(accent);
    _icon.Text = glyph;
    _icon.Foreground = accentBrush;
    _state.Text = state;
    _state.Foreground = accentBrush;
    _summary.Text = summary;
  }

  private static Border MakeCheckCard(string text) => new() {
    Background = new SolidColorBrush(Color.Parse("#242427")),
    BorderBrush = new SolidColorBrush(_failColor),
    BorderThickness = new Avalonia.Thickness(3, 0, 0, 0),
    CornerRadius = new Avalonia.CornerRadius(3),
    Padding = new Avalonia.Thickness(11, 8),
    Margin = new Avalonia.Thickness(0, 0, 0, 8),
    Child = new TextBlock {
      Text = text,
      Foreground = new SolidColorBrush(Color.Parse("#EDEDED")),
      TextWrapping = TextWrapping.Wrap,
      FontSize = 13,
    },
  };

  private void Refresh() {
    var cs = AppState.comPort.MAV?.cs;
    if (cs == null) {
      SetBanner(_mutedColor, "–", "No Vehicle", "Not connected.");
      _checks.ItemsSource = null;
      _empty.Text = "Connect a vehicle to see arming checks.";
      _empty.IsVisible = true;
      return;
    }

    bool ok = cs.prearmstatus;

    var failures = new List<Control>();

    (DateTime time, string message)[] msgs;
    try {
      msgs = cs.messages?.ToArray() ?? Array.Empty<(DateTime, string)>();
    } catch (InvalidOperationException) {
      return;
    }

    var seen = new HashSet<string>();
    foreach (var m in msgs.Reverse()) {
      var text = m.message?.Trim();
      if (string.IsNullOrEmpty(text)) {
        continue;
      }

      if (text.IndexOf("arm", StringComparison.OrdinalIgnoreCase) < 0 ||
          text.IndexOf("disarm", StringComparison.OrdinalIgnoreCase) >= 0) {
        continue;
      }

      if (seen.Add(text)) {
        failures.Add(MakeCheckCard(text));
      }
    }

    if (ok) {
      SetBanner(_okColor, "✓", "Ready to Arm", "All prearm checks passing.");
    } else {
      SetBanner(_failColor, "⚠", "Not Ready to Arm",
          failures.Count > 0
              ? $"{failures.Count} failing check{(failures.Count == 1 ? "" : "s")}."
              : "Waiting for check results.");
    }

    if (failures.Count == 0) {
      _checks.ItemsSource = null;
      _empty.Text = ok ? "No blocking checks. Vehicle is ready." : "No prearm messages received yet.";
      _empty.IsVisible = true;
    } else {
      _empty.IsVisible = false;
      _checks.ItemsSource = failures;
    }
  }
}

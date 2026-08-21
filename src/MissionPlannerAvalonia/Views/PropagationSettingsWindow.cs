using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using MissionPlannerAvalonia.Services;

namespace MissionPlannerAvalonia.Views;

public sealed class PropagationSettingsWindow : Window {
  private readonly NumericUpDown _clearance;
  private readonly ComboBox _resolution;
  private readonly ComboBox _azimuthStep;
  private readonly ComboBox _convergence;
  private readonly NumericUpDown _range;
  private readonly NumericUpDown _baseHeight;
  private readonly NumericUpDown _tolerance;
  private readonly NumericUpDown _minimumAltitude;
  private readonly NumericUpDown _maximumAltitude;
  private readonly CheckBox _elevation;
  private readonly CheckBox _terrain;
  private readonly CheckBox _rf;
  private readonly CheckBox _homeDistance;
  private readonly CheckBox _droneDistance;
  private readonly CheckBox _manualAltitude;
  private readonly CheckBox _showScale;
  private bool _loading = true;

  public PropagationSettingsWindow() {
    Title = "RF Propagation Settings";
    Width = 480;
    Height = 700;
    MinWidth = 420;
    MinHeight = 500;
    WindowStartupLocation = WindowStartupLocation.CenterOwner;

    PropagationOptions options = PropagationOptions.Load();
    _elevation = Check("Elevation", options.ElevationMap);
    _terrain = Check("Terrain", options.TerrainMap);
    _rf = Check("RF Map", options.RfMap);
    _droneDistance = Check("Drone Dist Left", options.DroneDistance);
    _homeDistance = Check("Home Dist Left", options.HomeDistance);
    _showScale = Check("Show Scale", options.ShowScale);
    _manualAltitude = Check("Altitude Filter", options.ManualAltitudeRange);
    _clearance = Number(options.ClearanceMeters, 0, 2000, 1);
    _resolution = Choice(new[] { "2", "4", "6", "8", "10" },
        options.ResolutionPixels.ToString(CultureInfo.InvariantCulture));
    _azimuthStep = Choice(new[] { "0.5", "1", "2", "5", "10" },
        Format(options.AzimuthStepDegrees));
    _convergence = Choice(new[] { "0", "1", "5", "10", "15" },
        Format(options.ConvergenceDegrees));
    _range = Number(options.RangeKilometers, 0, 2000, 1);
    _baseHeight = Number(options.BaseHeightMeters, 0, 2000, 1);
    _tolerance = Number(options.Tolerance, 0, 1, 2);
    _minimumAltitude = Number(options.MinimumAltitude, 0, 2000, 1);
    _maximumAltitude = Number(options.MaximumAltitude, 0, 2000, 1);

    var modes = new WrapPanel {
      Orientation = Orientation.Horizontal,
      ItemWidth = 185,
      Children = {
        _elevation,
        _terrain,
        _rf,
        _droneDistance,
        _homeDistance,
        _showScale,
        _manualAltitude,
      },
    };
    var parameters = new Grid {
      ColumnDefinitions = new ColumnDefinitions("*,150"),
      RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto"),
      ColumnSpacing = 12,
      RowSpacing = 8,
    };
    AddRow(parameters, 0, "Clearance [m]", _clearance);
    AddRow(parameters, 1, "Resolution", _resolution);
    AddRow(parameters, 2, "Azimuth Step", _azimuthStep);
    AddRow(parameters, 3, "Convergance", _convergence);
    AddRow(parameters, 4, "Range [Km]", _range);
    AddRow(parameters, 5, "Base Height [m]", _baseHeight);
    AddRow(parameters, 6, "Tolerance 0..1", _tolerance);
    AddRow(parameters, 7, "Min Alt [m]", _minimumAltitude);
    AddRow(parameters, 8, "Max Alt [m]", _maximumAltitude);

    var close = new Button {
      Content = "Close",
      HorizontalAlignment = HorizontalAlignment.Right,
      MinWidth = 90,
    };
    close.Click += (_, _) => Close();
    var content = new StackPanel {
      Margin = new Thickness(16),
      Spacing = 14,
      Children = {
        new TextBlock {
          Text = "Propagation overlays are displayed on Flight Data and Flight Planner maps.",
          TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        },
        modes,
        new Separator(),
        parameters,
        new TextBlock {
          Text = "Elevation takes precedence when both Elevation and Terrain are enabled. " +
                 "Missing SRTM areas are left transparent and retried in the background.",
          Opacity = 0.75,
          TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        },
        close,
      },
    };
    Content = new ScrollViewer { Content = content };
    WireChanges();
    _loading = false;
  }

  public static void OpenWindow() {
    var window = new PropagationSettingsWindow();
    var owner = Services.Dialogs.Owner;
    if (owner != null) {
      window.Show(owner);
    } else {
      window.Show();
    }
  }

  private void WireChanges() {
    foreach (CheckBox checkBox in new[] {
        _elevation, _terrain, _rf, _droneDistance, _homeDistance, _showScale,
        _manualAltitude,
    }) {
      checkBox.IsCheckedChanged += (_, _) => Save();
    }
    foreach (NumericUpDown number in new[] {
        _clearance, _range, _baseHeight, _tolerance, _minimumAltitude, _maximumAltitude,
    }) {
      number.ValueChanged += (_, _) => Save();
    }
    foreach (ComboBox choice in new[] { _resolution, _azimuthStep, _convergence }) {
      choice.SelectionChanged += (_, _) => Save();
    }
  }

  private void Save() {
    if (_loading) {
      return;
    }
    (PropagationOptions.Load() with {
      ClearanceMeters = Value(_clearance),
      ResolutionPixels = (int)ChoiceValue(_resolution, 4),
      AzimuthStepDegrees = ChoiceValue(_azimuthStep, 1),
      ConvergenceDegrees = ChoiceValue(_convergence, 1),
      RangeKilometers = Value(_range),
      BaseHeightMeters = Value(_baseHeight),
      Tolerance = Value(_tolerance),
      MinimumAltitude = Value(_minimumAltitude),
      MaximumAltitude = Value(_maximumAltitude),
      ElevationMap = _elevation.IsChecked == true,
      TerrainMap = _terrain.IsChecked == true,
      RfMap = _rf.IsChecked == true,
      HomeDistance = _homeDistance.IsChecked == true,
      DroneDistance = _droneDistance.IsChecked == true,
      ManualAltitudeRange = _manualAltitude.IsChecked == true,
      ShowScale = _showScale.IsChecked == true,
    }).Save();
  }

  private static CheckBox Check(string text, bool value) => new() {
    Content = text,
    IsChecked = value,
    Margin = new Thickness(0, 3),
  };

  private static NumericUpDown Number(double value, double minimum, double maximum,
      int decimalPlaces) => new() {
        Minimum = (decimal)minimum,
        Maximum = (decimal)maximum,
        Value = (decimal)Math.Clamp(value, minimum, maximum),
        FormatString = decimalPlaces switch { 0 => "0", 1 => "0.0", _ => "0.00" },
        Increment = decimalPlaces == 0 ? 1 : 0.1m,
        HorizontalAlignment = HorizontalAlignment.Stretch,
      };

  private static ComboBox Choice(string[] items, string selected) {
    var result = new ComboBox {
      ItemsSource = items,
      HorizontalAlignment = HorizontalAlignment.Stretch,
    };
    result.SelectedItem = Array.Find(items, item => item == selected) ?? items[0];
    return result;
  }

  private static void AddRow(Grid grid, int row, string label, Control control) {
    var text = new TextBlock {
      Text = label,
      VerticalAlignment = VerticalAlignment.Center,
    };
    Grid.SetRow(text, row);
    Grid.SetColumn(text, 0);
    grid.Children.Add(text);
    Grid.SetRow(control, row);
    Grid.SetColumn(control, 1);
    grid.Children.Add(control);
  }

  private static double Value(NumericUpDown number) => (double)(number.Value ?? 0);

  private static double ChoiceValue(ComboBox comboBox, double fallback) =>
      double.TryParse(comboBox.SelectedItem as string, NumberStyles.Float,
          CultureInfo.InvariantCulture, out double value)
          ? value
          : fallback;

  private static string Format(double value) =>
      value.ToString("0.###", CultureInfo.InvariantCulture);
}

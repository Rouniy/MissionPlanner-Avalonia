using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using MissionPlanner.Utilities;
using MissionPlannerAvalonia.Theme.Palettes;

namespace MissionPlannerAvalonia.Services;

public static class ThemeService {
  public static readonly string[] Names = { "Classic", "Emerald", "Lime Refined", "Deep Forest", "Custom" };

  public static readonly (string Key, string Label, string Fallback)[] EditableColors = {
    ("MpAccentColor", "Accent", "#94C11F"),
    ("MpAccent2Color", "Secondary accent", "#A6D62B"),
    ("MpControlBgColor", "Control background", "#434445"),
    ("MpBgColor", "Window background", "#262728"),
    ("MpPanelBgColor", "Panel background", "#3A3B3C"),
    ("MpInputBgColor", "Input background", "#2D2D2D"),
    ("MpBorderColor", "Borders", "#1F1F20"),
    ("MpBgDeepColor", "Deep background", "#1A1A1B"),
    ("MpButTopColor", "Button gradient top", "#94C11F"),
    ("MpButBotColor", "Button gradient bottom", "#CDE296"),
    ("MpButTextColor", "Button text", "#405704"),
    ("MpBanner1Color", "Banner gradient start", "#405704"),
    ("MpBanner2Color", "Banner gradient end", "#94C11F"),
    ("MpTextColor", "Text", "#FFFFFF"),
  };

  private static ResourceDictionary? _current;

  public static string Current { get; private set; } = "Classic";

  private static ResourceDictionary PaletteFor(string name) => name switch {
    "Emerald" => new EmeraldPalette(),
    "Lime Refined" => new LimeRefinedPalette(),
    "Deep Forest" => new DeepForestPalette(),
    "Custom" => BuildCustomPalette(),
    _ => new ClassicPalette(),
  };

  public static void Apply(string name) {
    var app = Application.Current;
    if (app is null) {
      return;
    }

    if (!Names.Contains(name)) {
      name = "Classic";
    }

    var md = app.Resources.MergedDictionaries;
    if (_current != null) {
      md.Remove(_current);
    }

    _current = PaletteFor(name);
    md.Add(_current);
    Current = name;
    Settings.Instance["colortheme"] = name;
  }

  public static void ApplySaved() {
    var saved = Settings.Instance["colortheme"];
    Apply(string.IsNullOrWhiteSpace(saved) ? "Emerald" : saved);
  }

  public static System.Collections.Generic.IReadOnlyDictionary<string, string> EditableValues() {
    var result = new System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var (key, _, fallback) in EditableColors) {
      string? saved = Settings.Instance["theme_custom_" + key];
      if (!string.IsNullOrWhiteSpace(saved) && Color.TryParse(saved, out var savedColor)) {
        result[key] = savedColor.ToString();
      } else if (_current != null && _current.TryGetValue(key, out object? value)
                 && value is Color currentColor) {
        result[key] = currentColor.ToString();
      } else {
        result[key] = fallback;
      }
    }
    return result;
  }

  public static bool SaveCustom(
      System.Collections.Generic.IReadOnlyDictionary<string, string> values, out string error) {
    var parsed = new System.Collections.Generic.Dictionary<string, Color>(StringComparer.Ordinal);
    foreach (var (key, label, _) in EditableColors) {
      if (!values.TryGetValue(key, out string? text) || !Color.TryParse(text, out var color)) {
        error = $"{label}: enter a color such as #34D399 or #FF34D399.";
        return false;
      }
      parsed[key] = color;
    }
    foreach (var pair in parsed) {
      Settings.Instance["theme_custom_" + pair.Key] = pair.Value.ToString();
    }
    error = "";
    Apply("Custom");
    return true;
  }

  private static ResourceDictionary BuildCustomPalette() {
    var values = EditableColors.ToDictionary(
        item => item.Key,
        item => Color.TryParse(Settings.Instance["theme_custom_" + item.Key], out var saved)
            ? saved
            : Color.Parse(item.Fallback),
        StringComparer.Ordinal);
    var result = new ResourceDictionary();
    foreach (var pair in values) {
      result[pair.Key] = pair.Value;
    }
    result["MpAccentBrush"] = new SolidColorBrush(values["MpAccentColor"]);
    result["MpAccent2Brush"] = new SolidColorBrush(values["MpAccent2Color"]);
    result["MpControlBg"] = new SolidColorBrush(values["MpControlBgColor"]);
    result["MpBg"] = new SolidColorBrush(values["MpBgColor"]);
    result["MpPanelBg"] = new SolidColorBrush(values["MpPanelBgColor"]);
    result["MpInputBg"] = new SolidColorBrush(values["MpInputBgColor"]);
    result["MpBorderBrush"] = new SolidColorBrush(values["MpBorderColor"]);
    result["MpBgDeepBrush"] = new SolidColorBrush(values["MpBgDeepColor"]);
    result["MpButTextBrush"] = new SolidColorBrush(values["MpButTextColor"]);
    result["MpButBotBrush"] = new SolidColorBrush(values["MpButBotColor"]);
    result["MpText"] = new SolidColorBrush(values["MpTextColor"]);
    result["MpButtonBrush"] = Gradient(
        values["MpButTopColor"], values["MpButBotColor"], horizontal: false);
    result["MpBannerBrush"] = Gradient(
        values["MpBanner1Color"], values["MpBanner2Color"], horizontal: true);
    return result;
  }

  private static LinearGradientBrush Gradient(Color first, Color second, bool horizontal) => new() {
    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
    EndPoint = new RelativePoint(horizontal ? 1 : 0, horizontal ? 0 : 1, RelativeUnit.Relative),
    GradientStops = new GradientStops {
      new GradientStop(first, 0),
      new GradientStop(second, 1),
    },
  };
}

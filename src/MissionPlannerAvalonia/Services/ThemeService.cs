using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using MissionPlanner.Utilities;
using MissionPlannerAvalonia.Theme.Palettes;

namespace MissionPlannerAvalonia.Services;

public static class ThemeService {
  public static readonly string[] Names = { "Classic", "Emerald", "Lime Refined", "Deep Forest" };

  private static ResourceDictionary? _current;

  public static string Current { get; private set; } = "Classic";

  private static ResourceDictionary PaletteFor(string name) => name switch {
    "Emerald" => new EmeraldPalette(),
    "Lime Refined" => new LimeRefinedPalette(),
    "Deep Forest" => new DeepForestPalette(),
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
}

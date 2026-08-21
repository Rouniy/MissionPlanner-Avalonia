using System;
using System.IO;
using MissionPlanner.Utilities;

namespace MissionPlannerAvalonia.Services;

/// <summary>
/// Owns the upstream-compatible DisplayView profile used by the Avalonia shell.
/// </summary>
public static class DisplayViewService {
  private static readonly object _gate = new();
  private static DisplayView? _current;

  public static event EventHandler? Changed;

  public static DisplayView Current {
    get {
      Initialize();
      return _current!;
    }
  }

  public static void Initialize() {
    lock (_gate) {
      if (_current != null) {
        return;
      }

      AppPaths.Initialize();
      var settings = Settings.Instance;
      bool hadStoredProfile = settings.ContainsKey("displayview");
      string? storedProfile = hadStoredProfile ? settings["displayview"] : null;
      bool legacyAdvanced = settings.ContainsKey("advancedview")
          && settings.GetBoolean("advancedview");

      DisplayView profile = ResolveStartupProfile(
          storedProfile, hadStoredProfile, legacyAdvanced, out string? canonical);
      if (settings.ContainsKey("advancedview")) {
        settings.Remove("advancedview");
      }
      if (canonical != null) {
        settings["displayview"] = canonical;
      }

      _current = profile;
    }
  }

  public static void SetPreset(DisplayNames name) {
    DisplayView profile = CreatePreset(name);
    lock (_gate) {
      _current = profile;
      Settings.Instance["displayview"] = profile.ConvertToString();
    }
    Changed?.Invoke(null, EventArgs.Empty);
  }

  internal static DisplayView ResolveStoredProfile(string? value, out string canonical) {
    DisplayView profile;
    if (Enum.TryParse(value, ignoreCase: true, out DisplayNames name)
        && string.Equals(value, name.ToString(), StringComparison.OrdinalIgnoreCase)) {
      // Migration for early Avalonia builds that wrote only the enum caption.
      profile = CreatePreset(name);
    } else if (!string.IsNullOrWhiteSpace(value)
               && DisplayViewExtensions.TryParse(value, out var parsed)) {
      profile = parsed;
    } else {
      // This is the same fallback returned by upstream Settings.GetDisplayView for bad data.
      profile = new DisplayView();
    }

    canonical = profile.ConvertToString();
    return profile;
  }

  internal static DisplayView ResolveStartupProfile(
      string? storedProfile,
      bool hadStoredProfile,
      bool legacyAdvanced,
      out string? canonical) {
    if (legacyAdvanced) {
      // MainV2 assigns Advanced through DisplayConfiguration first. Its setter replaces any
      // existing displayview value before the normal displayview load immediately below it.
      storedProfile = new DisplayView().Advanced().ConvertToString();
      hadStoredProfile = true;
    }
    if (!hadStoredProfile) {
      canonical = null;
      return DefaultProfile();
    }

    DisplayView profile = ResolveStoredProfile(storedProfile, out string normalized);
    canonical = normalized;
    ApplyStartupParameterVisibility(profile);
    return profile;
  }

  internal static DisplayView CreatePreset(DisplayNames name) => name switch {
    DisplayNames.Basic => new DisplayView().Basic(),
    DisplayNames.Custom => LoadCustomProfile(),
    _ => new DisplayView().Advanced(),
  };

  private static DisplayView DefaultProfile() {
    try {
      return File.Exists(DisplayViewExtensions.custompath)
          ? LoadCustomProfile()
          : new DisplayView().Advanced();
    } catch {
      return new DisplayView().Advanced();
    }
  }

  private static DisplayView LoadCustomProfile() {
    try {
      return new DisplayView().Custom();
    } catch {
      return new DisplayView().Advanced();
    }
  }

  private static void ApplyStartupParameterVisibility(DisplayView profile) {
    // Keep the startup migration in MainV2: friendly lists stay hidden while the complete list
    // remains reachable. A profile selected later is applied exactly as authored.
    profile.displayAdvancedParams = false;
    profile.displayStandardParams = false;
    profile.displayFullParamList = true;
  }
}

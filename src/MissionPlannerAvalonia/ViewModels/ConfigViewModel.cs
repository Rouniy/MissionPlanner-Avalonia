using System;
using MissionPlanner.Utilities;
using MissionPlannerAvalonia.Services;
using MissionPlannerAvalonia.ViewModels.GCSViews.ConfigurationView;

namespace MissionPlannerAvalonia.ViewModels;

public class ConfigViewModel : BackstageViewModel {

  private static Func<bool> When(
      Func<DisplayView, bool> profileAllows, Func<bool>? condition = null) =>
      () => profileAllows(DisplayViewService.Current) && (condition?.Invoke() ?? true);

  private static MissionPlanner.ArduPilot.Firmwares Fw =>
      AppState.comPort.MAV.cs.firmware;

  private static bool IsCopter => Fw == MissionPlanner.ArduPilot.Firmwares.ArduCopter2;
  private static bool IsPlane => Fw == MissionPlanner.ArduPilot.Firmwares.ArduPlane;
  private static bool IsRover => Fw == MissionPlanner.ArduPilot.Firmwares.ArduRover;

  private static bool IsHeli =>
      IsCopter && AppState.comPort.MAV.param.ContainsKey("H_SWASH_TYPE");

  [System.Obsolete]
  public ConfigViewModel() : base(persistKey: "config_lastpage") {
    Add("Flight Modes", () => new ConfigFlightModesViewModel(), requiresConnection: true,
        visibleWhen: When(profile => profile.displayFlightModes));
    Add("Standard Params", () => new ConfigFriendlyParamsViewModel(advanced: false), requiresConnection: true,
        visibleWhen: When(profile => profile.displayStandardParams));
    Add("Advanced Params", () => new ConfigFriendlyParamsViewModel(advanced: true), advanced: true,
        requiresConnection: true, visibleWhen: When(profile => profile.displayAdvancedParams));
    Add("GeoFence", () => new ConfigAC_FenceViewModel(), requiresConnection: true,
        visibleWhen: When(profile => profile.displayGeoFence));
    Add("Basic Tuning", () => new ConfigBasicTuningViewModel(), requiresConnection: true,
        visibleWhen: When(profile => profile.displayBasicTuning, () => IsCopter && !IsHeli));
    Add("Heli Setup", () => new ConfigTradHeliViewModel(), requiresConnection: true,
        visibleWhen: When(profile => profile.displayBasicTuning, () => IsHeli));

    Add("Basic Tuning (Plane)", () => new ConfigArduplaneViewModel(), requiresConnection: true,
        visibleWhen: When(profile => profile.displayBasicTuning, () => IsPlane));
    Add("Basic Tuning (Rover)", () => new ConfigArduroverViewModel(), requiresConnection: true,
        visibleWhen: When(profile => profile.displayBasicTuning, () => IsRover));
    Add(IsPlane ? "QP Extended Tuning" : "Extended Tuning",
        () => new ConfigExtendedTuningViewModel(), requiresConnection: true,
        visibleWhen: When(profile => profile.displayExtendedTuning));

    Add("Onboard OSD", () => new ConfigOSDViewModel(), requiresConnection: true,
        visibleWhen: When(profile => profile.displayOSD));
    Add("MAVFtp", () => new MavFTPUIViewModel(), requiresConnection: true,
        visibleWhen: When(profile => profile.displayMavFTP));
    Add("User Params", () => new ConfigUserDefinedViewModel(), requiresConnection: true,
        visibleWhen: When(profile => profile.displayUserParam));
    Add("Full Parameter List", () => new RawParamsViewModel(),
        visibleWhen: When(profile => profile.displayFullParamList));
    Add("Planner", () => new ConfigPlannerViewModel(),
        visibleWhen: When(profile => profile.displayPlannerSettings));
    Add("Planner (Advanced)", () => new ConfigPlannerAdvViewModel(), advanced: true,
        visibleWhen: When(profile => profile.displayPlannerSettings));

    SelectFirst();
  }
}

using System;
using MissionPlanner.Utilities;
using MissionPlannerAvalonia.Services;
using MissionPlannerAvalonia.ViewModels.GCSViews.ConfigurationView;
using MissionPlannerAvalonia.ViewModels.Setup;

namespace MissionPlannerAvalonia.ViewModels;

public class SetupViewModel : BackstageViewModel {
  private static Func<bool> When(
      Func<DisplayView, bool> profileAllows, Func<bool>? condition = null) =>
      () => profileAllows(DisplayViewService.Current) && (condition?.Invoke() ?? true);

  private static bool IsCopter() =>
      AppState.comPort.MAV.cs.firmware == MissionPlanner.ArduPilot.Firmwares.ArduCopter2;

  private static bool IsHeli() =>
      IsCopter() && AppState.comPort.MAV.param.ContainsKey("H_SWASH_TYPE");

  public SetupViewModel() : base(persistKey: "setup_lastpage") {
    Add("Install Firmware", () => new InstallFirmwareViewModel(),
        visibleWhen: When(profile => profile.displayInstallFirmware));
    Add("Install Firmware Legacy", () => new ConfigFirmwareLegacyViewModel(),
        visibleWhen: When(profile => profile.displayInstallFirmware), badge: "DEPRECATED");
    Add("Secure", () => new ConfigSecureApViewModel());
    Add("Secure (Bootloader Keys)", () => new ConfigSecureViewModel(), requiresConnection: true);

    Add(
        ">> Mandatory Hardware",
        () =>
            new InfoPageViewModel("Mandatory Hardware", "Required setup before flight. Pick a sub-page."),
        requiresConnection: true
    );

    Add("Heli Setup (4.0+)", () => new ConfigTradHeli4ViewModel(), sub: true, requiresConnection: true,
        visibleWhen: When(profile => profile.displayFrameType, IsHeli));

    Add("Frame Type", () => new ConfigFrameClassTypeViewModel(), sub: true, requiresConnection: true,
        visibleWhen: When(profile => profile.displayFrameType, IsCopter));
    Add("Frame Type (Legacy)", () => new ConfigFrameTypeViewModel(), sub: true, requiresConnection: true,
        visibleWhen: When(profile => profile.displayFrameType, IsCopter));
    Add("Default Settings", () => new ConfigDefaultSettingsViewModel(), sub: true,
        requiresConnection: true,
        visibleWhen: When(profile => profile.displayFrameType, IsCopter));
    Add("Accel Calibration", () => new ConfigAccelCalibrationViewModel(), sub: true, requiresConnection: true,
        visibleWhen: When(profile => profile.displayAccelCalibration));
    Add("Compass", () => new ConfigCompassViewModel(), sub: true, requiresConnection: true,
        visibleWhen: When(profile => profile.displayCompassConfiguration));

    Add("Compass (Legacy)", () => new ConfigCompassLegacyViewModel(), sub: true, requiresConnection: true,
        visibleWhen: When(profile => profile.displayCompassConfiguration));
    Add("Radio Calibration", () => new ConfigRadioInputViewModel(), sub: true, requiresConnection: true,
        visibleWhen: When(profile => profile.displayRadioCalibration));
    Add("Servo Output", () => new ConfigRadioOutputViewModel(), sub: true, requiresConnection: true,
        visibleWhen: When(profile => profile.displayServoOutput));
    Add("Serial Ports", () => new ConfigSerialViewModel(), sub: true, requiresConnection: true,
        visibleWhen: When(profile => profile.displaySerialPorts));
    Add("ESC Calibration", () => new ConfigESCCalibrationViewModel(), sub: true, requiresConnection: true,
        visibleWhen: When(profile => profile.displayEscCalibration));
    Add("Flight Modes", () => new ConfigFlightModesViewModel(), sub: true, requiresConnection: true,
        visibleWhen: When(profile => profile.displayFlightModes));
    Add("FailSafe", () => new ConfigFailSafeViewModel(), sub: true, requiresConnection: true,
        visibleWhen: When(profile => profile.displayFailSafe));

    Add("Initial Parameters", () => new ConfigInitialParamsViewModel(), sub: true, requiresConnection: true,
        visibleWhen: When(profile => profile.displayInitialParams));
    Add("HW ID", () => new ConfigHWIDViewModel(), sub: true, requiresConnection: true,
        visibleWhen: When(profile => profile.displayHWIDs));
    Add("ADSB", () => new ConfigADSBViewModel(), sub: true, requiresConnection: true,
        visibleWhen: When(profile => profile.displayADSB));

    Add(
        ">> Optional Hardware",
        () => new InfoPageViewModel("Optional Hardware", "Optional peripherals. Pick a sub-page.")
    );
    Add("RTK/GPS Inject", () => new ConfigGpsInjectViewModel(), sub: true,
        visibleWhen: When(profile => profile.displayRTKInject));
    Add("CubeID Update", () => new ConfigCubeIDViewModel(), sub: true);
    Add("Sik Radio", () => new SikRadioViewModel(), sub: true,
        visibleWhen: When(profile => profile.displaySikRadio));
    Add("CAN GPS Order", () => new ConfigGPSOrderViewModel(), sub: true, requiresConnection: true,
        visibleWhen: When(profile => profile.displayGPSOrder));
    Add("Battery Monitor", () => new ConfigBatteryMonitoringViewModel(), sub: true, requiresConnection: true,
        visibleWhen: When(profile => profile.displayBattMonitor));
    Add("Battery Monitor 2", () => new ConfigBatteryMonitoring2ViewModel(), sub: true, requiresConnection: true,
        visibleWhen: When(profile => profile.displayBattMonitor));
    Add("DroneCAN/UAVCAN", () => new ConfigDroneCanViewModel(), sub: true,
        visibleWhen: When(profile => profile.displayCAN));
    Add("Joystick", () => new ConfigJoystickViewModel(), sub: true,
        visibleWhen: When(profile => profile.displayJoystick));
    Add("Compass/Motor Calib", () => new ConfigCompassMotViewModel(), sub: true, requiresConnection: true,
        visibleWhen: When(profile => profile.displayCompassMotorCalib));
    Add("Range Finder", () => new ConfigRangeFinderViewModel(), sub: true, requiresConnection: true,
        visibleWhen: When(profile => profile.displayRangeFinder));
    Add("Airspeed", () => new ConfigAirspeedViewModel(), sub: true, requiresConnection: true,
        visibleWhen: When(profile => profile.displayAirSpeed));
    Add("PX4Flow", () => new ConfigPX4FlowViewModel(), sub: true,
        visibleWhen: When(profile => profile.displayPx4Flow));
    Add("Optical Flow", () => new ConfigOptFlowViewModel(), sub: true, requiresConnection: true,
        visibleWhen: When(profile => profile.displayOpticalFlow));
    Add("OSD", () => new ConfigHWOSDViewModel(), sub: true, requiresConnection: true,
        visibleWhen: When(profile => profile.displayOsd));
    Add("Camera Gimbal", () => new ConfigMountViewModel(), sub: true, requiresConnection: true,
        visibleWhen: When(profile => profile.displayCameraGimbal));
    Add("Motor Test", () => new ConfigMotorTestViewModel(), sub: true, requiresConnection: true,
        visibleWhen: When(profile => profile.displayMotorTest));
    Add("Bluetooth Setup", () => new ConfigHWBTViewModel(), sub: true,
        visibleWhen: When(profile => profile.displayBluetooth));
    Add("Parachute", () => new ConfigParachuteViewModel(), sub: true, requiresConnection: true,
        visibleWhen: When(profile => profile.displayParachute));
    Add("ESP8266 Setup", () => new ConfigHWESP8266ViewModel(), sub: true, requiresConnection: true,
        visibleWhen: When(profile => profile.displayEsp));
    Add("Antenna Tracker", () => new ConfigAntennaTrackerParamViewModel(), sub: true, requiresConnection: true,
        visibleWhen: When(profile => profile.displayAntennaTracker));
    Add("FFT Setup", () => new ConfigFFTViewModel(), sub: true, requiresConnection: true,
        visibleWhen: When(profile => profile.displayFFTSetup));

    Add("Antenna Tracker (Maestro)", () => new ConfigAntennaTrackerViewModel(), sub: true,
        visibleWhen: When(profile => profile.displayAntennaTracker));
    Add("Antenna Tracker (Live)", () => new AntennaTrackerUIViewModel(), sub: true,
        visibleWhen: When(profile => profile.displayAntennaTracker));
    Add("HW CAN", () => new ConfigHWCANViewModel(), sub: true, requiresConnection: true,
        visibleWhen: When(profile => profile.displayCAN));
    Add("MAVFtp", () => new MavFTPUIViewModel(), sub: true, requiresConnection: true,
        visibleWhen: When(profile => profile.displayMavFTP));

    Add(">> Advanced", () => new InfoPageViewModel("Advanced", "Advanced configuration. Pick a sub-page."),
        advanced: true);
    Add("Advanced Tools", () => new ConfigAdvancedViewModel(), advanced: true, sub: true);
    Add("Elevation Sources", () => new ConfigElevationSourcesViewModel(), advanced: true, sub: true);
    Add("Developer Tools", () => new ConfigDeveloperToolsViewModel(), advanced: true, sub: true);
    Add("Mission Command List", () => new ConfigMavCommandViewModel(), advanced: true, sub: true);
    Add("Terminal", () => new ConfigTerminalViewModel(), advanced: true, sub: true,
        visibleWhen: When(profile => profile.displayTerminal));
    Add("Onboard Lua REPL", () => new ConfigOnboardReplViewModel(), advanced: true, sub: true,
        requiresConnection: true, visibleWhen: When(profile => profile.displayREPL));
    Add("Local Script REPL", () => new ConfigScriptReplViewModel(), advanced: true, sub: true,
        visibleWhen: When(profile => profile.displayREPL));

    SelectFirst();
  }
}

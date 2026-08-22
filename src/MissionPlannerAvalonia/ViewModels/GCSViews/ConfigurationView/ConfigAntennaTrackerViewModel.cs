namespace MissionPlannerAvalonia.ViewModels.GCSViews.ConfigurationView;

/// <summary>
/// Hosts the standalone serial tracker workflow in the hardware-configuration navigation tree.
/// The live page and this compatibility page intentionally share one implementation so all
/// official Maestro, ArduTracker, and DegreeTracker outputs behave identically.
/// </summary>
public sealed class ConfigAntennaTrackerViewModel : AntennaTrackerUIViewModel {
}

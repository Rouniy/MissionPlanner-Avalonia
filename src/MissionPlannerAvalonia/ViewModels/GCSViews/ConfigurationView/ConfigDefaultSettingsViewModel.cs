using MissionPlannerAvalonia.Services;

namespace MissionPlannerAvalonia.ViewModels.GCSViews.ConfigurationView;

public sealed class ConfigDefaultSettingsViewModel : RawParamsViewModel, IActivationAware {
  public ConfigDefaultSettingsViewModel() : base(FrameDefaultCatalogService.Shared) { }

  public void Activate() {
    SynchronizeSelectedVehicle();
    if (FrameDefaults.Count == 0 && !LoadingFrameDefaults) {
      _ = EnsureFrameDefaultsAsync();
    }
  }
}

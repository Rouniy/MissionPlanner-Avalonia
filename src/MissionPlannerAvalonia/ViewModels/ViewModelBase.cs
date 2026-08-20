using CommunityToolkit.Mvvm.ComponentModel;

namespace MissionPlannerAvalonia.ViewModels;

public abstract class ViewModelBase : ObservableObject { }

public interface IDeactivationAware {
  void Deactivate();
}

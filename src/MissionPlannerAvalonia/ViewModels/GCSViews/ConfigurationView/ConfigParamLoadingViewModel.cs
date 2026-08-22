using System;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner;

namespace MissionPlannerAvalonia.ViewModels.GCSViews.ConfigurationView;

public partial class ConfigParamLoadingViewModel : ViewModelBase, IDisposable {
  private MAVLinkInterface _comPort => AppState.comPort;
  private readonly DispatcherTimer _timer;

  [ObservableProperty]
  private int _progressPercent;

  [ObservableProperty]
  private string _status = "Parameters are still loading. Many screens will not work until all parameters are loaded.";

  [ObservableProperty]
  private string _count = "";

  public bool GotAllParams => HasAllParameters(
      _comPort.MAV.param.TotalReceived, _comPort.MAV.param.TotalReported);

  public bool ParametersReady => GotAllParams;

  internal static bool HasAllParameters(int received, int reported) =>
      reported > 0 && received >= reported;

  public ConfigParamLoadingViewModel() {
    _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
    _timer.Tick += (_, _) => Tick();
    _timer.Start();
    Tick();
  }

  private void Tick() {
    var reported = _comPort.MAV.param.TotalReported;
    var received = _comPort.MAV.param.TotalReceived;

    ProgressPercent = reported > 0 ? (int)Math.Min(100, received * 100.0 / reported) : 0;
    Count = received + " / " + reported;
    if (GotAllParams && reported > 0) {
      Status = "All parameters loaded.";
    } else if (reported == 0) {
      Status = "Waiting for the first parameter response. Select another device or retry; "
          + "old-device values remain hidden.";
    } else {
      Status = $"Loading parameters ({received} / {reported})…";
    }
  }

  [RelayCommand]
  private async Task Retry() {
    Status = "Requesting parameters…";

    await AppState.ParameterLoads.LoadLatestAsync(
        _comPort.MAV.sysid, _comPort.MAV.compid);
    AppState.RaiseConnectionChanged();
  }

  public void Dispose() {
    _timer.Stop();
  }
}

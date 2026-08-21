using System;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner;

namespace MissionPlannerAvalonia.ViewModels.GCSViews.ConfigurationView;

public partial class ConfigParamLoadingViewModel : ViewModelBase, IDisposable {
  internal const long EmptyDownloadTimeoutMilliseconds = 10_000;
  private readonly MAVLinkInterface _comPort = AppState.comPort;
  private readonly DispatcherTimer _timer;
  private int _lastReceived = -1;
  private long _lastProgressAt = Environment.TickCount64;
  private bool _wasConnected;

  [ObservableProperty]
  private int _progressPercent;

  [ObservableProperty]
  private string _status = "Parameters are still loading. Many screens will not work until all parameters are loaded.";

  [ObservableProperty]
  private string _count = "";

  [ObservableProperty]
  private bool _emptyDownloadTimedOut;

  public bool GotAllParams => HasAllParameters(
      _comPort.MAV.param.TotalReceived, _comPort.MAV.param.TotalReported);

  public bool ParametersReady => GotAllParams || EmptyDownloadTimedOut;

  internal static bool HasAllParameters(int received, int reported) =>
      reported > 0 && received >= reported;

  internal static bool ShouldDismissEmptyDownload(
      bool connected, int reported, long noProgressMilliseconds) =>
      connected && reported == 0
      && noProgressMilliseconds >= EmptyDownloadTimeoutMilliseconds;

  public ConfigParamLoadingViewModel() {
    _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
    _timer.Tick += (_, _) => Tick();
    _timer.Start();
    Tick();
  }

  private void Tick() {
    var reported = _comPort.MAV.param.TotalReported;
    var received = _comPort.MAV.param.TotalReceived;
    bool connected = AppState.IsConnected;
    long now = Environment.TickCount64;
    if (!connected || !_wasConnected || received != _lastReceived || reported > 0) {
      _lastProgressAt = now;
      EmptyDownloadTimedOut = false;
    } else if (ShouldDismissEmptyDownload(connected, reported, now - _lastProgressAt)) {
      EmptyDownloadTimedOut = true;
    }
    _wasConnected = connected;
    _lastReceived = received;

    ProgressPercent = reported > 0 ? (int)Math.Min(100, received * 100.0 / reported) : 0;
    Count = received + " / " + reported;
    if (GotAllParams && reported > 0) {
      Status = "All parameters loaded.";
    } else if (EmptyDownloadTimedOut) {
      Status = "No parameter download was reported; showing the data currently available.";
    }
  }

  [RelayCommand]
  private async Task Retry() {
    Status = "Requesting parameters…";

    await Task.Run(() => _comPort.getParamListMavftp(_comPort.MAV.sysid, _comPort.MAV.compid));
  }

  public void Dispose() {
    _timer.Stop();
  }
}

using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MissionPlanner.Warnings;
using MissionPlannerAvalonia.ViewModels;
using MissionPlannerAvalonia.Views;

namespace MissionPlannerAvalonia;

public partial class App : Application {
  public override void Initialize() {
    AvaloniaXamlLoader.Load(this);
  }

  public override void OnFrameworkInitializationCompleted() {
    Services.AppPaths.Initialize();
    Services.ThemeService.ApplySaved();
    Services.Speech.Enabled = MissionPlanner.Utilities.Settings.Instance.GetBoolean("speechenable", false);
    // Upstream CurrentState/MAVLinkInterface speak mode, waypoint and severe-status messages
    // once these adapters are assigned.
    MissionPlanner.CurrentState.Speech = Services.Speech.Adapter;
    MissionPlanner.MAVLinkInterface.Speech = Services.Speech.Adapter;
    CustomWarning.defaultsrc = AppState.comPort.MAV.cs;
    WarningEngine.WarningMessage -= OnWarningMessage;
    WarningEngine.WarningMessage += OnWarningMessage;
    WarningEngine.Start(Services.Speech.Adapter);
    Services.SpeechAnnouncer.Start();

    if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) {
      desktop.MainWindow = new MainWindow { DataContext = new MainWindowViewModel() };

      desktop.Exit += (_, _) => {
        Services.SpeechAnnouncer.Stop();
        WarningEngine.Stop();
        Services.Speech.Stop();
        Services.SitlLauncher.StopAll();
        try {
          if (AppState.comPort.BaseStream?.IsOpen == true) {
            AppState.comPort.Close();
          }
        } catch {
        }
        try {
          MissionPlanner.Utilities.Settings.Instance.Save();
        } catch {
        }
      };

      _ = Services.Updater.CheckOnStartupAsync();
    }

    base.OnFrameworkInitializationCompleted();
  }

  private static void OnWarningMessage(object? sender, string message) {
    AppState.comPort.MAV.cs.messageHigh = message;
  }
}

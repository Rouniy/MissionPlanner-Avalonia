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
    CustomWarning.defaultsrc = AppState.comPort.MAV.cs;
    WarningEngine.WarningMessage -= OnWarningMessage;
    WarningEngine.WarningMessage += OnWarningMessage;
    WarningEngine.Start(Services.Speech.Adapter);

    if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) {
      desktop.MainWindow = new MainWindow { DataContext = new MainWindowViewModel() };

      desktop.Exit += (_, _) => {
        WarningEngine.Stop();
        Services.Speech.Stop();
        Services.SitlLauncher.StopAll();
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

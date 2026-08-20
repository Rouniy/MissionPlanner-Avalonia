using System;
using System.IO;
using Avalonia;
using Projektanker.Icons.Avalonia;
using Projektanker.Icons.Avalonia.FontAwesome;

namespace MissionPlannerAvalonia;

sealed class Program {
  [STAThread]
  public static void Main(string[] args) {

    Services.AppPaths.Initialize();

    AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash(e.ExceptionObject as Exception);

    IconProvider.Current.Register<FontAwesomeIconProvider>();
    try {
      BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    } catch (Exception ex) {
      LogCrash(ex);
      throw;
    }
  }

  private static void LogCrash(Exception? ex) {
    if (ex == null) {
      return;
    }
    try {
      Directory.CreateDirectory(Path.GetDirectoryName(Services.AppPaths.CrashLogPath)!);
      File.AppendAllText(Services.AppPaths.CrashLogPath, $"---- crash ----\n{ex}\n\n");
    } catch {

    }
  }

  public static AppBuilder BuildAvaloniaApp() =>
      AppBuilder.Configure<App>()
          .UsePlatformDetect()
          .WithInterFont()
          .LogToTrace();
}

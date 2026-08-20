using System;
using System.Diagnostics;
using MissionPlanner.Utilities;

namespace MissionPlannerAvalonia.Services;

public static class Speech {

  public static bool Enabled { get; set; }
  public static ISpeech Adapter { get; } = new SpeechAdapter();

  private static Process? _current;

  public static void Speak(string text) {
    if (!Enabled || string.IsNullOrWhiteSpace(text)) {
      return;
    }
    try {
      if (OperatingSystem.IsMacOS()) {
        Start("say", new[] { text });
      } else if (OperatingSystem.IsLinux()) {

        if (!TryStart("spd-say", new[] { text })) {
          StartWithStandardInput("festival", new[] { "--tts" }, text);
        }
      } else if (OperatingSystem.IsWindows()) {

        var ps = "Add-Type -AssemblyName System.Speech; " +
                 "(New-Object System.Speech.Synthesis.SpeechSynthesizer).Speak(" +
                 PowerShellLiteral(text) + ")";
        Start("powershell", new[] { "-NoProfile", "-Command", ps });
      }
    } catch {

    }
  }

  public static void Stop() {
    try {
      if (_current is { HasExited: false }) {
        _current.Kill(true);
      }
    } catch {

    } finally {
      _current = null;
    }
  }

  private static void Start(string file, string[] args) {
    var psi = new ProcessStartInfo(file) { UseShellExecute = false };
    foreach (var a in args) {
      psi.ArgumentList.Add(a);
    }
    _current = Process.Start(psi);
  }

  private static bool TryStart(string file, string[] args) {
    try {
      Start(file, args);
      return _current != null;
    } catch {
      return false;
    }
  }

  private static void StartWithStandardInput(string file, string[] args, string input) {
    var psi = new ProcessStartInfo(file) {
      UseShellExecute = false,
      RedirectStandardInput = true,
    };
    foreach (var a in args) {
      psi.ArgumentList.Add(a);
    }
    _current = Process.Start(psi);
    if (_current == null) {
      return;
    }
    _current.StandardInput.Write(input);
    _current.StandardInput.Close();
  }

  private static string PowerShellLiteral(string text) => "'" + text.Replace("'", "''") + "'";

  private sealed class SpeechAdapter : ISpeech {
    public bool speechEnable {
      get => Enabled;
      set => Enabled = value;
    }

    public bool IsReady {
      get {
        try {
          return _current is not { HasExited: false };
        } catch {
          return true;
        }
      }
    }

    public void SpeakAsync(string text) => Speak(text);

    public void SpeakAsyncCancelAll() => Stop();
  }
}

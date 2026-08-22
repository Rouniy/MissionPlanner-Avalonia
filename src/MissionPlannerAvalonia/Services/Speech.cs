using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using MissionPlanner.Utilities;

namespace MissionPlannerAvalonia.Services;

// Utterances are queued and spoken one at a time by a single pump task, so concurrent alerts
// (warning engine, upstream mode/waypoint hooks, SpeechAnnouncer) never talk over each other and
// Stop() always reaches the process that is actually speaking.
public static class Speech {
  internal const int MaxPendingUtterances = 4;
  internal const int MaximumUtteranceLength = 512;
  internal static readonly string[] LinuxCancelArguments =
      ["-N", "MissionPlanner-Avalonia", "-n", "main", "-C"];

  private static int _enabled;
  public static bool Enabled {
    get => Volatile.Read(ref _enabled) != 0;
    set {
      int next = value ? 1 : 0;
      int previous = Interlocked.Exchange(ref _enabled, next);
      if (previous != 0 && next == 0) {
        Stop();
      }
    }
  }

  public static ISpeech Adapter { get; } = new SpeechAdapter();

  private static readonly Queue<string> _queue = new();
  private static readonly HashSet<string> _queued = new(StringComparer.Ordinal);
  private static readonly object _sync = new();
  private static Process? _current;
  private static string? _currentText;
  private static Process? _cancel;
  private static bool _pumping;
  private static int _stopVersion;
  private static string _lastStatus = OperatingSystem.IsLinux()
      ? "Speech idle (Speech Dispatcher / espeak-ng)."
      : "Speech idle.";

  public static event Action<string>? StatusChanged;

  public static string LastStatus {
    get {
      lock (_sync) {
        return _lastStatus;
      }
    }
  }

  internal static bool ShouldSpeakForArmedState(bool enabled, bool armedOnly, bool armed) =>
      enabled && (!armedOnly || armed);

  private static bool AdapterEnabledForCurrentVehicle() {
    try {
      return ShouldSpeakForArmedState(
          Enabled,
          Settings.Instance.GetBoolean("speech_armed_only", false),
          AppState.comPort.MAV.cs.armed);
    } catch {
      return Enabled;
    }
  }

  public static void Speak(string text) {
    if (!Enabled || string.IsNullOrWhiteSpace(text)) {
      return;
    }
    string normalized = Normalize(text);
    bool startPump = false;
    lock (_sync) {
      if (!Enabled || string.Equals(_currentText, normalized, StringComparison.Ordinal)
          || !SpeechQueuePolicy.Add(
              _queue, _queued, normalized, MaxPendingUtterances)) {
        return;
      }
      if (!_pumping) {
        _pumping = true;
        startPump = true;
      }
    }
    if (startPump) {
      _ = Task.Run(Pump);
    }
  }

  public static void Stop() {
    Process? current;
    bool hadWork;
    lock (_sync) {
      hadWork = _pumping || _queue.Count > 0 || IsRunning(_current);
      _queue.Clear();
      _queued.Clear();
      // Invalidate any utterance the pump has dequeued but not yet registered in _current.
      _stopVersion++;
      current = _current;
    }
    Kill(current);
    if (OperatingSystem.IsLinux() && hadWork) {
      // Killing the waiting client is not guaranteed to remove audio already accepted by the
      // daemon. Use cancel-all, not spd-say -S: Speech Dispatcher 0.12.0-rc2 aborts in
      // speaking_get_queue when it receives the stop command. This request is itself coalesced so
      // repeated link-loss notifications cannot create a flock of short-lived spd-say processes.
      RequestLinuxCancel();
    }
    if (hadWork) {
      SetStatus("Speech stopped.");
    }
  }

  private static void RequestLinuxCancel() {
    Process? cancel;
    lock (_sync) {
      if (IsRunning(_cancel)) {
        return;
      }
      try {
        _cancel?.Dispose();
      } catch {
      }
      _cancel = TryStart("spd-say", LinuxCancelArguments);
      cancel = _cancel;
    }
    if (cancel != null) {
      _ = Task.Run(() => ObserveCancel(cancel));
    }
  }

  private static void ObserveCancel(Process process) {
    try {
      if (!process.WaitForExit(TimeSpan.FromSeconds(5))) {
        Kill(process);
      }
    } catch {
    } finally {
      lock (_sync) {
        if (ReferenceEquals(_cancel, process)) {
          _cancel = null;
        }
      }
      process.Dispose();
    }
  }

  private static void Pump() {
    while (true) {
      // Capture the stop version and dequeue under one lock: Stop() cannot leave an already-taken
      // phrase invisible to both the queue and the version check.
      int version;
      string text;
      lock (_sync) {
        version = _stopVersion;
        if (_queue.Count == 0) {
          _pumping = false;
          _currentText = null;
          return;
        }
        text = _queue.Dequeue();
        _queued.Remove(text);
      }

      RunningUtterance? utterance = null;
      Process? process = null;
      try {
        utterance = StartUtterance(text);
        process = utterance?.Process;
        if (utterance == null) {
          SetStatus(OperatingSystem.IsLinux()
              ? "Speech unavailable: install speech-dispatcher-espeak-ng."
              : "Speech backend is unavailable.");
          continue;
        }
        bool cancelled;
        lock (_sync) {
          cancelled = _stopVersion != version;
          if (!cancelled) {
            _current = process;
            _currentText = text;
          }
        }
        if (cancelled) {
          // Stop() arrived between process start and registration — kill it here instead.
          Kill(process);
          continue;
        }
        SetStatus($"Speaking via {utterance.Backend}…");
        // A broken synthesizer must not retain the sole pump forever. Normal Mission Planner
        // phrases are short; one minute is deliberately generous even at a slow speech rate.
        bool exited = process!.WaitForExit(TimeSpan.FromSeconds(60));
        if (!exited) {
          Kill(process);
          SetStatus($"Speech timed out in {utterance.Backend}.");
        } else {
          bool stopped;
          lock (_sync) {
            stopped = _stopVersion != version;
          }
          if (!stopped) {
            int exitCode = process.ExitCode;
            SetStatus(exitCode == 0
                ? $"Speech completed via {utterance.Backend}."
                : $"Speech backend {utterance.Backend} exited with code {exitCode}.");
          }
        }
      } catch (Exception ex) {
        SetStatus($"Speech failed: {ex.Message}");
      } finally {
        lock (_sync) {
          if (ReferenceEquals(_current, process)) {
            _current = null;
            _currentText = null;
          }
        }
        process?.Dispose();
      }
    }
  }

  private static RunningUtterance? StartUtterance(string text) {
    if (OperatingSystem.IsMacOS()) {
      return Wrap(Start("say", [text]), "macOS say");
    }
    if (OperatingSystem.IsLinux()) {
      // Without -w spd-say hands the text to the daemon and exits immediately, which would
      // defeat both serialization and Stop(). Select the real espeak-ng module explicitly: a
      // dummy Speech Dispatcher module can otherwise accept requests without producing audio.
      string language = DetectLanguage(text);
      Process? process = TryStart("spd-say", LinuxUtteranceArguments(text, language));
      if (process != null) {
        return new RunningUtterance(process, "Speech Dispatcher / espeak-ng");
      }
      return Wrap(TryStartWithStandardInput("festival", ["--tts"], text), "Festival");
    }
    if (OperatingSystem.IsWindows()) {
      string script = "Add-Type -AssemblyName System.Speech; "
          + "(New-Object System.Speech.Synthesis.SpeechSynthesizer).Speak("
          + PowerShellLiteral(text) + ")";
      return Wrap(Start("powershell", ["-NoProfile", "-Command", script]), "Windows Speech");
    }
    return null;
  }

  private static RunningUtterance? Wrap(Process? process, string backend) =>
      process == null ? null : new RunningUtterance(process, backend);

  internal static string[] LinuxUtteranceArguments(string text, string? language = null) => [
    "-N", "MissionPlanner-Avalonia",
    "-n", "main",
    "-o", "espeak-ng",
    "-l", string.IsNullOrWhiteSpace(language) ? DetectLanguage(text) : language,
    "-P", "notification",
    "-w",
    text,
  ];

  internal static string DetectLanguage(string text) {
    foreach (char character in text) {
      if (character is >= '\u0400' and <= '\u052f') {
        return "ru";
      }
    }
    string language = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
    return language.Length == 2 && !string.Equals(language, "iv", StringComparison.OrdinalIgnoreCase)
        ? language
        : "en";
  }

  private static Process? Start(string file, IReadOnlyList<string> arguments) {
    var startInfo = new ProcessStartInfo(file) { UseShellExecute = false };
    foreach (string argument in arguments) {
      startInfo.ArgumentList.Add(argument);
    }
    return Process.Start(startInfo);
  }

  private static Process? TryStart(string file, IReadOnlyList<string> arguments) {
    try {
      return Start(file, arguments);
    } catch {
      return null;
    }
  }

  private static Process? StartWithStandardInput(
      string file, IReadOnlyList<string> arguments, string input) {
    var startInfo = new ProcessStartInfo(file) {
      UseShellExecute = false,
      RedirectStandardInput = true,
    };
    foreach (string argument in arguments) {
      startInfo.ArgumentList.Add(argument);
    }
    Process? process = Process.Start(startInfo);
    if (process == null) {
      return null;
    }
    process.StandardInput.Write(input);
    process.StandardInput.Close();
    return process;
  }

  private static Process? TryStartWithStandardInput(
      string file, IReadOnlyList<string> arguments, string input) {
    try {
      return StartWithStandardInput(file, arguments, input);
    } catch {
      return null;
    }
  }

  private static string PowerShellLiteral(string text) => "'" + text.Replace("'", "''") + "'";

  private static string Normalize(string text) {
    string normalized = text.Trim();
    return normalized.Length <= MaximumUtteranceLength
        ? normalized
        : normalized[..MaximumUtteranceLength];
  }

  private static bool IsRunning(Process? process) {
    if (process == null) {
      return false;
    }
    try {
      return !process.HasExited;
    } catch {
      return false;
    }
  }

  private static void Kill(Process? process) {
    if (!IsRunning(process)) {
      return;
    }
    try {
      process!.Kill(true);
    } catch {
    }
  }

  private static void SetStatus(string status) {
    Action<string>? changed;
    lock (_sync) {
      _lastStatus = status;
      changed = StatusChanged;
    }
    try {
      changed?.Invoke(status);
    } catch {
      // Status consumers are diagnostic UI only and must never break the speech pump.
    }
  }

  internal static SpeechRuntimeState RuntimeState {
    get {
      lock (_sync) {
        return new SpeechRuntimeState(
            _queue.Count, _pumping, IsRunning(_current), IsRunning(_cancel));
      }
    }
  }

  private sealed class SpeechAdapter : ISpeech {
    public bool speechEnable {
      get => AdapterEnabledForCurrentVehicle();
      set => Enabled = value;
    }

    public bool IsReady {
      get {
        try {
          lock (_sync) {
            // _pumping covers the window where a phrase is dequeued but the process is not
            // yet registered in _current.
            return _queue.Count == 0 && !_pumping;
          }
        } catch {
          return true;
        }
      }
    }

    public void SpeakAsync(string text) {
      if (AdapterEnabledForCurrentVehicle()) {
        Speak(text);
      }
    }

    public void SpeakAsyncCancelAll() => Stop();
  }

  private sealed record RunningUtterance(Process Process, string Backend);
}

internal readonly record struct SpeechRuntimeState(
    int Pending,
    bool Pumping,
    bool UtteranceProcessRunning,
    bool CancelProcessRunning);

internal static class SpeechQueuePolicy {
  internal static bool Add(
      Queue<string> queue,
      HashSet<string> queued,
      string text,
      int maximumPending) {
    ArgumentNullException.ThrowIfNull(queue);
    ArgumentNullException.ThrowIfNull(queued);
    if (maximumPending <= 0) {
      throw new ArgumentOutOfRangeException(nameof(maximumPending));
    }
    if (!queued.Add(text)) {
      return false;
    }
    while (queue.Count >= maximumPending) {
      queued.Remove(queue.Dequeue());
    }
    queue.Enqueue(text);
    return true;
  }
}

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Tasks;
using MissionPlanner.Utilities;

namespace MissionPlannerAvalonia.Services;

// Utterances are queued and spoken one at a time by a single pump task, so concurrent alerts
// (warning engine, upstream mode/waypoint hooks, SpeechAnnouncer) never talk over each other and
// Stop() always reaches the process that is actually speaking.
public static class Speech {

  public static bool Enabled { get; set; }
  public static ISpeech Adapter { get; } = new SpeechAdapter();

  private static readonly ConcurrentQueue<string> _queue = new();
  private static readonly object _sync = new();
  private static Process? _current;
  private static bool _pumping;
  private static int _stopVersion;

  public static void Speak(string text) {
    if (!Enabled || string.IsNullOrWhiteSpace(text)) {
      return;
    }
    _queue.Enqueue(text);
    lock (_sync) {
      if (_pumping) {
        return;
      }
      _pumping = true;
    }
    _ = Task.Run(Pump);
  }

  public static void Stop() {
    lock (_sync) {
      _queue.Clear();
      // Invalidate any utterance the pump has dequeued but not yet registered in _current.
      _stopVersion++;
      try {
        if (_current is { HasExited: false }) {
          _current.Kill(true);
        }
      } catch {

      }
    }
    if (OperatingSystem.IsLinux()) {
      // Ask speech-dispatcher to drop anything it is still playing.
      try {
        using var cancel = Process.Start(new ProcessStartInfo("spd-say") {
          UseShellExecute = false,
          ArgumentList = { "-S" },
        });
      } catch {

      }
    }
  }

  private static void Pump() {
    while (true) {
      // Capture the stop version BEFORE dequeuing: a Stop() that lands between the dequeue and
      // the start of the utterance must invalidate the phrase that was already taken out.
      int version;
      lock (_sync) {
        version = _stopVersion;
      }

      if (!_queue.TryDequeue(out var text)) {
        lock (_sync) {
          if (_queue.IsEmpty) {
            _pumping = false;
            return;
          }
        }
        continue;
      }

      Process? p = null;
      try {
        p = StartUtterance(text);
        if (p == null) {
          continue;
        }
        bool cancelled;
        lock (_sync) {
          cancelled = _stopVersion != version;
          if (!cancelled) {
            _current = p;
          }
        }
        if (cancelled) {
          // Stop() arrived between process start and registration — kill it here instead.
          try {
            if (!p.HasExited) {
              p.Kill(true);
            }
          } catch {

          }
          continue;
        }
        p.WaitForExit();
      } catch {

      } finally {
        lock (_sync) {
          if (ReferenceEquals(_current, p)) {
            _current = null;
          }
        }
        p?.Dispose();
      }
    }
  }

  private static Process? StartUtterance(string text) {
    if (OperatingSystem.IsMacOS()) {
      return Start("say", new[] { text });
    }
    if (OperatingSystem.IsLinux()) {
      // Without -w spd-say hands the text to the daemon and exits immediately, which would
      // defeat both serialization and Stop().
      var p = TryStart("spd-say", new[] { "-w", text });
      return p ?? StartWithStandardInput("festival", new[] { "--tts" }, text);
    }
    if (OperatingSystem.IsWindows()) {
      var ps = "Add-Type -AssemblyName System.Speech; " +
               "(New-Object System.Speech.Synthesis.SpeechSynthesizer).Speak(" +
               PowerShellLiteral(text) + ")";
      return Start("powershell", new[] { "-NoProfile", "-Command", ps });
    }
    return null;
  }

  private static Process? Start(string file, string[] args) {
    var psi = new ProcessStartInfo(file) { UseShellExecute = false };
    foreach (var a in args) {
      psi.ArgumentList.Add(a);
    }
    return Process.Start(psi);
  }

  private static Process? TryStart(string file, string[] args) {
    try {
      return Start(file, args);
    } catch {
      return null;
    }
  }

  private static Process? StartWithStandardInput(string file, string[] args, string input) {
    var psi = new ProcessStartInfo(file) {
      UseShellExecute = false,
      RedirectStandardInput = true,
    };
    foreach (var a in args) {
      psi.ArgumentList.Add(a);
    }
    var p = Process.Start(psi);
    if (p == null) {
      return null;
    }
    p.StandardInput.Write(input);
    p.StandardInput.Close();
    return p;
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
          lock (_sync) {
            // _pumping covers the window where a phrase is dequeued but the process is not
            // yet registered in _current.
            return _queue.IsEmpty && !_pumping;
          }
        } catch {
          return true;
        }
      }
    }

    public void SpeakAsync(string text) => Speak(text);

    public void SpeakAsyncCancelAll() => Stop();
  }
}

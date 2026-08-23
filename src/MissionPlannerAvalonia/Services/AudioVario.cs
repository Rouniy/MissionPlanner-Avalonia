using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using LibVLCSharp.Shared;

namespace MissionPlannerAvalonia.Services;

internal readonly record struct VarioTone(int FrequencyHz, int DurationMs, int PauseMs);

internal interface IVarioTonePlayer : IDisposable {
  string Status { get; }

  bool EnsureAvailable();

  Task PlayAsync(VarioTone tone, CancellationToken cancellationToken);

  void Stop();
}

internal sealed class VarioController : IDisposable {
  internal const int MidTone = 700;

  private readonly object _gate = new();
  private readonly IVarioTonePlayer _player;
  private readonly Func<TimeSpan, CancellationToken, Task> _delay;
  private CancellationTokenSource? _runCancellation;
  private Task? _runTask;
  private float _climbRate;
  private string _status = "vario stopped";
  private bool _disposed;

  public event EventHandler? StateChanged;

  public VarioController(
      IVarioTonePlayer player,
      Func<TimeSpan, CancellationToken, Task>? delay = null) {
    _player = player;
    _delay = delay ?? Task.Delay;
  }

  public bool IsRunning {
    get {
      lock (_gate) {
        return _runCancellation is not null;
      }
    }
  }

  public string Status {
    get {
      lock (_gate) {
        return _status;
      }
    }
  }

  public void SetClimbRate(float climbRate) => Volatile.Write(ref _climbRate, climbRate);

  public bool Start() {
    bool started;
    lock (_gate) {
      ObjectDisposedException.ThrowIf(_disposed, this);
      if (_runCancellation is not null) {
        return true;
      }

      if (!_player.EnsureAvailable()) {
        _status = _player.Status;
        started = false;
      } else {
        var cancellation = new CancellationTokenSource();
        _runCancellation = cancellation;
        _status = "vario running";
        _runTask = Task.Run(() => RunAsync(cancellation));
        started = true;
      }
    }
    StateChanged?.Invoke(this, EventArgs.Empty);
    return started;
  }

  public void Stop() {
    CancellationTokenSource? cancellation;
    Task? runTask;
    lock (_gate) {
      cancellation = _runCancellation;
      runTask = _runTask;
    }

    if (cancellation is null) {
      return;
    }

    try {
      cancellation.Cancel();
    } catch (ObjectDisposedException) {
      // The playback loop can finish and dispose its CTS between the snapshot and cancellation.
    }
    _player.Stop();
    try {
      runTask?.GetAwaiter().GetResult();
    } catch (OperationCanceledException) {
    }

    lock (_gate) {
      if (!_disposed && _status == "vario running") {
        _status = "vario stopped";
      }
    }
    StateChanged?.Invoke(this, EventArgs.Empty);
  }

  internal static VarioTone? CreateTone(float climbRate) {
    if (!float.IsFinite(climbRate) || Math.Abs(climbRate) <= 0.3f) {
      return null;
    }

    double note = climbRate * 30d + MidTone;
    if (climbRate > 0) {
      return new VarioTone(
          Math.Clamp((int)note, 37, 20_000),
          Math.Clamp((int)(300d - climbRate * 5d), 20, 600),
          20);
    }

    return new VarioTone(Math.Clamp((int)note - 50, 37, 20_000), 600, 0);
  }

  private async Task RunAsync(CancellationTokenSource owner) {
    try {
      while (!owner.IsCancellationRequested) {
        var tone = CreateTone(Volatile.Read(ref _climbRate));
        if (tone is null) {
          await _delay(TimeSpan.FromMilliseconds(100), owner.Token).ConfigureAwait(false);
          continue;
        }

        await _player.PlayAsync(tone.Value, owner.Token).ConfigureAwait(false);
        if (tone.Value.PauseMs > 0) {
          await _delay(TimeSpan.FromMilliseconds(tone.Value.PauseMs), owner.Token)
              .ConfigureAwait(false);
        }
      }
    } catch (OperationCanceledException) when (owner.IsCancellationRequested) {
    } catch (Exception ex) {
      lock (_gate) {
        _status = $"vario unavailable: {ex.Message}";
      }
    } finally {
      _player.Stop();
      lock (_gate) {
        if (ReferenceEquals(_runCancellation, owner)) {
          _runCancellation = null;
          _runTask = null;
        }
      }
      owner.Dispose();
      StateChanged?.Invoke(this, EventArgs.Empty);
    }
  }

  public void Dispose() {
    lock (_gate) {
      if (_disposed) {
        return;
      }
      _disposed = true;
    }

    Stop();
    _player.Dispose();
  }
}

internal static class WavToneSynthesizer {
  private const int SampleRate = 44_100;
  private const int MaxCachedTones = 128;
  private static readonly object _cacheGate = new();
  private static readonly Dictionary<(int frequency, int duration), byte[]> _cache = new();
  private static readonly Queue<(int frequency, int duration)> _cacheOrder = new();

  public static byte[] GetWave(int frequencyHz, int durationMs) {
    var key = (frequencyHz, durationMs);
    lock (_cacheGate) {
      if (_cache.TryGetValue(key, out var existing)) {
        return existing;
      }
    }

    var wave = CreateWave(frequencyHz, durationMs);
    lock (_cacheGate) {
      if (_cache.TryGetValue(key, out var existing)) {
        return existing;
      }

      while (_cache.Count >= MaxCachedTones) {
        _cache.Remove(_cacheOrder.Dequeue());
      }
      _cache.Add(key, wave);
      _cacheOrder.Enqueue(key);
    }
    return wave;
  }

  internal static byte[] CreateWave(int frequencyHz, int durationMs) {
    ArgumentOutOfRangeException.ThrowIfLessThan(frequencyHz, 1);
    ArgumentOutOfRangeException.ThrowIfLessThan(durationMs, 1);

    int sampleCount = checked(SampleRate * durationMs / 1000);
    int dataLength = checked(sampleCount * sizeof(short));
    using var stream = new MemoryStream(44 + dataLength);
    using (var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true)) {
      writer.Write("RIFF"u8);
      writer.Write(36 + dataLength);
      writer.Write("WAVE"u8);
      writer.Write("fmt "u8);
      writer.Write(16);
      writer.Write((short)1);
      writer.Write((short)1);
      writer.Write(SampleRate);
      writer.Write(SampleRate * sizeof(short));
      writer.Write((short)sizeof(short));
      writer.Write((short)16);
      writer.Write("data"u8);
      writer.Write(dataLength);

      int fadeSamples = Math.Min(SampleRate / 200, sampleCount / 2);
      const double amplitude = short.MaxValue * 0.22;
      for (int i = 0; i < sampleCount; i++) {
        int edgeDistance = Math.Min(i, sampleCount - 1 - i);
        double envelope = fadeSamples == 0 ? 1d : Math.Min(1d, edgeDistance / (double)fadeSamples);
        double phase = 2d * Math.PI * frequencyHz * i / SampleRate;
        writer.Write((short)Math.Round(Math.Sin(phase) * amplitude * envelope));
      }
    }
    return stream.ToArray();
  }
}

internal sealed class LibVlcTonePlayer : IVarioTonePlayer {
  private readonly object _gate = new();
  private LibVLCSharp.Shared.LibVLC? _libVlc;
  private MediaPlayer? _mediaPlayer;
  private MemoryStream? _currentStream;
  private StreamMediaInput? _currentInput;
  private Media? _currentMedia;
  private string _status = "audio not initialized";
  private bool _disposed;

  public string Status {
    get {
      lock (_gate) {
        return _status;
      }
    }
  }

  public bool EnsureAvailable() {
    lock (_gate) {
      if (_disposed) {
        _status = "audio backend disposed";
        return false;
      }
      if (_mediaPlayer is not null) {
        return true;
      }

      try {
        _libVlc = LibVlcBootstrap.CreateInstance("--no-video-title-show", "--quiet");
        _mediaPlayer = new MediaPlayer(_libVlc);
        _status = "audio ready";
        return true;
      } catch (Exception ex) {
        ReleaseEngineCore();
        _status = $"vario unavailable: libVLC audio could not start ({ex.Message})";
        return false;
      }
    }
  }

  public async Task PlayAsync(VarioTone tone, CancellationToken cancellationToken) {
    Media media;
    var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    EventHandler<EventArgs> ended = (_, _) => completed.TrySetResult(true);
    EventHandler<EventArgs> failed = (_, _) =>
        completed.TrySetException(new InvalidOperationException("libVLC could not play the vario tone"));
    lock (_gate) {
      if (!EnsureAvailable()) {
        throw new InvalidOperationException(_status);
      }

      ReleaseCurrentMediaCore();
      _currentStream = new MemoryStream(
          WavToneSynthesizer.GetWave(tone.FrequencyHz, tone.DurationMs), writable: false);
      _currentInput = new StreamMediaInput(_currentStream);
      _currentMedia = new Media(_libVlc!, _currentInput, ":file-caching=0");
      media = _currentMedia;
      _mediaPlayer!.EndReached += ended;
      _mediaPlayer.EncounteredError += failed;
      if (!_mediaPlayer!.Play(media)) {
        _mediaPlayer.EndReached -= ended;
        _mediaPlayer.EncounteredError -= failed;
        ReleaseCurrentMediaCore();
        throw new InvalidOperationException("libVLC rejected the generated vario tone");
      }
      _status = $"vario tone {tone.FrequencyHz} Hz";
    }

    try {
      await completed.Task
          .WaitAsync(TimeSpan.FromMilliseconds(tone.DurationMs + 1_000), cancellationToken)
          .ConfigureAwait(false);
    } catch (TimeoutException) {
      // Some audio outputs do not emit EndReached; keep the loop alive after a bounded wait.
    } finally {
      lock (_gate) {
        if (_mediaPlayer is not null) {
          _mediaPlayer.EndReached -= ended;
          _mediaPlayer.EncounteredError -= failed;
        }
        if (ReferenceEquals(_currentMedia, media)) {
          ReleaseCurrentMediaCore();
        }
      }
    }
  }

  public void Stop() {
    lock (_gate) {
      ReleaseCurrentMediaCore();
      if (!_disposed && _mediaPlayer is not null) {
        _status = "audio ready";
      }
    }
  }

  private void ReleaseCurrentMediaCore() {
    try {
      _mediaPlayer?.Stop();
    } catch {
      // Continue releasing native resources if the output device disappeared.
    }
    _currentMedia?.Dispose();
    _currentMedia = null;
    _currentInput?.Dispose();
    _currentInput = null;
    _currentStream?.Dispose();
    _currentStream = null;
  }

  private void ReleaseEngineCore() {
    ReleaseCurrentMediaCore();
    _mediaPlayer?.Dispose();
    _mediaPlayer = null;
    _libVlc?.Dispose();
    _libVlc = null;
  }

  public void Dispose() {
    lock (_gate) {
      if (_disposed) {
        return;
      }
      _disposed = true;
      ReleaseEngineCore();
      _status = "audio backend disposed";
    }
  }
}

internal static class AudioVario {
  private static readonly object _gate = new();
  private static VarioController? _controller;

  public static event EventHandler? StateChanged;

  public static bool IsRunning => Controller.IsRunning;

  public static string Status => Controller.Status;

  private static VarioController Controller {
    get {
      lock (_gate) {
        if (_controller is null) {
          _controller = new VarioController(new LibVlcTonePlayer());
          _controller.StateChanged += OnControllerStateChanged;
        }
        return _controller;
      }
    }
  }

  private static void OnControllerStateChanged(object? sender, EventArgs e) =>
      StateChanged?.Invoke(sender, e);

  public static void SetClimbRate(float climbRate) => Controller.SetClimbRate(climbRate);

  public static bool Start() => Controller.Start();

  public static void Stop() => Controller.Stop();

  public static void Shutdown() {
    VarioController? controller;
    lock (_gate) {
      controller = _controller;
      _controller = null;
    }
    if (controller is not null) {
      controller.StateChanged -= OnControllerStateChanged;
      controller.Dispose();
    }
  }
}

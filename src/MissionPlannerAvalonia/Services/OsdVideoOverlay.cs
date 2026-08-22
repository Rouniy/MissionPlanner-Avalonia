using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LibVLCSharp.Shared;
using MissionPlanner;

namespace MissionPlannerAvalonia.Services;

internal sealed record OsdTelemetrySample(
    DateTime Time,
    double Roll,
    double Pitch,
    double Yaw,
    double Alt,
    double AirSpeed,
    double GroundSpeed,
    double VerticalSpeed,
    double SatCount,
    bool Armed,
    bool PrearmOk,
    int GpsFixType,
    string Mode,
    double BatteryVoltage,
    int BatteryRemaining,
    double CurrentAmps,
    double NavBearing,
    double TargetAlt,
    double TargetSpeed,
    double WindDir,
    double WindVel,
    double Aoa,
    double Ssa,
    double XTrackError,
    double TurnRate,
    double BatteryVoltage2,
    int BatteryRemaining2,
    double CurrentAmps2,
    double ThrottlePercent,
    bool Failsafe,
    bool SafetyActive,
    double LinkQuality,
    double WpDist,
    int WpNo) {

  internal static OsdTelemetrySample FromCurrentState(DateTime time, CurrentState state) => new(
      time,
      state.roll,
      state.pitch,
      state.yaw,
      state.alt,
      state.airspeed,
      state.groundspeed,
      state.verticalspeed,
      state.satcount,
      state.armed,
      state.prearmstatus,
      (int)state.gpsstatus,
      state.mode ?? "—",
      state.battery_voltage,
      SafeInteger(state.battery_remaining),
      state.current,
      state.nav_bearing,
      state.targetalt,
      state.targetairspeed,
      state.wind_dir,
      state.wind_vel,
      state.AOA,
      state.SSA,
      state.xtrack_error,
      state.turnrate,
      state.battery_voltage2,
      SafeInteger(state.battery_remaining2),
      state.current2,
      state.ch3percent,
      state.failsafe,
      state.safetyactive,
      state.linkqualitygcs,
      state.wp_dist,
      SafeInteger(state.wpno));

  private static int SafeInteger(double value) => !double.IsFinite(value)
      ? 0
      : (int)Math.Clamp(Math.Round(value), int.MinValue, int.MaxValue);
}

internal sealed class OsdTelemetryTimeline {
  private readonly OsdTelemetrySample[] _samples;

  internal OsdTelemetryTimeline(IEnumerable<OsdTelemetrySample> samples) {
    ArgumentNullException.ThrowIfNull(samples);
    _samples = [.. samples
        .OrderBy(sample => sample.Time)
        .GroupBy(sample => sample.Time)
        .Select(group => group.Last())];
    if (_samples.Length == 0) {
      throw new InvalidDataException("The telemetry log contains no readable MAVLink state.");
    }
  }

  internal int Count => _samples.Length;
  internal DateTime StartTime => _samples[0].Time;
  internal DateTime EndTime => _samples[^1].Time;

  internal OsdTelemetrySample At(TimeSpan videoPosition, TimeSpan offset) {
    if (videoPosition < TimeSpan.Zero) {
      videoPosition = TimeSpan.Zero;
    }
    DateTime target;
    try {
      target = StartTime + videoPosition + offset;
    } catch (ArgumentOutOfRangeException) {
      target = offset < TimeSpan.Zero ? DateTime.MinValue : DateTime.MaxValue;
    }

    int low = 0;
    int high = _samples.Length - 1;
    while (low <= high) {
      int middle = low + ((high - low) / 2);
      if (_samples[middle].Time <= target) {
        low = middle + 1;
      } else {
        high = middle - 1;
      }
    }
    return _samples[Math.Clamp(high, 0, _samples.Length - 1)];
  }

  internal static Task<OsdTelemetryTimeline> LoadAsync(
      string tlogPath,
      IProgress<double>? progress,
      CancellationToken cancellationToken) {
    ArgumentException.ThrowIfNullOrWhiteSpace(tlogPath);
    string fullPath = Path.GetFullPath(tlogPath);
    if (!File.Exists(fullPath)) {
      throw new FileNotFoundException("The telemetry log does not exist.", fullPath);
    }
    if (!Path.GetExtension(fullPath).Equals(".tlog", StringComparison.OrdinalIgnoreCase)) {
      throw new NotSupportedException("OSD video synchronization requires a .tlog telemetry log.");
    }
    return Task.Run(
        () => LoadCoreAsync(fullPath, progress, cancellationToken), cancellationToken);
  }

  private static async Task<OsdTelemetryTimeline> LoadCoreAsync(
      string path,
      IProgress<double>? progress,
      CancellationToken cancellationToken) {
    var samples = new List<OsdTelemetrySample>();
    using var link = new MAVLinkInterface();
    using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    using var reader = new BinaryReader(file, Encoding.UTF8, leaveOpen: true);
    link.logplaybackfile = reader;
    link.logreadmode = true;
    link.speechenabled = false;
    try {
      long length = Math.Max(1, file.Length);
      DateTime lastBucket = DateTime.MinValue;
      double lastProgress = -1;
      while (file.Position < file.Length) {
        cancellationToken.ThrowIfCancellationRequested();
        long before = file.Position;
        MAVLink.MAVLinkMessage packet = await link.readPacketAsync().ConfigureAwait(false);
        if (file.Position <= before || packet == null || packet.buffer == null
            || packet.buffer.Length == 0) {
          break;
        }

        CurrentState state = link.MAV.cs;
        state.datetime = link.lastlogread;
        state.UpdateCurrentSettings(null, true, link);
        DateTime bucket = RoundDown(link.lastlogread, TimeSpan.FromMilliseconds(100));
        var sample = OsdTelemetrySample.FromCurrentState(bucket, state);
        if (bucket == lastBucket && samples.Count > 0) {
          samples[^1] = sample;
        } else {
          samples.Add(sample);
          lastBucket = bucket;
        }
        double fraction = Math.Clamp((double)file.Position / length, 0, 1);
        if (fraction - lastProgress >= 0.005) {
          progress?.Report(fraction);
          lastProgress = fraction;
        }
      }
      cancellationToken.ThrowIfCancellationRequested();
      progress?.Report(1);
      return new OsdTelemetryTimeline(samples);
    } finally {
      link.logreadmode = false;
      link.logplaybackfile = null;
    }
  }
  internal static DateTime RoundDown(DateTime value, TimeSpan interval) {
    if (interval <= TimeSpan.Zero) {
      throw new ArgumentOutOfRangeException(nameof(interval));
    }
    long ticks = value.Ticks - (value.Ticks % interval.Ticks);
    return new DateTime(ticks, value.Kind);
  }
}

internal sealed record OsdVideoExportOptions(
    string VideoPath,
    string TlogPath,
    string OutputPath,
    int TimeOffsetSeconds,
    bool FullResolution,
    int PreviewWidth = 960,
    int JpegQuality = 85);

internal sealed record OsdVideoExportProgress(
    string Phase,
    double Fraction,
    int WrittenFrames);

internal sealed record OsdVideoExportResult(
    string OutputPath,
    int WrittenFrames,
    int Width,
    int Height,
    int FramesPerSecond,
    TimeSpan Duration);

internal interface IOsdVideoFrameRenderer : IDisposable {
  ValueTask<byte[]> RenderJpegAsync(
      byte[] bgraPixels,
      int sourceWidth,
      int sourceHeight,
      int sourceStride,
      int outputWidth,
      int outputHeight,
      OsdTelemetrySample sample,
      int jpegQuality,
      CancellationToken cancellationToken);
}

internal static class OsdVideoOverlayService {
  internal const int MinimumOffsetSeconds = -900;
  internal const int MaximumOffsetSeconds = 900;

  internal static async Task<OsdVideoExportResult> ExportAsync(
      OsdVideoExportOptions options,
      IOsdVideoFrameRenderer renderer,
      IProgress<OsdVideoExportProgress>? progress,
      CancellationToken cancellationToken) {
    Validate(options);
    ArgumentNullException.ThrowIfNull(renderer);

    progress?.Report(new OsdVideoExportProgress("Reading telemetry log…", 0, 0));
    var timelineProgress = new Progress<double>(fraction => progress?.Report(
        new OsdVideoExportProgress("Reading telemetry log…", fraction * 0.2, 0)));
    OsdTelemetryTimeline timeline = await OsdTelemetryTimeline.LoadAsync(
        options.TlogPath, timelineProgress, cancellationToken).ConfigureAwait(false);
    cancellationToken.ThrowIfCancellationRequested();

    using var session = new LibVlcOsdExportSession(options, timeline, renderer, progress);
    return await session.RunAsync(cancellationToken).ConfigureAwait(false);
  }

  internal static void Validate(OsdVideoExportOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    ArgumentException.ThrowIfNullOrWhiteSpace(options.VideoPath);
    ArgumentException.ThrowIfNullOrWhiteSpace(options.TlogPath);
    ArgumentException.ThrowIfNullOrWhiteSpace(options.OutputPath);
    string video = Path.GetFullPath(options.VideoPath);
    string tlog = Path.GetFullPath(options.TlogPath);
    string output = Path.GetFullPath(options.OutputPath);
    if (!File.Exists(video)) {
      throw new FileNotFoundException("The source video does not exist.", video);
    }
    if (!File.Exists(tlog)) {
      throw new FileNotFoundException("The telemetry log does not exist.", tlog);
    }
    if (!Path.GetExtension(tlog).Equals(".tlog", StringComparison.OrdinalIgnoreCase)) {
      throw new NotSupportedException("OSD video synchronization requires a .tlog telemetry log.");
    }
    if (!Path.GetExtension(output).Equals(".avi", StringComparison.OrdinalIgnoreCase)) {
      throw new NotSupportedException("The OSD video output must use the .avi extension.");
    }
    if (SamePath(video, output) || SamePath(tlog, output)) {
      throw new IOException("The output path must differ from both input files.");
    }
    if (File.Exists(output)) {
      throw new IOException("The output file already exists. Choose a new file name.");
    }
    if (options.TimeOffsetSeconds is < MinimumOffsetSeconds or > MaximumOffsetSeconds) {
      throw new ArgumentOutOfRangeException(
          nameof(options), $"The time offset must be between {MinimumOffsetSeconds} and "
              + $"{MaximumOffsetSeconds} seconds.");
    }
    if (options.PreviewWidth is < 160 or > 8192) {
      throw new ArgumentOutOfRangeException(nameof(options), "The preview width is invalid.");
    }
    if (options.JpegQuality is < 1 or > 100) {
      throw new ArgumentOutOfRangeException(nameof(options), "JPEG quality must be between 1 and 100.");
    }
  }

  internal static string DefaultOutputPath(string videoPath) {
    ArgumentException.ThrowIfNullOrWhiteSpace(videoPath);
    string fullPath = Path.GetFullPath(videoPath);
    string directory = Path.GetDirectoryName(fullPath)
        ?? throw new IOException("The video path has no parent directory.");
    string stem = Path.GetFileNameWithoutExtension(fullPath) + "-overlay";
    string candidate = Path.Combine(directory, stem + ".avi");
    for (int suffix = 2; File.Exists(candidate); suffix++) {
      candidate = Path.Combine(directory, $"{stem}-{suffix}.avi");
    }
    return candidate;
  }

  private static bool SamePath(string left, string right) => string.Equals(
      Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
      Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
      OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}

internal sealed class LibVlcOsdExportSession : IDisposable {
  private readonly OsdVideoExportOptions _options;
  private readonly OsdTelemetryTimeline _timeline;
  private readonly IOsdVideoFrameRenderer _renderer;
  private readonly IProgress<OsdVideoExportProgress>? _progress;
  private readonly object _bufferGate = new();
  private readonly object _renderGate = new();
  private readonly TaskCompletionSource<CompletionReason> _completion = new(
      TaskCreationOptions.RunContinuationsAsynchronously);
  private LibVLCSharp.Shared.LibVLC? _libVlc;
  private Media? _media;
  private MediaPlayer? _player;
  private MjpegAviWriter? _writer;
  private IntPtr _rawBuffer;
  private IntPtr _alignedBuffer;
  private byte[]? _decodedFrame;
  private int _visibleWidth;
  private int _visibleHeight;
  private int _sourceWidth;
  private int _sourceHeight;
  private int _sourceStride;
  private int _outputWidth;
  private int _outputHeight;
  private int _framesPerSecond;
  private int _writtenFrames;
  private long _durationMilliseconds;
  private byte[]? _lastWrittenRawFrame;
  private byte[]? _lastRenderedJpeg;
  private Exception? _callbackError;
  private CancellationToken _cancellationToken;
  private bool _disposed;

  internal LibVlcOsdExportSession(
      OsdVideoExportOptions options,
      OsdTelemetryTimeline timeline,
      IOsdVideoFrameRenderer renderer,
      IProgress<OsdVideoExportProgress>? progress) {
    _options = options;
    _timeline = timeline;
    _renderer = renderer;
    _progress = progress;
  }

  internal async Task<OsdVideoExportResult> RunAsync(CancellationToken cancellationToken) {
    ObjectDisposedException.ThrowIf(_disposed, this);
    _cancellationToken = cancellationToken;
    LibVlcBootstrap.Initialize();
    _libVlc = new LibVLCSharp.Shared.LibVLC(
        "--no-video-title-show", "--quiet", "--no-audio");
    _media = new Media(_libVlc, Path.GetFullPath(_options.VideoPath), FromType.FromPath);
    MediaParsedStatus parseStatus = await _media.Parse(
        MediaParseOptions.ParseLocal, 15_000, cancellationToken).ConfigureAwait(false);
    if (parseStatus != MediaParsedStatus.Done) {
      throw new InvalidDataException($"libVLC could not inspect the video ({parseStatus}).");
    }

    MediaTrack[] videoTracks = [.. _media.Tracks.Where(
        track => track.TrackType == TrackType.Video)];
    if (videoTracks.Length == 0 || videoTracks[0].Data.Video.Width == 0
        || videoTracks[0].Data.Video.Height == 0) {
      throw new InvalidDataException("The selected file contains no readable video track.");
    }
    VideoTrack video = videoTracks[0].Data.Video;
    _visibleWidth = checked((int)video.Width);
    _visibleHeight = checked((int)video.Height);
    _durationMilliseconds = Math.Max(0, _media.Duration);
    double frameRate = video.FrameRateDen > 0
        ? (double)video.FrameRateNum / video.FrameRateDen
        : HudFrameRecorder.DefaultFramesPerSecond;
    _framesPerSecond = Math.Clamp((int)Math.Round(frameRate), 1, 120);

    _player = new MediaPlayer(_libVlc) {
      EnableKeyInput = false,
      EnableMouseInput = false,
      Mute = true,
    };
    _player.SetVideoFormatCallbacks(ConfigureVideo, CleanupVideo);
    _player.SetVideoCallbacks(LockVideo, UnlockVideo, DisplayVideo);
    _player.EndReached += OnEndReached;
    _player.EncounteredError += OnEncounteredError;
    using CancellationTokenRegistration registration = cancellationToken.Register(
        () => _completion.TrySetCanceled(cancellationToken));

    _progress?.Report(new OsdVideoExportProgress("Opening source video…", 0.2, 0));
    if (!_player.Play(_media)) {
      throw new InvalidDataException("libVLC rejected the selected video.");
    }

    try {
      await _completion.Task.ConfigureAwait(false);
    } finally {
      try {
        _player.Stop();
      } catch {
        // Continue finalizing a partial AVI after the decoder has already stopped itself.
      }
    }
    lock (_renderGate) {
      cancellationToken.ThrowIfCancellationRequested();
      if (_callbackError != null) {
        throw new IOException(
            "OSD frame rendering failed. A playable partial AVI is retained when frames were written.",
            _callbackError);
      }
      FillWithLastFrame(ExpectedFrameCount());
      if (_writtenFrames == 0 || _writer == null) {
        throw new InvalidDataException("The video ended without producing a decodable frame.");
      }

      _writer.Dispose();
      _writer = null;
      _progress?.Report(new OsdVideoExportProgress("OSD video complete.", 1, _writtenFrames));
      return new OsdVideoExportResult(
          Path.GetFullPath(_options.OutputPath),
          _writtenFrames,
          _outputWidth,
          _outputHeight,
          _framesPerSecond,
          TimeSpan.FromMilliseconds(_durationMilliseconds));
    }
  }

  private uint ConfigureVideo(
      ref IntPtr opaque,
      IntPtr chroma,
      ref uint width,
      ref uint height,
      ref uint pitches,
      ref uint lines) {
    try {
      if (_visibleWidth is < 1 or > 8192 || _visibleHeight is < 1 or > 8192) {
        return 0;
      }
      width = checked((uint)_visibleWidth);
      height = checked((uint)_visibleHeight);
      byte[] rv32 = Encoding.ASCII.GetBytes("RV32");
      Marshal.Copy(rv32, 0, chroma, rv32.Length);
      uint pitch = checked((width * 4u + 31u) & ~31u);
      uint lineCount = checked((height + 31u) & ~31u);
      pitches = pitch;
      lines = lineCount;
      lock (_bufferGate) {
        FreeBuffer();
        long bytes = checked((long)pitch * lineCount);
        _rawBuffer = Marshal.AllocHGlobal(new IntPtr(checked(bytes + 31)));
        long aligned = (_rawBuffer.ToInt64() + 31L) & ~31L;
        _alignedBuffer = new IntPtr(aligned);
        _sourceWidth = checked((int)width);
        _sourceHeight = checked((int)height);
        _sourceStride = checked((int)pitch);
        (_outputWidth, _outputHeight) = OutputSize(
            _sourceWidth, _sourceHeight, _options.FullResolution, _options.PreviewWidth);
      }
      return 1;
    } catch (Exception ex) {
      FailFromCallback(ex);
      return 0;
    }
  }

  private IntPtr LockVideo(IntPtr opaque, IntPtr planes) {
    lock (_bufferGate) {
      if (_alignedBuffer == IntPtr.Zero) {
        return IntPtr.Zero;
      }
      Marshal.WriteIntPtr(planes, _alignedBuffer);
      return _alignedBuffer;
    }
  }

  private void UnlockVideo(IntPtr opaque, IntPtr picture, IntPtr planes) {
    try {
      lock (_bufferGate) {
        if (_alignedBuffer == IntPtr.Zero || _sourceStride <= 0 || _sourceHeight <= 0) {
          return;
        }
        int length = checked(_sourceStride * _sourceHeight);
        var frame = new byte[length];
        Marshal.Copy(_alignedBuffer, frame, 0, length);
        Volatile.Write(ref _decodedFrame, frame);
      }
    } catch (Exception ex) {
      FailFromCallback(ex);
    }
  }

  private void DisplayVideo(IntPtr opaque, IntPtr picture) {
    lock (_renderGate) {
      if (_completion.Task.IsCompleted) {
        return;
      }
      try {
        _cancellationToken.ThrowIfCancellationRequested();
        byte[] frame = Volatile.Read(ref _decodedFrame)
            ?? throw new InvalidDataException("libVLC displayed a frame before unlocking its pixel buffer.");
        long videoMilliseconds = Math.Max(0, _player?.Time ?? 0);
        if (_lastWrittenRawFrame != null && frame.AsSpan().SequenceEqual(_lastWrittenRawFrame)) {
          return;
        }
        long frameBucket = checked(videoMilliseconds * _framesPerSecond / 1000);
        FillWithLastFrame(frameBucket);
        OsdTelemetrySample sample = _timeline.At(
            TimeSpan.FromMilliseconds(videoMilliseconds),
            TimeSpan.FromSeconds(_options.TimeOffsetSeconds));
        byte[] jpeg = _renderer.RenderJpegAsync(
                frame,
                _sourceWidth,
                _sourceHeight,
                _sourceStride,
                _outputWidth,
                _outputHeight,
                sample,
                _options.JpegQuality,
                _cancellationToken)
            .AsTask().GetAwaiter().GetResult();
        WriteFrame(jpeg);
        _lastWrittenRawFrame = frame;
        _lastRenderedJpeg = jpeg;
        double videoFraction = _durationMilliseconds > 0
            ? Math.Clamp((double)videoMilliseconds / _durationMilliseconds, 0, 1)
            : 0;
        _progress?.Report(new OsdVideoExportProgress(
            "Rendering synchronized HUD overlay…", 0.2 + videoFraction * 0.8, _writtenFrames));
      } catch (OperationCanceledException) {
        _completion.TrySetCanceled(_cancellationToken);
      } catch (Exception ex) {
        FailFromCallback(ex);
      }
    }
  }

  private void CleanupVideo(ref IntPtr opaque) {
    lock (_bufferGate) {
      FreeBuffer();
    }
  }

  private void OnEndReached(object? sender, EventArgs e) =>
      _completion.TrySetResult(CompletionReason.EndReached);

  private void OnEncounteredError(object? sender, EventArgs e) =>
      FailFromCallback(new InvalidDataException("libVLC could not decode the selected video."));

  private void FailFromCallback(Exception exception) {
    Interlocked.CompareExchange(ref _callbackError, exception, null);
    _completion.TrySetResult(CompletionReason.Error);
  }

  private long ExpectedFrameCount() => Math.Max(
      1,
      Math.Min(
          int.MaxValue,
          (long)Math.Round(
              (double)_durationMilliseconds * _framesPerSecond / 1000,
              MidpointRounding.AwayFromZero)));

  private void FillWithLastFrame(long targetFrameCount) {
    byte[]? jpeg = _lastRenderedJpeg;
    if (jpeg == null) {
      return;
    }
    targetFrameCount = Math.Min(targetFrameCount, int.MaxValue);
    while (_writtenFrames < targetFrameCount) {
      _cancellationToken.ThrowIfCancellationRequested();
      WriteFrame(jpeg);
    }
  }

  private void WriteFrame(byte[] jpeg) {
    _writer ??= new MjpegAviWriter(
        Path.GetFullPath(_options.OutputPath), _outputWidth, _outputHeight, _framesPerSecond);
    _writer.WriteJpeg(jpeg);
    _writtenFrames++;
    if (_writtenFrames % _framesPerSecond == 0) {
      _writer.Checkpoint();
    }
  }

  internal static (int Width, int Height) OutputSize(
      int sourceWidth, int sourceHeight, bool fullResolution, int previewWidth) {
    ArgumentOutOfRangeException.ThrowIfLessThan(sourceWidth, 1);
    ArgumentOutOfRangeException.ThrowIfLessThan(sourceHeight, 1);
    if (fullResolution || sourceWidth <= previewWidth) {
      return (sourceWidth, sourceHeight);
    }
    int height = Math.Max(2, (int)Math.Round((double)sourceHeight * previewWidth / sourceWidth));
    if ((height & 1) != 0) {
      height++;
    }
    return (previewWidth, height);
  }

  private void FreeBuffer() {
    if (_rawBuffer != IntPtr.Zero) {
      Marshal.FreeHGlobal(_rawBuffer);
    }
    _rawBuffer = IntPtr.Zero;
    _alignedBuffer = IntPtr.Zero;
    _decodedFrame = null;
  }

  public void Dispose() {
    if (_disposed) {
      return;
    }
    _disposed = true;
    if (_player != null) {
      _player.EndReached -= OnEndReached;
      _player.EncounteredError -= OnEncounteredError;
      try {
        _player.Stop();
      } catch {
        // Release all remaining managed/native resources below.
      }
    }
    lock (_renderGate) {
      _writer?.Dispose();
      _writer = null;
    }
    lock (_bufferGate) {
      FreeBuffer();
    }
    _player?.Dispose();
    _media?.Dispose();
    _libVlc?.Dispose();
    _player = null;
    _media = null;
    _libVlc = null;
  }

  private enum CompletionReason {
    EndReached,
    Error,
  }
}

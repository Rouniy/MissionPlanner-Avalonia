using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using SkiaSharp;

namespace MissionPlannerAvalonia.Services;

internal enum HudPixelLayout {
  Bgra8888,
  Rgba8888,
  EncodedImage,
}

internal readonly record struct HudRecordingResult(
    string Path,
    int CapturedFrames,
    int WrittenFrames,
    int DroppedFrames,
    Exception? Error);

/// <summary>
/// Encodes Avalonia HUD pixel snapshots to the MJPEG/AVI format used by Mission Planner.
/// The one-frame queue deliberately drops capture work when encoding cannot keep up, so
/// recording can never build an unbounded UI or memory backlog.
/// </summary>
internal sealed class HudFrameRecorder : IAsyncDisposable {
  internal const int DefaultFramesPerSecond = 25;
  internal const int DefaultJpegQuality = 85;

  private readonly string _path;
  private readonly int _width;
  private readonly int _height;
  private readonly int _fps;
  private readonly int _jpegQuality;
  private readonly MjpegAviWriter _writer;
  private readonly Channel<RawFrame> _frames;
  private readonly Stopwatch _clock = Stopwatch.StartNew();
  private readonly Task _worker;
  private int _capturedFrames;
  private int _writtenFrames;
  private int _droppedFrames;
  private int _pendingFrames;
  private int _stopping;
  private Exception? _error;

  public HudFrameRecorder(
      string path,
      int width,
      int height,
      int fps = DefaultFramesPerSecond,
      int jpegQuality = DefaultJpegQuality) {
    ArgumentException.ThrowIfNullOrWhiteSpace(path);
    ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
    ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
    ArgumentOutOfRangeException.ThrowIfLessThan(fps, 1);
    ArgumentOutOfRangeException.ThrowIfLessThan(jpegQuality, 1);
    ArgumentOutOfRangeException.ThrowIfGreaterThan(jpegQuality, 100);

    _path = System.IO.Path.GetFullPath(path);
    _width = width;
    _height = height;
    _fps = fps;
    _jpegQuality = jpegQuality;
    _writer = new MjpegAviWriter(_path, width, height, fps);
    _frames = Channel.CreateBounded<RawFrame>(new BoundedChannelOptions(1) {
      FullMode = BoundedChannelFullMode.Wait,
      SingleReader = true,
      SingleWriter = true,
    });
    _worker = Task.Run(WriteFramesAsync);
  }

  public string Path => _path;

  public bool IsActive => Volatile.Read(ref _stopping) == 0 && !_worker.IsCompleted;

  public bool CanAcceptFrame => IsActive && Volatile.Read(ref _pendingFrames) < 2;

  public Exception? Error => _error;

  /// <summary>
  /// Takes ownership of a buffer rented from <see cref="ArrayPool{T}.Shared"/>.
  /// The buffer is returned to the pool whether the frame is queued or dropped.
  /// </summary>
  public bool SubmitPooledFrame(
      byte[] pixels,
      int width,
      int height,
      int stride,
      HudPixelLayout layout) {
    ArgumentNullException.ThrowIfNull(pixels);
    if (layout == HudPixelLayout.EncodedImage) {
      ArrayPool<byte>.Shared.Return(pixels);
      throw new ArgumentException("Use SubmitPooledEncodedFrame for encoded images.", nameof(layout));
    }
    if (width < 1 || height < 1 || stride < checked(width * 4)
        || pixels.Length < checked(stride * height)) {
      ArrayPool<byte>.Shared.Return(pixels);
      throw new ArgumentException("The HUD pixel buffer dimensions are invalid.", nameof(pixels));
    }

    return Submit(new RawFrame(
        pixels, checked(stride * height), width, height, stride, layout, _clock.Elapsed));
  }

  /// <summary>
  /// Takes ownership of a pooled PNG/JPEG buffer. This is the portable fallback for Avalonia
  /// render backends which do not expose a readable pixel format.
  /// </summary>
  public bool SubmitPooledEncodedFrame(byte[] image, int length, int width, int height) {
    ArgumentNullException.ThrowIfNull(image);
    if (length < 1 || length > image.Length || width < 1 || height < 1) {
      ArrayPool<byte>.Shared.Return(image);
      throw new ArgumentException("The encoded HUD image is invalid.", nameof(image));
    }
    return Submit(new RawFrame(
        image, length, width, height, 0, HudPixelLayout.EncodedImage, _clock.Elapsed));
  }

  private bool Submit(RawFrame frame) {
    if (!IsActive) {
      ArrayPool<byte>.Shared.Return(frame.Pixels);
      Interlocked.Increment(ref _droppedFrames);
      return false;
    }

    Interlocked.Increment(ref _pendingFrames);
    if (IsActive && _frames.Writer.TryWrite(frame)) {
      Interlocked.Increment(ref _capturedFrames);
      return true;
    }

    Interlocked.Decrement(ref _pendingFrames);
    ArrayPool<byte>.Shared.Return(frame.Pixels);
    Interlocked.Increment(ref _droppedFrames);
    return false;
  }

  public async ValueTask<HudRecordingResult> StopAsync() {
    if (Interlocked.Exchange(ref _stopping, 1) == 0) {
      _frames.Writer.TryComplete();
    }
    await _worker.ConfigureAwait(false);
    return Result();
  }

  public async ValueTask DisposeAsync() {
    await StopAsync().ConfigureAwait(false);
  }

  private HudRecordingResult Result() => new(
      _path,
      Volatile.Read(ref _capturedFrames),
      Volatile.Read(ref _writtenFrames),
      Volatile.Read(ref _droppedFrames),
      _error);

  private async Task WriteFramesAsync() {
    TimeSpan? previousCapture = null;
    try {
      await foreach (RawFrame frame in _frames.Reader.ReadAllAsync().ConfigureAwait(false)) {
        try {
          byte[] jpeg = EncodeJpeg(frame);
          int copies = HudFrameTimeline.CopiesForInterval(previousCapture, frame.CapturedAt, _fps);
          for (int i = 0; i < copies; i++) {
            _writer.WriteJpeg(jpeg);
          }
          _writer.Checkpoint();
          Interlocked.Add(ref _writtenFrames, copies);
          previousCapture = frame.CapturedAt;
        } finally {
          Interlocked.Decrement(ref _pendingFrames);
          ArrayPool<byte>.Shared.Return(frame.Pixels);
        }
      }
    } catch (Exception ex) {
      _error = ex;
      _frames.Writer.TryComplete(ex);
      while (_frames.Reader.TryRead(out RawFrame frame)) {
        Interlocked.Decrement(ref _pendingFrames);
        ArrayPool<byte>.Shared.Return(frame.Pixels);
      }
    } finally {
      try {
        _writer.Dispose();
      } catch (Exception ex) {
        _error ??= ex;
      }
    }
  }

  private byte[] EncodeJpeg(RawFrame frame) {
    using SKBitmap source = frame.Layout == HudPixelLayout.EncodedImage
        ? SKBitmap.Decode(frame.Pixels.AsSpan(0, frame.DataLength))
            ?? throw new InvalidDataException("The captured HUD image could not be decoded.")
        : CopyRawBitmap(frame);

    using SKImage sourceImage = SKImage.FromBitmap(source);
    if (source.Width == _width && source.Height == _height) {
      using SKData encoded = sourceImage.Encode(SKEncodedImageFormat.Jpeg, _jpegQuality);
      return encoded.ToArray();
    }

    var targetInfo = new SKImageInfo(
        _width, _height, SKColorType.Bgra8888, SKAlphaType.Opaque);
    using SKSurface target = SKSurface.Create(targetInfo)
        ?? throw new InvalidOperationException("Could not allocate a HUD recording surface.");
    target.Canvas.Clear(SKColors.Black);
    target.Canvas.DrawImage(sourceImage,
        new SKRect(0, 0, _width, _height),
        new SKSamplingOptions(SKFilterMode.Linear), null);
    target.Canvas.Flush();
    using SKImage resized = target.Snapshot();
    using SKData resizedJpeg = resized.Encode(SKEncodedImageFormat.Jpeg, _jpegQuality);
    return resizedJpeg.ToArray();
  }

  private static SKBitmap CopyRawBitmap(RawFrame frame) {
    var colorType = frame.Layout == HudPixelLayout.Bgra8888
        ? SKColorType.Bgra8888
        : SKColorType.Rgba8888;
    var source = new SKBitmap(new SKImageInfo(
        frame.Width, frame.Height, colorType, SKAlphaType.Premul));
    try {
      if (source.RowBytes == frame.Stride) {
        Marshal.Copy(frame.Pixels, 0, source.GetPixels(), frame.DataLength);
      } else {
        for (int row = 0; row < frame.Height; row++) {
          Marshal.Copy(frame.Pixels, row * frame.Stride,
              source.GetPixels() + row * source.RowBytes, frame.Width * 4);
        }
      }
      return source;
    } catch {
      source.Dispose();
      throw;
    }
  }

  private readonly record struct RawFrame(
      byte[] Pixels,
      int DataLength,
      int Width,
      int Height,
      int Stride,
      HudPixelLayout Layout,
      TimeSpan CapturedAt);
}

internal static class HudFrameTimeline {
  /// <summary>
  /// Repeats the latest encoded frame when captures were dropped, matching Mission Planner's
  /// wall-clock AVI behavior. Pauses longer than one second are collapsed so resume/suspend
  /// cannot produce an enormous run of duplicate frames.
  /// </summary>
  public static int CopiesForInterval(TimeSpan? previous, TimeSpan current, int fps) {
    if (previous == null || current <= previous.Value) {
      return 1;
    }
    double elapsedFrames = (current - previous.Value).TotalSeconds * fps;
    return Math.Clamp((int)Math.Round(elapsedFrames), 1, fps);
  }
}

internal static class HudRecordingPath {
  public static string Create(string directory, DateTime localTime) {
    ArgumentException.ThrowIfNullOrWhiteSpace(directory);
    Directory.CreateDirectory(directory);
    string stem = localTime.ToString("yyyy-MM-dd HH-mm-ss");
    string candidate = System.IO.Path.Combine(directory, stem + ".avi");
    for (int suffix = 2; File.Exists(candidate); suffix++) {
      candidate = System.IO.Path.Combine(directory, $"{stem}-{suffix}.avi");
    }
    return candidate;
  }
}

/// <summary>
/// Small cross-platform RIFF AVI writer for a single MJPEG video stream. Its layout follows
/// Mission Planner's AviWriter, while avoiding recursive frame insertion and native/GDI APIs.
/// </summary>
internal sealed class MjpegAviWriter : IDisposable {
  private static readonly byte[] _jpegStart = [0xff, 0xd8];
  private readonly FileStream _stream;
  private readonly BinaryWriter _writer;
  private readonly int _width;
  private readonly int _height;
  private readonly int _fps;
  private readonly List<AviIndexEntry> _index = [];
  private readonly long _aviHeaderPosition;
  private readonly long _streamHeaderPosition;
  private readonly long _moviSizePosition;
  private readonly long _moviDataPosition;
  private uint _largestFrame;
  private bool _disposed;

  public MjpegAviWriter(string path, int width, int height, int fps) {
    ArgumentException.ThrowIfNullOrWhiteSpace(path);
    ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
    ArgumentOutOfRangeException.ThrowIfGreaterThan(width, short.MaxValue);
    ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
    ArgumentOutOfRangeException.ThrowIfGreaterThan(height, short.MaxValue);
    ArgumentOutOfRangeException.ThrowIfLessThan(fps, 1);

    _width = width;
    _height = height;
    _fps = fps;
    _stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read,
        bufferSize: 128 * 1024, FileOptions.SequentialScan);
    _writer = new BinaryWriter(_stream, Encoding.ASCII, leaveOpen: true);

    FourCc("RIFF");
    _writer.Write(0u);
    FourCc("AVI ");

    FourCc("LIST");
    long headerListSizePosition = _stream.Position;
    _writer.Write(0u);
    FourCc("hdrl");

    FourCc("avih");
    _writer.Write(56u);
    _aviHeaderPosition = _stream.Position;
    WriteMainHeader();

    FourCc("LIST");
    long streamListSizePosition = _stream.Position;
    _writer.Write(0u);
    FourCc("strl");

    FourCc("strh");
    _writer.Write(56u);
    _streamHeaderPosition = _stream.Position;
    WriteStreamHeader();

    FourCc("strf");
    _writer.Write(40u);
    WriteBitmapHeader();

    long endOfHeader = _stream.Position;
    PatchUInt32(streamListSizePosition,
        CheckedUInt32(endOfHeader - (streamListSizePosition + sizeof(uint))));
    PatchUInt32(headerListSizePosition,
        CheckedUInt32(endOfHeader - (headerListSizePosition + sizeof(uint))));
    _stream.Position = endOfHeader;

    FourCc("LIST");
    _moviSizePosition = _stream.Position;
    _writer.Write(4u);
    FourCc("movi");
    _moviDataPosition = _stream.Position;
    Checkpoint();
  }

  public int FrameCount => _index.Count;

  public void WriteJpeg(ReadOnlySpan<byte> jpeg) {
    ObjectDisposedException.ThrowIf(_disposed, this);
    if (jpeg.Length < 4 || !jpeg[..2].SequenceEqual(_jpegStart)) {
      throw new InvalidDataException("The AVI frame is not a JPEG image.");
    }
    EnsureRiffCapacity(checked(jpeg.Length + 24L));

    long chunkPosition = _stream.Position;
    FourCc("00dc");
    _writer.Write((uint)jpeg.Length);
    _writer.Write(jpeg);
    if ((jpeg.Length & 1) != 0) {
      _writer.Write((byte)0);
    }

    uint offset = CheckedUInt32(chunkPosition - _moviDataPosition + 4);
    _index.Add(new AviIndexEntry(offset, (uint)jpeg.Length));
    _largestFrame = Math.Max(_largestFrame, (uint)jpeg.Length);
  }

  public void Checkpoint() {
    ObjectDisposedException.ThrowIf(_disposed, this);
    _writer.Flush();
    long end = _stream.Position;
    PatchHeaders(end, end);
    _stream.Position = end;
    _writer.Flush();
  }

  public void Dispose() {
    if (_disposed) {
      return;
    }
    _disposed = true;
    Exception? failure = null;
    try {
      _writer.Flush();
      long moviEnd = _stream.Position;
      EnsureRiffCapacity(checked(8L + _index.Count * 16L));
      FourCc("idx1");
      _writer.Write(checked((uint)(_index.Count * 16)));
      foreach (AviIndexEntry entry in _index) {
        FourCc("00dc");
        _writer.Write(0x10u);
        _writer.Write(entry.Offset);
        _writer.Write(entry.Size);
      }
      _writer.Flush();
      long fileEnd = _stream.Position;
      PatchHeaders(moviEnd, fileEnd);
      _stream.Position = fileEnd;
      _writer.Flush();
      _stream.Flush(flushToDisk: true);
    } catch (Exception ex) {
      failure = ex;
    } finally {
      _writer.Dispose();
      _stream.Dispose();
    }
    if (failure != null) {
      throw failure;
    }
  }

  private void PatchHeaders(long moviEnd, long fileEnd) {
    PatchUInt32(4, CheckedUInt32(fileEnd - 8));
    PatchUInt32(_moviSizePosition,
        CheckedUInt32(moviEnd - (_moviSizePosition + sizeof(uint))));
    long returnPosition = _stream.Position;
    _stream.Position = _aviHeaderPosition;
    WriteMainHeader();
    _stream.Position = _streamHeaderPosition;
    WriteStreamHeader();
    _stream.Position = returnPosition;
  }

  private void WriteMainHeader() {
    _writer.Write((uint)(1_000_000 / _fps));
    _writer.Write(SaturatingUInt32((ulong)_largestFrame * (uint)_fps));
    _writer.Write(0u);
    _writer.Write(_index.Count == 0 ? 0u : 0x10u);
    _writer.Write((uint)_index.Count);
    _writer.Write(0u);
    _writer.Write(1u);
    _writer.Write(_largestFrame);
    _writer.Write((uint)_width);
    _writer.Write((uint)_height);
    _writer.Write(0u);
    _writer.Write(0u);
    _writer.Write(0u);
    _writer.Write(0u);
  }

  private void WriteStreamHeader() {
    FourCc("vids");
    FourCc("MJPG");
    _writer.Write(0u);
    _writer.Write((ushort)0);
    _writer.Write((ushort)0);
    _writer.Write(0u);
    _writer.Write(1u);
    _writer.Write((uint)_fps);
    _writer.Write(0u);
    _writer.Write((uint)_index.Count);
    _writer.Write(_largestFrame);
    _writer.Write(uint.MaxValue);
    _writer.Write(0u);
    _writer.Write((short)0);
    _writer.Write((short)0);
    _writer.Write((short)_width);
    _writer.Write((short)_height);
  }

  private void WriteBitmapHeader() {
    _writer.Write(40u);
    _writer.Write(_width);
    _writer.Write(_height);
    _writer.Write((ushort)1);
    _writer.Write((ushort)24);
    FourCc("MJPG");
    _writer.Write(SaturatingUInt32((ulong)_width * (uint)_height * 3));
    _writer.Write(0);
    _writer.Write(0);
    _writer.Write(0u);
    _writer.Write(0u);
  }

  private void PatchUInt32(long position, uint value) {
    long returnPosition = _stream.Position;
    _stream.Position = position;
    _writer.Write(value);
    _stream.Position = returnPosition;
  }

  private void FourCc(string value) {
    if (value.Length != 4) {
      throw new ArgumentException("A RIFF FourCC must contain four characters.", nameof(value));
    }
    _writer.Write(Encoding.ASCII.GetBytes(value));
  }

  private void EnsureRiffCapacity(long additionalBytes) {
    if (_stream.Position + additionalBytes - 8 > uint.MaxValue) {
      throw new IOException("The HUD AVI reached the 4 GiB RIFF size limit. Stop and start a new recording.");
    }
  }

  private static uint CheckedUInt32(long value) {
    if (value is < 0 or > uint.MaxValue) {
      throw new IOException("The HUD AVI exceeded the RIFF size limit.");
    }
    return (uint)value;
  }

  private static uint SaturatingUInt32(ulong value) =>
      value > uint.MaxValue ? uint.MaxValue : (uint)value;

  private readonly record struct AviIndexEntry(uint Offset, uint Size);
}

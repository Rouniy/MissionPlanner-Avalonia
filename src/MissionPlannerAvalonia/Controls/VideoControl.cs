using System;
using System.IO;

using Avalonia.Controls;
using Avalonia.Threading;

using LibVLCSharp.Avalonia;
using LibVLCSharp.Shared;
using MissionPlannerAvalonia.Services;

namespace MissionPlannerAvalonia.Controls;

public class VideoControl : UserControl, IDisposable {
  private readonly VideoView _videoView;
  private LibVLCSharp.Shared.LibVLC? _libVlc;
  private MediaPlayer? _mediaPlayer;
  private Media? _currentMedia;
  private string? _temporarySourceFile;
  private string? _resolvedMrl;
  private FromType _resolvedFromType = FromType.FromLocation;
  private bool _isAvailable;
  private bool _disposed;
  private string _status = "video not started";

  public event EventHandler? StatusChanged;

  public VideoControl() {
    _videoView = new VideoView();
    Content = _videoView;
    InitializeCore();
  }

  public bool IsAvailable => _isAvailable;

  public string Status => _status;

  internal Control? OverlayContent {
    get => _videoView.Content as Control;
    set => _videoView.Content = value;
  }

  internal double VideoAspectRatio {
    get {
      if (_mediaPlayer != null) {
        try {
          uint width = 0;
          uint height = 0;
          if (_mediaPlayer.Size(0, ref width, ref height) && width > 0 && height > 0) {
            return (double)width / height;
          }
        } catch {
          // The decoder has not published a video size yet.
        }
      }
      return 16.0 / 9.0;
    }
  }

  public bool Play(string mrl) {
    if (_disposed || !_isAvailable || _libVlc is null || _mediaPlayer is null) {
      return false;
    }

    if (string.IsNullOrWhiteSpace(mrl)) {
      SetStatus("no source set");
      return false;
    }

    try {
      ReleaseCurrentMedia();
      var source = VideoSourceResolver.Resolve(mrl);
      _temporarySourceFile = source.TemporaryFile;
      _resolvedMrl = source.Mrl;
      _resolvedFromType = source.FromType;
      _currentMedia = new Media(_libVlc, source.Mrl, source.FromType);
      if (source.FromType == FromType.FromLocation) {
        _currentMedia.AddOption(":network-caching=250");
      }
      bool started = _mediaPlayer.Play(_currentMedia);
      if (!started) {
        SetStatus($"libVLC rejected source: {source.Mrl}");
        ReleaseCurrentMedia();
        return false;
      }
      _currentMrl = mrl.Trim();
      SetStatus($"opening: {source.Mrl}");
      return true;
    } catch (Exception ex) {
      ReleaseCurrentMedia();
      SetStatus($"play failed: {ex.Message}");
      return false;
    }
  }

  public void Stop() {
    if (!_isAvailable || _mediaPlayer is null) {
      return;
    }

    try {
      _mediaPlayer.Stop();
      ReleaseCurrentMedia();
      SetStatus("stopped");
    } catch (Exception ex) {
      SetStatus($"stop failed: {ex.Message}");
    }
  }

  public bool TryRecord(string outPath) {
    if (_disposed || !_isAvailable || _libVlc is null || _mediaPlayer is null) {
      return false;
    }

    if (string.IsNullOrWhiteSpace(outPath) || string.IsNullOrWhiteSpace(_resolvedMrl)) {
      SetStatus("record unavailable: no active source");
      return false;
    }

    try {
      string destination = Path.GetFullPath(outPath);
      string soutDestination = destination.Replace("'", "\\'", StringComparison.Ordinal);
      var sout = $":sout=#duplicate{{dst=display,dst=std{{access=file,mux=ts,dst='{soutDestination}'}}}}";
      _mediaPlayer.Stop();
      _currentMedia?.Dispose();
      _currentMedia = null;
      _currentMedia = new Media(_libVlc, _resolvedMrl, _resolvedFromType);
      _currentMedia.AddOption(sout);
      _currentMedia.AddOption(":sout-keep");
      bool started = _mediaPlayer.Play(_currentMedia);
      SetStatus(started ? $"recording: {destination}" : "record failed: libVLC rejected the source");
      return started;
    } catch (Exception ex) {
      SetStatus($"record failed: {ex.Message}");
      return false;
    }
  }

  public void Snapshot(string outPath) {
    if (!_isAvailable || _mediaPlayer is null) {
      return;
    }

    try {
      _mediaPlayer.TakeSnapshot(0, outPath, 0, 0);
      SetStatus($"snapshot: {outPath}");
    } catch (Exception ex) {
      SetStatus($"snapshot failed: {ex.Message}");
    }
  }

  private string? _currentMrl;

  private void InitializeCore() {

    try {
      LibVlcBootstrap.Initialize();
      _libVlc = new LibVLCSharp.Shared.LibVLC("--no-video-title-show", "--quiet");
      _mediaPlayer = new MediaPlayer(_libVlc);
      _mediaPlayer.EnableKeyInput = false;
      _mediaPlayer.EnableMouseInput = false;
      _mediaPlayer.Opening += (_, _) => PostStatus("opening video stream…");
      _mediaPlayer.Buffering += (_, args) => PostStatus($"buffering: {args.Cache:0}%");
      _mediaPlayer.Playing += (_, _) => PostStatus($"playing: {_currentMrl}");
      _mediaPlayer.EncounteredError += (_, _) =>
          PostStatus("libVLC could not open the source. Check the URL, protocol, codec and network reachability.");
      _mediaPlayer.EndReached += (_, _) => PostStatus("video stream ended");
      _videoView.MediaPlayer = _mediaPlayer;
      _isAvailable = true;
      SetStatus("video ready");
    } catch (Exception ex) {
      _isAvailable = false;
      SetStatus(
          "video unavailable: libVLC could not be loaded (" + ex.Message
          + "). Install the platform libVLC runtime and codec plugins.");
    }
  }

  private void PostStatus(string value) {
    if (Dispatcher.UIThread.CheckAccess()) {
      SetStatus(value);
    } else {
      Dispatcher.UIThread.Post(() => SetStatus(value));
    }
  }

  private void SetStatus(string value) {
    _status = value;
    StatusChanged?.Invoke(this, EventArgs.Empty);
  }

  private void ReleaseCurrentMedia() {
    _currentMedia?.Dispose();
    _currentMedia = null;
    _resolvedMrl = null;
    _resolvedFromType = FromType.FromLocation;
    if (!string.IsNullOrWhiteSpace(_temporarySourceFile)) {
      try {
        File.Delete(_temporarySourceFile);
      } catch {
        // The SDP may still be held briefly by libVLC; the OS temp cleaner will remove it.
      }
      _temporarySourceFile = null;
    }
  }

  public void Dispose() {
    if (_disposed) {
      return;
    }
    _disposed = true;
    try {
      _mediaPlayer?.Stop();
    } catch {
      // Continue releasing native resources even if the playback backend is already gone.
    }
    ReleaseCurrentMedia();
    _videoView.MediaPlayer = null;
    _mediaPlayer?.Dispose();
    _mediaPlayer = null;
    _libVlc?.Dispose();
    _libVlc = null;
    _isAvailable = false;
  }
}

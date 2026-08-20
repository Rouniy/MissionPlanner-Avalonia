using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner;
using MissionPlannerAvalonia.Services;
using AvPixelFormat = Avalonia.Platform.PixelFormat;

namespace MissionPlannerAvalonia.ViewModels.GCSViews.ConfigurationView;

public partial class ConfigPX4FlowViewModel : ViewModelBase, IDisposable {
  private readonly MAVLinkInterface _comPort = AppState.comPort;
  private Px4FlowReceiver? _flow;
  private bool _focusmode;

  [ObservableProperty]
  private WriteableBitmap? _image;

  [ObservableProperty]
  private string _status = "Waiting for PX4Flow image stream…";

  [ObservableProperty]
  private string _focusLabel = "Focus";

  public bool IsConnected => _comPort.BaseStream?.IsOpen == true || _comPort.logreadmode;

  public ConfigPX4FlowViewModel() {
    if (!IsConnected) {
      Status = "Not connected — connect to a PX4Flow sensor to view its image.";
      return;
    }

    _flow = new Px4FlowReceiver(_comPort, (byte)_comPort.sysidcurrent, (byte)_comPort.compidcurrent);
    _flow.FrameReceived += OnNewImage;
  }

  private void OnNewImage(object? sender, Px4FlowFrameEventArgs e) {
    Dispatcher.UIThread.Post(() => {
      Image = Convert(e.Grayscale, e.Width, e.Height);
      Status = $"Receiving image ({e.Width}x{e.Height}).";
    });
  }

  private static WriteableBitmap Convert(byte[] grayscale, int width, int height) {
    var managed = Px4FlowReceiver.ToBgra(grayscale, width, height);
    var wb = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96),
      AvPixelFormat.Bgra8888, AlphaFormat.Premul);
    using var fb = wb.Lock();
    int sourceRowBytes = checked(width * 4);
    int copyBytes = Math.Min(sourceRowBytes, fb.RowBytes);
    for (int y = 0; y < height; y++) {
      Marshal.Copy(managed, y * sourceRowBytes, fb.Address + y * fb.RowBytes, copyBytes);
    }

    return wb;
  }

  [RelayCommand]
  private void ToggleFocus() {
    if (_flow == null) {
      return;
    }

    _focusmode = !_focusmode;
    _flow.CalibrationMode(_focusmode);
    FocusLabel = _focusmode ? "Video" : "Focus";
  }

  public void Dispose() {
    if (_flow != null) {
      _flow.FrameReceived -= OnNewImage;
      _flow.CalibrationMode(false);
      _flow.Dispose();
      _flow = null;
    }
  }
}

using System;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner;
using MissionPlanner.Comms;

namespace MissionPlannerAvalonia.ViewModels.GCSViews.ConfigurationView;

public partial class ConfigTerminalViewModel : ViewModelBase, IDisposable, IDeactivationAware {
  private readonly MAVLinkInterface _comPort = AppState.comPort;
  private readonly DispatcherTimer _timer;
  private readonly ConcurrentQueue<byte> _shellBuffer = new();
  private readonly Decoder _shellDecoder = Encoding.UTF8.GetDecoder();
  private int _shellSubscription;
  private bool _ownsRawComPort;
  private DateTime _lastShellPoll = DateTime.MinValue;

  public ObservableCollection<string> TransportOptions { get; } =
      new() { "MAVLink shell (SERIAL_CONTROL)", "Raw active link" };

  [ObservableProperty]
  private string _selectedTransport = "MAVLink shell (SERIAL_CONTROL)";

  [ObservableProperty]
  private bool _sessionOpen;

  [ObservableProperty]
  private string _output = "";

  [ObservableProperty]
  private string _input = "";

  [ObservableProperty]
  private string _status = "Connect to a vehicle, then start the MAVLink shell session.";

  public bool IsConnected => _comPort.BaseStream?.IsOpen == true;

  public ConfigTerminalViewModel() {
    _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
    _timer.Tick += (_, _) => Pump();
    _timer.Start();
  }

  private void Pump() {
    if (!SessionOpen) {
      return;
    }

    if (IsMavlinkShell) {
      PumpMavlinkShell();
      return;
    }

    ICommsSerial port = _comPort.BaseStream;
    if (port == null || !port.IsOpen) {
      StopSession();
      Status = "The raw link was closed.";
      return;
    }

    try {
      if (port.BytesToRead > 0) {
        string data = port.ReadExisting();
        if (!string.IsNullOrEmpty(data)) {
          Append(data);
        }
      }
    } catch {

    }
  }

  private bool IsMavlinkShell =>
      SelectedTransport.StartsWith("MAVLink", StringComparison.Ordinal);

  private void PumpMavlinkShell() {
    if (_comPort.BaseStream?.IsOpen != true) {
      StopSession();
      Status = "The MAVLink link was closed.";
      return;
    }

    if (_lastShellPoll.AddMilliseconds(100) <= DateTime.UtcNow) {
      _lastShellPoll = DateTime.UtcNow;
      try {
        _comPort.SendSerialControl(MAVLink.SERIAL_CONTROL_DEV.SHELL, 50, Array.Empty<byte>());
      } catch (Exception ex) {
        Status = "Shell poll failed: " + ex.Message;
      }
    }

    if (_shellBuffer.IsEmpty) {
      return;
    }

    var bytes = new byte[Math.Min(_shellBuffer.Count, 16 * 1024)];
    int count = 0;
    while (count < bytes.Length && _shellBuffer.TryDequeue(out byte value)) {
      bytes[count++] = value;
    }
    if (count > 0) {
      var chars = new char[_shellDecoder.GetCharCount(bytes, 0, count, false)];
      int decoded = _shellDecoder.GetChars(bytes, 0, count, chars, 0, false);
      if (decoded > 0) {
        Append(new string(chars, 0, decoded));
      }
    }
  }

  private bool OnSerialControlPacket(MAVLink.MAVLinkMessage message) {
    try {
      var packet = message.ToStructure<MAVLink.mavlink_serial_control_t>();
      if (packet.device != (byte)MAVLink.SERIAL_CONTROL_DEV.SHELL) {
        return true;
      }
      int count = Math.Min(packet.count, (byte)packet.data.Length);
      for (int i = 0; i < count; i++) {
        _shellBuffer.Enqueue(packet.data[i]);
      }
    } catch {
      // A malformed packet must not detach the subscription.
    }
    return true;
  }

  private void Append(string data) {
    data = data.TrimEnd('\r');
    data = data.Replace("\0", " ");

    string text = Output + data;
    int back = text.IndexOf('\b');
    while (back >= 0) {
      text = text.Remove(back == 0 ? 0 : back - 1, back == 0 ? 1 : 2);
      back = text.IndexOf('\b');
    }

    const int max = 64 * 1024;
    if (text.Length > max) {
      text = text.Substring(text.Length - max);
    }

    Output = text;
  }

  [RelayCommand]
  private void StartSession() {
    if (_comPort.BaseStream?.IsOpen != true) {
      Status = "Not connected — open the serial/MAVLink link first.";
      return;
    }

    try {
      StopSession();
      if (_comPort.giveComport) {
        throw new InvalidOperationException("The active link is already reserved by another operation.");
      }
      if (IsMavlinkShell) {
        _shellSubscription = _comPort.SubscribeToPacketType(
            MAVLink.MAVLINK_MSG_ID.SERIAL_CONTROL,
            OnSerialControlPacket,
            (byte)_comPort.sysidcurrent,
            (byte)_comPort.compidcurrent,
            true);
        _comPort.SendSerialControl(MAVLink.SERIAL_CONTROL_DEV.SHELL, 50, Array.Empty<byte>());
        Status = "MAVLink shell started. Commands are carried by SERIAL_CONTROL.";
      } else {
        _comPort.giveComport = true;
        _ownsRawComPort = true;
        Status = "Raw-link terminal started. Normal MAVLink parsing is paused until it is stopped.";
      }
      SessionOpen = true;
    } catch (Exception ex) {
      StopSession();
      Status = "Unable to start terminal: " + ex.Message;
    }
  }

  [RelayCommand]
  private void StopSession() {
    bool wasOpen = SessionOpen || _shellSubscription != 0 || _ownsRawComPort;
    SessionOpen = false;
    if (_ownsRawComPort) {
      _comPort.giveComport = false;
      _ownsRawComPort = false;
    }
    if (_shellSubscription != 0) {
      try {
        _comPort.UnSubscribeToPacketType(_shellSubscription);
      } catch {
        // Connection teardown may already have removed all subscriptions.
      }
      _shellSubscription = 0;
    }
    while (_shellBuffer.TryDequeue(out _)) {
    }
    _shellDecoder.Reset();
    if (wasOpen && IsMavlinkShell && _comPort.BaseStream?.IsOpen == true) {
      try {
        _comPort.SendSerialControl(
            MAVLink.SERIAL_CONTROL_DEV.SHELL, 0, Array.Empty<byte>(), 0, true);
      } catch {
        // The vehicle may disappear while the terminal is being closed.
      }
    }
    if (wasOpen) {
      Status = "Terminal session stopped.";
    }
  }

  partial void OnSelectedTransportChanging(string value) {
    if (SessionOpen) {
      StopSession();
    }
  }

  [RelayCommand]
  private void Send() {
    if (!SessionOpen) {
      Status = "Start a terminal session first.";
      return;
    }

    string line = Input ?? "";
    try {
      string text = line == "+++" ? line : line + "\r";
      if (IsMavlinkShell) {
        _comPort.SendSerialControl(
            MAVLink.SERIAL_CONTROL_DEV.SHELL, 0, Encoding.UTF8.GetBytes(text));
      } else {
        ICommsSerial port = _comPort.BaseStream;
        if (port == null || !port.IsOpen) {
          StopSession();
          Status = "The raw link is not open.";
          return;
        }
        port.Write(text);
      }

      Append("\n" + line + "\n");
      Status = "Sent.";
    } catch (Exception ex) {
      Status = "Error writing to terminal: " + ex.Message;
    }

    Input = "";
  }

  [RelayCommand]
  private void Clear() {
    Output = "";
  }

  public void Deactivate() {
    if (SessionOpen || _shellSubscription != 0 || _ownsRawComPort) {
      StopSession();
    }
  }

  public void Dispose() {
    Deactivate();
    _timer.Stop();
  }
}

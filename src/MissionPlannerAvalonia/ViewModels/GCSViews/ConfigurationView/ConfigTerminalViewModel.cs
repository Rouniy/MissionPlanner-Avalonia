using System;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner;
using MissionPlanner.Comms;
using MissionPlanner.Utilities;
using MissionPlannerAvalonia.Services;

namespace MissionPlannerAvalonia.ViewModels.GCSViews.ConfigurationView;

public partial class ConfigTerminalViewModel : ViewModelBase, IDisposable, IDeactivationAware {
  internal const string MavlinkTransport = "MAVLink shell (SERIAL_CONTROL)";
  internal const string RawTransport = "Raw active link";
  internal const string SshTransport = "SSH companion computer";

  private readonly MAVLinkInterface _comPort = AppState.comPort;
  private readonly DispatcherTimer _timer;
  private readonly ConcurrentQueue<byte> _shellBuffer = new();
  private readonly Decoder _shellDecoder = Encoding.UTF8.GetDecoder();
  private readonly ISshTerminalSession _sshSession;
  private readonly AnsiTerminalBuffer _sshScreen = new(80, 24);
  private readonly bool _usePersistentSettings;
  private CancellationTokenSource? _sshCancellation;
  private int _shellSubscription;
  private bool _ownsRawComPort;
  private bool _sshOutputActive;
  private bool _disposed;
  private DateTime _lastShellPoll = DateTime.MinValue;

  public ObservableCollection<string> TransportOptions { get; } =
      new() { MavlinkTransport, RawTransport, SshTransport };

  [ObservableProperty]
  private string _selectedTransport = MavlinkTransport;

  [ObservableProperty]
  [NotifyCanExecuteChangedFor(nameof(StartSessionCommand))]
  [NotifyCanExecuteChangedFor(nameof(StopSessionCommand))]
  private bool _sessionOpen;

  [ObservableProperty]
  [NotifyCanExecuteChangedFor(nameof(StartSessionCommand))]
  [NotifyCanExecuteChangedFor(nameof(StopSessionCommand))]
  private bool _isStarting;

  [ObservableProperty]
  private string _output = "";

  [ObservableProperty]
  private TerminalSnapshot? _terminalScreen;

  [ObservableProperty]
  private string _input = "";

  [ObservableProperty]
  private string _status = "Connect to a vehicle, then start the MAVLink shell session.";

  [ObservableProperty]
  private string _sshHost = "";

  [ObservableProperty]
  private string _sshPort = "22";

  [ObservableProperty]
  private string _sshUsername = "";

  [ObservableProperty]
  private string _sshPassword = "";

  public bool IsConnected => _comPort.BaseStream?.IsOpen == true;
  public bool IsSshSelected => IsSshTransport;
  public bool IsSshSession => SessionOpen && IsSshTransport;
  public bool UsesLineInput => !IsSshSession;

  public ConfigTerminalViewModel() : this(new SshTerminalSession(), usePersistentSettings: true) {
  }

  internal ConfigTerminalViewModel(
      ISshTerminalSession sshSession, bool usePersistentSettings = false) {
    _sshSession = sshSession ?? throw new ArgumentNullException(nameof(sshSession));
    _usePersistentSettings = usePersistentSettings;
    _sshSession.TextReceived += OnSshTextReceived;
    _sshSession.ConnectionClosed += OnSshConnectionClosed;
    _sshScreen.ResponseGenerated += response => SendSshText(response);

    if (_usePersistentSettings) {
      SshHost = Settings.Instance["SSHTerminalHost"] ?? "";
      SshPort = Settings.Instance["SSHTerminalPort"] ?? "22";
      SshUsername = Settings.Instance["SSHTerminalUsername"] ?? "";
    }

    _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
    _timer.Tick += (_, _) => Pump();
    _timer.Start();
  }

  private void Pump() {
    if (!SessionOpen || IsSshTransport) {
      return;
    }

    if (IsMavlinkShell) {
      PumpMavlinkShell();
      return;
    }

    ICommsSerial port = _comPort.BaseStream;
    if (port == null || !port.IsOpen) {
      StopSessionNow();
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
      // The next timer tick will observe a closed transport if it disappeared.
    }
  }

  private bool IsMavlinkShell =>
      SelectedTransport.StartsWith("MAVLink", StringComparison.Ordinal);

  private bool IsSshTransport =>
      SelectedTransport.StartsWith("SSH", StringComparison.Ordinal);

  private void PumpMavlinkShell() {
    if (_comPort.BaseStream?.IsOpen != true) {
      StopSessionNow();
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

  private bool CanStartSession() => !SessionOpen && !IsStarting;

  [RelayCommand(CanExecute = nameof(CanStartSession))]
  private async Task StartSessionAsync() {
    IsStarting = true;
    try {
      await StopSessionCoreAsync(updateStatus: false);
      if (IsSshTransport) {
        await StartSshSessionAsync();
        return;
      }

      if (_comPort.BaseStream?.IsOpen != true) {
        Status = "Not connected — open the serial/MAVLink link first.";
        return;
      }
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
    } catch (OperationCanceledException) {
      Status = "Terminal connection cancelled.";
    } catch (Exception ex) {
      await StopSessionCoreAsync(updateStatus: false);
      Status = "Unable to start terminal: " + ex.Message;
    } finally {
      IsStarting = false;
    }
  }

  private async Task StartSshSessionAsync() {
    if (!int.TryParse(SshPort, out int selectedPort) || selectedPort is < 1 or > 65535) {
      Status = "SSH port must be a number between 1 and 65535.";
      return;
    }
    if (!SshEndpoint.TryParse(SshHost, selectedPort, out string host, out int port)) {
      Status = "Enter an SSH host, optionally as host:port or [IPv6]:port.";
      return;
    }
    string username = SshUsername.Trim();
    if (username.Length == 0) {
      Status = "Enter the SSH username.";
      return;
    }

    var cancellation = new CancellationTokenSource();
    _sshCancellation = cancellation;
    _sshOutputActive = true;
    _sshScreen.Reset();
    Output = "";
    TerminalScreen = _sshScreen.Snapshot();

    var connection = new SshTerminalConnection(host, port, username, SshPassword ?? "");
    string settingName = SshEndpoint.TrustedKeySettingName(host, port);
    string? trustedFingerprint = _usePersistentSettings ? Settings.Instance[settingName] : null;
    try {
      try {
        Status = $"Connecting to {host}:{port}…";
        await _sshSession.ConnectAsync(connection, trustedFingerprint, cancellation.Token);
      } catch (SshHostKeyException ex) {
        if (!await SshHostKeyPrompt.ConfirmAsync(ex.Challenge)) {
          Status = ex.Challenge.IsChanged
              ? "SSH connection rejected: the server host key changed."
              : "SSH connection cancelled: the server host key was not trusted.";
          return;
        }

        cancellation.Token.ThrowIfCancellationRequested();
        trustedFingerprint = ex.Challenge.PresentedFingerprint;
        if (_usePersistentSettings) {
          Settings.Instance[settingName] = trustedFingerprint;
          Settings.Instance.Save();
        }
        Status = $"Host key trusted. Connecting to {host}:{port}…";
        await _sshSession.ConnectAsync(connection, trustedFingerprint, cancellation.Token);
      }

      if (_usePersistentSettings) {
        Settings.Instance["SSHTerminalHost"] = host;
        Settings.Instance["SSHTerminalPort"] = port.ToString();
        Settings.Instance["SSHTerminalUsername"] = username;
        Settings.Instance.Save();
      }
      SshHost = host;
      SshPort = port.ToString();
      SshPassword = "";
      Input = "";
      SessionOpen = true;
      Status = $"SSH terminal connected to {username}@{host}:{port} (xterm 80×24).";
    } finally {
      if (!SessionOpen) {
        _sshOutputActive = false;
        TryCancel(cancellation);
        await _sshSession.StopAsync();
        if (ReferenceEquals(_sshCancellation, cancellation)) {
          _sshCancellation = null;
        }
        cancellation.Dispose();
        SshPassword = "";
      }
    }
  }

  private bool CanStopSession() => SessionOpen || IsStarting;

  [RelayCommand(CanExecute = nameof(CanStopSession))]
  private Task StopSessionAsync() => StopSessionCoreAsync(updateStatus: true);

  private async Task StopSessionCoreAsync(bool updateStatus) {
    bool connectionWasStarting = IsStarting;
    bool wasOpen = SessionOpen || IsStarting || _shellSubscription != 0 ||
        _ownsRawComPort || _sshOutputActive;
    _sshOutputActive = false;
    SessionOpen = false;

    var sshCancellation = _sshCancellation;
    _sshCancellation = null;
    TryCancel(sshCancellation);
    await _sshSession.StopAsync();
    if (!connectionWasStarting) {
      sshCancellation?.Dispose();
    }

    CloseVehicleTerminal();
    if (wasOpen && updateStatus) {
      Status = "Terminal session stopped.";
    }
  }

  private void StopSessionNow() {
    bool connectionWasStarting = IsStarting;
    bool wasOpen = SessionOpen || IsStarting || _shellSubscription != 0 ||
        _ownsRawComPort || _sshOutputActive;
    _sshOutputActive = false;
    SessionOpen = false;
    var cancellation = _sshCancellation;
    _sshCancellation = null;
    TryCancel(cancellation);
    if (!connectionWasStarting) {
      cancellation?.Dispose();
    }
    _sshSession.Stop();
    CloseVehicleTerminal();
    if (wasOpen) {
      Status = "Terminal session stopped.";
    }
  }

  private void CloseVehicleTerminal() {
    bool closeMavlinkShell = _shellSubscription != 0;
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
    if (closeMavlinkShell && _comPort.BaseStream?.IsOpen == true) {
      try {
        _comPort.SendSerialControl(
            MAVLink.SERIAL_CONTROL_DEV.SHELL, 0, Array.Empty<byte>(), 0, true);
      } catch {
        // The vehicle may disappear while the terminal is being closed.
      }
    }
  }

  private static void TryCancel(CancellationTokenSource? cancellation) {
    try {
      cancellation?.Cancel();
    } catch (ObjectDisposedException) {
    }
  }

  partial void OnSelectedTransportChanging(string value) {
    if (SessionOpen || IsStarting) {
      StopSessionNow();
    }
  }

  partial void OnSelectedTransportChanged(string value) {
    OnPropertyChanged(nameof(IsSshSelected));
    OnPropertyChanged(nameof(IsSshSession));
    OnPropertyChanged(nameof(UsesLineInput));
    TerminalScreen = IsSshTransport ? _sshScreen.Snapshot() : null;
    Status = IsSshTransport
        ? "Enter the companion-computer SSH address and credentials, then start the session."
        : "Connect to a vehicle, then start the terminal session.";
  }

  partial void OnSessionOpenChanged(bool value) {
    OnPropertyChanged(nameof(IsSshSession));
    OnPropertyChanged(nameof(UsesLineInput));
  }

  [RelayCommand]
  private async Task SendAsync() {
    if (!SessionOpen) {
      Status = "Start a terminal session first.";
      return;
    }

    string line = Input ?? "";
    try {
      string text = line == "+++" ? line : line + "\r";
      if (IsSshTransport) {
        await _sshSession.WriteAsync(text, _sshCancellation?.Token ?? default);
      } else if (IsMavlinkShell) {
        _comPort.SendSerialControl(
            MAVLink.SERIAL_CONTROL_DEV.SHELL, 0, Encoding.UTF8.GetBytes(text));
        Append("\n" + line + "\n");
      } else {
        ICommsSerial port = _comPort.BaseStream;
        if (port == null || !port.IsOpen) {
          StopSessionNow();
          Status = "The raw link is not open.";
          return;
        }
        port.Write(text);
        Append("\n" + line + "\n");
      }

      Status = "Sent.";
    } catch (Exception ex) {
      Status = "Error writing to terminal: " + ex.Message;
    }

    Input = "";
  }

  public void SendSshText(string? text) {
    if (!IsSshSession || string.IsNullOrEmpty(text)) {
      return;
    }
    _ = SendSshTextAsync(text);
  }

  private async Task SendSshTextAsync(string text) {
    try {
      await _sshSession.WriteAsync(text, _sshCancellation?.Token ?? default);
    } catch (OperationCanceledException) {
    } catch (Exception ex) {
      Dispatcher.UIThread.Post(() => Status = "Error writing to SSH terminal: " + ex.Message);
    }
  }

  public string? EncodeSshKey(Avalonia.Input.Key key, Avalonia.Input.KeyModifiers modifiers) =>
      IsSshSession
          ? TerminalKeyEncoder.Encode(key, modifiers, _sshScreen.ApplicationCursorKeys)
          : null;

  private void OnSshTextReceived(string text) {
    Dispatcher.UIThread.Post(() => {
      if (!_sshOutputActive) {
        return;
      }
      _sshScreen.Write(text);
      Output = _sshScreen.Render();
      TerminalScreen = _sshScreen.Snapshot();
    });
  }

  private void OnSshConnectionClosed(string reason) {
    Dispatcher.UIThread.Post(() => {
      if (!_sshOutputActive) {
        return;
      }
      _sshOutputActive = false;
      SessionOpen = false;
      var cancellation = _sshCancellation;
      _sshCancellation = null;
      TryCancel(cancellation);
      cancellation?.Dispose();
      _sshSession.Stop();
      Status = reason;
    });
  }

  [RelayCommand]
  private void Clear() {
    if (IsSshTransport) {
      _sshScreen.Reset();
      TerminalScreen = _sshScreen.Snapshot();
    }
    Output = "";
  }

  public void Deactivate() {
    if (SessionOpen || IsStarting || _shellSubscription != 0 ||
        _ownsRawComPort || _sshOutputActive) {
      StopSessionNow();
    }
  }

  public void Dispose() {
    if (_disposed) {
      return;
    }
    _disposed = true;
    Deactivate();
    _timer.Stop();
    _sshSession.TextReceived -= OnSshTextReceived;
    _sshSession.ConnectionClosed -= OnSshConnectionClosed;
    _sshSession.Stop();
    _ = _sshSession.DisposeAsync();
  }
}

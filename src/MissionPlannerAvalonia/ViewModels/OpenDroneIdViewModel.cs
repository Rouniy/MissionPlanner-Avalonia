using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner;
using MissionPlanner.Comms;
using MissionPlanner.Utilities;
using MissionPlannerAvalonia.Services;

namespace MissionPlannerAvalonia.ViewModels;

internal interface IOpenDroneIdMavlinkAdapter {
  bool IsBusy(NmeaVehicleTarget target);
  int SubscribeArmStatus(
      NmeaVehicleTarget target, byte componentId, Func<byte, string, bool> handler);
  void Unsubscribe(NmeaVehicleTarget target, int subscription);
  void Send(NmeaVehicleTarget target, object packet);
}

internal sealed class OpenDroneIdMavlinkAdapter : IOpenDroneIdMavlinkAdapter {
  public bool IsBusy(NmeaVehicleTarget target) => target.Link.giveComport;

  public int SubscribeArmStatus(
      NmeaVehicleTarget target, byte componentId, Func<byte, string, bool> handler) =>
      target.Link.SubscribeToPacketType(
          MAVLink.MAVLINK_MSG_ID.OPEN_DRONE_ID_ARM_STATUS,
          message => {
            var armStatus =
                message.ToStructure<MAVLink.mavlink_open_drone_id_arm_status_t>();
            string error = Encoding.ASCII.GetString(armStatus.error ?? [])
                .TrimEnd('\0', ' ', '\r', '\n');
            return handler(armStatus.status, error);
          },
          target.SystemId,
          componentId);

  public void Unsubscribe(NmeaVehicleTarget target, int subscription) =>
      target.Link.UnSubscribeToPacketType(subscription);

  public void Send(NmeaVehicleTarget target, object packet) {
    // The payload broadcasts to component 0, but frame/signing state must come from the
    // exact selected component rather than from a usually non-existent MAV[sys,0] entry.
    target.Link.sendPacket(packet, target.SystemId, target.ComponentId);
  }
}

public partial class OpenDroneIdViewModel : ViewModelBase, IDisposable {
  internal const string TcpHost = "TCP Host";
  internal const string TcpClient = "TCP Client";
  internal const string UdpHost = "UDP Host";
  internal const string UdpClient = "UDP Client";
  internal const string EmergencyText = "Pilot Emergency Status Declared";

  private static readonly byte[] ArmStatusComponents = [
    (byte)MAVLink.MAV_COMPONENT.MAV_COMP_ID_ODID_TXRX_1,
    (byte)MAVLink.MAV_COMPONENT.MAV_COMP_ID_ODID_TXRX_2,
    (byte)MAVLink.MAV_COMPONENT.MAV_COMP_ID_ODID_TXRX_3,
    (byte)MAVLink.MAV_COMPONENT.MAV_COMP_ID_AUTOPILOT1,
  ];

  private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
  private readonly object _stateGate = new();
  private readonly Func<bool, NmeaVehicleTarget?> _activeTarget;
  private readonly IOpenDroneIdMavlinkAdapter _mavlink;
  private readonly Func<NmeaVehicleTarget, Task<bool>> _confirmTransmit;
  private readonly Func<string, int, string, int, (ICommsSerial Input, TcpListener? Listener)>
      _openInput;
  private readonly Action<NmeaVehicleTarget, PointLatLngAlt> _setOperatorPosition;
  private readonly bool _subscribedToAppState;
  private readonly bool _usePersistentSettings;
  private readonly List<(NmeaVehicleTarget Target, int Id)> _subscriptions = [];
  private readonly Queue<string> _rawLines = [];
  private readonly Queue<string> _statusLines = [];

  private CancellationTokenSource? _cts;
  private ICommsSerial? _input;
  private TcpListener? _listener;
  private Task? _readerTask;
  private Task? _senderTask;
  private Task? _acceptTask;
  private NmeaVehicleTarget? _boundTarget;
  private OpenDroneIdConfiguration? _configuration;
  private NmeaGgaFix? _lastFix;
  private DateTimeOffset _lastFixUtc;
  private DateTimeOffset _lastArmStatusUtc;
  private bool _moduleDetected;
  private byte _armStatus;
  private string _armError = "";
  private volatile bool _targetInvalidated;
  private int _stopScheduled;
  private bool _disposed;
  private MAVLink.MAV_ODID_DESC_TYPE _preEmergencyDescriptionType;
  private string _preEmergencyDescription = "";

  public OpenDroneIdViewModel()
      : this(
          NmeaVehicleSession.CaptureActive,
          new OpenDroneIdMavlinkAdapter(),
          static target => Dialogs.ConfirmDangerous(
              "Enable Open Drone ID Transmission",
              $"Open Drone ID will continuously send the configured aircraft and operator "
              + $"identity plus the external GPS position to {NmeaVehicleSession.Describe(target)}. "
              + "That information may be broadcast over radio and can identify the operator. "
              + "Verify every value and the selected modem before enabling transmission.",
              "Enable Transmission"),
          OpenInput,
          static (target, location) => target.Link.MAV.cs.Base = location,
          subscribeToAppState: true,
          usePersistentSettings: true) {
  }

  internal OpenDroneIdViewModel(
      Func<bool, NmeaVehicleTarget?> activeTarget,
      IOpenDroneIdMavlinkAdapter mavlink,
      Func<NmeaVehicleTarget, Task<bool>> confirmTransmit,
      Func<string, int, string, int, (ICommsSerial Input, TcpListener? Listener)> openInput,
      Action<NmeaVehicleTarget, PointLatLngAlt>? setOperatorPosition = null,
      bool subscribeToAppState = false,
      bool usePersistentSettings = false) {
    _activeTarget = activeTarget;
    _mavlink = mavlink;
    _confirmTransmit = confirmTransmit;
    _openInput = openInput;
    _setOperatorPosition = setOperatorPosition ?? (static (_, _) => { });
    _subscribedToAppState = subscribeToAppState;
    _usePersistentSettings = usePersistentSettings;

    RefreshInputs();
    if (_usePersistentSettings) {
      LoadSettings();
    }
    RefreshTargetDescription();
    if (_subscribedToAppState) {
      AppState.ConnectionChanged += OnConnectionChanged;
    }
  }

  public ObservableCollection<string> Inputs { get; } = [];
  public ObservableCollection<int> Bauds { get; } = new() {
      4800, 9600, 14400, 19200, 28800, 38400, 57600, 115200,
  };
  public IReadOnlyList<MAVLink.MAV_ODID_ID_TYPE> UasIdTypes { get; } =
      Enum.GetValues<MAVLink.MAV_ODID_ID_TYPE>();
  public IReadOnlyList<MAVLink.MAV_ODID_UA_TYPE> UaTypes { get; } =
      Enum.GetValues<MAVLink.MAV_ODID_UA_TYPE>();
  public IReadOnlyList<MAVLink.MAV_ODID_DESC_TYPE> DescriptionTypes { get; } =
      Enum.GetValues<MAVLink.MAV_ODID_DESC_TYPE>();
  public IReadOnlyList<MAVLink.MAV_ODID_OPERATOR_LOCATION_TYPE> OperatorLocationTypes { get; } =
      Enum.GetValues<MAVLink.MAV_ODID_OPERATOR_LOCATION_TYPE>();
  public IReadOnlyList<MAVLink.MAV_ODID_CLASSIFICATION_TYPE> ClassificationTypes { get; } =
      Enum.GetValues<MAVLink.MAV_ODID_CLASSIFICATION_TYPE>();
  public IReadOnlyList<MAVLink.MAV_ODID_CATEGORY_EU> CategoriesEu { get; } =
      Enum.GetValues<MAVLink.MAV_ODID_CATEGORY_EU>();
  public IReadOnlyList<MAVLink.MAV_ODID_CLASS_EU> ClassesEu { get; } =
      Enum.GetValues<MAVLink.MAV_ODID_CLASS_EU>();
  public IReadOnlyList<MAVLink.MAV_ODID_OPERATOR_ID_TYPE> OperatorIdTypes { get; } =
      Enum.GetValues<MAVLink.MAV_ODID_OPERATOR_ID_TYPE>();

  [ObservableProperty]
  private string? _selectedInput;

  [ObservableProperty]
  private int _selectedBaud = 4800;

  [ObservableProperty]
  private string _networkHost = "127.0.0.1";

  [ObservableProperty]
  private int _networkPort = 14551;

  [ObservableProperty]
  private MAVLink.MAV_ODID_ID_TYPE _selectedUasIdType;

  [ObservableProperty]
  private string _uasId = "";

  [ObservableProperty]
  private MAVLink.MAV_ODID_UA_TYPE _selectedUaType;

  [ObservableProperty]
  private MAVLink.MAV_ODID_DESC_TYPE _selectedDescriptionType;

  [ObservableProperty]
  private string _description = "";

  [ObservableProperty]
  private int _areaCount = 1;

  [ObservableProperty]
  private int _areaRadiusM;

  [ObservableProperty]
  private double _areaCeilingM = OpenDroneIdMessageFactory.UnknownAltitudeM;

  [ObservableProperty]
  private double _areaFloorM = OpenDroneIdMessageFactory.UnknownAltitudeM;

  [ObservableProperty]
  private MAVLink.MAV_ODID_CATEGORY_EU _selectedCategoryEu;

  [ObservableProperty]
  private MAVLink.MAV_ODID_CLASS_EU _selectedClassEu;

  [ObservableProperty]
  private MAVLink.MAV_ODID_CLASSIFICATION_TYPE _selectedClassificationType;

  [ObservableProperty]
  private MAVLink.MAV_ODID_OPERATOR_LOCATION_TYPE _selectedOperatorLocationType =
      MAVLink.MAV_ODID_OPERATOR_LOCATION_TYPE.LIVE_GNSS;

  [ObservableProperty]
  private MAVLink.MAV_ODID_OPERATOR_ID_TYPE _selectedOperatorIdType;

  [ObservableProperty]
  private string _operatorId = "";

  [ObservableProperty]
  private string _status = "Stopped.";

  [ObservableProperty]
  private string _armStatusText = "No Remote ID module status received.";

  [ObservableProperty]
  private string _gpsStatus = "No external GPS fix received.";

  [ObservableProperty]
  private string _targetDescription = "No vehicle selected.";

  [ObservableProperty]
  private string _rawNmea = "";

  [ObservableProperty]
  private string _statusLog = "";

  [ObservableProperty]
  private string _connectButtonText = "Start";

  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(CanEditSettings))]
  private bool _busy;

  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(MapStatusVisible))]
  private bool _running;

  [ObservableProperty]
  private string _mapStatusText = "Remote ID stopped";

  [ObservableProperty]
  private IBrush _mapStatusBrush = new SolidColorBrush(Color.Parse("#A0606060"));

  [ObservableProperty]
  private bool _emergencyDeclared;

  public bool CanEditSettings => !Busy && !Running;
  public bool IsSerialInput => !IsNetworkInput(SelectedInput);
  public bool IsNetworkClient => SelectedInput is TcpClient or UdpClient;
  public string NetworkPortLabel => SelectedInput is TcpHost or UdpHost ? "Local port" : "Remote port";
  public bool MapStatusVisible => Running;

  partial void OnSelectedInputChanged(string? value) {
    OnPropertyChanged(nameof(IsSerialInput));
    OnPropertyChanged(nameof(IsNetworkClient));
    OnPropertyChanged(nameof(NetworkPortLabel));
  }

  [RelayCommand]
  private void RefreshInputs() {
    string? selected = SelectedInput;
    Inputs.Clear();
    foreach (string port in System.IO.Ports.SerialPort.GetPortNames()
                 .Distinct().OrderBy(item => item)) {
      Inputs.Add(port);
    }
    Inputs.Add(TcpHost);
    Inputs.Add(TcpClient);
    Inputs.Add(UdpHost);
    Inputs.Add(UdpClient);
    SelectedInput = selected != null && Inputs.Contains(selected)
        ? selected
        : Inputs.FirstOrDefault();
  }

  [RelayCommand]
  private async Task ToggleAsync() {
    await _lifecycleGate.WaitAsync();
    Busy = true;
    try {
      if (Running || _cts != null) {
        await StopCoreAsync("Stopped.");
      } else {
        await StartCoreAsync();
      }
    } finally {
      Busy = false;
      _lifecycleGate.Release();
    }
  }

  private async Task StartCoreAsync() {
    if (_disposed) {
      return;
    }
    if (!TryBuildConfiguration(out OpenDroneIdConfiguration? configuration, out string error)) {
      Status = error;
      return;
    }
    if (string.IsNullOrWhiteSpace(SelectedInput)) {
      Status = "Select a serial or network NMEA input first.";
      return;
    }
    if (IsSerialInput && !Bauds.Contains(SelectedBaud)) {
      Status = "Select a supported serial baud rate.";
      return;
    }
    if (NetworkPort is < 1 or > 65535) {
      Status = "Network port must be between 1 and 65535.";
      return;
    }

    NmeaVehicleTarget? target = _activeTarget(true);
    if (target == null) {
      Status = "Connect and select a MAVLink vehicle before enabling Open Drone ID.";
      RefreshTargetDescription();
      return;
    }
    if (!await _confirmTransmit(target)) {
      Status = "Open Drone ID transmission was cancelled.";
      return;
    }
    if (!IsTargetCurrent(target)) {
      Status = TargetChangedMessage;
      RefreshTargetDescription();
      return;
    }

    try {
      var opened = await Task.Run(() =>
          _openInput(SelectedInput!, SelectedBaud, NetworkHost.Trim(), NetworkPort));
      if (!IsTargetCurrent(target)) {
        CloseOpened(opened.Input, opened.Listener);
        throw new InvalidOperationException(TargetChangedMessage);
      }

      var cts = new CancellationTokenSource();
      _input = opened.Input;
      _listener = opened.Listener;
      _boundTarget = target;
      _configuration = configuration;
      _targetInvalidated = false;
      ResetSessionState();
      SubscribeArmStatus(target);
      _cts = cts;
      if (_listener != null && _input is TcpSerial tcp) {
        _acceptTask = AcceptClientsAsync(_listener, tcp, cts.Token);
      }
      _readerTask = Task.Run(() => ReadLoop(target, cts.Token), cts.Token);
      _senderTask = Task.Run(() => SendLoopAsync(target, cts.Token), cts.Token);
      PersistSettings();
      TargetDescription = "Bound to " + NmeaVehicleSession.Describe(target) + ".";
      ConnectButtonText = "Stop";
      Running = true;
      Status = "Waiting for OPEN_DRONE_ID_ARM_STATUS before transmitting.";
      MapStatusText = "Remote ID: waiting for module";
      MapStatusBrush = Brush("#C88A6500");
      AppendStatus("Session started; waiting for a Remote ID transmitter status message.");
    } catch (Exception ex) {
      UnsubscribeAll();
      CloseTransport();
      _boundTarget = null;
      _configuration = null;
      Status = _targetInvalidated || ex.Message == TargetChangedMessage
          ? TargetChangedMessage
          : "Open Drone ID start failed: " + ex.Message;
      RefreshTargetDescription();
    }
  }

  private void SubscribeArmStatus(NmeaVehicleTarget target) {
    foreach (byte component in ArmStatusComponents) {
      int subscription = _mavlink.SubscribeArmStatus(
          target, component, (status, error) => OnArmStatus(component, status, error));
      _subscriptions.Add((target, subscription));
    }
  }

  private bool OnArmStatus(byte component, byte status, string error) {
    bool first;
    bool changed;
    lock (_stateGate) {
      first = !_moduleDetected;
      changed = first || _armStatus != status || _armError != error;
      _moduleDetected = true;
      _lastArmStatusUtc = DateTimeOffset.UtcNow;
      _armStatus = status;
      _armError = error;
    }
    if (changed) {
      Dispatcher.UIThread.Post(() => {
        string componentName = ((MAVLink.MAV_COMPONENT)component).ToString();
        string message = status == (byte)MAVLink.MAV_ODID_ARM_STATUS.GOOD_TO_ARM
            ? $"Remote ID ready ({componentName})."
            : $"Remote ID arm error ({componentName}): "
              + (string.IsNullOrWhiteSpace(error) ? "unspecified failure" : error);
        AppendStatus(message);
        UpdateArmPresentation(DateTimeOffset.UtcNow);
        if (first) {
          Status = "Remote ID module detected; identity transmission is active.";
        }
      });
    }
    return true;
  }

  private void ReadLoop(NmeaVehicleTarget target, CancellationToken cancellationToken) {
    while (!cancellationToken.IsCancellationRequested) {
      try {
        if (!IsTargetCurrent(target)) {
          InvalidateTarget();
          return;
        }
        ICommsSerial? input = _input;
        if (input?.IsOpen != true) {
          cancellationToken.WaitHandle.WaitOne(100);
          continue;
        }
        string line = input.ReadLine();
        if (string.IsNullOrWhiteSpace(line)) {
          continue;
        }
        string trimmed = line.TrimEnd();
        Dispatcher.UIThread.Post(() => AppendRaw(trimmed));
        if (!NmeaGgaParser.TryParse(trimmed, out NmeaGgaFix fix, out string parseError)) {
          if (parseError == "GPS has no position fix.") {
            Dispatcher.UIThread.Post(() => GpsStatus = parseError);
          }
          continue;
        }
        lock (_stateGate) {
          _lastFix = fix;
          _lastFixUtc = DateTimeOffset.UtcNow;
        }
        if (!IsTargetCurrent(target)) {
          InvalidateTarget();
          return;
        }
        _setOperatorPosition(target, new PointLatLngAlt(
            fix.Latitude,
            fix.Longitude,
            fix.AltitudeM,
            $"Open Drone ID operator; WGS84 {fix.GeodeticAltitudeM:0.0} m"));
        Dispatcher.UIThread.Post(() => GpsStatus = string.Format(
            CultureInfo.InvariantCulture,
            "{0:0.0000000}, {1:0.0000000}; MSL {2:0.0} m; WGS84 {3:0.0} m; "
            + "fix {4}, sats {5}, HDOP {6:0.##}",
            fix.Latitude, fix.Longitude, fix.AltitudeM, fix.GeodeticAltitudeM,
            fix.FixQuality, fix.Satellites, fix.Hdop));
      } catch (TimeoutException) {
      } catch (Exception ex) when (!cancellationToken.IsCancellationRequested) {
        RequestStop("Open Drone ID NMEA input stopped: " + ex.Message);
        return;
      }
    }
  }

  private async Task SendLoopAsync(
      NmeaVehicleTarget target, CancellationToken cancellationToken) {
    var scheduler = new OpenDroneIdSendScheduler();
    while (!cancellationToken.IsCancellationRequested) {
      if (!IsTargetCurrent(target)) {
        InvalidateTarget();
        return;
      }

      DateTimeOffset now = DateTimeOffset.UtcNow;
      NmeaGgaFix? fix;
      DateTimeOffset lastFix;
      DateTimeOffset lastArm;
      bool detected;
      OpenDroneIdConfiguration? configuration;
      lock (_stateGate) {
        fix = _lastFix;
        lastFix = _lastFixUtc;
        lastArm = _lastArmStatusUtc;
        detected = _moduleDetected;
        configuration = _configuration;
      }
      bool freshGps = fix.HasValue && now - lastFix < OpenDroneIdSendScheduler.MaxGpsAge;
      Dispatcher.UIThread.Post(() => UpdateArmPresentation(now));

      bool portBusy = detected && _mavlink.IsBusy(target);
      OpenDroneIdScheduledMessage? scheduled = portBusy
          ? null
          : scheduler.Next(now, detected, freshGps);
      if (portBusy) {
        Dispatcher.UIThread.Post(() =>
            Status = "MAVLink port is busy; Open Drone ID transmission is waiting.");
      } else if (scheduled.HasValue && configuration != null) {
        try {
          object packet = scheduled.Value.SystemUpdate
              ? OpenDroneIdMessageFactory.SystemUpdate(target.SystemId, fix!.Value, now)
              : OpenDroneIdMessageFactory.Extended(
                  scheduled.Value.ExtendedKind,
                  target.SystemId,
                  configuration,
                  freshGps ? fix : null,
                  now);
          if (!IsTargetCurrent(target)) {
            InvalidateTarget();
            return;
          }
          _mavlink.Send(target, packet);
          Dispatcher.UIThread.Post(() => {
            if (detected && now - lastArm <= OpenDroneIdSendScheduler.ArmStatusTimeout) {
              Status = freshGps
                  ? "Open Drone ID identity and live operator position are transmitting."
                  : "Open Drone ID identity is transmitting; waiting for a fresh GPS fix.";
            }
          });
        } catch (Exception ex) {
          RequestStop("Open Drone ID transmission stopped: " + ex.Message);
          return;
        }
      }

      try {
        await Task.Delay(100, cancellationToken).ConfigureAwait(false);
      } catch (OperationCanceledException) {
        return;
      }
    }
  }

  private void UpdateArmPresentation(DateTimeOffset now) {
    bool detected;
    DateTimeOffset lastArm;
    byte armStatus;
    string armError;
    lock (_stateGate) {
      detected = _moduleDetected;
      lastArm = _lastArmStatusUtc;
      armStatus = _armStatus;
      armError = _armError;
    }

    if (!detected) {
      ArmStatusText = "Waiting for OPEN_DRONE_ID_ARM_STATUS.";
      MapStatusText = EmergencyDeclared
          ? "Remote ID: EMERGENCY (waiting)"
          : "Remote ID: waiting for module";
      MapStatusBrush = Brush(EmergencyDeclared ? "#D8B00020" : "#C88A6500");
      return;
    }
    if (now - lastArm > OpenDroneIdSendScheduler.ArmStatusTimeout) {
      ArmStatusText = "Remote ID status timeout (more than 5 seconds).";
      MapStatusText = EmergencyDeclared
          ? "Remote ID: EMERGENCY / timeout"
          : "Remote ID: FAIL / timeout";
      MapStatusBrush = Brush("#D8B00020");
      return;
    }
    bool ready = armStatus == (byte)MAVLink.MAV_ODID_ARM_STATUS.GOOD_TO_ARM;
    ArmStatusText = ready
        ? "Remote ID module reports ready to arm."
        : "Remote ID module reports: "
          + (string.IsNullOrWhiteSpace(armError) ? "arming failure" : armError);
    MapStatusText = EmergencyDeclared
        ? "Remote ID: EMERGENCY"
        : ready ? "Remote ID: OK" : "Remote ID: FAIL";
    MapStatusBrush = Brush(EmergencyDeclared || !ready ? "#D8B00020" : "#C8007A35");
  }

  [RelayCommand]
  private async Task DeclareEmergencyAsync() {
    if (!await Dialogs.ConfirmDangerous(
            "Declare Open Drone ID Emergency",
            "This changes the transmitted Self ID to an operator-declared emergency. "
            + "Use it only for a real emergency and verify that the correct vehicle is selected.",
            "Declare Emergency")) {
      return;
    }
    if (!EmergencyDeclared) {
      _preEmergencyDescriptionType = SelectedDescriptionType;
      _preEmergencyDescription = Description;
    }
    SelectedDescriptionType = MAVLink.MAV_ODID_DESC_TYPE.EMERGENCY;
    Description = EmergencyText;
    EmergencyDeclared = true;
    RefreshLiveConfiguration();
    AppendStatus("Pilot declared an Open Drone ID emergency.");
    UpdateArmPresentation(DateTimeOffset.UtcNow);
  }

  [RelayCommand]
  private void ClearEmergency() {
    if (!EmergencyDeclared) {
      return;
    }
    SelectedDescriptionType = _preEmergencyDescriptionType;
    Description = _preEmergencyDescription;
    EmergencyDeclared = false;
    RefreshLiveConfiguration();
    AppendStatus("Open Drone ID emergency declaration cleared.");
    UpdateArmPresentation(DateTimeOffset.UtcNow);
  }

  private void RefreshLiveConfiguration() {
    if (!TryBuildConfiguration(out OpenDroneIdConfiguration? configuration, out string error)) {
      Status = error;
      return;
    }
    lock (_stateGate) {
      if (_configuration != null) {
        _configuration = configuration;
      }
    }
    PersistSettings();
  }

  public async Task StopAsync() {
    await _lifecycleGate.WaitAsync();
    try {
      await StopCoreAsync("Stopped.");
    } finally {
      _lifecycleGate.Release();
    }
  }

  private async Task StopCoreAsync(string reason) {
    CancellationTokenSource? cts = _cts;
    Task? reader = _readerTask;
    Task? sender = _senderTask;
    Task? accept = _acceptTask;
    _cts = null;
    _readerTask = null;
    _senderTask = null;
    _acceptTask = null;
    cts?.Cancel();
    CloseTransport();
    UnsubscribeAll();

    foreach (Task? task in new[] { reader, sender, accept }) {
      if (task == null) {
        continue;
      }
      try {
        await task.WaitAsync(TimeSpan.FromSeconds(2));
      } catch (OperationCanceledException) {
      } catch (TimeoutException) {
      }
    }
    cts?.Dispose();
    _boundTarget = null;
    _configuration = null;
    _targetInvalidated = false;
    Running = false;
    ConnectButtonText = "Start";
    Status = reason;
    MapStatusText = "Remote ID stopped";
    MapStatusBrush = Brush("#A0606060");
    RefreshTargetDescription();
  }

  private async Task AcceptClientsAsync(
      TcpListener listener, TcpSerial input, CancellationToken cancellationToken) {
    while (!cancellationToken.IsCancellationRequested) {
      try {
        TcpClient client = await listener.AcceptTcpClientAsync(cancellationToken);
        TcpClient previous = input.client;
        input.client = client;
        previous.Dispose();
        Dispatcher.UIThread.Post(() =>
            Status = "Open Drone ID NMEA TCP client connected: " + client.Client.RemoteEndPoint);
      } catch (OperationCanceledException) {
        return;
      } catch (ObjectDisposedException) {
        return;
      } catch (Exception ex) when (!cancellationToken.IsCancellationRequested) {
        Dispatcher.UIThread.Post(() => Status = "NMEA TCP accept failed: " + ex.Message);
      }
    }
  }

  private static (ICommsSerial Input, TcpListener? Listener) OpenInput(
      string input, int baud, string host, int port) {
    switch (input) {
      case TcpHost: {
          var listener = new TcpListener(IPAddress.Any, port);
          listener.Start();
          return (new TcpSerial { ReadTimeout = 1000 }, listener);
        }
      case TcpClient: {
          if (string.IsNullOrWhiteSpace(host)) {
            throw new InvalidOperationException("Enter a remote TCP host.");
          }
          var tcp = new TcpSerial {
            Host = host,
            Port = port.ToString(CultureInfo.InvariantCulture),
            ReadTimeout = 1000,
            autoReconnect = true,
            retrys = int.MaxValue,
          };
          tcp.Open();
          return (tcp, null);
        }
      case UdpHost:
        return (new UdpSerial(new UdpClient(port)) { ReadTimeout = 1000 }, null);
      case UdpClient: {
          if (string.IsNullOrWhiteSpace(host)) {
            throw new InvalidOperationException("Enter a remote UDP host.");
          }
          var udp = new UdpSerialConnect { ReadTimeout = 1000 };
          udp.Open(host, port.ToString(CultureInfo.InvariantCulture));
          return (udp, null);
        }
      default: {
          var serial = new MissionPlanner.Comms.SerialPort {
            PortName = input,
            BaudRate = baud,
            ReadTimeout = 1000,
          };
          serial.Open();
          return (serial, null);
        }
    }
  }

  private void OnConnectionChanged() {
    if (_disposed) {
      return;
    }
    NmeaVehicleTarget? bound = _boundTarget;
    if (bound == null) {
      Dispatcher.UIThread.Post(RefreshTargetDescription);
    } else if (!IsTargetCurrent(bound)) {
      InvalidateTarget();
    }
  }

  internal void SynchronizeActiveTarget() => OnConnectionChanged();

  private bool IsTargetCurrent(NmeaVehicleTarget target) =>
      NmeaVehicleSession.ShouldContinue(
          _targetInvalidated, target, _activeTarget(true), requireOpen: true);

  private void InvalidateTarget() {
    _targetInvalidated = true;
    _cts?.Cancel();
    CloseTransport();
    RequestStop(TargetChangedMessage);
  }

  private void RequestStop(string reason) {
    if (_disposed || Interlocked.Exchange(ref _stopScheduled, 1) != 0) {
      return;
    }
    Dispatcher.UIThread.Post(() => _ = StopForReasonAsync(reason));
  }

  private async Task StopForReasonAsync(string reason) {
    await _lifecycleGate.WaitAsync();
    try {
      if (_cts != null || _boundTarget != null) {
        await StopCoreAsync(reason);
      } else if (!_disposed) {
        Status = reason;
      }
    } finally {
      _lifecycleGate.Release();
      Interlocked.Exchange(ref _stopScheduled, 0);
    }
  }

  private void UnsubscribeAll() {
    foreach (var subscription in _subscriptions.ToArray()) {
      try {
        _mavlink.Unsubscribe(subscription.Target, subscription.Id);
      } catch {
      }
    }
    _subscriptions.Clear();
  }

  private void CloseTransport() {
    TcpListener? listener = Interlocked.Exchange(ref _listener, null);
    try {
      listener?.Stop();
    } catch {
    }
    ICommsSerial? input = Interlocked.Exchange(ref _input, null);
    CloseOpened(input, null);
  }

  private static void CloseOpened(ICommsSerial? input, TcpListener? listener) {
    try {
      listener?.Stop();
    } catch {
    }
    try {
      input?.Close();
    } catch {
    }
    if (input is IDisposable disposable) {
      disposable.Dispose();
    }
  }

  private void ResetSessionState() {
    lock (_stateGate) {
      _lastFix = null;
      _lastFixUtc = default;
      _lastArmStatusUtc = default;
      _moduleDetected = false;
      _armStatus = 0;
      _armError = "";
    }
    ArmStatusText = "Waiting for OPEN_DRONE_ID_ARM_STATUS.";
    GpsStatus = "No external GPS fix received.";
  }

  private bool TryBuildConfiguration(
      out OpenDroneIdConfiguration? configuration, out string error) {
    configuration = null;
    if (AreaCount is < 1 or > ushort.MaxValue) {
      error = "Aircraft count must be between 1 and 65535.";
      return false;
    }
    if (AreaRadiusM is < 0 or > ushort.MaxValue) {
      error = "Operation radius must be between 0 and 65535 metres.";
      return false;
    }
    if (!double.IsFinite(AreaCeilingM) || !double.IsFinite(AreaFloorM)
        || AreaCeilingM is < float.MinValue or > float.MaxValue
        || AreaFloorM is < float.MinValue or > float.MaxValue) {
      error = "Operation ceiling and floor must be finite values.";
      return false;
    }
    configuration = new OpenDroneIdConfiguration(
        SelectedUasIdType,
        UasId,
        SelectedUaType,
        SelectedDescriptionType,
        Description,
        (ushort)AreaCount,
        (ushort)AreaRadiusM,
        (float)AreaCeilingM,
        (float)AreaFloorM,
        SelectedCategoryEu,
        SelectedClassEu,
        SelectedClassificationType,
        SelectedOperatorLocationType,
        SelectedOperatorIdType,
        OperatorId);
    return OpenDroneIdMessageFactory.TryValidate(configuration, out error);
  }

  private void AppendRaw(string line) {
    EnqueueBounded(_rawLines, line);
    RawNmea = string.Join(Environment.NewLine, _rawLines);
  }

  private void AppendStatus(string line) {
    string stamped = $"{DateTime.Now:HH:mm:ss} {line}";
    EnqueueBounded(_statusLines, stamped);
    StatusLog = string.Join(Environment.NewLine, _statusLines);
  }

  private static void EnqueueBounded(Queue<string> queue, string line) {
    queue.Enqueue(line);
    while (queue.Count > 200) {
      queue.Dequeue();
    }
  }

  private void RefreshTargetDescription() {
    if (_disposed || _boundTarget != null) {
      return;
    }
    NmeaVehicleTarget? target = _activeTarget(false);
    TargetDescription = target == null
        ? "No vehicle selected."
        : "Ready for " + NmeaVehicleSession.Describe(target) + ".";
  }

  private void LoadSettings() {
    Settings settings = Settings.Instance;
    SelectedBaud = LoadInt(settings, "OpenDroneIdNmeaBaud", 4800);
    NetworkHost = settings["OpenDroneIdNmeaHost"] ?? "127.0.0.1";
    NetworkPort = LoadInt(settings, "OpenDroneIdNmeaPort", 14551);
    string? savedInput = settings["OpenDroneIdNmeaInput"];
    if (!string.IsNullOrWhiteSpace(savedInput) && Inputs.Contains(savedInput)) {
      SelectedInput = savedInput;
    }
    SelectedUasIdType = LoadEnum(
        settings, "OpenDroneIdUasIdType", MAVLink.MAV_ODID_ID_TYPE.NONE);
    UasId = settings["ODID_UAS_ID"] ?? "";
    SelectedUaType = LoadEnum(
        settings, "OpenDroneIdUaType", MAVLink.MAV_ODID_UA_TYPE.NONE);
    SelectedDescriptionType = LoadEnum(
        settings, "OpenDroneIdDescriptionType", MAVLink.MAV_ODID_DESC_TYPE.TEXT);
    Description = settings["OpenDroneIdDescription"] ?? "";
    // An emergency is a live operator declaration, never a startup preference. Do not silently
    // resume transmitting a declaration left over from an interrupted prior process.
    if (SelectedDescriptionType == MAVLink.MAV_ODID_DESC_TYPE.EMERGENCY) {
      SelectedDescriptionType = MAVLink.MAV_ODID_DESC_TYPE.TEXT;
      if (Description == EmergencyText) {
        Description = "";
      }
    }
    AreaCount = LoadInt(settings, "OpenDroneIdAreaCount", 1);
    AreaRadiusM = LoadInt(settings, "OpenDroneIdAreaRadius", 0);
    AreaCeilingM = LoadDouble(
        settings, "OpenDroneIdAreaCeiling", OpenDroneIdMessageFactory.UnknownAltitudeM);
    AreaFloorM = LoadDouble(
        settings, "OpenDroneIdAreaFloor", OpenDroneIdMessageFactory.UnknownAltitudeM);
    SelectedCategoryEu = LoadEnum(
        settings, "OpenDroneIdCategoryEu", MAVLink.MAV_ODID_CATEGORY_EU.UNDECLARED);
    SelectedClassEu = LoadEnum(
        settings, "OpenDroneIdClassEu", MAVLink.MAV_ODID_CLASS_EU.UNDECLARED);
    SelectedClassificationType = LoadEnum(
        settings, "OpenDroneIdClassificationType",
        MAVLink.MAV_ODID_CLASSIFICATION_TYPE.UNDECLARED);
    SelectedOperatorLocationType = LoadEnum(
        settings, "OpenDroneIdOperatorLocationType",
        MAVLink.MAV_ODID_OPERATOR_LOCATION_TYPE.LIVE_GNSS);
    SelectedOperatorIdType = LoadEnum(
        settings, "OpenDroneIdOperatorIdType", MAVLink.MAV_ODID_OPERATOR_ID_TYPE.CAA);
    OperatorId = settings["OpenDroneIdOperatorId"] ?? "";
  }

  private void PersistSettings() {
    if (!_usePersistentSettings) {
      return;
    }
    Settings settings = Settings.Instance;
    settings["OpenDroneIdNmeaInput"] = SelectedInput ?? "";
    settings["OpenDroneIdNmeaBaud"] = SelectedBaud.ToString(CultureInfo.InvariantCulture);
    settings["OpenDroneIdNmeaHost"] = NetworkHost;
    settings["OpenDroneIdNmeaPort"] = NetworkPort.ToString(CultureInfo.InvariantCulture);
    settings["OpenDroneIdUasIdType"] = SelectedUasIdType.ToString();
    settings["ODID_UAS_ID"] = UasId;
    settings["OpenDroneIdUaType"] = SelectedUaType.ToString();
    MAVLink.MAV_ODID_DESC_TYPE persistedDescriptionType = EmergencyDeclared
        ? _preEmergencyDescriptionType
        : SelectedDescriptionType;
    string persistedDescription = EmergencyDeclared ? _preEmergencyDescription : Description;
    settings["OpenDroneIdDescriptionType"] = persistedDescriptionType.ToString();
    settings["OpenDroneIdDescription"] = persistedDescription;
    settings["OpenDroneIdAreaCount"] = AreaCount.ToString(CultureInfo.InvariantCulture);
    settings["OpenDroneIdAreaRadius"] = AreaRadiusM.ToString(CultureInfo.InvariantCulture);
    settings["OpenDroneIdAreaCeiling"] = AreaCeilingM.ToString(CultureInfo.InvariantCulture);
    settings["OpenDroneIdAreaFloor"] = AreaFloorM.ToString(CultureInfo.InvariantCulture);
    settings["OpenDroneIdCategoryEu"] = SelectedCategoryEu.ToString();
    settings["OpenDroneIdClassEu"] = SelectedClassEu.ToString();
    settings["OpenDroneIdClassificationType"] = SelectedClassificationType.ToString();
    settings["OpenDroneIdOperatorLocationType"] = SelectedOperatorLocationType.ToString();
    settings["OpenDroneIdOperatorIdType"] = SelectedOperatorIdType.ToString();
    settings["OpenDroneIdOperatorId"] = OperatorId;
    settings.Save();
  }

  private static int LoadInt(Settings settings, string key, int fallback) =>
      int.TryParse(settings[key], NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
          ? value
          : fallback;

  private static double LoadDouble(Settings settings, string key, double fallback) =>
      double.TryParse(settings[key], NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
          ? value
          : fallback;

  private static T LoadEnum<T>(Settings settings, string key, T fallback) where T : struct, Enum =>
      Enum.TryParse(settings[key], ignoreCase: true, out T value) && Enum.IsDefined(value)
          ? value
          : fallback;

  private static bool IsNetworkInput(string? value) =>
      value is TcpHost or TcpClient or UdpHost or UdpClient;

  private static IBrush Brush(string value) => new SolidColorBrush(Color.Parse(value));

  public void Dispose() {
    if (_disposed) {
      return;
    }
    _disposed = true;
    if (_subscribedToAppState) {
      AppState.ConnectionChanged -= OnConnectionChanged;
    }
    _targetInvalidated = true;
    CancellationTokenSource? cts = _cts;
    _cts = null;
    _readerTask = null;
    _senderTask = null;
    _acceptTask = null;
    cts?.Cancel();
    CloseTransport();
    UnsubscribeAll();
    _boundTarget = null;
    _configuration = null;
    cts?.Dispose();
  }

  private const string TargetChangedMessage =
      "The active modem or vehicle changed or disconnected. Open Drone ID was stopped; "
      + "verify the selected target before starting it again.";
}

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner.Comms;
using MissionPlanner.Utilities;
using MissionPlannerAvalonia.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SerialPort = MissionPlanner.Comms.SerialPort;

namespace MissionPlannerAvalonia.ViewModels;

public partial class SerialOutputCotViewModel : ViewModelBase, IDisposable {
  public const string TakMulticast = "TAK Multicast";
  public const string UdpClient = "UDP Client";
  public const string UdpHost = "UDP Host";
  public const string TcpClient = "TCP Client";
  public const string TcpHost = "TCP Host";

  private readonly object _clientsSync = new();
  private readonly List<System.Net.Sockets.TcpClient> _clients = new();
  private readonly HashSet<CotIdentityRow> _subscribedIdentityRows = [];
  private CancellationTokenSource? _runCts;
  private System.Net.Sockets.UdpClient? _udp;
  private IPEndPoint? _udpDestination;
  private TcpListener? _listener;
  private System.Net.Sockets.TcpClient? _tcpClient;
  private SerialPort? _serial;
  private CotIdentitySnapshot[] _identitySnapshot = [];
  private bool _identitiesDirty;

  public SerialOutputCotViewModel() {
    Host = "239.2.3.1";
    Port = 6969;
    Baud = 57600;
    UpdateSeconds = 10;
    EventType = "a-f-A-M-F-Q";
    UidPrefix = "MissionPlanner";

    RefreshEndpoints();
    var settings = Settings.Instance;
    var savedEndpoint = settings["CoT_AvaloniaEndpoint"];
    SelectedEndpoint = savedEndpoint != null && Endpoints.Contains(savedEndpoint)
        ? savedEndpoint
        : TakMulticast;
    Host = settings["CoT_AvaloniaHost", Host];
    Port = settings.GetInt32("CoT_AvaloniaPort", Port);
    Baud = settings.GetInt32("CoT_AvaloniaBaud", Baud);
    UpdateSeconds = settings.GetDouble("CoT_AvaloniaUpdateSeconds", UpdateSeconds);
    EventType = settings["CoT_AvaloniaEventType", EventType];
    UidPrefix = settings["CoT_AvaloniaUidPrefix", UidPrefix];
    Callsign = settings["CoT_AvaloniaCallsign", Callsign];
    IndentXml = settings.GetBoolean("CoT_AvaloniaIndentXml", IndentXml);
    AdvancedMode = settings.GetBoolean("CoT_CB_advancedMode", true);
    foreach (var row in ParseIdentityRows(settings["CoTUID"])) {
      Identities.Add(row);
    }
    Identities.CollectionChanged += OnIdentitiesChanged;
    foreach (var row in Identities) {
      SubscribeIdentity(row);
    }
    RebuildIdentitySnapshot();
  }

  public ObservableCollection<string> Endpoints { get; } = new();
  public ObservableCollection<int> Bauds { get; } = new() { 4800, 9600, 19200, 38400, 57600, 115200 };
  public ObservableCollection<CotIdentityRow> Identities { get; } = [];

  [ObservableProperty] private string? _selectedEndpoint;
  [ObservableProperty] private string _host = "";
  [ObservableProperty] private int _port;
  [ObservableProperty] private int _baud;
  [ObservableProperty] private double _updateSeconds;
  [ObservableProperty] private string _eventType = "";
  [ObservableProperty] private string _uidPrefix = "";
  [ObservableProperty] private string _callsign = "";
  [ObservableProperty] private bool _indentXml;
  [ObservableProperty] private bool _advancedMode = true;
  [ObservableProperty] private CotIdentityRow? _selectedIdentity;
  [ObservableProperty] private string _status = "Stopped.";
  [ObservableProperty] private string _connectButtonText = "Connect";
  [ObservableProperty] private string _lastEvent = "";

  public bool IsRunning => _runCts != null;
  public bool IsNetworkEndpoint => SelectedEndpoint is TakMulticast or UdpClient or UdpHost or TcpClient or TcpHost;
  public bool IsSerialEndpoint => !string.IsNullOrWhiteSpace(SelectedEndpoint) && !IsNetworkEndpoint;

  partial void OnSelectedEndpointChanged(string? value) {
    switch (value) {
      case TakMulticast:
        Host = "239.2.3.1";
        Port = 6969;
        break;
      case UdpHost:
      case TcpHost:
        Host = "0.0.0.0";
        Port = 14551;
        break;
      case UdpClient:
      case TcpClient:
        if (Host is "" or "0.0.0.0" or "239.2.3.1") {
          Host = "127.0.0.1";
        }
        Port = 14551;
        break;
    }
    OnPropertyChanged(nameof(IsNetworkEndpoint));
    OnPropertyChanged(nameof(IsSerialEndpoint));
  }

  [RelayCommand]
  private void RefreshEndpoints() {
    var selected = SelectedEndpoint;
    Endpoints.Clear();
    Endpoints.Add(TakMulticast);
    Endpoints.Add(UdpClient);
    Endpoints.Add(UdpHost);
    Endpoints.Add(TcpClient);
    Endpoints.Add(TcpHost);
    foreach (var port in SerialPort.GetPortNames().Distinct()) {
      Endpoints.Add(port);
    }
    SelectedEndpoint = selected != null && Endpoints.Contains(selected)
        ? selected
        : Endpoints.FirstOrDefault();
  }

  [RelayCommand]
  private void RefreshIdentitySystems() {
    try {
      int added = 0;
      foreach (byte systemId in AppState.comPort.MAVlist.Select(mav => mav.sysid).Distinct().Order()) {
        string systemIdText = systemId.ToString(CultureInfo.InvariantCulture);
        if (Volatile.Read(ref _identitySnapshot).Any(row => row.SystemId == systemIdText)) {
          continue;
        }
        Identities.Add(new CotIdentityRow {
          SystemId = systemIdText,
          Uid = $"{UidPrefix}-{systemId}",
        });
        added++;
      }
      Status = added == 0
          ? "No new MAVLink systems found. Identity rows are preserved for offline systems."
          : $"Added {added} MAVLink system identity row(s).";
    } catch (Exception ex) {
      Status = "Unable to refresh CoT systems: " + ex.Message;
    }
  }

  [RelayCommand]
  private void AddIdentity() {
    int systemId = Enumerable.Range(1, 255).FirstOrDefault(candidate =>
        !Volatile.Read(ref _identitySnapshot).Any(row =>
            row.SystemId == candidate.ToString(CultureInfo.InvariantCulture)));
    if (systemId == 0) {
      Status = "All MAVLink system IDs already have identity rows.";
      return;
    }
    var row = new CotIdentityRow {
      SystemId = systemId.ToString(CultureInfo.InvariantCulture),
      Uid = $"{UidPrefix}-{systemId}",
    };
    Identities.Add(row);
    SelectedIdentity = row;
  }

  [RelayCommand]
  private void RemoveIdentity() {
    if (SelectedIdentity == null) {
      return;
    }
    Identities.Remove(SelectedIdentity);
    SelectedIdentity = null;
  }

  [RelayCommand]
  private async Task ToggleConnect() {
    if (IsRunning) {
      Stop();
      return;
    }
    if (string.IsNullOrWhiteSpace(SelectedEndpoint)) {
      Status = "Select an output endpoint.";
      return;
    }
    if (UpdateSeconds is < 0.1 or > 3600) {
      Status = "Update interval must be between 0.1 and 3600 seconds.";
      return;
    }

    var cts = new CancellationTokenSource();
    try {
      await OpenEndpointAsync(SelectedEndpoint, cts.Token);
      _runCts = cts;
      _ = Task.Run(() => OutputLoopAsync(cts.Token), cts.Token);
      ConnectButtonText = "Stop";
      var runningStatus = SelectedEndpoint == UdpHost
          ? $"Listening for a UDP peer on port {Port}."
          : $"Emitting Cursor-on-Target events through {SelectedEndpoint}.";
      try {
        SaveSettings();
        Status = runningStatus;
      } catch (Exception ex) {
        Status = runningStatus + " Settings could not be saved: " + ex.Message;
      }
      OnPropertyChanged(nameof(IsRunning));
    } catch (Exception ex) {
      cts.Dispose();
      CloseEndpoint();
      Status = "Unable to start CoT output: " + ex.Message;
    }
  }

  private void SaveSettings() {
    var settings = Settings.Instance;
    settings["CoT_AvaloniaEndpoint"] = SelectedEndpoint ?? "";
    settings["CoT_AvaloniaHost"] = Host;
    settings["CoT_AvaloniaPort"] = Port.ToString();
    settings["CoT_AvaloniaBaud"] = Baud.ToString();
    settings["CoT_AvaloniaUpdateSeconds"] = UpdateSeconds.ToString();
    settings["CoT_AvaloniaEventType"] = EventType;
    settings["CoT_AvaloniaUidPrefix"] = UidPrefix;
    settings["CoT_AvaloniaCallsign"] = Callsign;
    settings["CoT_AvaloniaIndentXml"] = IndentXml.ToString();
    settings["CoT_CB_advancedMode"] = AdvancedMode.ToString();
    if (_identitiesDirty) {
      settings["CoTUID"] = SerializeIdentityRows(Identities);
    }
    settings.Save();
    _identitiesDirty = false;
  }

  private async Task OpenEndpointAsync(string endpoint, CancellationToken token) {
    switch (endpoint) {
      case TakMulticast:
      case UdpClient: {
          var address = await ResolveAddressAsync(Host);
          _udpDestination = new IPEndPoint(address, Port);
          _udp = new System.Net.Sockets.UdpClient(address.AddressFamily);
          break;
        }
      case UdpHost:
        _udp = new System.Net.Sockets.UdpClient(Port);
        _ = Task.Run(() => LearnUdpPeerAsync(_udp, token), token);
        break;
      case TcpClient:
        _tcpClient = new System.Net.Sockets.TcpClient();
        await _tcpClient.ConnectAsync(Host, Port, token);
        break;
      case TcpHost:
        _listener = new TcpListener(ParseListenAddress(Host), Port);
        _listener.Start();
        _ = Task.Run(() => AcceptClientsAsync(_listener, token), token);
        break;
      default:
        _serial = new SerialPort { PortName = endpoint, BaudRate = Baud };
        _serial.Open();
        break;
    }
  }

  private static async Task<IPAddress> ResolveAddressAsync(string host) {
    if (IPAddress.TryParse(host, out var address)) {
      return address;
    }
    var addresses = await Dns.GetHostAddressesAsync(host);
    return addresses.First(address => address.AddressFamily is AddressFamily.InterNetwork
        or AddressFamily.InterNetworkV6);
  }

  private static IPAddress ParseListenAddress(string host) =>
      string.IsNullOrWhiteSpace(host) || host == "0.0.0.0"
          ? IPAddress.Any
          : IPAddress.Parse(host);

  private async Task LearnUdpPeerAsync(System.Net.Sockets.UdpClient udp, CancellationToken token) {
    try {
      while (!token.IsCancellationRequested) {
        var received = await udp.ReceiveAsync(token);
        _udpDestination = received.RemoteEndPoint;
        Dispatcher.UIThread.Post(() => Status =
            $"Emitting Cursor-on-Target events to UDP peer {received.RemoteEndPoint}.");
      }
    } catch (OperationCanceledException) {
    } catch (ObjectDisposedException) {
    } catch (Exception ex) {
      Dispatcher.UIThread.Post(() => Status = "UDP host error: " + ex.Message);
    }
  }

  private async Task AcceptClientsAsync(TcpListener listener, CancellationToken token) {
    try {
      while (!token.IsCancellationRequested) {
        var client = await listener.AcceptTcpClientAsync(token);
        lock (_clientsSync) {
          _clients.Add(client);
        }
        Dispatcher.UIThread.Post(() => Status = $"CoT TCP client connected: {client.Client.RemoteEndPoint}.");
      }
    } catch (OperationCanceledException) {
    } catch (ObjectDisposedException) {
    } catch (Exception ex) {
      Dispatcher.UIThread.Post(() => Status = "TCP host error: " + ex.Message);
    }
  }

  private async Task OutputLoopAsync(CancellationToken token) {
    while (!token.IsCancellationRequested) {
      try {
        var identities = Volatile.Read(ref _identitySnapshot);
        var events = AppState.comPort.MAVlist.Select(mav => {
          string systemId = mav.sysid.ToString(CultureInfo.InvariantCulture);
          var identity = identities.FirstOrDefault(row => row.SystemId == systemId);
          string eventUid = ResolveEventUid(
              UidPrefix, mav.sysid, mav.compid, identity != null, identity?.Uid);
          return CotEventSerializer.Serialize(
              eventUid, EventType,
              mav.cs.lat, mav.cs.lng, mav.cs.altasl, mav.cs.groundcourse, mav.cs.groundspeed,
              string.IsNullOrWhiteSpace(Callsign) ? null : $"{Callsign}-{mav.sysid}",
              IndentXml,
              identity: AdvancedMode && eventUid.Length > 0 ? identity?.ToDetail() : null);
        }).ToArray();
        foreach (var xml in events) {
          await SendAsync(xml + "\n", token);
        }
        string preview = string.Join(Environment.NewLine, events);
        Dispatcher.UIThread.Post(() => LastEvent = preview);
        await Task.Delay(TimeSpan.FromSeconds(UpdateSeconds), token);
      } catch (OperationCanceledException) {
        break;
      } catch (Exception ex) {
        Dispatcher.UIThread.Post(() => Status = "CoT output warning: " + ex.Message);
        try {
          await Task.Delay(TimeSpan.FromSeconds(1), token);
        } catch (OperationCanceledException) {
          break;
        }
      }
    }
  }

  private async Task SendAsync(string text, CancellationToken token) {
    var bytes = Encoding.UTF8.GetBytes(text);
    if (_udp != null && _udpDestination != null) {
      await _udp.SendAsync(bytes, _udpDestination, token);
    }
    if (_tcpClient?.Connected == true) {
      await _tcpClient.GetStream().WriteAsync(bytes, token);
    }
    List<System.Net.Sockets.TcpClient> clients;
    lock (_clientsSync) {
      clients = _clients.ToList();
    }
    foreach (var client in clients) {
      try {
        await client.GetStream().WriteAsync(bytes, token);
      } catch {
        lock (_clientsSync) {
          _clients.Remove(client);
        }
        client.Dispose();
      }
    }
    if (_serial?.IsOpen == true) {
      _serial.Write(text);
    }
  }

  private void Stop() {
    var cts = _runCts;
    _runCts = null;
    cts?.Cancel();
    CloseEndpoint();
    cts?.Dispose();
    ConnectButtonText = "Connect";
    Status = "Stopped.";
    OnPropertyChanged(nameof(IsRunning));
  }

  private void CloseEndpoint() {
    try { _listener?.Stop(); } catch { }
    _listener = null;
    try { _udp?.Dispose(); } catch { }
    _udp = null;
    _udpDestination = null;
    try { _tcpClient?.Dispose(); } catch { }
    _tcpClient = null;
    try { _serial?.Close(); } catch { }
    _serial = null;
    lock (_clientsSync) {
      foreach (var client in _clients) {
        client.Dispose();
      }
      _clients.Clear();
    }
  }

  private void OnIdentitiesChanged(object? sender, NotifyCollectionChangedEventArgs e) {
    if (e.Action == NotifyCollectionChangedAction.Reset) {
      foreach (var row in _subscribedIdentityRows.ToArray()) {
        UnsubscribeIdentity(row);
      }
      foreach (var row in Identities) {
        SubscribeIdentity(row);
      }
    }
    if (e.OldItems != null) {
      foreach (CotIdentityRow row in e.OldItems) {
        UnsubscribeIdentity(row);
      }
    }
    if (e.NewItems != null) {
      foreach (CotIdentityRow row in e.NewItems) {
        SubscribeIdentity(row);
      }
    }
    _identitiesDirty = true;
    RebuildIdentitySnapshot();
  }

  private void SubscribeIdentity(CotIdentityRow row) {
    if (_subscribedIdentityRows.Add(row)) {
      row.PropertyChanged += OnIdentityChanged;
    }
  }

  private void UnsubscribeIdentity(CotIdentityRow row) {
    if (_subscribedIdentityRows.Remove(row)) {
      row.PropertyChanged -= OnIdentityChanged;
    }
  }

  private void OnIdentityChanged(object? sender, PropertyChangedEventArgs e) {
    _identitiesDirty = true;
    RebuildIdentitySnapshot();
  }

  private void RebuildIdentitySnapshot() => Volatile.Write(ref _identitySnapshot,
      [.. Identities.Select(row => new CotIdentitySnapshot(
          row.SystemId, row.Uid, row.IncludeTakv, row.ContactCallsign,
          row.ContactEndpoint, row.Vmf))]);

  internal static string SerializeIdentityRows(IEnumerable<CotIdentityRow> rows) =>
      new JArray(rows.Select(row => new JArray(
          SystemIdToken(row.SystemId), TextToken(row.Uid), row.IncludeTakv,
          TextToken(row.ContactCallsign), TextToken(row.ContactEndpoint),
          TextToken(row.Vmf)))).ToString(Formatting.None);

  internal static string ResolveEventUid(
      string uidPrefix, byte systemId, byte componentId,
      bool hasIdentityRow, string? identityUid) =>
      hasIdentityRow
          ? identityUid ?? ""
          : $"{uidPrefix}-{systemId}-{componentId}";

  internal static IReadOnlyList<CotIdentityRow> ParseIdentityRows(string? json) {
    if (string.IsNullOrWhiteSpace(json)) {
      return [];
    }
    try {
      var result = new List<CotIdentityRow>();
      foreach (var token in JArray.Parse(json).OfType<JArray>()) {
        if (token.Count < 2) {
          continue;
        }
        result.Add(new CotIdentityRow {
          SystemId = TokenText(token[0]),
          Uid = TokenText(token[1]),
          IncludeTakv = ParseBoolean(token.Count > 2 ? token[2] : null),
          ContactCallsign = token.Count > 3 ? TokenText(token[3]) : null,
          ContactEndpoint = token.Count > 4 ? TokenText(token[4]) : null,
          Vmf = token.Count > 5 ? TokenText(token[5]) : null,
        });
      }
      return result;
    } catch (JsonException) {
      return [];
    }
  }

  private static bool ParseBoolean(JToken? token) =>
      token?.Type == JTokenType.Boolean
          ? token.Value<bool>()
          : bool.TryParse(token?.ToString(), out bool value) && value;

  private static string? TokenText(JToken? token) =>
      token == null || token.Type == JTokenType.Null ? null : token.ToString();

  private static JToken SystemIdToken(string? systemId) =>
      int.TryParse(systemId, NumberStyles.Integer, CultureInfo.InvariantCulture, out int numeric)
          ? new JValue(numeric)
          : systemId == null ? JValue.CreateNull() : new JValue(systemId);

  private static JToken TextToken(string? value) =>
      value == null ? JValue.CreateNull() : new JValue(value);

  public void Dispose() {
    try {
      SaveSettings();
    } catch {
      // Closing the tool must still release sockets/ports if settings storage is unavailable.
    }
    Identities.CollectionChanged -= OnIdentitiesChanged;
    foreach (var row in _subscribedIdentityRows.ToArray()) {
      UnsubscribeIdentity(row);
    }
    Stop();
  }

  private sealed record CotIdentitySnapshot(
      string? SystemId,
      string? Uid,
      bool IncludeTakv,
      string? ContactCallsign,
      string? ContactEndpoint,
      string? Vmf) {
    public CotIdentityDetail ToDetail() => new(
        IncludeTakv, ContactCallsign, ContactEndpoint, Vmf);
  }
}

public partial class CotIdentityRow : ObservableObject {
  [ObservableProperty] private string? _systemId;
  [ObservableProperty] private string? _uid;
  [ObservableProperty] private bool _includeTakv;
  [ObservableProperty] private string? _contactCallsign;
  [ObservableProperty] private string? _contactEndpoint;
  [ObservableProperty] private string? _vmf;
}

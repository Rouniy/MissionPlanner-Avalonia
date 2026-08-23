using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlannerAvalonia.Services;

namespace MissionPlannerAvalonia.ViewModels.Setup;

public sealed record NvRadioChoice(int Channel, string Label) {
  public override string ToString() => Label;
}

public partial class NvModemDeviceChoice : ObservableObject {
  internal NvModemDeviceChoice(NvModemDeviceState state) {
    State = state;
  }

  internal NvModemDeviceState State { get; }

  [ObservableProperty]
  private string _label = "NV modem";

  public override string ToString() => Label;
}

public partial class NvModemParameterRow : ObservableObject {
  internal NvModemParameterRow(string name, double original, byte type) {
    Name = name;
    Group = NvModemCatalog.Group(name);
    Description = NvModemCatalog.Description(name);
    Type = type;
    Original = original;
    _valueText = NvModemParameterCodec.Display(original, type);
    Revalidate();
  }

  public string Name { get; }
  public string Group { get; }
  public string Description { get; }
  internal byte Type { get; }
  internal double Original { get; private set; }
  public bool IsReadOnly => NvModemCatalog.IsReadOnly(Name);

  [ObservableProperty]
  private string _valueText;

  [ObservableProperty]
  private bool _isValid;

  [ObservableProperty]
  private bool _isChanged;

  public string EditState => !IsValid ? "Invalid"
      : IsChanged ? "Changed"
      : IsReadOnly ? "Read-only" : "";

  partial void OnValueTextChanged(string value) => Revalidate();

  partial void OnIsValidChanged(bool value) => OnPropertyChanged(nameof(EditState));

  partial void OnIsChangedChanged(bool value) => OnPropertyChanged(nameof(EditState));

  internal bool TryValue(out double value) =>
      NvModemParameterCodec.TryParse(ValueText, Type, out value)
      && (!NvModemCatalog.IsNv5KeyByte(Name)
          || value is >= 0 and <= 255
          || (NvModemParameterCodec.NearlyEqual(value, -1)
              && NvModemParameterCodec.NearlyEqual(Original, -1)));

  internal void Accept(double value) {
    Original = value;
    ValueText = NvModemParameterCodec.Display(value, Type);
    Revalidate();
  }

  private void Revalidate() {
    IsValid = TryValue(out double value);
    IsChanged = IsValid && (double.IsNaN(Original)
        || !NvModemParameterCodec.NearlyEqual(value, Original));
  }
}

public partial class NvRadioStatusRow : ObservableObject {
  internal NvRadioStatusRow(int channel) {
    Channel = channel;
  }

  public int Channel { get; }
  public string Radio => $"Radio {Channel}";

  [ObservableProperty]
  private string _identity = "—";

  [ObservableProperty]
  private string _mode = "—";

  [ObservableProperty]
  private string _link = "—";

  [ObservableProperty]
  private string _traffic = "—";

  [ObservableProperty]
  private string _details = "No live status received.";
}

public sealed record NvRadioCopyChoice(
    object DeviceIdentity, int Channel, string Label) {
  public override string ToString() => Label;
}

internal sealed record NvModemDeviceKey(
    NvModemLink Link, byte SystemId, byte ComponentId);

internal sealed class NvModemDeviceState {
  internal NvModemDeviceState(NvModemDeviceKey key) {
    Key = key;
  }

  internal NvModemDeviceKey Key { get; }
  internal NvModemGeneration Generation { get; set; }
  internal uint ProductProfile { get; set; }
  internal DateTime LastSeenUtc { get; set; }
  internal int ExpectedParameterCount { get; set; }
  internal Dictionary<string, double> Parameters { get; } = new(StringComparer.Ordinal);
  internal Dictionary<string, byte> ParameterTypes { get; } = new(StringComparer.Ordinal);
  internal Dictionary<string, double> LocallyKnownKeyBytes { get; } = new(StringComparer.Ordinal);
  internal Dictionary<int, uint> LegacyKeyWords { get; } = [];
  internal string LegacyKeyFingerprint { get; set; } = "";
  internal Dictionary<int, Nv5LinkStatusMessage> Links { get; } = [];
  internal NvRxStatMessage LegacyRxStatus { get; set; }
  internal bool LegacyRxReady { get; set; }
  internal MAVLink.mavlink_radio_status_t LegacyRadioStatus { get; set; }
  internal bool LegacyRadioReady { get; set; }
  internal bool ParameterRefreshPending { get; set; }
  internal bool ParameterListInProgress { get; set; }
  internal DateTime ParameterListLastProgressUtc { get; set; }
  internal int ParameterListRetries { get; set; }
  internal DateTime RebootReadyUtc { get; set; }
  internal string RtspPath { get; set; } = "";
  internal bool RtspPathReady { get; set; }
  internal NvModemDeviceChoice? Choice { get; set; }
}

internal enum NvWriteKind {
  Parameter,
  RtspPath,
  Reboot,
  TransmitControl,
}

internal sealed class NvWriteOperation {
  internal NvWriteKind Kind { get; init; }
  internal required NvModemDeviceState Device { get; init; }
  internal string Name { get; init; } = "";
  internal double Value { get; init; }
  internal byte ParameterType { get; init; }
  internal string Path { get; init; } = "";
  internal uint TransactionId { get; init; }
  internal byte Channel { get; init; }
  internal bool Enabled { get; init; }
  internal int Retries { get; set; }
  internal DateTime SentUtc { get; set; }
}

public partial class NvModemViewModel : ViewModelBase, IDisposable {
  private static readonly TimeSpan OnlineTimeout = TimeSpan.FromSeconds(5);
  private static readonly TimeSpan TransactionTimeout = TimeSpan.FromMilliseconds(1200);
  private static readonly TimeSpan ParameterListTimeout = TimeSpan.FromSeconds(3);
  private static readonly TimeSpan ParameterListRetry = TimeSpan.FromSeconds(2);
  private const int MaximumWriteAttempts = 3;
  private const int MaximumParameterListRetries = 2;
  private const ushort SetTransmitEnabledCommand = 42010;
  private const byte LegacyNv5ComponentId = (byte)MAVLink.MAV_COMPONENT.MAV_COMP_ID_TELEMETRY_RADIO;

  private readonly INvModemMavlinkTransport _transport;
  private readonly Func<DateTime> _utcNow;
  private readonly Dictionary<NvModemDeviceKey, NvModemDeviceState> _devices = [];
  private readonly Dictionary<string, NvModemParameterRow> _parameterRows =
      new(StringComparer.Ordinal);
  private readonly Queue<NvWriteOperation> _writeQueue = [];
  private readonly Dictionary<(NvModemLink Link, byte SystemId, byte ComponentId), DateTime>
      _legacyProbeTimes = [];
  private readonly DispatcherTimer? _serviceTimer;
  private NvWriteOperation? _currentWrite;
  private uint _nextTransactionId = unchecked((uint)Environment.TickCount64);
  private bool _switchGuard;
  private bool _settingRtsp;
  private bool _rtspEdited;
  private bool _disposed;
  private bool _parameterWriteInTransaction;
  private bool _rebootRequiredInTransaction;
  private bool _keyWriteInTransaction;
  private int _keyWriteChannel;

  public NvModemViewModel()
      : this(new NvModemMavlinkTransport(), () => DateTime.UtcNow, startTimer: true) {
  }

  internal NvModemViewModel(
      INvModemMavlinkTransport transport, Func<DateTime> utcNow, bool startTimer) {
    _transport = transport;
    _utcNow = utcNow;
    _transport.PacketReceived += OnPacketReceived;
    _transport.LinksChanged += OnLinksChanged;
    RadioStatuses.Add(new NvRadioStatusRow(1));
    RadioStatuses.Add(new NvRadioStatusRow(2));
    SelectedDebugRadio = DebugRadios[0];
    if (startTimer) {
      _serviceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
      _serviceTimer.Tick += OnServiceTick;
      _serviceTimer.Start();
    }
    Discover();
  }

  public string Title => "NV Modem";
  public string Instructions =>
      "Discover and configure NV4/NV5 modems through the MAVLink connections already open in "
      + "Mission Planner. No separate UDP, TCP or serial port is opened.";

  public ObservableCollection<NvModemDeviceChoice> Devices { get; } = [];
  public ObservableCollection<NvModemParameterRow> Parameters { get; } = [];
  public ObservableCollection<NvRadioStatusRow> RadioStatuses { get; } = [];
  public ObservableCollection<NvRadioChoice> PresetRadios { get; } = [];
  public ObservableCollection<NvRadioChoice> KeyRadios { get; } = [];
  public ObservableCollection<NvRadioCopyChoice> CopySources { get; } = [];
  public IReadOnlyList<NvRadioChoice> DebugRadios { get; } = [
    new(1, "Radio 1"), new(2, "Radio 2"),
  ];

  [ObservableProperty]
  private NvModemDeviceChoice? _selectedDevice;

  [ObservableProperty]
  private NvRadioChoice? _selectedPresetRadio;

  [ObservableProperty]
  private NvRadioChoice? _selectedKeyRadio;

  [ObservableProperty]
  private NvRadioChoice? _selectedDebugRadio;

  [ObservableProperty]
  private NvRadioCopyChoice? _selectedCopySource;

  [ObservableProperty]
  private string _connectionText = "No modem discovered";

  [ObservableProperty]
  private string _identityText = "Open a MAVLink connection, then press Discover.";

  [ObservableProperty]
  private string _parameterProgress = "Parameters: 0 / 0";

  [ObservableProperty]
  private string _status = "Ready. Discovering NV4/NV5 modems over open MAVLink links…";

  [ObservableProperty]
  private bool _isError;

  [ObservableProperty]
  private bool _isBusy;

  [ObservableProperty]
  private string _keyText = "";

  [ObservableProperty]
  private string _keyFingerprint = "Stored fingerprint: unavailable";

  [ObservableProperty]
  private string _keyHint = "Read parameters to edit an encryption key.";

  [ObservableProperty]
  private string _rtspPath = "";

  public bool HasSelectedDevice => SelectedDevice != null;
  public bool HasParameters => Parameters.Count != 0;
  public bool HasPendingChanges => _parameterRows.Values.Any(row => row.IsChanged)
      || SelectedState is { } device && RtspPending(device);
  public bool CanEdit => SelectedState is { } device && IsOnline(device) && !IsBusy;
  public bool CanSave => CanEdit && HasPendingChanges;
  public bool CanUseNv5Controls => CanEdit
      && SelectedState?.Generation == NvModemGeneration.Nv5;
  public bool CanUseRtsp => CanEdit && SelectedState is { } device && SupportsRtsp(device);
  public bool CanReboot => CanUseNv5Controls && !HasPendingChanges
      && SelectedState!.RebootReadyUtc <= _utcNow();
  public bool CanGenerateKey => CanEdit && SelectedKeyRadio != null;
  public bool CanSetKey => CanEdit && SelectedKeyRadio != null && KeyText.Length != 0;
  public bool CanCopyRadio => CanUseNv5Controls
      && SelectedPresetRadio != null && SelectedCopySource != null;
  public bool CanControlTransmitter => CanUseNv5Controls
      && SelectedDebugRadio is { } selected
      && SelectedState!.Links.TryGetValue(selected.Channel, out var link)
      && link.Role != 0;

  private NvModemDeviceState? SelectedState => SelectedDevice?.State;

  partial void OnSelectedDeviceChanged(
      NvModemDeviceChoice? oldValue, NvModemDeviceChoice? newValue) {
    if (_switchGuard || ReferenceEquals(oldValue, newValue)) {
      return;
    }
    if (IsBusy && oldValue != null) {
      _switchGuard = true;
      SelectedDevice = oldValue;
      _switchGuard = false;
      return;
    }
    if (oldValue != null && newValue != null && HasPendingChangesFor(oldValue.State)) {
      _switchGuard = true;
      SelectedDevice = oldValue;
      _switchGuard = false;
      _ = ConfirmDeviceSwitchAsync(newValue);
      return;
    }
    ShowSelectedDevice(clearDeviceParameters: newValue != null);
  }

  partial void OnSelectedPresetRadioChanged(NvRadioChoice? value) => RefreshControls();

  partial void OnSelectedDebugRadioChanged(NvRadioChoice? value) => RefreshControls();

  partial void OnSelectedCopySourceChanged(NvRadioCopyChoice? value) => RefreshControls();

  partial void OnSelectedKeyRadioChanged(NvRadioChoice? value) {
    SyncKeyText();
    RefreshControls();
  }

  partial void OnKeyTextChanged(string value) => RefreshControls();

  partial void OnRtspPathChanged(string value) {
    if (!_settingRtsp) {
      _rtspEdited = true;
      RefreshControls();
    }
  }

  private async Task ConfirmDeviceSwitchAsync(NvModemDeviceChoice requested) {
    bool discard = await Dialogs.Confirm(
        "Unsaved modem settings",
        "The selected modem has unsaved parameter changes. Discard them and switch devices?");
    if (!discard || _disposed || IsBusy) {
      return;
    }
    _switchGuard = true;
    SelectedDevice = requested;
    _switchGuard = false;
    ShowSelectedDevice(clearDeviceParameters: true);
  }

  [RelayCommand]
  private void Discover() {
    int openLinks = 0;
    foreach (NvModemLink link in _transport.Snapshot()) {
      if (link.Link.BaseStream?.IsOpen != true) {
        continue;
      }
      openLinks++;
      SendIdentityRequest(link, 0, 0);
      foreach (byte systemId in new byte[] { 1, 255 }) {
        for (byte componentId = 16; componentId <= 19; componentId++) {
          ProbeLegacy(link, systemId, componentId, force: true);
        }
      }
    }
    SetStatus(openLinks == 0
        ? "No open MAVLink connections. Connect over UDP, TCP or serial and press Discover."
        : $"Discovery request sent on {openLinks} open MAVLink connection(s).", openLinks == 0);
  }

  [RelayCommand]
  private void RefreshSelected() {
    NvModemDeviceState? device = SelectedState;
    if (device == null || IsBusy) {
      return;
    }
    ClearDeviceParameters(device);
    if (device.Generation == NvModemGeneration.Nv5) {
      SendIdentityRequest(device.Key.Link, device.Key.SystemId, device.Key.ComponentId);
    }
    SendParameterRequestList(device);
    if (SupportsRtsp(device)) {
      SendRtspRequest(device);
    }
    SetStatus($"Reading parameters from {ModemName(device)} "
        + $"{device.Key.SystemId}:{device.Key.ComponentId}…");
  }

  [RelayCommand]
  private void ReadRtsp() {
    NvModemDeviceState? device = SelectedState;
    if (device == null || !CanUseRtsp) {
      SetStatus("Select an online NV5 modem with an LR2021 radio.", true);
      return;
    }
    SendRtspRequest(device);
    SetStatus("RTSP path requested.");
  }

  [RelayCommand]
  private async Task Save() {
    NvModemDeviceState? device = SelectedState;
    if (device == null || IsBusy) {
      return;
    }
    NvModemParameterRow? invalid = _parameterRows.Values.FirstOrDefault(row => !row.IsValid);
    if (invalid != null) {
      SetStatus($"Parameter {invalid.Name} has an invalid value.", true);
      return;
    }
    NvModemParameterRow[] changed = [.. _parameterRows.Values
        .Where(row => row.IsChanged && !row.IsReadOnly && IsApplicable(row.Name))
        .OrderBy(row => WritePriority(row.Name)).ThenBy(row => row.Name, StringComparer.Ordinal)];
    bool rtspChanged = RtspPending(device);
    if (rtspChanged && (!RtspPath.StartsWith("/", StringComparison.Ordinal)
        || Encoding.Latin1.GetByteCount(RtspPath) > 95)) {
      SetStatus("RTSP path must begin with '/' and fit in 95 Latin-1 bytes.", true);
      return;
    }
    if (changed.Length == 0 && !rtspChanged) {
      SetStatus("No changed settings to apply.");
      return;
    }
    string details = string.Join(Environment.NewLine, changed.Take(20)
        .Select(row => $"{row.Name}: {NvModemParameterCodec.Display(row.Original, row.Type)} → {row.ValueText}"));
    if (changed.Length > 20) {
      details += $"{Environment.NewLine}…and {changed.Length - 20} more";
    }
    if (rtspChanged) {
      details += $"{Environment.NewLine}RTSP path: {device.RtspPath} → {RtspPath}";
    }
    if (!await Dialogs.Confirm("Save NV modem settings",
            $"Write {changed.Length + (rtspChanged ? 1 : 0)} change(s) to "
            + $"{ModemName(device)} {device.Key.SystemId}:{device.Key.ComponentId}?\n\n{details}")) {
      return;
    }
    if (!ReferenceEquals(device, SelectedState) || !CanEdit) {
      SetStatus("The selected modem or its MAVLink connection changed before confirmation.", true);
      return;
    }
    QueueParameterWrites(device, changed);
    if (rtspChanged) {
      _writeQueue.Enqueue(new NvWriteOperation {
        Kind = NvWriteKind.RtspPath,
        Device = device,
        Path = RtspPath,
        TransactionId = ++_nextTransactionId,
      });
    }
    BeginQueuedWrites(device, keyOnly: false, keyChannel: 0);
  }

  [RelayCommand]
  private void CancelChanges() {
    RebuildParameters();
    NvModemDeviceState? device = SelectedState;
    SetRtspPath(device is { RtspPathReady: true } ? device.RtspPath : "");
    SyncKeyText();
    SetStatus("Staged changes discarded.");
  }

  [RelayCommand]
  private void StageRadioPreset(string? preset) {
    NvModemDeviceState? device = SelectedState;
    int channel = SelectedPresetRadio?.Channel ?? 0;
    if (device?.Generation != NvModemGeneration.Nv5 || !CanEdit || channel is < 1 or > 2) {
      SetStatus("Select an online NV5 radio before applying a preset.", true);
      return;
    }
    string prefix = $"CH{channel}_";
    long chip = (long)Math.Round(StagedValue(prefix + "CHIP", -1));
    long modulation = (long)Math.Round(StagedValue(prefix + "MOD", -1));
    if (preset == "factory" && chip < 0) {
      SetStatus("Wait until the selected radio chip type has been read.", true);
      return;
    }
    if (preset is "flrc" or "flrc13" or "flrc26" && chip != 0) {
      SetStatus("FLRC presets are supported only by an LR2021 radio.", true);
      return;
    }
    if (preset is "flrc13" or "flrc26" or "fec_on" or "fec_off" && modulation != 1) {
      SetStatus("This setting is available only in FLRC mode. Select FLRC first.", true);
      return;
    }
    int staged = 0;
    void Set(string suffix, double value, MAVLink.MAV_PARAM_TYPE type = MAVLink.MAV_PARAM_TYPE.UINT32,
        bool create = false) {
      if (StageParameter(prefix + suffix, value, (byte)type, create)) {
        staged++;
      }
    }
    string name;
    switch (preset) {
      case "factory":
        bool lora = chip != 0;
        name = "factory defaults";
        Set("MOD", lora ? 0 : 1); Set("FRAME", lora ? 64 : 240); Set("FHSS", 1);
        Set("FHSS_KHZ", lora ? 2000 : 14000); Set("GUARD_US", 3000);
        Set("DWELL_SH", lora ? 0 : 5); Set("SYNC_PER", 16); Set("SCAN_DW", lora ? 9 : 2);
        Set("ENCRYPT", 1); Set("RADIO_CRC", 1);
        Set("GUARD_MULT", 0, MAVLink.MAV_PARAM_TYPE.REAL32); Set("OPEN_LOOP", 0);
        Set("LINK_MS", 5000); Set("TX_PERIOD", lora ? 100000 : 20000);
        if (lora) {
          Set("LORA_KHZ", 500, MAVLink.MAV_PARAM_TYPE.REAL32, true);
          Set("LORA_SF", 7, create: true); Set("LORA_CR", 5, create: true);
          Set("LORA_SYNC", 0x41, create: true); Set("LORA_PRE", 8, create: true);
        } else {
          Set("FEC", 1, create: true); Set("FEC_K", 3, create: true);
          Set("FEC_N", 4, create: true); Set("FLRC_RATE", 1300000, create: true);
          Set("FLRC_CR", 1, create: true); Set("FLRC_SHAPE", 2, create: true);
          Set("FLRC_PRE", 32, create: true); Set("FLRC_SYNC0", 0x90, create: true);
          Set("FLRC_SYNC1", 0x56, create: true); Set("FLRC_SYNC2", 0x34, create: true);
          Set("FLRC_SYNC3", 0x12, create: true);
        }
        break;
      case "lora": name = "LoRa mode"; Set("MOD", 0); break;
      case "flrc": name = "FLRC mode"; Set("MOD", 1); break;
      case "flrc13": name = "FLRC rate 1.3M"; Set("FLRC_RATE", 1300000, create: true); break;
      case "flrc26": name = "FLRC rate 2.6M"; Set("FLRC_RATE", 2600000, create: true); break;
      case "rx": name = "RX role"; Set("ROLE", 0); break;
      case "tx": name = "TX role"; Set("ROLE", 1); break;
      case "fhss_on": name = "FHSS ON"; Set("FHSS", 1); break;
      case "fhss_off": name = "FHSS OFF"; Set("FHSS", 0); break;
      case "fec_on": name = "FEC ON"; Set("FEC", 1, create: true); break;
      case "fec_off": name = "FEC OFF"; Set("FEC", 0, create: true); break;
      default: return;
    }
    RebuildVisibleParameters();
    RefreshControls();
    SetStatus($"Staged {name} for Radio {channel}: {staged} parameter value(s). "
        + "Nothing was sent — press Save to selected modem.", staged == 0);
  }

  [RelayCommand]
  private void StageRtspPreset(string? preset) {
    if (!CanUseRtsp) {
      SetStatus("Select an online NV5 modem with an LR2021 radio.", true);
      return;
    }
    string parameter = preset is "rtp" or "annexb" ? "RTSP_OUTPUT" : "RTSP_ENABLE";
    if (parameter == "RTSP_OUTPUT" && StagedValue("RTSP_ENABLE", 0) < 0.5) {
      SetStatus("Stage RTSP ON before selecting RTP or raw H264/H265 transport.", true);
      return;
    }
    double value = preset is "on" or "rtp" ? 1 : 0;
    if (!StageParameter(parameter, value, (byte)MAVLink.MAV_PARAM_TYPE.UINT32, false)) {
      SetStatus($"The selected modem did not publish {parameter}.", true);
      return;
    }
    RefreshControls();
    SetStatus($"Staged {preset}. Nothing was sent — press Save to selected modem.");
  }

  [RelayCommand]
  private void GenerateKey() {
    NvModemDeviceState? device = SelectedState;
    int count = device?.Generation == NvModemGeneration.Nv4
        ? NvModemCatalog.Nv4KeyBytes : NvModemCatalog.Nv5KeyBytes;
    const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
    Span<char> generated = stackalloc char[count];
    for (int index = 0; index < generated.Length; index++) {
      generated[index] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
    }
    KeyText = new string(generated);
    if (StageEncryptionKey()) {
      SetStatus("Generated and staged a new encryption key. Nothing was sent.");
    }
  }

  [RelayCommand]
  private void SetKey() {
    NvModemDeviceState? device = SelectedState;
    int channel = SelectedKeyRadio?.Channel ?? 0;
    if (device == null || !CanEdit || channel == 0 || !StageEncryptionKey()) {
      return;
    }
    string[] names = device.Generation == NvModemGeneration.Nv4
        ? [.. Enumerable.Range(1, 8).Select(index => $"ENC_KEY_BYTE{index}")]
        : [.. Enumerable.Range(0, NvModemCatalog.Nv5KeyBytes)
            .Select(index => $"CH{channel}_KEY{index:00}")];
    NvModemParameterRow[] rows = [.. names.Select(name => _parameterRows[name])];
    QueueParameterWrites(device, rows, addLegacyRefresh: true);
    BeginQueuedWrites(device, keyOnly: true, keyChannel: channel);
  }

  [RelayCommand]
  private void CopyRadioSettings() {
    NvModemDeviceState? target = SelectedState;
    int targetChannel = SelectedPresetRadio?.Channel ?? 0;
    if (target?.Generation != NvModemGeneration.Nv5 || SelectedCopySource == null
        || targetChannel is < 1 or > 2
        || SelectedCopySource.DeviceIdentity is not NvModemDeviceState source) {
      SetStatus("Select a target radio and a fully read radio from another NV5 modem.", true);
      return;
    }
    int sourceChannel = SelectedCopySource.Channel;
    string sourcePrefix = $"CH{sourceChannel}_";
    string targetPrefix = $"CH{targetChannel}_";
    long sourceMode = (long)Math.Round(source.Parameters.GetValueOrDefault(sourcePrefix + "MOD", -1));
    long targetChip = (long)Math.Round(StagedValue(targetPrefix + "CHIP", -1));
    if (sourceMode == 1 && targetChip != 0) {
      SetStatus("Cannot copy an FLRC profile to a radio whose chip does not support FLRC.", true);
      return;
    }
    int staged = 0;
    foreach ((string sourceName, double value) in source.Parameters) {
      if (!sourceName.StartsWith(sourcePrefix, StringComparison.Ordinal)
          || NvModemCatalog.IsReadOnly(sourceName)) {
        continue;
      }
      string suffix = sourceName[sourcePrefix.Length..];
      string targetName = targetPrefix + suffix;
      bool create = suffix.StartsWith("LORA_", StringComparison.Ordinal)
          || suffix.StartsWith("FLRC_", StringComparison.Ordinal)
          || suffix is "FEC" or "FEC_K" or "FEC_N" or "FREQ_KHZ";
      if (StageParameter(targetName, value,
              source.ParameterTypes.GetValueOrDefault(sourceName,
                  (byte)MAVLink.MAV_PARAM_TYPE.REAL32), create)) {
        staged++;
      }
    }
    RebuildVisibleParameters();
    SyncKeyText();
    RefreshControls();
    SetStatus($"Staged {staged} channel-local setting(s) from {SelectedCopySource.Label}. "
        + "Network, transport and system IDs were not copied.", staged == 0);
  }

  [RelayCommand]
  private async Task Reboot() {
    NvModemDeviceState? device = SelectedState;
    if (device == null || !CanReboot
        || !await Dialogs.Confirm("Reboot NV5",
            $"Reboot NV5 {device.Key.SystemId}:{device.Key.ComponentId} now? "
            + "The management link will briefly disappear.")) {
      return;
    }
    if (!ReferenceEquals(device, SelectedState) || !CanReboot) {
      SetStatus("The selected modem or its MAVLink connection changed before confirmation.", true);
      return;
    }
    StartSingleWrite(new NvWriteOperation { Kind = NvWriteKind.Reboot, Device = device });
  }

  [RelayCommand]
  private void SetTransmitterEnabled(string? enabledText) {
    NvModemDeviceState? device = SelectedState;
    int channel = SelectedDebugRadio?.Channel ?? 0;
    if (device == null || !CanControlTransmitter) {
      SetStatus("Select an online transmitter or transceiver radio first.", true);
      return;
    }
    StartSingleWrite(new NvWriteOperation {
      Kind = NvWriteKind.TransmitControl,
      Device = device,
      Channel = (byte)channel,
      Enabled = string.Equals(enabledText, "true", StringComparison.OrdinalIgnoreCase),
    });
  }

  internal string ExportParameterFile() {
    NvModemDeviceState? device = SelectedState;
    if (device == null) {
      return "";
    }
    var output = new StringBuilder();
    output.AppendLine(device.Generation == NvModemGeneration.Nv4
        ? "#WARNING: NV4 export contains the readable encryption key words"
        : "#WARNING: NV5 export contains the key-byte values currently visible in Mission Planner");
    if (SupportsRtsp(device) && RtspPath.Length != 0) {
      output.Append("#NV5_RTSP_PATH,").AppendLine(RtspPath);
    }
    foreach (NvModemParameterRow row in _parameterRows.Values.OrderBy(row => row.Name,
                 StringComparer.Ordinal)) {
      output.Append(row.Name).Append(',').AppendLine(row.ValueText);
    }
    return output.ToString()
        .Replace("\r\n", "\n", StringComparison.Ordinal)
        .Replace("\n", "\r\n", StringComparison.Ordinal);
  }

  internal string SuggestedParameterFileName() {
    NvModemDeviceState? device = SelectedState;
    if (device == null) {
      return "nv-modem.param";
    }
    string stem = $"{ModemName(device)}_{HardwareModel(device)}_{RoleSummary(device)}";
    string safe = new(stem.Select(character => char.IsLetterOrDigit(character)
        || character is '.' or '-' or '_' ? character : '_').ToArray());
    while (safe.Contains("__", StringComparison.Ordinal)) {
      safe = safe.Replace("__", "_", StringComparison.Ordinal);
    }
    safe = safe.Trim('.', '-', '_');
    return (safe.Length == 0 ? "nv-modem" : safe) + ".param";
  }

  internal bool ImportParameterFile(string contents) {
    if (SelectedState == null || IsBusy) {
      return false;
    }
    int staged = 0;
    int unknown = 0;
    int invalid = 0;
    foreach (string raw in contents.Split('\n')) {
      string line = raw.Trim();
      if (line.Length == 0) {
        continue;
      }

      if (line.StartsWith("#NV5_RTSP_PATH,", StringComparison.Ordinal)) {
        if (CanUseRtsp) {
          RtspPath = line["#NV5_RTSP_PATH,".Length..].Trim();
          staged++;
        }
        continue;
      }
      if (line.StartsWith('#')) {
        continue;
      }

      string[] fields = line.Contains(',')
          ? line.Split(',', StringSplitOptions.TrimEntries)
          : line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
      if (fields.Length < 2 || !_parameterRows.TryGetValue(fields[0], out var row)) {
        unknown++;
        continue;
      }
      if (row.IsReadOnly) {
        continue;
      }

      string previous = row.ValueText;
      row.ValueText = fields[1];
      if (!row.IsValid) {
        row.ValueText = previous;
        invalid++;
      } else {
        staged++;
      }
    }
    RebuildVisibleParameters();
    RefreshControls();
    SetStatus($"Loaded {staged} setting(s); {unknown} unknown, {invalid} invalid.", invalid != 0);
    return true;
  }

  private void OnPacketReceived(NvModemLink source, MAVLink.MAVLinkMessage packet) {
    if (_disposed) {
      return;
    }

    if (Dispatcher.UIThread.CheckAccess()) {
      HandlePacket(source, packet);
    } else {
      Dispatcher.UIThread.Post(() => HandlePacket(source, packet));
    }
  }

  private void OnLinksChanged() {
    if (_disposed) {
      return;
    }

    if (Dispatcher.UIThread.CheckAccess()) {
      Discover();
    } else {
      Dispatcher.UIThread.Post(Discover);
    }
  }

  internal void HandlePacket(NvModemLink source, MAVLink.MAVLinkMessage packet) {
    if (_disposed) {
      return;
    }

    var key = new NvModemDeviceKey(source, packet.sysid, packet.compid);
    _devices.TryGetValue(key, out NvModemDeviceState? device);
    uint msgid = packet.msgid;
    if (device == null && msgid == (uint)MAVLink.MAVLINK_MSG_ID.HEARTBEAT) {
      var heartbeat = packet.ToStructure<MAVLink.mavlink_heartbeat_t>();
      if (heartbeat.type == (byte)MAVLink.MAV_TYPE.ONBOARD_CONTROLLER
          && heartbeat.autopilot == (byte)MAVLink.MAV_AUTOPILOT.INVALID) {
        ProbeLegacy(source, packet.sysid, packet.compid, force: false);
      }
      return;
    }

    string parameterName = "";
    MAVLink.mavlink_param_value_t parameter = default;
    bool nv5Identity = msgid is NvModemMessageIds.Nv5LinkStatus
        or NvModemMessageIds.Nv5RtspConfig or NvModemMessageIds.Nv5RtspConfigAck
        || msgid == (uint)MAVLink.MAVLINK_MSG_ID.AUTOPILOT_VERSION
            && packet.compid == LegacyNv5ComponentId;
    bool nv4Identity = msgid == NvModemMessageIds.NvRxStat;
    if (msgid == (uint)MAVLink.MAVLINK_MSG_ID.PARAM_VALUE) {
      parameter = packet.ToStructure<MAVLink.mavlink_param_value_t>();
      parameterName = NvModemParameterCodec.Name(parameter.param_id);
      nv5Identity = NvModemCatalog.IsNv5Signature(parameterName);
      nv4Identity = NvModemCatalog.IsNv4Signature(parameterName)
          && (device != null || _legacyProbeTimes.ContainsKey(
              (source, packet.sysid, packet.compid)));
    }
    if (device == null && !nv5Identity && !nv4Identity) {
      return;
    }
    bool inserted;
    if (device == null) {
      inserted = true;
      device = new NvModemDeviceState(key);
      _devices.Add(key, device);
      device.Choice = new NvModemDeviceChoice(device);
      Devices.Add(device.Choice);
    } else {
      inserted = false;
    }
    if (nv4Identity) {
      device.Generation = NvModemGeneration.Nv4;
    } else if (nv5Identity) {
      device.Generation = NvModemGeneration.Nv5;
    }

    device.LastSeenUtc = _utcNow();
    UpdateDeviceLabel(device);
    if (SelectedDevice == null) {
      SelectedDevice = device.Choice;
    } else if (inserted && device.Generation == NvModemGeneration.Nv5) {
      SendIdentityRequest(device.Key.Link, device.Key.SystemId, device.Key.ComponentId);
      SendParameterRequestList(device);
    }

    if (msgid == (uint)MAVLink.MAVLINK_MSG_ID.AUTOPILOT_VERSION
        && device.Generation == NvModemGeneration.Nv5) {
      device.ProductProfile = packet.ToStructure<MAVLink.mavlink_autopilot_version_t>().product_id;
      UpdateDeviceLabel(device);
    } else if (msgid == (uint)MAVLink.MAVLINK_MSG_ID.PARAM_VALUE) {
      HandleParameterValue(device, parameter, parameterName);
    } else if (msgid == NvModemMessageIds.NvRxStat
        && device.Generation == NvModemGeneration.Nv4) {
      device.LegacyRxStatus = packet.ToStructure<NvRxStatMessage>();
      device.LegacyRxReady = true;
    } else if (msgid == (uint)MAVLink.MAVLINK_MSG_ID.RADIO_STATUS
        && device.Generation == NvModemGeneration.Nv4) {
      device.LegacyRadioStatus = packet.ToStructure<MAVLink.mavlink_radio_status_t>();
      device.LegacyRadioReady = true;
    } else if (msgid == NvModemMessageIds.Nv5LinkStatus) {
      Nv5LinkStatusMessage status = packet.ToStructure<Nv5LinkStatusMessage>();
      device.Links[status.Channel] = status;
      UpdateDeviceLabel(device);
    } else if (msgid == NvModemMessageIds.Nv5RtspConfig) {
      Nv5RtspConfigMessage value = packet.ToStructure<Nv5RtspConfigMessage>();
      if (value.Operation == 2) {
        bool editorDirty = ReferenceEquals(device, SelectedState) && RtspPending(device);
        device.RtspPath = Latin1String(value.Path);
        device.RtspPathReady = true;
        if (ReferenceEquals(device, SelectedState) && !editorDirty) {
          SetRtspPath(device.RtspPath);
        }
      }
    } else if (msgid == NvModemMessageIds.Nv5RtspConfigAck) {
      HandleRtspAck(device, packet.ToStructure<Nv5RtspConfigAckMessage>());
    } else if (msgid == (uint)MAVLink.MAVLINK_MSG_ID.COMMAND_ACK) {
      HandleCommandAck(device, packet.ToStructure<MAVLink.mavlink_command_ack_t>());
    }

    if (ReferenceEquals(device, SelectedState)) {
      UpdateSelectedState();
    }
    RebuildCopySources();
  }

  private void HandleParameterValue(
      NvModemDeviceState device, MAVLink.mavlink_param_value_t message, string name) {
    double decoded = NvModemParameterCodec.Decode(message.param_value, message.param_type);
    if (device.ParameterRefreshPending) {
      device.Parameters.Clear();
      device.ParameterTypes.Clear();
      device.LegacyKeyWords.Clear();
      device.LegacyKeyFingerprint = "";
      device.ExpectedParameterCount = 0;
      device.ParameterRefreshPending = false;
      if (ReferenceEquals(device, SelectedState)) {
        ClearParameterRows();
      }
    }
    bool newParameter = !device.Parameters.ContainsKey(name);
    double stored = decoded;
    if (device.Generation == NvModemGeneration.Nv5 && NvModemCatalog.IsNv5KeyByte(name)) {
      if (NvModemParameterCodec.NearlyEqual(decoded, -1)
          && device.LocallyKnownKeyBytes.TryGetValue(name, out double known)) {
        stored = known;
      } else if (decoded is >= 0 and <= 255) {
        device.LocallyKnownKeyBytes[name] = decoded;
      }
    }
    int legacyWord = NvModemCatalog.Nv4KeyWordIndex(name);
    if (device.Generation == NvModemGeneration.Nv4 && legacyWord >= 0) {
      uint raw = unchecked((uint)BitConverter.SingleToInt32Bits(message.param_value));
      device.LegacyKeyWords[legacyWord] = raw;
      if (device.LegacyKeyWords.Count == 8) {
        byte[] bytes = new byte[NvModemCatalog.Nv4KeyBytes];
        for (int index = 0; index < 8; index++) {
          BitConverter.GetBytes(device.LegacyKeyWords[index]).CopyTo(bytes, index * 4);
        }
        device.LegacyKeyFingerprint = Convert.ToHexString(SHA256.HashData(bytes))[..12].ToLowerInvariant();
      }
    }
    device.Parameters[name] = stored;
    device.ParameterTypes[name] = message.param_type;
    device.ExpectedParameterCount = message.param_count;
    device.ParameterListLastProgressUtc = _utcNow();
    if (device.ParameterListInProgress && message.param_count > 0
        && device.Parameters.Count >= message.param_count) {
      device.ParameterListInProgress = false;
      if (ReferenceEquals(device, SelectedState) && !IsBusy) {
        SetStatus($"Read {device.Parameters.Count} parameters from {ModemName(device)} "
            + $"{device.Key.SystemId}:{device.Key.ComponentId}.");
      }
    }
    if (name == "MODEM_PROFILE") {
      device.Generation = NvModemGeneration.Nv5;
      device.ProductProfile = (uint)Math.Max(0, decoded);
    } else if (name == "HW_VERSION") {
      device.Generation = NvModemGeneration.Nv4;
    }
    UpdateDeviceLabel(device);

    if (_currentWrite is { Kind: NvWriteKind.Parameter } write
        && ReferenceEquals(write.Device, device) && write.Name == name) {
      bool protectedAck = device.Generation == NvModemGeneration.Nv5
          && NvModemCatalog.IsNv5KeyByte(name)
          && NvModemParameterCodec.NearlyEqual(decoded, -1);
      double visible = protectedAck ? write.Value : decoded;
      bool accepted = protectedAck || (NvModemParameterCodec.IsInteger(write.ParameterType)
          ? decoded == write.Value : NvModemParameterCodec.NearlyEqual(decoded, write.Value));
      if (!accepted) {
        FailWrites($"Modem rejected {name}: reported {decoded:G9} instead of {write.Value:G9}.");
        return;
      }
      device.Parameters[name] = visible;
      if (device.Generation == NvModemGeneration.Nv5 && NvModemCatalog.IsNv5KeyByte(name)) {
        device.LocallyKnownKeyBytes[name] = visible;
      }
      if (_parameterRows.TryGetValue(name, out var acceptedRow)) {
        acceptedRow.Accept(visible);
      }
      if (NvModemCatalog.RequiresManualReboot(device.Generation, name)) {
        double saveMs = device.Parameters.GetValueOrDefault("MAV_SAVE_MS", 2000);
        device.RebootReadyUtc = _utcNow().AddMilliseconds(Math.Max(100, saveMs) + 250);
      }
      CompleteCurrentWrite();
    } else if (ReferenceEquals(device, SelectedState)) {
      if (_parameterRows.TryGetValue(name, out var row)) {
        if (!row.IsChanged) {
          row.Accept(stored);
        }
      } else if (newParameter) {
        AddParameterRow(name, stored, message.param_type);
      }
    }
  }

  private void HandleRtspAck(NvModemDeviceState device, Nv5RtspConfigAckMessage ack) {
    if (_currentWrite is not { Kind: NvWriteKind.RtspPath } write
        || !ReferenceEquals(write.Device, device) || write.TransactionId != ack.TransactionId) {
      return;
    }
    if (ack.Result != 0) {
      FailWrites($"RTSP path rejected: result {ack.Result}, detail {ack.Detail}.");
      return;
    }
    device.RtspPath = write.Path;
    device.RtspPathReady = true;
    if (ReferenceEquals(device, SelectedState)) {
      SetRtspPath(write.Path);
    }
    CompleteCurrentWrite();
  }

  private void HandleCommandAck(NvModemDeviceState device, MAVLink.mavlink_command_ack_t ack) {
    if (_currentWrite is not { } write || !ReferenceEquals(write.Device, device)) {
      return;
    }

    bool relevant = write.Kind == NvWriteKind.Reboot
        && ack.command == (ushort)MAVLink.MAV_CMD.PREFLIGHT_REBOOT_SHUTDOWN
        || write.Kind == NvWriteKind.TransmitControl && ack.command == SetTransmitEnabledCommand;
    if (!relevant) {
      return;
    }

    IsBusy = false;
    _currentWrite = null;
    if (ack.result != (byte)MAVLink.MAV_RESULT.ACCEPTED) {
      SetStatus($"Modem rejected the command (MAV_RESULT {ack.result}).", true);
    } else if (write.Kind == NvWriteKind.Reboot) {
      device.LastSeenUtc = _utcNow() - OnlineTimeout - TimeSpan.FromMilliseconds(1);
      SetStatus($"Reboot accepted by NV5 {device.Key.SystemId}:{device.Key.ComponentId}; "
          + "waiting for it to return…");
      _ = DiscoverAfterRebootAsync();
    } else {
      SetStatus($"Radio {write.Channel} TX {(write.Enabled ? "enabled" : "suppressed")} on "
          + $"NV5 {device.Key.SystemId}:{device.Key.ComponentId}.");
    }
    RefreshControls();
  }

  private void ShowSelectedDevice(bool clearDeviceParameters) {
    ClearParameterRows();
    SetRtspPath("");
    ResetRadioRows();
    NvModemDeviceState? device = SelectedState;
    if (device == null) {
      UpdateSelectedState();
      return;
    }
    if (clearDeviceParameters) {
      ClearDeviceParameters(device);
      SendParameterRequestList(device);
      if (device.Generation == NvModemGeneration.Nv5) {
        SendIdentityRequest(device.Key.Link, device.Key.SystemId, device.Key.ComponentId);
      }
    } else {
      RebuildParameters();
    }
    if (SupportsRtsp(device)) {
      SendRtspRequest(device);
    }

    UpdateSelectedState();
  }

  private void ClearDeviceParameters(NvModemDeviceState device) {
    device.Parameters.Clear();
    device.ParameterTypes.Clear();
    device.LegacyKeyWords.Clear();
    device.LegacyKeyFingerprint = "";
    device.ExpectedParameterCount = 0;
    device.ParameterRefreshPending = false;
    device.ParameterListInProgress = false;
    if (ReferenceEquals(device, SelectedState)) {
      ClearParameterRows();
      ParameterProgress = "Parameters: 0 / 0";
      KeyText = "";
    }
  }

  private void ClearParameterRows() {
    foreach (NvModemParameterRow row in _parameterRows.Values) {
      row.PropertyChanged -= OnParameterRowChanged;
    }
    _parameterRows.Clear();
    Parameters.Clear();
    RebuildRadioChoices();
    RefreshControls();
  }

  private void RebuildParameters() {
    ClearParameterRows();
    NvModemDeviceState? device = SelectedState;
    if (device == null) {
      return;
    }

    foreach ((string name, double value) in device.Parameters.OrderBy(item => item.Key,
                 StringComparer.Ordinal)) {
      AddParameterRow(name, value,
          device.ParameterTypes.GetValueOrDefault(name, (byte)MAVLink.MAV_PARAM_TYPE.REAL32));
    }
    RebuildVisibleParameters();
    SyncKeyText();
  }

  private void AddParameterRow(string name, double value, byte type) {
    if (_parameterRows.ContainsKey(name)) {
      return;
    }

    var row = new NvModemParameterRow(name, value, type);
    row.PropertyChanged += OnParameterRowChanged;
    _parameterRows.Add(name, row);
    RebuildVisibleParameters();
    RebuildRadioChoices();
  }

  private void OnParameterRowChanged(object? sender, PropertyChangedEventArgs e) {
    if (e.PropertyName is nameof(NvModemParameterRow.IsChanged)
        or nameof(NvModemParameterRow.IsValid)) {
      if (sender is NvModemParameterRow row && row.Name.EndsWith("_MOD", StringComparison.Ordinal)) {
        RebuildVisibleParameters();
      }
      RefreshControls();
    }
  }

  private void RebuildVisibleParameters() {
    var staged = _parameterRows.Values
        .Where(row => row.TryValue(out _))
        .ToDictionary(row => row.Name, row => { row.TryValue(out double value); return value; },
            StringComparer.Ordinal);
    NvModemParameterRow[] wanted = [.. _parameterRows.Values
        .Where(row => NvModemCatalog.Applicable(row.Name, staged))
        .OrderBy(row => row.Group, StringComparer.Ordinal)
        .ThenBy(row => row.Name, StringComparer.Ordinal)];
    if (Parameters.SequenceEqual(wanted)) {
      return;
    }

    Parameters.Clear();
    foreach (var row in wanted) {
      Parameters.Add(row);
    }

    OnPropertyChanged(nameof(HasParameters));
  }

  private bool StageParameter(string name, double value, byte type, bool createIfMissing) {
    NvModemDeviceState? device = SelectedState;
    if (device == null) {
      return false;
    }

    if (!_parameterRows.TryGetValue(name, out var row)) {
      if (!createIfMissing) {
        return false;
      }

      row = new NvModemParameterRow(name, double.NaN, type);
      row.PropertyChanged += OnParameterRowChanged;
      _parameterRows.Add(name, row);
      device.ParameterTypes[name] = type;
    }
    string next = NvModemParameterCodec.Display(value, row.Type);
    row.ValueText = next;
    return row.IsValid;
  }

  private double StagedValue(string name, double fallback) =>
      _parameterRows.TryGetValue(name, out var row) && row.TryValue(out double value)
          ? value : fallback;

  private bool IsApplicable(string name) {
    var staged = _parameterRows.Values.Where(row => row.TryValue(out _))
        .ToDictionary(row => row.Name, row => { row.TryValue(out double value); return value; },
            StringComparer.Ordinal);
    return NvModemCatalog.Applicable(name, staged);
  }

  private bool StageEncryptionKey() {
    NvModemDeviceState? device = SelectedState;
    int channel = SelectedKeyRadio?.Channel ?? 0;
    if (device == null || channel == 0 || IsBusy) {
      SetStatus("Read parameters and select a radio before setting its key.", true);
      return false;
    }
    int expected = device.Generation == NvModemGeneration.Nv4
        ? NvModemCatalog.Nv4KeyBytes : NvModemCatalog.Nv5KeyBytes;
    byte[] bytes;
    try {
      bytes = Encoding.Latin1.GetBytes(KeyText.Normalize(NormalizationForm.FormC));
    } catch {
      bytes = [];
    }
    if (bytes.Length != expected || KeyText.Any(character => character is < ' ' or > '~')) {
      SetStatus($"Enter exactly {expected} printable ASCII characters.", true);
      return false;
    }
    if (device.Generation == NvModemGeneration.Nv4) {
      for (int word = 0; word < 8; word++) {
        string name = $"ENC_KEY_BYTE{word + 1}";
        if (!_parameterRows.ContainsKey(name)) {
          SetStatus("The selected NV4 did not publish all eight encryption-key words.", true);
          return false;
        }
        int signed = BitConverter.ToInt32(bytes, word * 4);
        if (!StageParameter(name, signed,
                device.ParameterTypes.GetValueOrDefault(name,
                    (byte)MAVLink.MAV_PARAM_TYPE.INT32), false)) {
          return false;
        }
      }
    } else {
      for (int index = 0; index < NvModemCatalog.Nv5KeyBytes; index++) {
        string name = $"CH{channel}_KEY{index:00}";
        if (!_parameterRows.ContainsKey(name)
            || !StageParameter(name, bytes[index],
                device.ParameterTypes.GetValueOrDefault(name,
                    (byte)MAVLink.MAV_PARAM_TYPE.INT32), false)) {
          SetStatus("The selected NV5 did not publish the complete key-byte set.", true);
          return false;
        }
      }
    }
    RebuildVisibleParameters();
    RefreshControls();
    return true;
  }

  private void SyncKeyText() {
    NvModemDeviceState? device = SelectedState;
    int channel = SelectedKeyRadio?.Channel ?? 0;
    if (device == null || channel == 0) {
      KeyText = "";
      KeyFingerprint = "Stored fingerprint: unavailable";
      return;
    }
    var bytes = new List<byte>();
    if (device.Generation == NvModemGeneration.Nv4) {
      for (int word = 1; word <= 8; word++) {
        if (!_parameterRows.TryGetValue($"ENC_KEY_BYTE{word}", out var row)
            || !row.TryValue(out double numeric)) {
          bytes.Clear(); break;
        }
        bytes.AddRange(BitConverter.GetBytes(unchecked((int)Math.Round(numeric))));
      }
      KeyHint = "NV4: exactly 32 printable characters mapped to eight signed 32-bit words.";
      KeyFingerprint = device.LegacyKeyFingerprint.Length == 0
          ? "Stored fingerprint: unavailable"
          : "Stored fingerprint: " + device.LegacyKeyFingerprint;
    } else {
      for (int index = 0; index < NvModemCatalog.Nv5KeyBytes; index++) {
        if (!_parameterRows.TryGetValue($"CH{channel}_KEY{index:00}", out var row)
            || !row.TryValue(out double numeric) || numeric is < 0 or > 255) {
          bytes.Clear(); break;
        }
        bytes.Add((byte)numeric);
      }
      KeyHint = "NV5: exactly 16 printable characters mapped to KEY00 through KEY15.";
      string hash = $"CH{channel}_KEY_HASH";
      KeyFingerprint = _parameterRows.TryGetValue(hash, out var hashRow)
          ? "Stored fingerprint: " + hashRow.ValueText
          : "Stored fingerprint: unavailable";
    }
    KeyText = bytes.Count == (device.Generation == NvModemGeneration.Nv4 ? 32 : 16)
        ? Encoding.Latin1.GetString(bytes.ToArray()) : "";
  }

  private void RebuildRadioChoices() {
    NvModemDeviceState? device = SelectedState;
    int oldPreset = SelectedPresetRadio?.Channel ?? 0;
    int oldKey = SelectedKeyRadio?.Channel ?? 0;
    PresetRadios.Clear();
    KeyRadios.Clear();
    if (device?.Generation == NvModemGeneration.Nv4) {
      if (Enumerable.Range(1, 8).All(index => _parameterRows.ContainsKey($"ENC_KEY_BYTE{index}"))) {
        KeyRadios.Add(new NvRadioChoice(1, "RFM radio"));
      }
    } else if (device?.Generation == NvModemGeneration.Nv5) {
      for (int channel = 1; channel <= 2; channel++) {
        if (_parameterRows.ContainsKey($"CH{channel}_MOD")) {
          PresetRadios.Add(new NvRadioChoice(channel, $"Radio {channel}"));
        }
        if (Enumerable.Range(0, NvModemCatalog.Nv5KeyBytes)
            .All(index => _parameterRows.ContainsKey($"CH{channel}_KEY{index:00}"))) {
          KeyRadios.Add(new NvRadioChoice(channel, $"Radio {channel}"));
        }
      }
    }
    SelectedPresetRadio = PresetRadios.FirstOrDefault(item => item.Channel == oldPreset)
        ?? PresetRadios.FirstOrDefault();
    SelectedKeyRadio = KeyRadios.FirstOrDefault(item => item.Channel == oldKey)
        ?? KeyRadios.FirstOrDefault();
  }

  private void RebuildCopySources() {
    object? previous = SelectedCopySource?.DeviceIdentity;
    int previousChannel = SelectedCopySource?.Channel ?? 0;
    CopySources.Clear();
    foreach (NvModemDeviceState source in _devices.Values) {
      if (ReferenceEquals(source, SelectedState) || source.Generation != NvModemGeneration.Nv5
          || source.ParameterListInProgress || source.ExpectedParameterCount <= 0
          || source.Parameters.Count < source.ExpectedParameterCount) {
        continue;
      }

      for (int channel = 1; channel <= 2; channel++) {
        if (!source.Parameters.ContainsKey($"CH{channel}_MOD")) {
          continue;
        }

        CopySources.Add(new NvRadioCopyChoice(source, channel,
            $"{ModemName(source)} {source.Key.SystemId}:{source.Key.ComponentId} · "
            + $"{HardwareModel(source)} · Radio {channel}"));
      }
    }
    SelectedCopySource = CopySources.FirstOrDefault(item =>
        ReferenceEquals(item.DeviceIdentity, previous) && item.Channel == previousChannel)
        ?? CopySources.FirstOrDefault();
  }

  private void QueueParameterWrites(
      NvModemDeviceState device, IEnumerable<NvModemParameterRow> rows,
      bool addLegacyRefresh = true) {
    bool any = false;
    foreach (NvModemParameterRow row in rows) {
      if (!row.TryValue(out double value)) {
        continue;
      }

      _writeQueue.Enqueue(new NvWriteOperation {
        Kind = NvWriteKind.Parameter,
        Device = device,
        Name = row.Name,
        Value = value,
        ParameterType = row.Type,
      });
      any = true;
    }
    if (any && addLegacyRefresh && device.Generation == NvModemGeneration.Nv4
        && device.Parameters.ContainsKey("REFRESH_SETTING")) {
      _writeQueue.Enqueue(new NvWriteOperation {
        Kind = NvWriteKind.Parameter,
        Device = device,
        Name = "REFRESH_SETTING",
        Value = 1,
        ParameterType = device.ParameterTypes.GetValueOrDefault("REFRESH_SETTING",
            (byte)MAVLink.MAV_PARAM_TYPE.UINT32),
      });
    }
  }

  private void BeginQueuedWrites(NvModemDeviceState device, bool keyOnly, int keyChannel) {
    if (_writeQueue.Count == 0) {
      return;
    }

    _parameterWriteInTransaction = _writeQueue.Any(item => item.Kind == NvWriteKind.Parameter);
    _rebootRequiredInTransaction = _writeQueue.Any(item => item.Kind == NvWriteKind.Parameter
        && NvModemCatalog.RequiresManualReboot(device.Generation, item.Name));
    _keyWriteInTransaction = keyOnly;
    _keyWriteChannel = keyChannel;
    IsBusy = true;
    SetStatus($"Applying {_writeQueue.Count} change(s) to {ModemName(device)} "
        + $"{device.Key.SystemId}:{device.Key.ComponentId}…");
    SendNextWrite();
  }

  private void StartSingleWrite(NvWriteOperation operation) {
    if (IsBusy) {
      return;
    }

    IsBusy = true;
    _currentWrite = operation;
    SendOperation(operation);
    RefreshControls();
  }

  private void SendNextWrite() {
    if (_currentWrite != null) {
      return;
    }

    if (_writeQueue.Count == 0) {
      IsBusy = false;
      NvModemDeviceState? device = SelectedState;
      if (_keyWriteInTransaction) {
        SyncKeyText();
        SetStatus(device?.Generation == NvModemGeneration.Nv4
            ? "NV4 RFM key accepted, stored and applied. Set the same key text on its peer."
            : $"Radio {_keyWriteChannel} key accepted. Set the same key on its peer; "
                + "reboot after the flash commit.");
      } else if (_rebootRequiredInTransaction) {
        SetStatus("Parameters accepted. Wait for the flash commit, then reboot the modem.");
      } else if (_parameterWriteInTransaction) {
        SetStatus(device?.Generation == NvModemGeneration.Nv4
            ? "NV4 parameters accepted, stored and applied."
            : "Parameters accepted and will take effect after the flash commit.");
      } else {
        SetStatus("RTSP configuration applied successfully.");
      }
      _parameterWriteInTransaction = false;
      _rebootRequiredInTransaction = false;
      _keyWriteInTransaction = false;
      _keyWriteChannel = 0;
      RefreshControls();
      return;
    }
    _currentWrite = _writeQueue.Dequeue();
    SendOperation(_currentWrite);
  }

  private void CompleteCurrentWrite() {
    _currentWrite = null;
    SendNextWrite();
  }

  private void SendOperation(NvWriteOperation operation) {
    object packet = operation.Kind switch {
      NvWriteKind.Parameter => new MAVLink.mavlink_param_set_t {
        target_system = operation.Device.Key.SystemId,
        target_component = operation.Device.Key.ComponentId,
        param_id = NvModemParameterCodec.NameBytes(operation.Name),
        param_value = NvModemParameterCodec.Encode(operation.Value, operation.ParameterType),
        param_type = operation.ParameterType,
      },
      NvWriteKind.RtspPath => new Nv5RtspConfigMessage {
        TransactionId = operation.TransactionId,
        TargetSystem = operation.Device.Key.SystemId,
        TargetComponent = operation.Device.Key.ComponentId,
        Operation = 1,
        Path = FixedLatin1(operation.Path, 96, 95),
      },
      NvWriteKind.Reboot => CommandLong(operation.Device,
          MAVLink.MAV_CMD.PREFLIGHT_REBOOT_SHUTDOWN, 1, 0),
      NvWriteKind.TransmitControl => CommandLong(operation.Device,
          (MAVLink.MAV_CMD)SetTransmitEnabledCommand, operation.Channel,
          operation.Enabled ? 1 : 0),
      _ => throw new ArgumentOutOfRangeException(),
    };
    operation.SentUtc = _utcNow();
    if (!_transport.Send(operation.Device.Key.Link, packet,
            operation.Device.Key.SystemId, operation.Device.Key.ComponentId)) {
      FailWrites("The selected MAVLink connection is no longer open.");
    }
  }

  private void FailWrites(string message) {
    _writeQueue.Clear();
    _currentWrite = null;
    IsBusy = false;
    _parameterWriteInTransaction = false;
    _rebootRequiredInTransaction = false;
    _keyWriteInTransaction = false;
    SetStatus(message, true);
    RefreshControls();
  }

  private void OnServiceTick(object? sender, EventArgs e) => ServiceTransactions();

  internal void ServiceTransactions() {
    DateTime now = _utcNow();
    foreach (NvModemDeviceState device in _devices.Values) {
      UpdateDeviceLabel(device);
      if (!device.ParameterListInProgress) {
        continue;
      }

      TimeSpan silent = now - device.ParameterListLastProgressUtc;
      if (silent >= ParameterListRetry && device.ParameterListRetries < MaximumParameterListRetries) {
        SendParameterRequestList(device, retry: true);
        if (ReferenceEquals(device, SelectedState) && !IsBusy) {
          SetStatus($"No new parameter response; retrying request {device.ParameterListRetries} "
              + $"/ {MaximumParameterListRetries}…");
        }
      } else if (silent >= ParameterListTimeout) {
        device.ParameterListInProgress = false;
        if (ReferenceEquals(device, SelectedState) && !IsBusy) {
          SetStatus($"Parameter read stopped at {device.Parameters.Count} / "
              + $"{device.ExpectedParameterCount}. Press Refresh selected to retry.", true);
        }
      }
    }
    if (_currentWrite != null && now - _currentWrite.SentUtc >= TransactionTimeout) {
      _currentWrite.Retries++;
      if (_currentWrite.Retries >= MaximumWriteAttempts) {
        FailWrites("Modem did not acknowledge the configuration command.");
      } else {
        SendOperation(_currentWrite);
      }
    }
    UpdateSelectedState();
  }

  private void SendParameterRequestList(NvModemDeviceState device, bool retry = false) {
    device.ParameterRefreshPending = true;
    device.ParameterListInProgress = true;
    device.ParameterListLastProgressUtc = _utcNow();
    device.ParameterListRetries = retry ? device.ParameterListRetries + 1 : 0;
    var request = new MAVLink.mavlink_param_request_list_t {
      target_system = device.Key.SystemId,
      target_component = device.Key.ComponentId,
    };
    _transport.Send(device.Key.Link, request, device.Key.SystemId, device.Key.ComponentId);
  }

  private void ProbeLegacy(NvModemLink link, byte systemId, byte componentId, bool force) {
    var key = (link, systemId, componentId);
    DateTime now = _utcNow();
    if (!force && _legacyProbeTimes.TryGetValue(key, out DateTime last)
        && now - last < OnlineTimeout) {
      return;
    }

    _legacyProbeTimes[key] = now;
    var request = new MAVLink.mavlink_param_request_list_t {
      target_system = systemId,
      target_component = componentId,
    };
    _transport.Send(link, request, systemId, componentId);
  }

  private void SendIdentityRequest(NvModemLink link, byte systemId, byte componentId) {
    var command = new MAVLink.mavlink_command_long_t {
      target_system = systemId,
      target_component = componentId,
      command = (ushort)MAVLink.MAV_CMD.REQUEST_MESSAGE,
      confirmation = 0,
      param1 = (float)MAVLink.MAVLINK_MSG_ID.AUTOPILOT_VERSION,
    };
    _transport.Send(link, command, systemId, componentId);
  }

  private void SendRtspRequest(NvModemDeviceState device) {
    var request = new Nv5RtspConfigMessage {
      TransactionId = ++_nextTransactionId,
      TargetSystem = device.Key.SystemId,
      TargetComponent = device.Key.ComponentId,
      Operation = 0,
      Path = new byte[96],
    };
    _transport.Send(device.Key.Link, request, device.Key.SystemId, device.Key.ComponentId);
  }

  private static MAVLink.mavlink_command_long_t CommandLong(
      NvModemDeviceState device, MAVLink.MAV_CMD command, float param1, float param2) => new() {
        target_system = device.Key.SystemId,
        target_component = device.Key.ComponentId,
        command = (ushort)command,
        confirmation = 0,
        param1 = param1,
        param2 = param2,
      };

  private void UpdateSelectedState() {
    NvModemDeviceState? device = SelectedState;
    if (device == null) {
      ConnectionText = "No modem discovered";
      IdentityText = "Open a MAVLink connection, then press Discover.";
      ParameterProgress = "Parameters: 0 / 0";
      ResetRadioRows();
      RefreshControls();
      return;
    }
    TimeSpan age = _utcNow() - device.LastSeenUtc;
    ConnectionText = age <= OnlineTimeout ? "Online" : $"Offline ({Math.Max(0, age.TotalSeconds):F0} s ago)";
    IdentityText = $"System {device.Key.SystemId}, component {device.Key.ComponentId}, "
        + $"hardware {HardwareModel(device)}, link {device.Key.Link.Name}";
    ParameterProgress = $"Parameters: {device.Parameters.Count} / {device.ExpectedParameterCount}";
    UpdateRadioRows(device);
    RefreshControls();
  }

  private void UpdateRadioRows(NvModemDeviceState device) {
    ResetRadioRows();
    if (device.Generation == NvModemGeneration.Nv4) {
      bool transmitter = device.Parameters.GetValueOrDefault("TX_ON") >= 0.5;
      string frequency = device.LegacyRxReady
          ? $"{device.LegacyRxStatus.Frequency / 1e6:F3} MHz" : "—";
      string rssi = transmitter ? "n/a" : device.LegacyRxReady
          ? $"{device.LegacyRxStatus.Snr:F1} dBm"
          : device.LegacyRadioReady ? device.LegacyRadioStatus.rssi.ToString(CultureInfo.InvariantCulture) : "—";
      string quality = transmitter ? "n/a" : device.LegacyRxReady
          ? $"{device.LegacyRxStatus.Quality:F0}%" : "—";
      var row = RadioStatuses[0];
      row.Identity = $"RFM/SX1278 · {(transmitter ? "TX" : "RX")}";
      row.Mode = $"LoRa · {frequency}";
      row.Link = $"RSSI {rssi} · Q {quality}";
      row.Traffic = device.LegacyRxReady
          ? $"RX {device.LegacyRxStatus.BytesReceived} B/s · {device.LegacyRxStatus.MavParsed} msg/s"
          : "—";
      row.Details = $"Role: {(transmitter ? "Transmitter" : "Receiver")}\nFrequency: {frequency}\n"
          + $"RSSI: {rssi}\nQuality: {quality}";
      return;
    }
    foreach ((int channel, Nv5LinkStatusMessage status) in device.Links) {
      if (channel is < 1 or > 2) {
        continue;
      }

      var row = RadioStatuses[channel - 1];
      bool receives = status.Role != 1;
      bool rssiValid = receives && (status.Flags & (1 << 6)) != 0
          && status.PacketRssiDbmX10 != short.MinValue;
      bool snrValid = receives && (status.Flags & (1 << 7)) != 0
          && status.PacketSnrDbX10 != short.MinValue;
      bool locked = receives && (status.Flags & (1 << 2)) != 0;
      string rssi = receives ? rssiValid ? $"{status.PacketRssiDbmX10 / 10.0:F1}" : "—" : "n/a";
      string snr = receives ? snrValid ? $"{status.PacketSnrDbX10 / 10.0:F1}" : "—" : "n/a";
      string quality = receives ? $"{status.LinkQuality}%" : "n/a";
      double sample = Math.Max(1, status.SampleMs);
      double txKbps = status.TxRadioBytes * 8 / sample;
      double rxKbps = status.RxRadioBytes * 8 / sample;
      string txState = status.Role == 0 ? "—" : status.TxState switch {
        1 => "On",
        2 => "Off",
        _ => "?",
      };
      row.Identity = $"{NvModemCatalog.ChipName(status.RadioChip)} · "
          + $"{NvModemCatalog.RoleName(status.Role)} · {txState}";
      row.Mode = $"{NvModemCatalog.ModulationName(status.Modulation)} · {status.FrequencyHz / 1e6:F3} MHz";
      row.Link = $"L {(locked ? "yes" : receives ? "no" : "n/a")} · R {rssi} · S {snr} · Q {quality}";
      row.Traffic = $"RF {txKbps:F1}/{rxKbps:F1} kbit/s · E {status.Errors}/{status.DroppedBytes}";
      row.Details = $"Chip: {NvModemCatalog.ChipName(status.RadioChip)}\n"
          + $"Role: {NvModemCatalog.RoleName(status.Role)}\n"
          + $"Mode: {NvModemCatalog.ModulationName(status.Modulation)}\n"
          + $"Frequency: {status.FrequencyHz / 1e6:F3} MHz\nRSSI: {rssi} dBm\n"
          + $"SNR: {snr} dB\nQuality: {quality}\nQueues: {status.TxQueueBytes}/{status.RxQueueBytes} B\n"
          + $"FEC recovered: {status.FecRecovered}; hop missed: {status.HopMissed}; sync lost: {status.SyncLost}";
    }
  }

  private void ResetRadioRows() {
    foreach (NvRadioStatusRow row in RadioStatuses) {
      row.Identity = "—"; row.Mode = "—"; row.Link = "—"; row.Traffic = "—";
      row.Details = "No live status received.";
    }
  }

  private void UpdateDeviceLabel(NvModemDeviceState device) {
    if (device.Choice == null) {
      return;
    }

    string label = $"{ModemName(device)} {device.Key.SystemId}:{device.Key.ComponentId}  "
        + HardwareModel(device);
    string roles = RoleSummary(device);
    if (roles.Length != 0) {
      label += " · " + roles;
    }

    if (!IsOnline(device)) {
      label += " [offline]";
    }

    device.Choice.Label = $"{label} — {device.Key.Link.Name}";
  }

  private string HardwareModel(NvModemDeviceState device) => NvModemCatalog.HardwareModel(
      device.Generation, device.ProductProfile, device.Links.Values.Select(link => link.RadioChip));

  private static string ModemName(NvModemDeviceState device) => device.Generation switch {
    NvModemGeneration.Nv4 => "NV4",
    NvModemGeneration.Nv5 => "NV5",
    _ => "NV modem",
  };

  private static string RoleSummary(NvModemDeviceState device) {
    if (device.Generation == NvModemGeneration.Nv4
        && device.Parameters.TryGetValue("TX_ON", out double tx)) {
      return tx >= 0.5 ? "TX" : "RX";
    }

    if (device.Generation != NvModemGeneration.Nv5) {
      return "";
    }

    var roles = new List<string>();
    for (int channel = 1; channel <= 2; channel++) {
      if (device.Links.TryGetValue(channel, out var status)) {
        roles.Add(NvModemCatalog.RoleName(status.Role));
      } else if (device.Parameters.TryGetValue($"CH{channel}_ROLE", out double role)
                 && Math.Round(role) is >= 0 and <= 2) {
        roles.Add(NvModemCatalog.RoleName((byte)Math.Round(role)));
      }
    }
    return string.Join('/', roles);
  }

  private bool IsOnline(NvModemDeviceState device) => _utcNow() - device.LastSeenUtc <= OnlineTimeout;

  private static bool SupportsRtsp(NvModemDeviceState device) =>
      device.Generation == NvModemGeneration.Nv5
      && (Enumerable.Range(1, 2).Any(channel =>
              device.Parameters.GetValueOrDefault($"CH{channel}_CHIP", -1) == 0)
          || device.Links.Values.Any(link => link.RadioChip == 0));

  private static int WritePriority(string name) => name.EndsWith("_MOD", StringComparison.Ordinal)
      ? 0 : name == "RTSP_ENABLE" ? 2 : 1;

  private void SetRtspPath(string path) {
    _settingRtsp = true;
    RtspPath = path;
    _settingRtsp = false;
    _rtspEdited = false;
  }

  private bool RtspPending(NvModemDeviceState device) => SupportsRtsp(device)
      && (device.RtspPathReady ? RtspPath != device.RtspPath : _rtspEdited);

  private bool HasPendingChangesFor(NvModemDeviceState device) =>
      _parameterRows.Values.Any(row => row.IsChanged) || RtspPending(device);

  private async Task DiscoverAfterRebootAsync() {
    await Task.Delay(1500);
    if (_disposed) {
      return;
    }
    if (Dispatcher.UIThread.CheckAccess()) {
      Discover();
    } else {
      Dispatcher.UIThread.Post(Discover);
    }
  }

  private void SetStatus(string text, bool error = false) {
    Status = error ? "Error: " + text : text;
    IsError = error;
  }

  private void RefreshControls() {
    OnPropertyChanged(nameof(HasSelectedDevice));
    OnPropertyChanged(nameof(HasParameters));
    OnPropertyChanged(nameof(HasPendingChanges));
    OnPropertyChanged(nameof(CanEdit));
    OnPropertyChanged(nameof(CanSave));
    OnPropertyChanged(nameof(CanUseNv5Controls));
    OnPropertyChanged(nameof(CanUseRtsp));
    OnPropertyChanged(nameof(CanReboot));
    OnPropertyChanged(nameof(CanGenerateKey));
    OnPropertyChanged(nameof(CanSetKey));
    OnPropertyChanged(nameof(CanCopyRadio));
    OnPropertyChanged(nameof(CanControlTransmitter));
  }

  private static byte[] FixedLatin1(string text, int size, int maximumBytes) {
    byte[] output = new byte[size];
    byte[] input = Encoding.Latin1.GetBytes(text);
    Array.Copy(input, output, Math.Min(maximumBytes, input.Length));
    return output;
  }

  private static string Latin1String(byte[]? value) {
    if (value == null) {
      return "";
    }

    int length = Array.IndexOf(value, (byte)0);
    return Encoding.Latin1.GetString(value, 0, length < 0 ? value.Length : length);
  }

  public void Dispose() {
    if (_disposed) {
      return;
    }

    _disposed = true;
    _serviceTimer?.Stop();
    _transport.PacketReceived -= OnPacketReceived;
    _transport.LinksChanged -= OnLinksChanged;
    _transport.Dispose();
    foreach (var row in _parameterRows.Values) {
      row.PropertyChanged -= OnParameterRowChanged;
    }
  }
}

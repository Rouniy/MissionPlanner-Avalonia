using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner.Utilities;
using MissionPlannerAvalonia.Services;

namespace MissionPlannerAvalonia.ViewModels;

public partial class TrackerHomeModuleViewModel : ViewModelBase, IDisposable {
  internal const string TcpClient = "TCP NMEA Client";
  internal const string TcpHost = "TCP NMEA Host";
  internal const string UdpListener = "UDP NMEA Listener";
  internal const string Gpsd = "GPSD";
  private static readonly string[] NetworkInputs = [TcpClient, TcpHost, UdpListener, Gpsd];

  private readonly TrackerHomeNmeaService _reader;
  private readonly bool _usePersistentSettings;
  private readonly Queue<string> _rawLines = [];
  private CancellationTokenSource? _readCts;
  private bool _userCancelled;
  private bool _disposed;
  private string? _previousInput;

  public TrackerHomeModuleViewModel()
      : this(new TrackerHomeNmeaService(), usePersistentSettings: true) {
  }

  internal TrackerHomeModuleViewModel(
      TrackerHomeNmeaService reader, bool usePersistentSettings = false) {
    _reader = reader;
    _usePersistentSettings = usePersistentSettings;
    RefreshInputs();
    if (_usePersistentSettings) {
      LoadSettings();
    }
  }

  internal event Action<NmeaGgaFix>? FixAcquired;

  public ObservableCollection<string> Inputs { get; } = [];
  public IReadOnlyList<int> Bauds { get; } = new[] {
      4800, 9600, 19200, 38400, 57600, 115200,
  };

  [ObservableProperty]
  private string? _selectedInput;

  [ObservableProperty]
  private int _selectedBaud = 9600;

  [ObservableProperty]
  private string _networkHost = "127.0.0.1";

  [ObservableProperty]
  private int _networkPort = 2947;

  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(CanEditSettings))]
  private bool _reading;

  [ObservableProperty]
  private string _readButtonText = "Obtain Fix";

  [ObservableProperty]
  private string _status = "Select the external GPS source, then obtain one validated GGA fix.";

  [ObservableProperty]
  private string _rawNmea = "";

  public bool IsSerialInput => !NetworkInputs.Contains(SelectedInput, StringComparer.Ordinal);
  public bool IsNetworkInput => !IsSerialInput;
  public bool NeedsHost => SelectedInput is TcpClient or Gpsd;
  public string PortLabel => SelectedInput is TcpHost or UdpListener ? "Local port" : "Remote port";
  public bool CanEditSettings => !Reading;

  partial void OnSelectedInputChanged(string? value) {
    if (value == Gpsd && _previousInput != Gpsd && NetworkPort == 14551) {
      NetworkPort = 2947;
    } else if (value != Gpsd && _previousInput == Gpsd && NetworkPort == 2947) {
      NetworkPort = 14551;
    }
    _previousInput = value;
    OnPropertyChanged(nameof(IsSerialInput));
    OnPropertyChanged(nameof(IsNetworkInput));
    OnPropertyChanged(nameof(NeedsHost));
    OnPropertyChanged(nameof(PortLabel));
  }

  [RelayCommand]
  private void RefreshInputs() {
    string? selected = SelectedInput;
    Inputs.Clear();
    foreach (string port in System.IO.Ports.SerialPort.GetPortNames()
                 .Distinct(StringComparer.Ordinal).OrderBy(item => item, StringComparer.Ordinal)) {
      Inputs.Add(port);
    }
    foreach (string networkInput in NetworkInputs) {
      Inputs.Add(networkInput);
    }
    SelectedInput = selected != null && Inputs.Contains(selected)
        ? selected
        : Inputs.FirstOrDefault();
  }

  [RelayCommand]
  private async Task ObtainAsync() {
    if (Reading) {
      _userCancelled = true;
      _readCts?.Cancel();
      return;
    }
    if (!TryBuildOptions(out TrackerHomeNmeaOptions? options, out string error)) {
      Status = error;
      return;
    }

    _userCancelled = false;
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    _readCts = cts;
    Reading = true;
    ReadButtonText = "Cancel Read";
    Status = "Waiting for a valid NMEA GGA position fix…";
    try {
      PersistSettings();
      NmeaGgaFix fix = await _reader.ReadFixAsync(
          options!,
          line => Dispatcher.UIThread.Post(() => AppendRaw(line)),
          cts.Token);
      Status = "Valid GPS fix obtained.";
      FixAcquired?.Invoke(fix);
    } catch (OperationCanceledException) {
      Status = _userCancelled
          ? "GPS read cancelled."
          : "No valid GPS fix was received within 30 seconds.";
    } catch (Exception ex) {
      Status = "Unable to obtain tracker position: " + ex.Message;
    } finally {
      if (ReferenceEquals(_readCts, cts)) {
        _readCts = null;
      }
      Reading = false;
      ReadButtonText = "Obtain Fix";
    }
  }

  internal void CancelRead() {
    _userCancelled = true;
    _readCts?.Cancel();
  }

  private bool TryBuildOptions(
      out TrackerHomeNmeaOptions? options, out string error) {
    options = null;
    if (string.IsNullOrWhiteSpace(SelectedInput)) {
      error = "Select an external GPS source.";
      return false;
    }
    TrackerHomeNmeaTransport transport = SelectedInput switch {
      TcpClient => TrackerHomeNmeaTransport.TcpClient,
      TcpHost => TrackerHomeNmeaTransport.TcpHost,
      UdpListener => TrackerHomeNmeaTransport.UdpListener,
      Gpsd => TrackerHomeNmeaTransport.Gpsd,
      _ => TrackerHomeNmeaTransport.Serial,
    };
    if (transport == TrackerHomeNmeaTransport.Serial && !Bauds.Contains(SelectedBaud)) {
      error = "Select a supported serial baud rate.";
      return false;
    }
    if (transport != TrackerHomeNmeaTransport.Serial && NetworkPort is < 1 or > 65535) {
      error = "Network port must be between 1 and 65535.";
      return false;
    }
    if ((transport is TrackerHomeNmeaTransport.TcpClient or TrackerHomeNmeaTransport.Gpsd)
        && string.IsNullOrWhiteSpace(NetworkHost)) {
      error = "Enter the remote NMEA host.";
      return false;
    }
    options = new TrackerHomeNmeaOptions(
        transport, SelectedInput, SelectedBaud, NetworkHost.Trim(), NetworkPort);
    error = "";
    return true;
  }

  private void AppendRaw(string line) {
    _rawLines.Enqueue(line);
    while (_rawLines.Count > 50) {
      _rawLines.Dequeue();
    }
    RawNmea = string.Join(Environment.NewLine, _rawLines);
  }

  private void LoadSettings() {
    Settings settings = Settings.Instance;
    string? input = settings["TrackerHomeNmeaInput"];
    if (!string.IsNullOrWhiteSpace(input) && Inputs.Contains(input)) {
      SelectedInput = input;
    }
    SelectedBaud = LoadInt(settings, "TrackerHomeNmeaBaud", 9600);
    NetworkHost = settings["TrackerHomeNmeaHost"] ?? "127.0.0.1";
    NetworkPort = LoadInt(settings, "TrackerHomeNmeaPort", SelectedInput == Gpsd ? 2947 : 14551);
  }

  private void PersistSettings() {
    if (!_usePersistentSettings) {
      return;
    }
    Settings settings = Settings.Instance;
    settings["TrackerHomeNmeaInput"] = SelectedInput ?? "";
    settings["TrackerHomeNmeaBaud"] = SelectedBaud.ToString(CultureInfo.InvariantCulture);
    settings["TrackerHomeNmeaHost"] = NetworkHost;
    settings["TrackerHomeNmeaPort"] = NetworkPort.ToString(CultureInfo.InvariantCulture);
    settings.Save();
  }

  private static int LoadInt(Settings settings, string key, int fallback) =>
      int.TryParse(settings[key], NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
          ? value
          : fallback;

  public void Dispose() {
    if (_disposed) {
      return;
    }
    _disposed = true;
    CancelRead();
  }
}

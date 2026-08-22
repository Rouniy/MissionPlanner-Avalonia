using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using MissionPlanner.Utilities;

namespace MissionPlannerAvalonia.Services;

public enum SitlVehicle { Plane, Copter, Rover, Heli }

public enum SitlChannel { Dev, Beta, Stable, Skip }

public sealed class SitlStartOptions {
  public SitlVehicle Vehicle { get; init; }
  public SitlChannel Channel { get; init; } = SitlChannel.Dev;

  public string Model { get; init; } = "";

  public string Home { get; init; } = "";
  public int Speed { get; init; } = 1;
  public string ExtraCmdline { get; init; } = "";
  public bool WipeEeprom { get; init; }
  public int Instance { get; init; }
  public int SystemId { get; init; } = 1;
  public bool UseIdentityParameters { get; init; }
  public int? SecondarySerialClientPort { get; init; }
}

internal sealed record SitlSwarmInstancePlan(
    int Instance,
    int SystemId,
    string Home,
    int? SecondarySerialClientPort);

public class SitlLauncher {
  private const string _sitlBaseUrl =
      "https://firmware.ardupilot.org/Tools/MissionPlanner/sitl/";
  private const string _manifestUrl = "https://firmware.ardupilot.org/manifest.json.gz";
  // ArduPilot publishes the generated vehicle database at this stable Mission Planner URL.
  // There is no Tools/autotest/vehicleinfo.py in the source tree (it lives below pysim/),
  // so pointing at raw.githubusercontent.com here makes every defaults lookup fail with 404.
  private const string _vehicleInfoUrl =
      "https://firmware.ardupilot.org/Tools/MissionPlanner/vehicleinfo.py";
  private const string _autotestBaseUrl =
      "https://raw.githubusercontent.com/ArduPilot/ardupilot/master/Tools/autotest/";
  private const string _defaultHome = "-35.363261,149.165230,584,353";
  private const string _host = "127.0.0.1";
  private const int _baseTcpPort = 5760;
  private const int _baseRcOverridePort = 5501;

  private static readonly string[] _cygwinDlls = {
    "cygatomic-1.dll", "cyggcc_s-1.dll", "cyggcc_s-seh-1.dll", "cyggomp-1.dll",
    "cygiconv-2.dll", "cygintl-8.dll", "cygquadmath-0.dll", "cygssp-0.dll",
    "cygstdc++-6.dll", "cygwin1.dll"
  };

  private static readonly HttpClient _http =
      new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

  private Process? _process;

  private UdpClient? _rcSend;
  private int _instance;

  private static readonly System.Collections.Generic.List<SitlLauncher> _live = new();
  private static SitlLauncher? _primaryConnection;

  public static void StopAll() {
    SitlLauncher[] all;
    lock (_live) {
      all = _live.ToArray();
    }
    foreach (var s in all) {
      s.Stop();
    }
  }

  public static bool PlatformSupported =>
      RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ||
      RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

  public event Action<string>? Log;

  public bool IsRunning => _process is { HasExited: false };

  public int TcpPort => TcpPortForInstance(_instance);

  public string TcpEndpoint => $"tcp:{_host}:{TcpPort}";

  private static string CacheDir => AppPaths.SitlCacheRoot;

  public static (string ExeName, string DefaultModel) Map(SitlVehicle vehicle) => vehicle switch {
    SitlVehicle.Plane => ("ArduPlane", "plane"),
    SitlVehicle.Copter => ("ArduCopter", "+"),
    SitlVehicle.Rover => ("ArduRover", "rover"),
    SitlVehicle.Heli => ("ArduHeli", "heli"),
    _ => ("ArduCopter", "+"),
  };

  internal static int TcpPortForInstance(int instance) {
    ArgumentOutOfRangeException.ThrowIfNegative(instance);
    return checked(_baseTcpPort + 10 * instance);
  }

  internal static int RcOverridePortForInstance(int instance) {
    ArgumentOutOfRangeException.ThrowIfNegative(instance);
    return checked(_baseRcOverridePort + 10 * instance);
  }

  internal static IReadOnlyList<SitlSwarmInstancePlan> BuildSwarmPlan(
      double latitude,
      double longitude,
      double altitude,
      int heading,
      int count,
      bool chained) {
    if (count is < 2 or > 50) {
      throw new ArgumentOutOfRangeException(nameof(count), "SITL swarm size must be between 2 and 50.");
    }

    var origin = new PointLatLngAlt(latitude, longitude, altitude);
    var plans = new List<SitlSwarmInstancePlan>(count);
    for (int instance = count - 1; instance >= 0; instance--) {
      PointLatLngAlt position = origin.newpos(heading, instance * 4.0);
      string home = string.Format(CultureInfo.InvariantCulture,
          "{0},{1},{2},{3}", position.Lat, position.Lng, altitude, heading);
      int? secondaryClientPort = chained && instance < count - 1
          ? 5772 + 10 * instance
          : null;
      plans.Add(new SitlSwarmInstancePlan(
          instance, instance + 1, home, secondaryClientPort));
    }
    return plans;
  }

  internal static string BuildIdentityParameters(int systemId) {
    if (systemId is < 1 or > 255) {
      throw new ArgumentOutOfRangeException(nameof(systemId));
    }
    return "SERIAL0_PROTOCOL=2\n" +
        "SERIAL1_PROTOCOL=2\n" +
        $"SYSID_THISMAV={systemId}\n" +
        $"MAV_SYSID={systemId}\n" +
        "SIM_TERRAIN=0\n" +
        "TERRAIN_ENABLE=0\n" +
        "SCHED_LOOP_RATE=50\n" +
        "SIM_RATE_HZ=400\n" +
        "SIM_DRIFT_SPEED=0\n" +
        "SIM_DRIFT_TIME=0\n";
  }

  internal static string BuildLaunchArguments(
      string model,
      string home,
      int speed,
      int instance,
      string? defaults,
      string? extraCmdline,
      bool wipeEeprom,
      int? secondarySerialClientPort) {
    if (string.IsNullOrWhiteSpace(model)) {
      throw new ArgumentException("A SITL model is required.", nameof(model));
    }
    if (string.IsNullOrWhiteSpace(home)) {
      throw new ArgumentException("A SITL home is required.", nameof(home));
    }
    _ = TcpPortForInstance(instance);

    string arguments = $"--model {QuoteArgument(model.Trim())} " +
        $"--home {QuoteArgument(home.Trim())} -O{QuoteArgument(home.Trim())} " +
        $"-s{Math.Max(1, speed)} --instance {instance} --serial0 tcp:0";
    if (secondarySerialClientPort is { } serialPort) {
      if (serialPort is < 1 or > 65535) {
        throw new ArgumentOutOfRangeException(nameof(secondarySerialClientPort));
      }
      arguments += $" --serial2 tcpclient:{_host}:{serialPort}";
    }
    if (!string.IsNullOrWhiteSpace(defaults)) {
      arguments += $" --defaults {QuoteArgument(defaults)}";
    }
    if (wipeEeprom) {
      arguments += " --wipe";
    }
    if (!string.IsNullOrWhiteSpace(extraCmdline)) {
      arguments += " " + extraCmdline.Trim();
    }
    return arguments;
  }

  public async Task<bool> StartAsync(SitlStartOptions opts) {
    if (IsRunning) {
      Emit("SITL already running.");
      return true;
    }

    if (opts.Instance is < 0 or > 50) {
      Emit("SITL instance must be between 0 and 50.");
      return false;
    }
    if (opts.UseIdentityParameters && opts.SystemId is < 1 or > 255) {
      Emit("SITL system ID must be between 1 and 255.");
      return false;
    }
    if (opts.UseIdentityParameters &&
        ContainsCommandLineOption(opts.ExtraCmdline, "--defaults")) {
      Emit("Custom --defaults cannot be combined with swarm identity parameters.");
      return false;
    }

    _instance = opts.Instance;
    var (exeName, defaultModel) = Map(opts.Vehicle);

    var model = string.IsNullOrWhiteSpace(opts.Model) ? defaultModel : opts.Model.Trim();

    string? binary;
    try {
      binary = await EnsureBinaryAsync(exeName, opts.Channel).ConfigureAwait(false);
    } catch (Exception ex) {
      Emit($"SITL download failed: {ex.Message}");
      return false;
    }

    if (binary == null || !File.Exists(binary)) {
      Emit("Could not obtain a SITL binary for this platform/channel.");
      return false;
    }

    var workdir = opts.UseIdentityParameters
        ? Path.Combine(CacheDir, "instances", SafeDir(model), (opts.Instance + 1).ToString(
            CultureInfo.InvariantCulture))
        : Path.Combine(CacheDir, SafeDir(model));
    Directory.CreateDirectory(workdir);

    var home = string.IsNullOrWhiteSpace(opts.Home) ? _defaultHome : opts.Home.Trim();
    var speed = opts.Speed <= 0 ? 1 : opts.Speed;

    var extra = opts.ExtraCmdline?.Trim() ?? "";
    string? defaultsArgument = null;
    if (!ContainsCommandLineOption(extra, "--defaults")) {
      try {
        string? defaults = await EnsureDefaultParametersAsync(model).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(defaults)) {
          defaultsArgument = defaults;
          Emit($"Using frame defaults for {model}: {defaults}");
        }
      } catch (Exception ex) {
        // Defaults improve frame fidelity but must not make an otherwise usable simulator fail.
        Emit($"Could not load frame defaults for {model}: {ex.Message}");
      }
    }

    if (opts.UseIdentityParameters) {
      string identityPath = Path.Combine(workdir, "identity.parm");
      await File.WriteAllTextAsync(identityPath, BuildIdentityParameters(opts.SystemId))
          .ConfigureAwait(false);
      defaultsArgument = string.IsNullOrWhiteSpace(defaultsArgument)
          ? identityPath
          : defaultsArgument + "," + identityPath;
    }

    string args = BuildLaunchArguments(
        model, home, speed, opts.Instance, defaultsArgument, extra,
        opts.WipeEeprom, opts.SecondarySerialClientPort);

    var psi = new ProcessStartInfo {
      FileName = binary,
      Arguments = args,
      WorkingDirectory = workdir,
      UseShellExecute = false,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      CreateNoWindow = true,
    };
    psi.EnvironmentVariables["HOME"] = workdir;

    try {
      Emit($"Starting SITL: {Path.GetFileName(binary)} {args}");
      _process = Process.Start(psi);
    } catch (Exception ex) {
      Emit($"Failed to start SITL process: {ex.Message}");
      _process = null;
      return false;
    }

    if (_process == null) {
      Emit("Failed to start SITL process.");
      return false;
    }

    lock (_live) {
      _live.Add(this);
    }
    _process.OutputDataReceived += (_, e) => { if (e.Data != null) { Emit(e.Data); } };
    _process.ErrorDataReceived += (_, e) => { if (e.Data != null) { Emit(e.Data); } };
    _process.BeginOutputReadLine();
    _process.BeginErrorReadLine();

    Emit($"Waiting for {TcpEndpoint} ...");
    if (await WaitForPortAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false)) {
      Emit($"SITL listening on {TcpEndpoint}.");
      OpenRcOverride();
      return true;
    }

    Emit("Timed out waiting for SITL to open its TCP port.");
    Stop();
    return false;
  }

  private void OpenRcOverride() {
    try {
      _rcSend?.Dispose();
      _rcSend = new UdpClient();
      _rcSend.Connect(_host, RcOverridePortForInstance(_instance));
    } catch (Exception ex) {
      Emit($"RC override socket unavailable: {ex.Message}");
      _rcSend = null;
    }
  }

  internal static bool TrySendRcInput(JoystickOutputSnapshot snapshot) {
    SitlLauncher? launcher;
    lock (_live) {
      launcher = _primaryConnection;
    }
    return launcher?.TrySendRcInput(JoystickControlPackets.BuildSitlDatagram(snapshot)) == true;
  }

  internal void SetAsPrimaryConnection(bool connected) {
    lock (_live) {
      if (connected) {
        _primaryConnection = this;
      } else if (ReferenceEquals(_primaryConnection, this)) {
        _primaryConnection = null;
      }
    }
  }

  internal static void ClearPrimaryConnection() {
    lock (_live) {
      _primaryConnection = null;
    }
  }

  private bool TrySendRcInput(byte[] packet) {
    try {
      var send = _rcSend;
      if (send == null || !IsRunning) {
        return false;
      }

      send.Send(packet, packet.Length);
      return true;
    } catch {
      return false;
    }
  }

  public void Stop() {
    lock (_live) {
      _live.Remove(this);
      if (ReferenceEquals(_primaryConnection, this)) {
        _primaryConnection = null;
      }
    }
    try {
      _rcSend?.Dispose();
    } catch {

    }
    _rcSend = null;

    var proc = _process;
    _process = null;
    if (proc == null) {
      return;
    }

    try {
      if (!proc.HasExited) {
        proc.Kill(entireProcessTree: true);
      }
    } catch (Exception ex) {
      Emit($"Failed to stop SITL: {ex.Message}");
    } finally {
      proc.Dispose();
    }
  }

  private static string SafeDir(string model) {
    foreach (var c in Path.GetInvalidFileNameChars()) {
      model = model.Replace(c, '_');
    }
    return string.IsNullOrEmpty(model) ? "sitl" : model;
  }

  private async Task<string?> EnsureDefaultParametersAsync(string model) {
    Directory.CreateDirectory(CacheDir);
    string vehicleInfoPath = Path.Combine(CacheDir, "vehicleinfo.py");
    await RefreshCachedFileAsync(_vehicleInfoUrl, vehicleInfoPath).ConfigureAwait(false);
    if (!File.Exists(vehicleInfoPath)) {
      return null;
    }

    var relativePaths = ParseDefaultParameterPaths(
        await File.ReadAllTextAsync(vehicleInfoPath).ConfigureAwait(false), model);
    if (relativePaths.Count == 0) {
      return null;
    }

    var localPaths = new System.Collections.Generic.List<string>();
    foreach (string relativePath in relativePaths) {
      string normalized = relativePath.Replace('\\', '/').TrimStart('/');
      if (normalized.Split('/').Any(segment => segment == "..")) {
        throw new InvalidDataException($"Unsafe SITL defaults path: {relativePath}");
      }

      string root = Path.Combine(CacheDir, "autotest");
      string destination = Path.GetFullPath(Path.Combine(root,
          normalized.Replace('/', Path.DirectorySeparatorChar)));
      string rootedPrefix = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
      if (!destination.StartsWith(rootedPrefix, StringComparison.Ordinal)) {
        throw new InvalidDataException($"Unsafe SITL defaults path: {relativePath}");
      }
      Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
      var url = new Uri(new Uri(_autotestBaseUrl), normalized).ToString();
      await RefreshCachedFileAsync(url, destination).ConfigureAwait(false);
      if (!File.Exists(destination)) {
        return null;
      }
      localPaths.Add(destination);
    }
    return string.Join(',', localPaths);
  }

  private async Task RefreshCachedFileAsync(string url, string destination) {
    bool fresh = File.Exists(destination)
                 && DateTime.UtcNow - File.GetLastWriteTimeUtc(destination) < TimeSpan.FromDays(1);
    if (fresh) {
      return;
    }
    try {
      await DownloadAsync(url, destination).ConfigureAwait(false);
    } catch when (File.Exists(destination)) {
      Emit($"Using cached {Path.GetFileName(destination)} after refresh failed.");
    }
  }

  internal static IReadOnlyList<string> ParseDefaultParameterPaths(string vehicleInfo, string model) {
    if (string.IsNullOrWhiteSpace(vehicleInfo) || string.IsNullOrWhiteSpace(model)) {
      return [];
    }
    try {
      string json = StripPythonComments(ExtractFirstDictionary(vehicleInfo));
      json = Regex.Replace(json, @"\bTrue\b", "true");
      json = Regex.Replace(json, @"\bFalse\b", "false");
      json = Regex.Replace(json, @"\bNone\b", "null");
      using var doc = JsonDocument.Parse(json,
          new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
      foreach (var vehicle in doc.RootElement.EnumerateObject()) {
        if (!TryGetPropertyIgnoreCase(vehicle.Value, "frames", out JsonElement frames)) {
          continue;
        }
        JsonElement frame = default;
        bool found = false;
        foreach (var candidate in frames.EnumerateObject()) {
          if (string.Equals(candidate.Name, model, StringComparison.OrdinalIgnoreCase)) {
            frame = candidate.Value;
            found = true;
            break;
          }
        }
        if (!found || !TryGetPropertyIgnoreCase(
                frame, "default_params_filename", out JsonElement defaults)) {
          continue;
        }
        if (defaults.ValueKind == JsonValueKind.String
            && defaults.GetString() is { Length: > 0 } single) {
          return [single];
        }
        if (defaults.ValueKind == JsonValueKind.Array) {
          return defaults.EnumerateArray()
              .Where(value => value.ValueKind == JsonValueKind.String)
              .Select(value => value.GetString())
              .Where(value => !string.IsNullOrWhiteSpace(value))
              .Select(value => value!)
              .ToList();
        }
      }
    } catch {

    }
    return [];
  }

  private static bool TryGetPropertyIgnoreCase(
      JsonElement element, string name, out JsonElement value) {
    if (element.ValueKind == JsonValueKind.Object) {
      foreach (var property in element.EnumerateObject()) {
        if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) {
          value = property.Value;
          return true;
        }
      }
    }
    value = default;
    return false;
  }

  private static string ExtractFirstDictionary(string text) {
    int start = text.IndexOf('{');
    if (start < 0) {
      throw new FormatException("vehicleinfo.py contains no dictionary");
    }
    int depth = 0;
    char quote = '\0';
    bool escape = false;
    for (int index = start; index < text.Length; index++) {
      char current = text[index];
      if (quote != '\0') {
        if (escape) {
          escape = false;
        } else if (current == '\\') {
          escape = true;
        } else if (current == quote) {
          quote = '\0';
        }
        continue;
      }
      if (current is '\'' or '"') {
        quote = current;
      } else if (current == '{') {
        depth++;
      } else if (current == '}' && --depth == 0) {
        return text.Substring(start, index - start + 1);
      }
    }
    throw new FormatException("vehicleinfo.py contains an incomplete dictionary");
  }

  private static string StripPythonComments(string text) {
    var result = new System.Text.StringBuilder(text.Length);
    char quote = '\0';
    bool escape = false;
    for (int index = 0; index < text.Length; index++) {
      char current = text[index];
      if (quote != '\0') {
        result.Append(current);
        if (escape) {
          escape = false;
        } else if (current == '\\') {
          escape = true;
        } else if (current == quote) {
          quote = '\0';
        }
        continue;
      }
      if (current is '\'' or '"') {
        quote = current;
        result.Append(current);
      } else if (current == '#') {
        while (index + 1 < text.Length && text[index + 1] is not '\r' and not '\n') {
          index++;
        }
      } else {
        result.Append(current);
      }
    }
    return result.ToString();
  }

  private static bool ContainsCommandLineOption(string arguments, string option) =>
      Regex.IsMatch(arguments ?? "", $@"(^|\s){Regex.Escape(option)}(?:\s|=|$)",
          RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

  private static string QuoteArgument(string value) =>
      '"' + value.Replace("\"", "\\\"") + '"';

  private async Task<string?> EnsureBinaryAsync(string exeName, SitlChannel channel) {
    Directory.CreateDirectory(CacheDir);

    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
      var path = Path.Combine(CacheDir, exeName + ".exe");

      if (channel == SitlChannel.Skip) {
        if (File.Exists(path)) {
          Emit($"Skip download — using cached {Path.GetFileName(path)}.");
          return path;
        }
        Emit("Skip download selected but no cached SITL binary is present.");
        return null;
      }

      var baseUrl = WindowsBaseUrl(channel, exeName);
      Emit($"Downloading {exeName} ({channel}, Windows) from {baseUrl} ...");
      await DownloadAsync(baseUrl + exeName + ".elf", path).ConfigureAwait(false);
      foreach (var dll in _cygwinDlls) {
        var dllPath = Path.Combine(CacheDir, dll);
        if (!File.Exists(dllPath)) {
          try {
            await DownloadAsync(baseUrl + dll, dllPath).ConfigureAwait(false);
          } catch (Exception ex) {
            Emit($"Optional dependency {dll} failed: {ex.Message}");
          }
        }
      }
      return path;
    }

    if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) {
      var platform = (RuntimeInformation.OSArchitecture is Architecture.Arm or Architecture.Arm64)
          ? "SITL_arm_linux_gnueabihf"
          : "SITL_x86_64_linux_gnu";
      var path = Path.Combine(CacheDir, exeName);

      if (channel == SitlChannel.Skip) {
        if (File.Exists(path)) {
          Emit($"Skip download — using cached {Path.GetFileName(path)}.");
          return path;
        }
        Emit("Skip download selected but no cached SITL binary is present.");
        return null;
      }

      if (File.Exists(path)) {
        Emit($"Using cached {Path.GetFileName(path)}.");
        return path;
      }

      Emit($"Resolving {exeName} for {platform} from manifest ({channel}) ...");
      var url = await ResolveLinuxUrlAsync(platform, exeName).ConfigureAwait(false);
      if (url == null) {
        Emit($"No {platform} SITL build for {exeName} in the firmware manifest.");
        return null;
      }
      Emit($"Downloading {exeName} (Linux) ...");
      await DownloadAsync(url, path).ConfigureAwait(false);
      MakeExecutable(path);
      return path;
    }

    Emit("Prebuilt ArduPilot SITL binaries are not published for this platform " +
         "(macOS/other). Build SITL from source or run it under Linux/WSL.");
    return null;
  }

  private static string WindowsBaseUrl(SitlChannel channel, string exeName) {
    switch (channel) {
      case SitlChannel.Beta:
        return _sitlBaseUrl + "Beta/";
      case SitlChannel.Stable:
        var n = exeName.ToLowerInvariant();
        if (n.Contains("plane")) {
          return _sitlBaseUrl + "PlaneStable/";
        }
        if (n.Contains("rover")) {
          return _sitlBaseUrl + "RoverStable/";
        }

        return _sitlBaseUrl + "CopterStable/";
      default:
        return _sitlBaseUrl;
    }
  }

  private async Task<string?> ResolveLinuxUrlAsync(string platform, string exeName) {
    using var stream = await _http.GetStreamAsync(_manifestUrl).ConfigureAwait(false);
    using var gz = new GZipStream(stream, CompressionMode.Decompress);
    using var doc = await JsonDocument.ParseAsync(gz).ConfigureAwait(false);

    if (!doc.RootElement.TryGetProperty("firmware", out var firmware)) {
      return null;
    }

    string? fallback = null;
    foreach (var entry in firmware.EnumerateArray()) {
      if (!entry.TryGetProperty("platform", out var p) ||
          !string.Equals(p.GetString(), platform, StringComparison.OrdinalIgnoreCase)) {
        continue;
      }

      if (!entry.TryGetProperty("url", out var u)) {
        continue;
      }

      var url = u.GetString();
      if (url == null) {
        continue;
      }

      var leaf = url.TrimEnd('/');
      leaf = leaf.Substring(leaf.LastIndexOf('/') + 1);
      if (!string.Equals(leaf, exeName, StringComparison.OrdinalIgnoreCase)) {
        continue;
      }

      var latest = entry.TryGetProperty("latest", out var l) && l.TryGetInt64(out var lv) && lv == 1;
      if (latest) {
        return url;
      }

      fallback ??= url;
    }
    return fallback;
  }

  private async Task DownloadAsync(string url, string destination) {
    var tmp = destination + ".part";
    using (var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead)
               .ConfigureAwait(false)) {
      resp.EnsureSuccessStatusCode();
      using var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
      using var dst = File.Create(tmp);
      await src.CopyToAsync(dst).ConfigureAwait(false);
    }
    if (File.Exists(destination)) {
      File.Delete(destination);
    }

    File.Move(tmp, destination);
  }

  private void MakeExecutable(string path) {
    if (OperatingSystem.IsWindows()) {
      return;
    }
    try {
      File.SetUnixFileMode(path,
          UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
          UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
          UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    } catch (Exception ex) {
      Emit($"Could not set executable bit: {ex.Message}");
    }
  }

  private async Task<bool> WaitForPortAsync(TimeSpan timeout) {
    var deadline = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < deadline) {
      if (!IsRunning) {
        Emit("SITL process exited before opening its port.");
        return false;
      }
      try {
        using var client = new TcpClient();
        var connect = client.ConnectAsync(_host, TcpPort);
        if (await Task.WhenAny(connect, Task.Delay(500)).ConfigureAwait(false) == connect &&
            client.Connected) {
          return true;
        }
      } catch {

      }
      await Task.Delay(500).ConfigureAwait(false);
    }
    return false;
  }

  private void Emit(string message) => Log?.Invoke(message);
}

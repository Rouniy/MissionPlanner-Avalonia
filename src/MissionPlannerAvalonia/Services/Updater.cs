using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using MissionPlanner.Utilities;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace MissionPlannerAvalonia.Services;

public enum UpdateChannel { Stable, Beta }

public static class Updater {
  public const string DefaultOwnerRepo = "sema-aviation/MissionPlanner-Avalonia";
  public const string PagesBaseUrl = "https://sema-aviation.github.io/MissionPlanner-Avalonia";

  public const string PublicKeyBase64 = "A0WFYpVPY1BvbOSpAzmuCTfbV6SR/cw9sUPy4AKZSgg=";

  private const string _stableSkipKey = "update_skip_version";
  private const string _betaSkipKey = "update_skip_beta_version";

  private static readonly HttpClient _http = CreateClient();

  private static HttpClient CreateClient() {
    var c = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
    c.DefaultRequestHeaders.UserAgent.ParseAdd("MissionPlannerAvalonia-Updater");
    return c;
  }

  public static Task CheckOnStartupAsync() => AppPaths.IsPackageManaged
      ? Task.CompletedTask
      : RunAsync(SelectedChannel(), silentWhenUpToDate: true, respectSkip: true);

  public static Task CheckNowAsync() => AppPaths.IsPackageManaged
      ? Dialogs.Alert("Update", "This installation is managed by the system package manager. " +
          "Use apt to install updates.")
      : RunAsync(SelectedChannel(), silentWhenUpToDate: false, respectSkip: false);

  public static Task CheckBetaNowAsync() => AppPaths.IsPackageManaged
      ? Dialogs.Alert("Beta update", "This installation is managed by the system package manager. " +
          "Install a beta .deb from the beta release instead of overwriting package-owned files.")
      : RunAsync(UpdateChannel.Beta, silentWhenUpToDate: false, respectSkip: false);

  private static UpdateChannel SelectedChannel() =>
      Settings.Instance.GetBoolean("beta_updates", false) ? UpdateChannel.Beta : UpdateChannel.Stable;

  private static async Task RunAsync(
      UpdateChannel channel, bool silentWhenUpToDate, bool respectSkip) {
    UpdateEngine engine;
    UpdateEngine.Manifest? m;
    try {
      engine = NewEngine();
      if (channel == UpdateChannel.Beta) {
        UpdateManifestEndpoint? endpoint = await BetaUpdateLocator.FindLatestAsync(
            _http, DefaultOwnerRepo, UpdateEngine.Rid()).ConfigureAwait(true);
        m = endpoint == null
            ? null
            : await engine.FetchManifestAsync(
                endpoint.ManifestUrl, endpoint.SignatureUrl).ConfigureAwait(true);
        if (m != null && m.Bundle == null) {
          throw new InvalidDataException(
              "The signed beta manifest does not contain a full update bundle.");
        }
      } else {
        m = await engine.FetchManifestAsync().ConfigureAwait(true);
      }
    } catch (Exception ex) {
      if (!silentWhenUpToDate) {
        await Dialogs.Alert(ChannelTitle(channel), "Update check failed: " + ex.Message);
      }
      return;
    }

    if (m == null) {
      if (!silentWhenUpToDate) {
        string message = channel == UpdateChannel.Beta
            ? "No signed beta release is currently published for this platform."
            : "Could not reach the update server.";
        await Dialogs.Alert(ChannelTitle(channel), message);
      }
      return;
    }

    string local = AppVersion.Informational;
    string localDisplay = AppVersion.Full;
    if (!UpdateEngine.IsNewer(m.Version, local)) {
      if (!silentWhenUpToDate) {
        await Dialogs.Alert(ChannelTitle(channel), $"You are up to date ({localDisplay}).");
      }
      return;
    }

    string skipKey = channel == UpdateChannel.Beta ? _betaSkipKey : _stableSkipKey;
    if (respectSkip && Settings.Instance[skipKey] == m.Version) {
      return;
    }

    while (true) {
      string warning = channel == UpdateChannel.Beta
          ? " This is a prerelease build and may be less stable."
          : "";
      var choice = await Dialogs.Choice(ChannelTitle(channel) + " available",
          $"Version {AppVersion.Parse(m.Version).Display} is available " +
          $"(you have {localDisplay}).{warning}",
          "Install", "What's new", "Skip this version", "Later");
      if (choice == "What's new") {
        Dialogs.OpenUrl(string.IsNullOrEmpty(m.Notes)
            ? $"https://github.com/{DefaultOwnerRepo}/releases"
            : m.Notes);
        continue;
      }
      if (choice == "Skip this version") {
        Settings.Instance[skipKey] = m.Version;
        Settings.Instance.Save();
        return;
      }
      if (choice != "Install") {
        return;
      }
      break;
    }

    if (m.Bundle != null) {
      await InstallBundleAsync(engine, m.Bundle);
      return;
    }

    var changed = engine.Diff(m);
    if (changed.Count == 0) {
      await Dialogs.Alert("Update", "You already have all the latest files.");
      return;
    }

    string staging = Path.Combine(engine.CacheDir, "staging");
    try {
      if (Directory.Exists(staging)) {
        Directory.Delete(staging, true);
      }
      Directory.CreateDirectory(staging);
    } catch { }

    var progress = new ProgressReporter("Downloading update");
    progress.Show2();
    try {
      await engine.DownloadAsync(changed, staging,
          new Progress<double>(p => progress.Set(p, "Downloading…")), progress.Token).ConfigureAwait(true);
    } catch (OperationCanceledException) {
      progress.Close();
      return;
    } catch (Exception ex) {
      progress.Close();
      await Dialogs.Alert("Update", "Download failed: " + ex.Message);
      return;
    }
    progress.Close();

    try {
      ApplyAndRestart(engine, changed, staging);
    } catch (Exception ex) {
      await Dialogs.Alert("Update", "Install failed: " + ex.Message);
    }
  }

  private static UpdateEngine NewEngine() =>
      new(_http, AppPaths.InstallRoot, PagesBaseUrl, Convert.FromBase64String(PublicKeyBase64));

  private static string ChannelTitle(UpdateChannel channel) =>
      channel == UpdateChannel.Beta ? "Beta update" : "Update";

  private static void ApplyAndRestart(
      UpdateEngine engine, IReadOnlyList<UpdateEngine.ManifestFile> changed, string staging) {
    string exe = Environment.ProcessPath ?? "";
    if (OperatingSystem.IsWindows()) {

      RunWindowsHelper(engine.InstallDir, staging, exe);
    } else {
      // Linux: in-place per-file swap. macOS never reaches here — a mac manifest always carries a
      // bundle, so RunAsync takes the full-bundle path (swapping loose files would break the
      // Developer ID signature + notarization staple that Gatekeeper checks).
      engine.Apply(changed, staging);
      if (!string.IsNullOrEmpty(exe)) {
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false });
      }
    }
    Shutdown();
  }

  // Beta releases use a signed manifest containing one SHA-256-pinned full bundle on every target.
  // Stable macOS releases use the same bundle path because loose-file replacement would invalidate
  // the Developer ID signature and notarization staple.
  private static async Task InstallBundleAsync(UpdateEngine engine, UpdateEngine.ManifestBundle bundle) {
    string staging = Path.Combine(engine.CacheDir, "staging");
    try {
      if (Directory.Exists(staging)) {
        Directory.Delete(staging, true);
      }
      Directory.CreateDirectory(staging);
    } catch { }
    string zip = Path.Combine(staging, "update.zip");

    var progress = new ProgressReporter("Downloading update");
    progress.Show2();
    try {
      await engine.DownloadBundleAsync(bundle, zip,
          new Progress<double>(p => progress.Set(p, "Downloading…")), progress.Token).ConfigureAwait(true);
    } catch (OperationCanceledException) {
      progress.Close();
      return;
    } catch (Exception ex) {
      progress.Close();
      await Dialogs.Alert("Update", "Download failed: " + ex.Message);
      return;
    }
    progress.Close();

    try {
      if (OperatingSystem.IsMacOS()) {
        RunMacBundleHelper(engine.InstallDir, zip, staging);
      } else {
        string extracted = Path.Combine(staging, "extract");
        IReadOnlyList<UpdateEngine.ManifestFile> files = engine.ExtractBundle(zip, extracted);
        if (OperatingSystem.IsWindows()) {
          RunWindowsHelper(engine.InstallDir, extracted, Environment.ProcessPath ?? "");
        } else {
          engine.Apply(files, extracted);
          string exe = Environment.ProcessPath ?? "";
          if (!string.IsNullOrEmpty(exe)) {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false });
          }
        }
      }
      Shutdown();
    } catch (Exception ex) {
      await Dialogs.Alert("Update", "Install failed: " + ex.Message);
    }
  }

  private static void RunMacBundleHelper(string installDir, string zip, string staging) {
    string app = Path.GetFullPath(Path.Combine(installDir, "..", ".."));
    if (!app.EndsWith(".app", StringComparison.OrdinalIgnoreCase)) {
      throw new InvalidOperationException("Not running from a .app bundle; cannot self-update.");
    }
    int pid = Environment.ProcessId;
    string extract = Path.Combine(staging, "extract");
    string newApp = Path.Combine(extract, Path.GetFileName(app));
    string sh = Path.Combine(Path.GetTempPath(), $"mp-update-{pid}.sh");

    // Wait for us to exit, unpack with ditto (preserves +x/symlinks/xattrs/signature), swap the
    // whole bundle, relaunch. Rollback from <app>.old if the copy fails; the staging tree is wiped.
    string script = string.Join('\n', new[] {
      "#!/bin/sh",
      $"while kill -0 {pid} 2>/dev/null; do sleep 0.5; done",
      $"rm -rf {Q(extract)}",
      $"ditto -x -k {Q(zip)} {Q(extract)} || {{ open {Q(app)}; exit 1; }}",
      $"[ -d {Q(newApp)} ] || {{ open {Q(app)}; exit 1; }}",
      $"rm -rf {Q(app + ".old")}",
      $"mv {Q(app)} {Q(app + ".old")} || {{ open {Q(app)}; exit 1; }}",
      $"if ditto {Q(newApp)} {Q(app)}; then",
      $"  xattr -dr com.apple.quarantine {Q(app)} 2>/dev/null",
      $"  rm -rf {Q(app + ".old")} {Q(staging)}",
      $"  open {Q(app)}",
      "else",
      $"  rm -rf {Q(app)}; mv {Q(app + ".old")} {Q(app)}; open {Q(app)}",
      "fi",
      "rm -f \"$0\"",
      "",
    });
    File.WriteAllText(sh, script);
    Process.Start(new ProcessStartInfo("/bin/sh", $"\"{sh}\"") { UseShellExecute = false });
  }

  private static string Q(string s) => "'" + s.Replace("'", "'\\''") + "'";

  private static void RunWindowsHelper(string installDir, string staging, string exe) {
    int pid = Environment.ProcessId;
    string bat = Path.Combine(Path.GetTempPath(), $"mp-update-{pid}.cmd");
    string script =
        "@echo off\r\n" +
        ":wait\r\n" +
        $"tasklist /fi \"PID eq {pid}\" | find \"{pid}\" >nul && (timeout /t 1 /nobreak >nul & goto wait)\r\n" +
        $"xcopy /e /y /i \"{staging}\\*\" \"{installDir}\\\" >nul\r\n" +
        $"start \"\" \"{exe}\"\r\n" +
        "del \"%~f0\"\r\n";
    File.WriteAllText(bat, script);
    Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{bat}\"") {
      UseShellExecute = false,
      CreateNoWindow = true,
    });
  }

  private static void Shutdown() {
    if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d) {
      Dispatcher.UIThread.Post(() => d.Shutdown());
    } else {
      Environment.Exit(0);
    }
  }
}

public sealed record UpdateManifestEndpoint(string ManifestUrl, string SignatureUrl);

public static class BetaUpdateLocator {
  private sealed record ReleaseAsset(
      [property: JsonPropertyName("name")] string Name,
      [property: JsonPropertyName("browser_download_url")] string DownloadUrl);

  private sealed record Release(
      [property: JsonPropertyName("draft")] bool Draft,
      [property: JsonPropertyName("prerelease")] bool Prerelease,
      [property: JsonPropertyName("assets")] IReadOnlyList<ReleaseAsset>? Assets);

  public static async Task<UpdateManifestEndpoint?> FindLatestAsync(
      HttpClient http, string ownerRepo, string rid, CancellationToken ct = default) {
    ArgumentNullException.ThrowIfNull(http);
    if (string.IsNullOrWhiteSpace(ownerRepo) || string.IsNullOrWhiteSpace(rid)) {
      throw new ArgumentException("Repository and runtime identifier are required.");
    }

    string api = $"https://api.github.com/repos/{ownerRepo.Trim('/')}/releases?per_page=30";
    IReadOnlyList<Release>? releases;
    try {
      releases = JsonSerializer.Deserialize<IReadOnlyList<Release>>(
          await http.GetByteArrayAsync(api, ct).ConfigureAwait(false));
    } catch (HttpRequestException) {
      return null;
    } catch (TaskCanceledException) {
      return null;
    }

    string manifestName = rid + "-manifest.json";
    string signatureName = rid + "-manifest.sig";
    foreach (Release release in releases ?? Array.Empty<Release>()) {
      if (release.Draft || !release.Prerelease || release.Assets == null) {
        continue;
      }
      string? manifest = release.Assets.FirstOrDefault(asset =>
          string.Equals(asset.Name, manifestName, StringComparison.OrdinalIgnoreCase))?.DownloadUrl;
      string? signature = release.Assets.FirstOrDefault(asset =>
          string.Equals(asset.Name, signatureName, StringComparison.OrdinalIgnoreCase))?.DownloadUrl;
      if (IsHttps(manifest) && IsHttps(signature)) {
        return new UpdateManifestEndpoint(manifest!, signature!);
      }
    }
    return null;
  }

  private static bool IsHttps(string? value) =>
      Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
      && uri.Scheme == Uri.UriSchemeHttps;
}

public sealed class UpdateEngine {
  private static readonly JsonSerializerOptions _jsonOpts = new() { PropertyNameCaseInsensitive = true };

  private readonly HttpClient _http;
  private readonly string _baseUrl;
  private readonly byte[] _publicKey;
  private readonly string _rid;

  public string InstallDir { get; }

  public string CacheDir => AppPaths.UpdateCacheRoot;

  public UpdateEngine(HttpClient http, string installDir, string baseUrl, byte[] publicKey,
      string? rid = null) {
    _http = http;
    InstallDir = installDir;
    _baseUrl = baseUrl.TrimEnd('/');
    _publicKey = publicKey;
    _rid = rid ?? Rid();
  }

  public sealed record ManifestFile(string Path, string Sha256, long Size);

  public sealed record ManifestBundle(string Url, string Sha256, long Size);

  public sealed record Manifest(
      string Version, string? Notes, IReadOnlyList<ManifestFile> Files, ManifestBundle? Bundle = null);

  public async Task<Manifest?> FetchManifestAsync(CancellationToken ct = default) {
    return await FetchManifestAsync(
        $"{_baseUrl}/{_rid}/manifest.json",
        $"{_baseUrl}/{_rid}/manifest.sig", ct).ConfigureAwait(false);
  }

  public async Task<Manifest?> FetchManifestAsync(
      string manifestUrl, string signatureUrl, CancellationToken ct = default) {
    byte[] json, sig;
    try {
      json = await _http.GetByteArrayAsync(manifestUrl, ct).ConfigureAwait(false);
      string sigText = await _http.GetStringAsync(signatureUrl, ct).ConfigureAwait(false);
      sig = Convert.FromBase64String(sigText.Trim());
    } catch (HttpRequestException) {
      return null;
    } catch (TaskCanceledException) {
      return null;
    }

    if (!VerifySignature(json, sig)) {
      throw new SecurityException("Update manifest signature is invalid.");
    }
    return JsonSerializer.Deserialize<Manifest>(json, _jsonOpts);
  }

  public IReadOnlyList<ManifestFile> ExtractBundle(string zipPath, string extractionDir) {
    if (Directory.Exists(extractionDir)) {
      Directory.Delete(extractionDir, recursive: true);
    }
    Directory.CreateDirectory(extractionDir);
    ZipFile.ExtractToDirectory(zipPath, extractionDir);

    string launcher = Path.Combine(extractionDir,
        OperatingSystem.IsWindows() ? "MissionPlannerAvalonia.exe" : "MissionPlannerAvalonia");
    if (!File.Exists(launcher)) {
      throw new InvalidDataException("The update bundle does not contain the application launcher.");
    }
    if (!OperatingSystem.IsWindows()) {
      File.SetUnixFileMode(launcher, File.GetUnixFileMode(launcher)
          | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
    }

    return Directory.EnumerateFiles(extractionDir, "*", SearchOption.AllDirectories)
        .Select(path => new ManifestFile(
            Path.GetRelativePath(extractionDir, path), Sha256File(path), new FileInfo(path).Length))
        .OrderBy(file => file.Path, StringComparer.Ordinal)
        .ToArray();
  }

  public bool VerifySignature(byte[] data, byte[] signature) {
    var verifier = new Ed25519Signer();
    verifier.Init(forSigning: false, new Ed25519PublicKeyParameters(_publicKey, 0));
    verifier.BlockUpdate(data, 0, data.Length);
    return verifier.VerifySignature(signature);
  }

  public List<ManifestFile> Diff(Manifest m) {
    var changed = new List<ManifestFile>();
    foreach (var f in m.Files) {
      string local = Path.Combine(InstallDir, f.Path);
      if (!File.Exists(local) ||
          !string.Equals(Sha256File(local), f.Sha256, StringComparison.OrdinalIgnoreCase)) {
        changed.Add(f);
      }
    }
    return changed;
  }

  public async Task DownloadAsync(IReadOnlyList<ManifestFile> changed, string stagingDir,
      IProgress<double>? progress = null, CancellationToken ct = default) {
    long total = changed.Sum(f => f.Size);
    long done = 0;

    await Parallel.ForEachAsync(changed,
        new ParallelOptions { MaxDegreeOfParallelism = 3, CancellationToken = ct },
        async (f, c) => {
          string url = $"{_baseUrl}/{_rid}/{f.Path.Replace('\\', '/')}";
          string dest = Path.Combine(stagingDir, f.Path);
          Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

          using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, c)
              .ConfigureAwait(false);
          resp.EnsureSuccessStatusCode();
          await using var src = await resp.Content.ReadAsStreamAsync(c).ConfigureAwait(false);
          await using var dst = File.Create(dest);

          var buffer = new byte[81920];
          int n;
          while ((n = await src.ReadAsync(buffer, c).ConfigureAwait(false)) > 0) {
            await dst.WriteAsync(buffer.AsMemory(0, n), c).ConfigureAwait(false);
            if (total > 0) {
              long d = Interlocked.Add(ref done, n);
              progress?.Report(Math.Clamp(d * 100.0 / total, 0, 100));
            }
          }
        }).ConfigureAwait(false);

    foreach (var f in changed) {
      string dest = Path.Combine(stagingDir, f.Path);
      if (!string.Equals(Sha256File(dest), f.Sha256, StringComparison.OrdinalIgnoreCase)) {
        throw new InvalidDataException($"Downloaded file hash mismatch: {f.Path}");
      }
    }
  }

  public async Task DownloadBundleAsync(ManifestBundle bundle, string destZip,
      IProgress<double>? progress = null, CancellationToken ct = default) {
    if (!Uri.TryCreate(bundle.Url, UriKind.Absolute, out Uri? bundleUri)
        || bundleUri.Scheme != Uri.UriSchemeHttps) {
      throw new SecurityException("Update bundles must use HTTPS.");
    }
    Directory.CreateDirectory(Path.GetDirectoryName(destZip)!);
    using (var resp = await _http.GetAsync(bundleUri, HttpCompletionOption.ResponseHeadersRead, ct)
        .ConfigureAwait(false)) {
      resp.EnsureSuccessStatusCode();
      long total = resp.Content.Headers.ContentLength ?? bundle.Size;
      await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
      await using var dst = File.Create(destZip);
      var buffer = new byte[81920];
      long done = 0;
      int n;
      while ((n = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0) {
        await dst.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
        done += n;
        if (total > 0) {
          progress?.Report(Math.Clamp(done * 100.0 / total, 0, 100));
        }
      }
    }
    if (!string.Equals(Sha256File(destZip), bundle.Sha256, StringComparison.OrdinalIgnoreCase)) {
      throw new InvalidDataException("Downloaded update package hash mismatch.");
    }
  }

  public void Apply(IReadOnlyList<ManifestFile> changed, string stagingDir) {
    var moved = new List<(string live, string old)>();
    var placed = new List<string>();
    try {
      foreach (var f in changed) {
        string live = Path.Combine(InstallDir, f.Path);
        string staged = Path.Combine(stagingDir, f.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(live)!);
        string old = live + ".old";
        if (File.Exists(old)) {
          File.Delete(old);
        }
        if (File.Exists(live)) {
          File.Move(live, old);
          moved.Add((live, old));
        }
        File.Move(staged, live);
        placed.Add(live);
      }
    } catch {
      foreach (string live in placed) {
        try {
          File.Delete(live);
        } catch { }
      }
      foreach (var (live, old) in moved) {
        try {
          if (File.Exists(live)) {
            File.Delete(live);
          }
          File.Move(old, live);
        } catch { }
      }
      throw;
    }
    foreach (var (_, old) in moved) {
      try {
        File.Delete(old);
      } catch { }
    }
  }

  public static bool IsNewer(string remote, string local) {
    AppVersionParts remoteParts = AppVersion.Parse(remote);
    AppVersionParts localParts = AppVersion.Parse(local);
    var remoteNumber = V3(remoteParts.Number);
    var localNumber = V3(localParts.Number);

    // One-time migration from the port's old YYYY.M.patch CalVer to the official
    // MissionPlanner x.y.z base. The first official-version build must update an older
    // CalVer installation even though its numeric major is necessarily smaller.
    bool remoteCalVer = IsLegacyCalVer(remoteNumber);
    bool localCalVer = IsLegacyCalVer(localNumber);
    if (remoteCalVer != localCalVer) {
      return !remoteCalVer;
    }

    int numberComparison = remoteNumber.CompareTo(localNumber);
    if (numberComparison != 0) {
      return numberComparison > 0;
    }

    int dateComparison = string.CompareOrdinal(remoteParts.BuildDate, localParts.BuildDate);
    if (dateComparison != 0) {
      return dateComparison > 0;
    }

    // Git hashes do not have a useful ordering. If the release server and local build have
    // different commits for the same official version/date, install the server's selected
    // release once; after the update their hashes match and it no longer prompts.
    return remoteParts.Hash.Length > 0
        && localParts.Hash.Length > 0
        && !string.Equals(remoteParts.Hash, localParts.Hash, StringComparison.OrdinalIgnoreCase);
  }

  private static (int, int, int) V3(string s) {
    s = (s ?? "").Trim();
    if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase)) {
      s = s.Substring(1);
    }
    int cut = s.IndexOfAny(new[] { '-', '+', ' ' });
    if (cut >= 0) {
      s = s.Substring(0, cut);
    }
    var p = s.Split('.');
    int g(int i) => i < p.Length && int.TryParse(p[i], out var x) ? x : 0;
    return (g(0), g(1), g(2));
  }

  private static bool IsLegacyCalVer((int Major, int Minor, int Patch) version) =>
      version.Major is >= 2000 and <= 2999 && version.Minor is >= 1 and <= 12;

  private static string Sha256File(string path) {
    using var stream = File.OpenRead(path);
    using var sha = SHA256.Create();
    return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
  }

  public static string Rid() {
    string os = OperatingSystem.IsWindows() ? "win"
        : OperatingSystem.IsMacOS() ? "osx"
        : "linux";
    string arch = RuntimeInformation.OSArchitecture switch {
      Architecture.Arm64 => "arm64",
      Architecture.Arm => "arm",
      Architecture.X86 => "x86",
      _ => "x64",
    };
    return $"{os}-{arch}";
  }
}

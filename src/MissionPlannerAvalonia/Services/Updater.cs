using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using MissionPlanner.Utilities;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace MissionPlannerAvalonia.Services;

public static class Updater {
  public const string DefaultOwnerRepo = "sema-aviation/MissionPlanner-Avalonia";
  public const string PagesBaseUrl = "https://sema-aviation.github.io/MissionPlanner-Avalonia";

  public const string PublicKeyBase64 = "A0WFYpVPY1BvbOSpAzmuCTfbV6SR/cw9sUPy4AKZSgg=";

  private const string _skipKey = "update_skip_version";

  private static readonly HttpClient _http = CreateClient();

  private static HttpClient CreateClient() {
    var c = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
    c.DefaultRequestHeaders.UserAgent.ParseAdd("MissionPlannerAvalonia-Updater");
    return c;
  }

  public static Task CheckOnStartupAsync() => AppPaths.IsPackageManaged
      ? Task.CompletedTask
      : RunAsync(silentWhenUpToDate: true, respectSkip: true);

  public static Task CheckNowAsync() => AppPaths.IsPackageManaged
      ? Dialogs.Alert("Update", "This installation is managed by the system package manager. " +
          "Use apt to install updates.")
      : RunAsync(silentWhenUpToDate: false, respectSkip: false);

  private static async Task RunAsync(bool silentWhenUpToDate, bool respectSkip) {
    UpdateEngine engine;
    UpdateEngine.Manifest? m;
    try {
      engine = NewEngine();
      m = await engine.FetchManifestAsync().ConfigureAwait(true);
    } catch (Exception ex) {
      if (!silentWhenUpToDate) {
        await Dialogs.Alert("Update", "Update check failed: " + ex.Message);
      }
      return;
    }

    if (m == null) {
      if (!silentWhenUpToDate) {
        await Dialogs.Alert("Update", "Could not reach the update server.");
      }
      return;
    }

    string local = AppVersion.Number;
    if (!UpdateEngine.IsNewer(m.Version, local)) {
      if (!silentWhenUpToDate) {
        await Dialogs.Alert("Update", $"You are up to date ({local}).");
      }
      return;
    }

    if (respectSkip && Settings.Instance[_skipKey] == m.Version) {
      return;
    }

    while (true) {
      var choice = await Dialogs.Choice("Update available",
          $"Version {m.Version} is available (you have {local}).",
          "Install", "What's new", "Skip this version", "Later");
      if (choice == "What's new") {
        Dialogs.OpenUrl(string.IsNullOrEmpty(m.Notes)
            ? $"https://github.com/{DefaultOwnerRepo}/releases"
            : m.Notes);
        continue;
      }
      if (choice == "Skip this version") {
        Settings.Instance[_skipKey] = m.Version;
        Settings.Instance.Save();
        return;
      }
      if (choice != "Install") {
        return;
      }
      break;
    }

    if (m.Bundle != null && OperatingSystem.IsMacOS()) {
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

  // macOS: download the whole notarized .app zip, verify its hash, then hand off to a detached
  // helper that swaps the entire bundle after we exit and relaunches. Whole-bundle replacement
  // keeps the signature + stapled notarization ticket intact.
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
      RunMacBundleHelper(engine.InstallDir, zip, staging);
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
    byte[] json, sig;
    try {
      json = await _http.GetByteArrayAsync($"{_baseUrl}/{_rid}/manifest.json", ct).ConfigureAwait(false);
      string sigText = await _http.GetStringAsync($"{_baseUrl}/{_rid}/manifest.sig", ct).ConfigureAwait(false);
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
    Directory.CreateDirectory(Path.GetDirectoryName(destZip)!);
    using (var resp = await _http.GetAsync(bundle.Url, HttpCompletionOption.ResponseHeadersRead, ct)
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
      }
    } catch {
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

  public static bool IsNewer(string remote, string local) => V3(remote).CompareTo(V3(local)) > 0;

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

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using LibVLCSharp.Shared;

namespace MissionPlannerAvalonia.Services;

internal static class LibVlcBootstrap {
  private static readonly object _initializeSync = new();
  private static int _configured;
  private static int _initialized;

  public static void Initialize() {
    if (Volatile.Read(ref _initialized) != 0) {
      return;
    }

    lock (_initializeSync) {
      if (_initialized != 0) {
        return;
      }

      ConfigureLinuxResolver();
      if (OperatingSystem.IsMacOS()) {
        MacVlcRuntimePaths runtime = LocateMacRuntime(AppContext.BaseDirectory)
            ?? throw new FileNotFoundException(
                "The bundled macOS VLC runtime is incomplete. Reinstall the matching "
                + "Mission Planner architecture.");
        // libvlc reads this when it creates its first instance and may load modules later,
        // so retain the exact bundled path for the lifetime of the process.
        Environment.SetEnvironmentVariable("VLC_PLUGIN_PATH", runtime.PluginDirectory);
        Environment.SetEnvironmentVariable("VLC_DATA_PATH", runtime.DataDirectory);
        LibVLCSharp.Shared.Core.Initialize(runtime.LibraryDirectory);
      } else {
        LibVLCSharp.Shared.Core.Initialize();
      }
      Volatile.Write(ref _initialized, 1);
    }
  }

  public static LibVLCSharp.Shared.LibVLC CreateInstance(params string[] options) {
    ArgumentNullException.ThrowIfNull(options);
    Initialize();
    if (!OperatingSystem.IsMacOS()) {
      return new LibVLCSharp.Shared.LibVLC(options);
    }

    // The runtime is extracted from an exact SHA-256-pinned VideoLAN image and its complete file
    // manifest is verified at build time. Trust its matching plugins.dat instead of rescanning
    // relocated dylibs: scan mode compares package timestamps and attempts to dlopen every plugin
    // before the foreign host's loader context has been established.
    string[] macOptions = options.Contains("--no-plugins-scan", StringComparer.Ordinal)
        ? options
        : [.. options, "--no-plugins-scan"];
    return new LibVLCSharp.Shared.LibVLC(macOptions);
  }

  internal static MacVlcRuntimePaths? LocateMacRuntime(string baseDirectory) {
    ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
    string libraryDirectory = Path.Combine(baseDirectory, "lib");
    string pluginDirectory = Path.Combine(baseDirectory, "plugins");
    string dataDirectory = Path.Combine(baseDirectory, "share");
    string libVlc = Path.Combine(libraryDirectory, "libvlc.dylib");
    string libVlcCore = Path.Combine(libraryDirectory, "libvlccore.dylib");
    string pluginCache = Path.Combine(pluginDirectory, "plugins.dat");
    string luaDirectory = Path.Combine(dataDirectory, "lua");
    return File.Exists(libVlc) && File.Exists(libVlcCore) && File.Exists(pluginCache)
        && Directory.Exists(luaDirectory)
        ? new MacVlcRuntimePaths(libraryDirectory, pluginDirectory, dataDirectory)
        : null;
  }

  private static void ConfigureLinuxResolver() {
    if (!OperatingSystem.IsLinux() || Interlocked.Exchange(ref _configured, 1) != 0) {
      return;
    }

    try {
      NativeLibrary.SetDllImportResolver(
          typeof(LibVLCSharp.Shared.LibVLC).Assembly, ResolveLinuxLibrary);
    } catch (InvalidOperationException) {
      // A host may have configured a resolver before the video control was created.
    }
  }

  private static IntPtr ResolveLinuxLibrary(
      string libraryName, Assembly assembly, DllImportSearchPath? searchPath) {
    string fileName = Path.GetFileName(libraryName);
    if (fileName.StartsWith("libvlccore", StringComparison.OrdinalIgnoreCase)
        || (!fileName.Equals("libvlc", StringComparison.OrdinalIgnoreCase)
            && !fileName.StartsWith("libvlc.so", StringComparison.OrdinalIgnoreCase))) {
      return IntPtr.Zero;
    }

    foreach (string candidate in LinuxCandidates()) {
      if (NativeLibrary.TryLoad(candidate, out IntPtr handle)) {
        return handle;
      }
    }
    return IntPtr.Zero;
  }

  private static IEnumerable<string> LinuxCandidates() {
    string? configured = Environment.GetEnvironmentVariable("MISSIONPLANNER_LIBVLC_PATH");
    if (!string.IsNullOrWhiteSpace(configured)) {
      yield return Directory.Exists(configured)
          ? Path.Combine(configured, "libvlc.so.5")
          : configured;
    }

    yield return Path.Combine(AppContext.BaseDirectory, "native", "libvlc.so.5");
    yield return "libvlc.so.5";
    yield return "/usr/lib/x86_64-linux-gnu/libvlc.so.5";
    yield return "/lib/x86_64-linux-gnu/libvlc.so.5";
    yield return "/usr/lib/aarch64-linux-gnu/libvlc.so.5";
    yield return "/lib/aarch64-linux-gnu/libvlc.so.5";
  }
}

internal sealed record MacVlcRuntimePaths(
    string LibraryDirectory,
    string PluginDirectory,
    string DataDirectory);

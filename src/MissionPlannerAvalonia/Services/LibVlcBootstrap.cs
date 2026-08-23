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
  private static int _linuxResolverConfigured;
  private static int _macResolverConfigured;
  private static int _initialized;
  private static IntPtr _macVlcCoreHandle;
  private static IntPtr _macVlcHandle;

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
        SetMacEnvironmentVariable("VLC_PLUGIN_PATH", runtime.PluginDirectory);
        SetMacEnvironmentVariable("VLC_DATA_PATH", runtime.DataDirectory);
        PromoteMacLibrariesToGlobalScope(runtime.LibraryDirectory);
        ConfigureMacResolver();
        LibVLCSharp.Shared.Core.Initialize(runtime.LibraryDirectory);
      } else {
        LibVLCSharp.Shared.Core.Initialize();
      }
      Volatile.Write(ref _initialized, 1);
    }
  }

  public static LibVLCSharp.Shared.LibVLC CreateInstance(params string[] options) {
    return CreateInstance(enableDebugLogs: false, options);
  }

  internal static LibVLCSharp.Shared.LibVLC CreateInstance(
      bool enableDebugLogs, params string[] options) {
    ArgumentNullException.ThrowIfNull(options);
    Initialize();
    if (!OperatingSystem.IsMacOS()) {
      return new LibVLCSharp.Shared.LibVLC(enableDebugLogs, options);
    }

    // The runtime is extracted from an exact SHA-256-pinned VideoLAN image and its complete file
    // manifest is verified at build time. Trust its matching plugins.dat instead of rescanning
    // relocated dylibs: scan mode compares package timestamps and attempts to dlopen every plugin
    // before the foreign host's loader context has been established.
    string[] macOptions = options.Contains("--no-plugins-scan", StringComparer.Ordinal)
        ? options
        : [.. options, "--no-plugins-scan"];
    return new LibVLCSharp.Shared.LibVLC(enableDebugLogs, macOptions);
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

  private static void PromoteMacLibrariesToGlobalScope(string libraryDirectory) {
    // LibVLCSharp 3.x opens custom macOS libraries with RTLD_LOCAL. The official VLC app's
    // modules link back to @rpath/libvlccore.dylib, so dyld must be able to reuse the already
    // loaded core image when libvlccore later opens a codec/access/output plugin. Loading both
    // images globally first retains the upstream signed binaries and establishes that scope.
    _macVlcCoreHandle = OpenMacLibrary(
        Path.Combine(libraryDirectory, "libvlccore.dylib"));
    _macVlcHandle = OpenMacLibrary(Path.Combine(libraryDirectory, "libvlc.dylib"));
  }

  private static IntPtr OpenMacLibrary(string path) {
    const int rtldNow = 0x2;
    const int rtldGlobal = 0x8;
    _ = MacDlError();
    IntPtr handle = MacDlopen(path, rtldNow | rtldGlobal);
    if (handle != IntPtr.Zero) {
      return handle;
    }

    string detail = Marshal.PtrToStringUTF8(MacDlError()) ?? "unknown dyld error";
    throw new DllNotFoundException($"Unable to load bundled macOS library '{path}': {detail}");
  }

  private static void SetMacEnvironmentVariable(string name, string value) {
    // CoreCLR normally forwards Environment.SetEnvironmentVariable to libc, but libVLC reads these
    // paths with getenv() during its earliest initialization. Call setenv directly as well, matching
    // LibVLCSharp's Cocoa loader and avoiding runtime-specific managed/native environment caching.
    Environment.SetEnvironmentVariable(name, value);
    if (MacSetEnvironmentVariable(name, value, overwrite: 1) != 0) {
      throw new InvalidOperationException($"Unable to set the native macOS environment variable {name}.");
    }
  }

  private static void ConfigureMacResolver() {
    if (Interlocked.Exchange(ref _macResolverConfigured, 1) != 0) {
      return;
    }

    NativeLibrary.SetDllImportResolver(
        typeof(LibVLCSharp.Shared.LibVLC).Assembly, ResolveMacLibrary);
  }

  private static IntPtr ResolveMacLibrary(
      string libraryName, Assembly assembly, DllImportSearchPath? searchPath) {
    string fileName = Path.GetFileName(libraryName);
    if (fileName.StartsWith("libvlccore", StringComparison.OrdinalIgnoreCase)) {
      return _macVlcCoreHandle;
    }
    if (fileName.Equals("libvlc", StringComparison.OrdinalIgnoreCase)
        || fileName.StartsWith("libvlc.", StringComparison.OrdinalIgnoreCase)) {
      return _macVlcHandle;
    }
    return IntPtr.Zero;
  }

  [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "dlopen")]
  private static extern IntPtr MacDlopen(
      [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int mode);

  [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "dlerror")]
  private static extern IntPtr MacDlError();

  [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "setenv")]
  private static extern int MacSetEnvironmentVariable(
      [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
      [MarshalAs(UnmanagedType.LPUTF8Str)] string value,
      int overwrite);

  private static void ConfigureLinuxResolver() {
    if (!OperatingSystem.IsLinux()
        || Interlocked.Exchange(ref _linuxResolverConfigured, 1) != 0) {
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

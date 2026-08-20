using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using LibVLCSharp.Shared;

namespace MissionPlannerAvalonia.Services;

internal static class LibVlcBootstrap {
  private static int _configured;

  public static void Initialize() {
    ConfigureLinuxResolver();
    LibVLCSharp.Shared.Core.Initialize();
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

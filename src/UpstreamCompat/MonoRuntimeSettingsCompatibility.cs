// MissionPlanner.Utilities.Settings uses the presence of Mono.Runtime only to select a
// user-writable data directory on Unix. The Avalonia port runs on CoreCLR, so inject the
// marker into that upstream assembly without modifying the pinned submodule.
namespace Mono {
  internal sealed class Runtime {
  }
}

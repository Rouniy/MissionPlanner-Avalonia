using System;
using System.IO;
using MissionPlanner.Plugin;
using MissionPlannerAvalonia.TestPlugin.Dependency;

namespace MissionPlannerAvalonia.TestPlugin;

public sealed class LifecyclePlugin : Plugin {
  private readonly object _gate = new();

  public override string Name => FixtureIdentity.Name;

  public override string Version => "1.2.3";

  public override string Author => "MissionPlanner-Avalonia Tests";

  public override bool Init() {
    Write("Init");
    loopratehz = 100;
    return true;
  }

  public override bool Loaded() {
    Write("Loaded");
    Host.RegisterFlightAction("Fixture_Action", _ => Write("Action"));
    Host.RegisterHudOverlay(_ => Write("Hud"));
    Host.ConnectionChanged += () => Write("Connection");
    return true;
  }

  public override bool Loop() {
    Write("Loop");
    loopratehz = 0;
    return true;
  }

  public override bool Exit() {
    Write("Exit");
    return true;
  }

  private void Write(string value) {
    lock (_gate) {
      Directory.CreateDirectory(Host.DataDirectory);
      File.AppendAllText(Path.Combine(Host.DataDirectory, "lifecycle.txt"),
          value + Environment.NewLine);
    }
  }
}

public sealed class FaultingLoopPlugin : Plugin {
  public override string Name => "Faulting Fixture";

  public override string Version => "1.0";

  public override string Author => "MissionPlanner-Avalonia Tests";

  public override bool Init() {
    loopratehz = 100;
    return true;
  }

  public override bool Loaded() => true;

  public override bool Loop() => throw new InvalidOperationException("fixture loop failure");

  public override bool Exit() {
    Directory.CreateDirectory(Host.DataDirectory);
    File.WriteAllText(Path.Combine(Host.DataDirectory, "exited.txt"), "yes");
    return true;
  }
}

public sealed class DecliningPlugin : Plugin {
  public override string Name => "Declining Fixture";

  public override string Version => "1.0";

  public override string Author => "MissionPlanner-Avalonia Tests";

  public override bool Init() => false;

  public override bool Loaded() => throw new InvalidOperationException("must not run");

  public override bool Exit() => true;
}

public sealed class BlockingConditionalPlugin : Plugin {
  public override string Name => "Blocking Fixture";

  public override string Version => "1.0";

  public override string Author => "MissionPlanner-Avalonia Tests";

  public override bool Init() {
    if (File.Exists(Path.Combine(Host.DataDirectory, "block"))) {
      loopratehz = 100;
    }
    return true;
  }

  public override bool Loaded() => true;

  public override bool Loop() {
    File.WriteAllText(Path.Combine(Host.DataDirectory, "started.txt"), "yes");
    System.Threading.Thread.Sleep(TimeSpan.FromSeconds(10));
    return true;
  }

  public override bool Exit() {
    File.WriteAllText(Path.Combine(Host.DataDirectory, "blocking-exited.txt"), "yes");
    return true;
  }
}

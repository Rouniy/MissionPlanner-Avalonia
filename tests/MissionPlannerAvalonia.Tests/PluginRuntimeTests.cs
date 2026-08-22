using System.Collections.Concurrent;
using System.Runtime.Loader;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using MissionPlanner.Plugin;
using MissionPlannerAvalonia.Services;
using MissionPlannerAvalonia.Views;

namespace MissionPlannerAvalonia.Tests;

public sealed class PluginRuntimeTests {
  [Fact]
  public async Task PortableDllRunsOfficialLifecycleAndCleansRegistrations() {
    string root = CreateTempRoot();
    string plugins = Path.Combine(root, "plugins");
    Directory.CreateDirectory(plugins);
    string pluginPath = CopyFixturePlugin(plugins);
    Assert.DoesNotContain(AssemblyLoadContext.Default.Assemblies,
        assembly => assembly.GetName().Name == "MissionPlannerAvalonia.TestPlugin.Dependency");
    var hosts = new ConcurrentDictionary<string, FakePluginHost>();
    var diagnostics = new ConcurrentQueue<string>();
    var runtime = new PluginRuntime(
        [plugins],
        [],
        (_, type) => hosts.GetOrAdd(type.Name,
            name => new FakePluginHost(Path.Combine(root, "data", name))),
        diagnostic: diagnostics.Enqueue);

    try {
      await runtime.RefreshAsync();
      await WaitUntilAsync(() => File.Exists(Path.Combine(
          hosts[nameof(TestPlugin.LifecyclePlugin)].DataDirectory, "lifecycle.txt"))
          && diagnostics.Any(line => line.Contains(
              "disabled after three consecutive failures", StringComparison.Ordinal)));

      PluginFileSnapshot snapshot = Assert.Single(runtime.Snapshot());
      Assert.Equal(pluginPath, snapshot.Path);
      Assert.Equal(PluginFileState.Loaded, snapshot.State);
      Assert.Contains("Lifecycle Fixture", snapshot.Name, StringComparison.Ordinal);
      Assert.Contains("Faulting Fixture", snapshot.Name, StringComparison.Ordinal);
      Assert.Contains("DecliningPlugin: Init returned false", snapshot.Error,
          StringComparison.Ordinal);
      Assert.Contains("fixture loop failure", snapshot.Error, StringComparison.Ordinal);

      FakePluginHost lifecycleHost = hosts[nameof(TestPlugin.LifecyclePlugin)];
      Assert.Equal(1, lifecycleHost.ActionCount);
      Assert.Equal(1, lifecycleHost.HudCount);
      lifecycleHost.InvokeAction("Fixture_Action");
      lifecycleHost.DrawHud();
      lifecycleHost.RaiseConnectionChanged();

      string lifecyclePath = Path.Combine(lifecycleHost.DataDirectory, "lifecycle.txt");
      await WaitUntilAsync(() => File.ReadAllLines(lifecyclePath).Contains("Connection"));
      string[] beforeExit = File.ReadAllLines(lifecyclePath);
      Assert.Equal("Init", beforeExit[0]);
      Assert.Equal("Loaded", beforeExit[1]);
      Assert.Contains("Loop", beforeExit);
      Assert.Contains("Action", beforeExit);
      Assert.Contains("Hud", beforeExit);
      Assert.Contains("Connection", beforeExit);
    } finally {
      await runtime.DisposeAsync();
    }

    FakePluginHost disposedHost = hosts[nameof(TestPlugin.LifecyclePlugin)];
    Assert.Equal(0, disposedHost.ActionCount);
    Assert.Equal(0, disposedHost.HudCount);
    Assert.Equal("Exit", File.ReadAllLines(
        Path.Combine(disposedHost.DataDirectory, "lifecycle.txt"))[^1]);
    Assert.True(File.Exists(Path.Combine(
        hosts[nameof(TestPlugin.FaultingLoopPlugin)].DataDirectory, "exited.txt")));
    Assert.Contains(diagnostics,
        line => line.Contains("disabled after three consecutive failures", StringComparison.Ordinal));
  }

  [Fact]
  public async Task DisabledPluginIsDiscoveredWithoutExecutingCode() {
    string root = CreateTempRoot();
    string plugins = Path.Combine(root, "plugins");
    Directory.CreateDirectory(plugins);
    string path = CopyFixturePlugin(plugins);
    int hostCalls = 0;
    await using var runtime = new PluginRuntime(
        [plugins],
        [Path.GetFileName(path).ToUpperInvariant()],
        (_, type) => {
          Interlocked.Increment(ref hostCalls);
          return new FakePluginHost(Path.Combine(root, type.Name));
        });

    await runtime.RefreshAsync();

    PluginFileSnapshot snapshot = Assert.Single(runtime.Snapshot());
    Assert.False(snapshot.Enabled);
    Assert.Equal(PluginFileState.Disabled, snapshot.State);
    Assert.Equal(0, hostCalls);
  }

  [Fact]
  public async Task InvalidDllProducesVisibleFailureWithoutEscapingRefresh() {
    string root = CreateTempRoot();
    string plugins = Path.Combine(root, "plugins");
    Directory.CreateDirectory(plugins);
    string path = Path.Combine(plugins, "broken.dll");
    await File.WriteAllTextAsync(path, "not a managed assembly");
    await using var runtime = new PluginRuntime(
        [plugins],
        [],
        (_, type) => new FakePluginHost(Path.Combine(root, type.Name)));

    await runtime.RefreshAsync();

    PluginFileSnapshot snapshot = Assert.Single(runtime.Snapshot());
    Assert.Equal(PluginFileState.Failed, snapshot.State);
    Assert.NotEmpty(snapshot.Error);
  }

  [Fact]
  public async Task WritableUserPluginOverridesSameNamedInstalledPlugin() {
    string root = CreateTempRoot();
    string userPlugins = Path.Combine(root, "user");
    string installedPlugins = Path.Combine(root, "installed");
    Directory.CreateDirectory(userPlugins);
    Directory.CreateDirectory(installedPlugins);
    string expected = CopyFixturePlugin(userPlugins);
    CopyFixturePlugin(installedPlugins);
    await using var runtime = new PluginRuntime(
        [userPlugins, installedPlugins],
        [],
        (_, type) => new FakePluginHost(Path.Combine(root, "data", type.Name)));

    await runtime.RefreshAsync();

    PluginFileSnapshot snapshot = Assert.Single(runtime.Snapshot());
    Assert.Equal(expected, snapshot.Path);
    Assert.Equal(PluginFileState.Loaded, snapshot.State);
  }

  [Fact]
  public async Task BlockingLoopCannotHoldRuntimeShutdown() {
    string root = CreateTempRoot();
    string plugins = Path.Combine(root, "plugins");
    Directory.CreateDirectory(plugins);
    CopyFixturePlugin(plugins);
    var diagnostics = new ConcurrentQueue<string>();
    var runtime = new PluginRuntime(
        [plugins],
        [],
        (_, type) => {
          var host = new FakePluginHost(Path.Combine(root, "data", type.Name));
          if (type.Name == nameof(TestPlugin.BlockingConditionalPlugin)) {
            File.WriteAllText(Path.Combine(host.DataDirectory, "block"), "yes");
          }
          return host;
        },
        diagnostic: diagnostics.Enqueue);

    await runtime.RefreshAsync();
    string data = Path.Combine(root, "data", nameof(TestPlugin.BlockingConditionalPlugin));
    await WaitUntilAsync(() => File.Exists(Path.Combine(data, "started.txt")));
    var stopwatch = System.Diagnostics.Stopwatch.StartNew();

    await runtime.DisposeAsync();

    stopwatch.Stop();
    Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), stopwatch.Elapsed.ToString());
    Assert.False(File.Exists(Path.Combine(data, "blocking-exited.txt")));
    Assert.Contains(diagnostics,
        line => line.Contains("Skipped Exit for Blocking Fixture", StringComparison.Ordinal));
  }

  [Fact]
  public void PluginManagerIsExposedThroughOfficialShortcutAndWarningUi() {
    string root = FindRepoRoot();
    string xaml = File.ReadAllText(Path.Combine(
        root, "src/MissionPlannerAvalonia/Views/MainWindow.axaml"));
    string code = File.ReadAllText(Path.Combine(
        root, "src/MissionPlannerAvalonia/Views/MainWindow.axaml.cs"));
    string manager = File.ReadAllText(Path.Combine(
        root, "src/MissionPlannerAvalonia/Views/PluginManagerWindow.axaml"));

    Assert.Contains("Plugin Manager     Ctrl+P", xaml, StringComparison.Ordinal);
    Assert.Contains("case Key.P when ctrl", code, StringComparison.Ordinal);
    Assert.Contains("Only install plugins you trust", manager, StringComparison.Ordinal);
    Assert.Contains("x:Name=\"PluginGrid\"", manager, StringComparison.Ordinal);
    Assert.Contains("x:Name=\"RestartWarning\"", manager, StringComparison.Ordinal);
  }

  [AvaloniaFact]
  public void PluginManagerWindowLoadsItsCompiledAvaloniaControls() {
    var window = new PluginManagerWindow();

    Assert.NotNull(window.FindControl<Avalonia.Controls.DataGrid>("PluginGrid"));
    Assert.NotNull(window.FindControl<Avalonia.Controls.TextBlock>("RestartWarning"));
    window.Close();
  }

  [Fact]
  public void PortableContractRetainsOfficialLifecycleNames() {
    Type plugin = typeof(Plugin);
    Assert.NotNull(plugin.GetMethod(nameof(Plugin.Init)));
    Assert.NotNull(plugin.GetMethod(nameof(Plugin.Loaded)));
    Assert.NotNull(plugin.GetMethod(nameof(Plugin.Loop)));
    Assert.NotNull(plugin.GetMethod(nameof(Plugin.Exit)));
    Assert.NotNull(plugin.GetProperty(nameof(Plugin.NextRun)));
    Assert.NotNull(plugin.GetProperty("loopratehz"));
    Assert.NotNull(typeof(PluginHost).GetProperty("comPort"));
    Assert.NotNull(typeof(PluginHost).GetProperty("cs"));
    Assert.NotNull(typeof(PluginHost).GetProperty("config"));
  }

  private static string CopyFixturePlugin(string directory) {
    string source = Path.Combine(AppContext.BaseDirectory,
        "MissionPlannerAvalonia.TestPlugin.dll");
    string dependency = Path.Combine(AppContext.BaseDirectory,
        "MissionPlannerAvalonia.TestPlugin.Dependency.dll");
    Assert.True(File.Exists(source), "The fixture plugin project output was not copied.");
    Assert.True(File.Exists(dependency), "The fixture plugin dependency output was not copied.");
    string destination = Path.Combine(directory, Path.GetFileName(source));
    File.Copy(source, destination);
    File.Copy(dependency, Path.Combine(directory, Path.GetFileName(dependency)));
    return destination;
  }

  private static async Task WaitUntilAsync(Func<bool> predicate) {
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    while (!predicate()) {
      await Task.Delay(20, timeout.Token);
    }
  }

  private static string CreateTempRoot() {
    string path = Path.Combine(Path.GetTempPath(), "mp-plugin-tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(path);
    return path;
  }

  private static string FindRepoRoot() {
    string? path = AppContext.BaseDirectory;
    while (path != null && !File.Exists(Path.Combine(path, "MissionPlannerAvalonia.slnx"))) {
      path = Directory.GetParent(path)?.FullName;
    }
    return path ?? throw new DirectoryNotFoundException("Repository root not found.");
  }

  private sealed class FakePluginHost : PluginHost, IDisposable {
    private readonly MissionPlanner.MAVLinkInterface _port = new();
    private readonly Dictionary<string, Action<string>> _actions = new(StringComparer.Ordinal);
    private readonly List<Action<HudOverlayContext>> _hud = [];
    private Action? _connectionChanged;

    public FakePluginHost(string dataDirectory) {
      DataDirectory = dataDirectory;
      Directory.CreateDirectory(DataDirectory);
    }

    public int ActionCount => _actions.Count;

    public int HudCount => _hud.Count;

    public override MissionPlanner.MAVLinkInterface comPort => _port;

    public override string DataDirectory { get; }

    public override event Action? ConnectionChanged {
      add => _connectionChanged += value;
      remove => _connectionChanged -= value;
    }

    public override IDisposable RegisterFlightAction(
        string action, Action<string> handler, string? after = null, string? before = null) {
      _actions.Add(action, handler);
      return new Registration(() => _actions.Remove(action));
    }

    public override IDisposable RegisterHudOverlay(Action<HudOverlayContext> painter) {
      _hud.Add(painter);
      return new Registration(() => _hud.Remove(painter));
    }

    public override void PostToUi(Action action) => action();

    public override void Navigate(string screen) {
    }

    public override void Log(string message) {
    }

    public void InvokeAction(string action) => _actions[action](action);

    public void DrawHud() {
      var args = new HudOverlayContext(null!, new Avalonia.Rect(0, 0, 100, 100), 1);
      foreach (Action<HudOverlayContext> painter in _hud.ToArray()) {
        painter(args);
      }
    }

    public void RaiseConnectionChanged() => _connectionChanged?.Invoke();

    public void Dispose() {
      _actions.Clear();
      _hud.Clear();
      _connectionChanged = null;
      _port.Dispose();
    }

    private sealed class Registration(Action dispose) : IDisposable {
      private Action? _dispose = dispose;

      public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
  }
}

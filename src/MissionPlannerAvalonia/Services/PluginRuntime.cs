extern alias LegacyPluginContract;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using MissionPlanner.Plugin;
using LegacyMainV2 = LegacyPluginContract::MissionPlanner.MainV2;
using LegacyPlugin = LegacyPluginContract::MissionPlanner.Plugin.Plugin;
using LegacyPluginHost = LegacyPluginContract::MissionPlanner.Plugin.PluginHost;
using PortablePlugin = MissionPlanner.Plugin.Plugin;

namespace MissionPlannerAvalonia.Services;

internal enum PluginFileState {
  Discovered,
  Disabled,
  Loading,
  Loaded,
  Declined,
  Dependency,
  Failed,
}

internal sealed record PluginFileSnapshot(
    string Path,
    string FileName,
    bool Enabled,
    PluginFileState State,
    string Name,
    string Version,
    string Author,
    string Error);

/// <summary>
/// Loads the portable counterpart of Mission Planner plugins while keeping third-party dependency
/// graphs out of the default AssemblyLoadContext. Plugin code is trusted code, just as it is in the
/// official application; this class provides fault boundaries, not a security sandbox.
/// </summary>
internal sealed class PluginRuntime : IAsyncDisposable {
  private static readonly StringComparer _fileNameComparer = StringComparer.OrdinalIgnoreCase;
  private static readonly StringComparer _pathComparer = OperatingSystem.IsWindows()
      ? StringComparer.OrdinalIgnoreCase
      : StringComparer.Ordinal;

  private readonly object _stateGate = new();
  private readonly string[] _pluginDirectories;
  private readonly Func<string, Type, PluginHost> _hostFactory;
  private readonly Func<Func<bool>, Task<bool>> _loadedInvoker;
  private readonly Action<string>? _diagnostic;
  private readonly SemaphoreSlim _loadGate = new(1, 1);
  private readonly CancellationTokenSource _shutdown = new();
  private readonly Dictionary<string, PluginFileEntry> _files;
  private HashSet<string> _disabled;
  private Task? _scheduler;
  private bool _disposed;

  public PluginRuntime(
      IEnumerable<string> pluginDirectories,
      IEnumerable<string> disabledPluginNames,
      Func<string, Type, PluginHost> hostFactory,
      Func<Func<bool>, Task<bool>>? loadedInvoker = null,
      Action<string>? diagnostic = null) {
    ArgumentNullException.ThrowIfNull(pluginDirectories);
    ArgumentNullException.ThrowIfNull(disabledPluginNames);
    ArgumentNullException.ThrowIfNull(hostFactory);

    _pluginDirectories = pluginDirectories
        .Where(path => !string.IsNullOrWhiteSpace(path))
        .Select(Path.GetFullPath)
        .Distinct(_pathComparer)
        .ToArray();
    _disabled = disabledPluginNames
        .Select(NormalizeFileName)
        .Where(name => name.Length > 0)
        .ToHashSet(_fileNameComparer);
    _hostFactory = hostFactory;
    _loadedInvoker = loadedInvoker ?? (callback => Task.FromResult(callback()));
    _diagnostic = diagnostic;
    _files = new Dictionary<string, PluginFileEntry>(_pathComparer);
  }

  public event Action? Changed;

  public IReadOnlyList<PluginFileSnapshot> Snapshot() {
    lock (_stateGate) {
      return _files.Values
          .Where(file => file.State != PluginFileState.Dependency || file.Error.Length > 0)
          .OrderBy(file => file.FileName, _fileNameComparer)
          .ThenBy(file => file.Path, _pathComparer)
          .Select(file => file.ToSnapshot(!_disabled.Contains(file.FileName)))
          .ToArray();
    }
  }

  public void UpdateDisabled(IEnumerable<string> disabledPluginNames) {
    ArgumentNullException.ThrowIfNull(disabledPluginNames);
    lock (_stateGate) {
      _disabled = disabledPluginNames
          .Select(NormalizeFileName)
          .Where(name => name.Length > 0)
          .ToHashSet(_fileNameComparer);
      foreach (var file in _files.Values.Where(file => file.Plugins.Count == 0)) {
        file.State = _disabled.Contains(file.FileName)
            ? PluginFileState.Disabled
            : PluginFileState.Discovered;
      }
    }
    Changed?.Invoke();
  }

  public async Task RefreshAsync(CancellationToken cancellationToken = default) {
    ObjectDisposedException.ThrowIf(_disposed, this);
    using var linked = CancellationTokenSource.CreateLinkedTokenSource(
        cancellationToken, _shutdown.Token);
    await _loadGate.WaitAsync(linked.Token).ConfigureAwait(false);
    try {
      DiscoverFiles();
      PluginFileEntry[] candidates;
      lock (_stateGate) {
        candidates = _files.Values
            .Where(file => file.Context == null
                && file.State is PluginFileState.Discovered or PluginFileState.Failed
                && !_disabled.Contains(file.FileName))
            .ToArray();
      }

      foreach (var file in candidates) {
        linked.Token.ThrowIfCancellationRequested();
        await LoadFileAsync(file, linked.Token).ConfigureAwait(false);
      }

      lock (_stateGate) {
        if (_scheduler == null && _files.Values.Any(file => file.Plugins.Count > 0)) {
          _scheduler = Task.Run(SchedulerLoopAsync);
        }
      }
    } finally {
      _loadGate.Release();
      Changed?.Invoke();
    }
  }

  private void DiscoverFiles() {
    var selectedByName = new Dictionary<string, string>(_fileNameComparer);
    foreach (string directory in _pluginDirectories) {
      try {
        if (!Directory.Exists(directory)) {
          continue;
        }
        foreach (string path in Directory.EnumerateFiles(
                     directory, "*", SearchOption.TopDirectoryOnly)
                     .Where(path => string.Equals(
                         Path.GetExtension(path), ".dll", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(path => path, _pathComparer)) {
          selectedByName.TryAdd(Path.GetFileName(path), Path.GetFullPath(path));
        }
      } catch (Exception ex) {
        Report($"Cannot enumerate plugin directory {directory}: {ex.Message}");
      }
    }

    lock (_stateGate) {
      foreach ((string fileName, string path) in selectedByName) {
        if (_files.ContainsKey(path)) {
          continue;
        }
        _files[path] = new PluginFileEntry(path, fileName) {
          State = _disabled.Contains(fileName)
              ? PluginFileState.Disabled
              : PluginFileState.Discovered,
        };
      }
    }
  }

  private async Task LoadFileAsync(PluginFileEntry file, CancellationToken cancellationToken) {
    SetState(file, PluginFileState.Loading, "");
    if (IsNativePortableExecutable(file.Path)) {
      SetState(file, PluginFileState.Dependency, "");
      return;
    }
    var context = new PluginLoadContext(file.Path);
    try {
      Assembly assembly = context.LoadFromAssemblyPath(file.Path);
      Type[] types = GetLoadableTypes(assembly, out string loaderErrors)
          .Where(type => !type.IsAbstract && type != typeof(PortablePlugin)
              && typeof(PortablePlugin).IsAssignableFrom(type))
          .OrderBy(type => type.FullName, StringComparer.Ordinal)
          .ToArray();
      if (types.Length == 0) {
        SetState(file, PluginFileState.Dependency, loaderErrors);
        context.Unload();
        return;
      }

      file.Context = context;
      var failures = new List<string>();
      var declined = new List<string>();
      foreach (Type type in types) {
        cancellationToken.ThrowIfCancellationRequested();
        PluginHost? host = null;
        PortablePlugin? plugin = null;
        try {
          plugin = (PortablePlugin?)Activator.CreateInstance(type)
              ?? throw new InvalidOperationException("The plugin constructor returned null.");
          PluginHost nativeHost = _hostFactory(file.Path, type);
          host = plugin is LegacyPlugin
              ? new LegacyPluginHostBridge(nativeHost)
              : nativeHost;
          plugin.Assembly = assembly;
          plugin.FileName = file.FileName;
          plugin.Host = host;
          if (plugin is LegacyPlugin legacyPlugin) {
            legacyPlugin.Assembly = assembly;
            legacyPlugin.FileName = file.FileName;
            legacyPlugin.Host = (LegacyPluginHost)host;
          }

          bool initialized = await Task.Run(plugin.Init, cancellationToken).ConfigureAwait(false);
          if (!initialized) {
            declined.Add($"{type.FullName}: Init returned false");
            DisposeHost(host);
            continue;
          }

          bool loaded = await _loadedInvoker(plugin.Loaded).ConfigureAwait(false);
          if (!loaded) {
            declined.Add($"{type.FullName}: Loaded returned false");
            await ExitPluginAsync(plugin, TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            DisposeHost(host);
            continue;
          }

          string name = ReadMetadata(plugin.Name, type.Name);
          string version = ReadMetadata(plugin.Version, "—");
          string author = ReadMetadata(plugin.Author, "—");
          lock (_stateGate) {
            file.Plugins.Add(new LoadedPlugin(plugin, host, name, version, author));
          }
          Report($"Loaded plugin {name} {version} by {author} from {file.FileName}.");
        } catch (Exception ex) {
          failures.Add($"{type.FullName}: {RootMessage(ex)}");
          if (plugin != null) {
            await ExitPluginAsync(plugin, TimeSpan.FromSeconds(1)).ConfigureAwait(false);
          }
          DisposeHost(host);
        }
      }

      lock (_stateGate) {
        file.Error = string.Join(Environment.NewLine,
            new[] { loaderErrors }
                .Concat(failures)
                .Concat(declined)
                .Where(message => !string.IsNullOrWhiteSpace(message)));
        file.State = file.Plugins.Count > 0
            ? PluginFileState.Loaded
            : failures.Count > 0 ? PluginFileState.Failed : PluginFileState.Declined;
      }
      if (file.Plugins.Count == 0) {
        file.Context = null;
        context.Unload();
      }
    } catch (OperationCanceledException) {
      await CleanupFilePluginsAsync(file).ConfigureAwait(false);
      file.Context = null;
      context.Unload();
      throw;
    } catch (Exception ex) {
      await CleanupFilePluginsAsync(file).ConfigureAwait(false);
      file.Context = null;
      context.Unload();
      SetState(file, PluginFileState.Failed, RootMessage(ex));
      Report($"Failed to load plugin {file.FileName}: {RootMessage(ex)}");
    } finally {
      Changed?.Invoke();
    }
  }

  private async Task SchedulerLoopAsync() {
    using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10));
    try {
      while (await timer.WaitForNextTickAsync(_shutdown.Token).ConfigureAwait(false)) {
        LoadedPlugin[] plugins;
        lock (_stateGate) {
          plugins = _files.Values.SelectMany(file => file.Plugins).ToArray();
        }
        DateTime now = DateTime.UtcNow;
        foreach (LoadedPlugin loaded in plugins) {
          float rate;
          DateTime next;
          try {
            rate = loaded.Plugin.loopratehz;
            next = loaded.Plugin.NextRun;
          } catch (Exception ex) {
            loaded.RecordLoopFailure(RootMessage(ex));
            continue;
          }
          if (!float.IsFinite(rate) || rate <= 0 || loaded.LoopDisabled
              || now < NormalizeUtc(next) || !loaded.TryBeginLoop()) {
            continue;
          }

          double hertz = Math.Clamp(rate, 0.01f, 100f);
          loaded.Plugin.NextRun = now.AddSeconds(1d / hertz);
          loaded.LoopTask = Task.Run(() => RunLoop(loaded));
        }
      }
    } catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) {
    }
  }

  private void RunLoop(LoadedPlugin loaded) {
    try {
      loaded.Plugin.Loop();
      loaded.RecordLoopSuccess();
    } catch (Exception ex) {
      string message = RootMessage(ex);
      loaded.RecordLoopFailure(message);
      Report($"Plugin {loaded.Name} loop failed: {message}");
      if (loaded.LoopDisabled) {
        Report($"Plugin {loaded.Name} loop was disabled after three consecutive failures.");
      }
      Changed?.Invoke();
    } finally {
      loaded.EndLoop();
    }
  }

  public async ValueTask DisposeAsync() {
    if (_disposed) {
      return;
    }
    _disposed = true;
    _shutdown.Cancel();

    bool loadStopped;
    try {
      loadStopped = await _loadGate.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
    } catch (ObjectDisposedException) {
      return;
    }
    if (!loadStopped) {
      Report("Plugin loading did not stop within two seconds; abandoning it during process exit.");
      return;
    }

    Task? scheduler;
    LoadedPlugin[] plugins;
    PluginLoadContext[] contexts;
    lock (_stateGate) {
      scheduler = _scheduler;
      plugins = _files.Values.SelectMany(file => file.Plugins).ToArray();
      contexts = _files.Values.Select(file => file.Context).OfType<PluginLoadContext>().ToArray();
    }

    if (scheduler != null) {
      await AwaitWithin(scheduler, TimeSpan.FromSeconds(1)).ConfigureAwait(false);
    }

    Task[] loops = plugins.Select(plugin => plugin.LoopTask).OfType<Task>().ToArray();
    if (loops.Length > 0) {
      await AwaitWithin(Task.WhenAll(loops), TimeSpan.FromSeconds(2)).ConfigureAwait(false);
    }

    foreach (LoadedPlugin loaded in plugins.Reverse()) {
      if (!loaded.IsRunning) {
        await ExitPluginAsync(loaded.Plugin, TimeSpan.FromSeconds(1)).ConfigureAwait(false);
      } else {
        Report($"Skipped Exit for {loaded.Name}: its Loop callback did not stop in time.");
      }
      DisposeHost(loaded.Host);
    }

    lock (_stateGate) {
      foreach (PluginFileEntry file in _files.Values) {
        file.Plugins.Clear();
        file.Context = null;
      }
    }
    foreach (PluginLoadContext context in contexts) {
      context.Unload();
    }
    _shutdown.Dispose();
    _loadGate.Dispose();
  }

  private async Task CleanupFilePluginsAsync(PluginFileEntry file) {
    LoadedPlugin[] loaded;
    lock (_stateGate) {
      loaded = file.Plugins.ToArray();
      file.Plugins.Clear();
    }
    foreach (LoadedPlugin plugin in loaded.Reverse()) {
      await ExitPluginAsync(plugin.Plugin, TimeSpan.FromSeconds(1)).ConfigureAwait(false);
      DisposeHost(plugin.Host);
    }
  }

  private static async Task ExitPluginAsync(PortablePlugin plugin, TimeSpan timeout) {
    try {
      await Task.Run(plugin.Exit).WaitAsync(timeout).ConfigureAwait(false);
    } catch {
      // A third-party Exit implementation must not hold application shutdown or another plugin.
    }
  }

  private static async Task AwaitWithin(Task task, TimeSpan timeout) {
    try {
      await task.WaitAsync(timeout).ConfigureAwait(false);
    } catch (TimeoutException) {
    } catch (OperationCanceledException) {
    } catch {
    }
  }

  private static void DisposeHost(PluginHost? host) {
    try {
      (host as IDisposable)?.Dispose();
    } catch {
    }
  }

  private static IEnumerable<Type> GetLoadableTypes(Assembly assembly, out string loaderErrors) {
    try {
      loaderErrors = "";
      return assembly.GetTypes();
    } catch (ReflectionTypeLoadException ex) {
      loaderErrors = string.Join(Environment.NewLine,
          ex.LoaderExceptions.Where(error => error != null).Select(error => error!.Message));
      return ex.Types.OfType<Type>();
    }
  }

  private void SetState(PluginFileEntry file, PluginFileState state, string error) {
    lock (_stateGate) {
      file.State = state;
      file.Error = error;
    }
    Changed?.Invoke();
  }

  private void Report(string message) {
    try {
      _diagnostic?.Invoke(message);
    } catch {
    }
  }

  private static DateTime NormalizeUtc(DateTime value) => value.Kind switch {
    DateTimeKind.Utc => value,
    DateTimeKind.Local => value.ToUniversalTime(),
    _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
  };

  private static string NormalizeFileName(string value) =>
      Path.GetFileName(value.Trim()).ToLowerInvariant();

  private static string ReadMetadata(string? value, string fallback) =>
      string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

  private static string RootMessage(Exception exception) {
    while (exception is TargetInvocationException { InnerException: not null }) {
      exception = exception.InnerException;
    }
    return exception.Message;
  }

  private static bool IsNativePortableExecutable(string path) {
    try {
      using var stream = File.OpenRead(path);
      using var reader = new PEReader(stream, PEStreamOptions.LeaveOpen);
      return reader.PEHeaders.PEHeader != null && !reader.HasMetadata;
    } catch {
      return false;
    }
  }

  private sealed class PluginFileEntry(string path, string fileName) {
    public string Path { get; } = path;
    public string FileName { get; } = fileName;
    public PluginFileState State { get; set; }
    public string Error { get; set; } = "";
    public PluginLoadContext? Context { get; set; }
    public List<LoadedPlugin> Plugins { get; } = [];

    public PluginFileSnapshot ToSnapshot(bool enabled) => new(
        Path,
        FileName,
        enabled,
        State,
        string.Join(", ", Plugins.Select(plugin => plugin.Name)),
        string.Join(", ", Plugins.Select(plugin => plugin.Version)),
        string.Join(", ", Plugins.Select(plugin => plugin.Author)),
        string.Join(Environment.NewLine,
            new[] { Error }
                .Concat(Plugins.Select(plugin => plugin.LastError))
                .Where(message => !string.IsNullOrWhiteSpace(message))));
  }

  private sealed class LoadedPlugin(
      PortablePlugin plugin,
      PluginHost host,
      string name,
      string version,
      string author) {
    private int _running;
    private int _consecutiveFailures;

    public PortablePlugin Plugin { get; } = plugin;
    public PluginHost Host { get; } = host;
    public string Name { get; } = name;
    public string Version { get; } = version;
    public string Author { get; } = author;
    public string LastError { get; private set; } = "";
    public bool LoopDisabled { get; private set; }
    public Task? LoopTask { get; set; }
    public bool IsRunning => Volatile.Read(ref _running) != 0;

    public bool TryBeginLoop() => Interlocked.CompareExchange(ref _running, 1, 0) == 0;

    public void EndLoop() => Volatile.Write(ref _running, 0);

    public void RecordLoopSuccess() {
      _consecutiveFailures = 0;
      LastError = "";
    }

    public void RecordLoopFailure(string message) {
      LastError = message;
      if (++_consecutiveFailures >= 3) {
        LoopDisabled = true;
      }
    }
  }

  private sealed class LegacyPluginHostBridge : LegacyPluginHost, IDisposable {
    private PluginHost? _inner;
    private readonly MissionPlanner.MAVLinkInterface _lastPort;

    public LegacyPluginHostBridge(PluginHost inner) {
      _inner = inner;
      _lastPort = inner.comPort;
      inner.ConnectionChanged += OnConnectionChanged;
      SynchronizeMainV2();
    }

    private PluginHost Inner => _inner
        ?? throw new ObjectDisposedException(nameof(LegacyPluginHostBridge));

    public override MissionPlanner.MAVLinkInterface comPort => Inner.comPort;

    public override IReadOnlyList<MissionPlanner.MAVLinkInterface> comPorts => Inner.comPorts;

    public override MissionPlanner.CurrentState cs => Inner.cs;

    public override MissionPlanner.Utilities.Settings config => Inner.config;

    public override string DataDirectory => Inner.DataDirectory;

    public override event Action? ConnectionChanged {
      add => Inner.ConnectionChanged += value;
      remove {
        if (_inner != null) {
          _inner.ConnectionChanged -= value;
        }
      }
    }

    public override IDisposable RegisterFlightAction(
        string action, Action<string> handler, string? after = null, string? before = null) =>
        Inner.RegisterFlightAction(action, handler, after, before);

    public override IDisposable RegisterHudOverlay(Action<HudOverlayContext> painter) =>
        Inner.RegisterHudOverlay(painter);

    public override void PostToUi(Action action) => Inner.PostToUi(action);

    public override void Navigate(string screen) => Inner.Navigate(screen);

    public override void Log(string message) => Inner.Log(message);

    public override int AddWPtoList(
        MAVLink.MAV_CMD cmd,
        double p1,
        double p2,
        double p3,
        double p4,
        double x,
        double y,
        double z,
        object? tag = null) => Inner.AddWPtoList(cmd, p1, p2, p3, p4, x, y, z, tag);

    public override void InsertWP(
        int idx,
        MAVLink.MAV_CMD cmd,
        double p1,
        double p2,
        double p3,
        double p4,
        double x,
        double y,
        double z,
        object? tag = null) => Inner.InsertWP(idx, cmd, p1, p2, p3, p4, x, y, z, tag);

    public override void GetWPs() => Inner.GetWPs();

    public void Dispose() {
      PluginHost? inner = Interlocked.Exchange(ref _inner, null);
      if (inner == null) {
        return;
      }
      inner.ConnectionChanged -= OnConnectionChanged;
      DisposeHost(inner);
    }

    private void OnConnectionChanged() {
      SynchronizeMainV2();
      ProcessConnectionChanged();
      ProcessDeviceChanged(LegacyMainV2.WM_DEVICECHANGE_enum.DBT_DEVNODES_CHANGED);
      LegacyMainV2.instance.ProcessDeviceChanged(
          LegacyMainV2.WM_DEVICECHANGE_enum.DBT_DEVNODES_CHANGED);
    }

    private void SynchronizeMainV2() {
      LegacyMainV2.ComPortProvider = () => _inner?.comPort ?? _lastPort;
      LegacyMainV2.Comports = [.. (_inner?.comPorts ?? [_lastPort]).Distinct()];
    }
  }

  private sealed class PluginLoadContext : AssemblyLoadContext {
    private readonly AssemblyDependencyResolver _resolver;
    private readonly string _pluginDirectory;

    public PluginLoadContext(string pluginPath)
        : base($"MissionPlanner plugin: {Path.GetFileName(pluginPath)}", isCollectible: true) {
      _resolver = new AssemblyDependencyResolver(pluginPath);
      _pluginDirectory = Path.GetDirectoryName(pluginPath)!;
    }

    protected override Assembly? Load(AssemblyName assemblyName) {
      if (string.Equals(assemblyName.Name, typeof(LegacyPlugin).Assembly.GetName().Name,
              StringComparison.OrdinalIgnoreCase)) {
        return typeof(LegacyPlugin).Assembly;
      }
      Assembly? shared = Default.Assemblies.FirstOrDefault(assembly =>
          AssemblyName.ReferenceMatchesDefinition(assembly.GetName(), assemblyName));
      if (shared != null) {
        return shared;
      }
      string? path = _resolver.ResolveAssemblyToPath(assemblyName);
      if (path == null && !string.IsNullOrWhiteSpace(assemblyName.Name)) {
        string local = Path.Combine(_pluginDirectory, assemblyName.Name + ".dll");
        if (File.Exists(local)) {
          path = local;
        }
      }
      return path == null ? null : LoadFromAssemblyPath(path);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName) {
      string? path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
      if (path == null) {
        string[] candidates = OperatingSystem.IsWindows()
            ? [unmanagedDllName, unmanagedDllName + ".dll"]
            : OperatingSystem.IsMacOS()
                ? [unmanagedDllName, "lib" + unmanagedDllName + ".dylib",
                    unmanagedDllName + ".dylib"]
                : [unmanagedDllName, "lib" + unmanagedDllName + ".so", unmanagedDllName + ".so"];
        path = candidates.Select(name => Path.Combine(_pluginDirectory, name))
            .FirstOrDefault(File.Exists);
      }
      return path == null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }
  }
}

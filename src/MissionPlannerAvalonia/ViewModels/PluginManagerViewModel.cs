using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlannerAvalonia.Services;

namespace MissionPlannerAvalonia.ViewModels;

public partial class PluginManagerViewModel : ViewModelBase, IDisposable {
  public ObservableCollection<PluginListItem> Entries { get; } = [];

  public string PluginDirectory => PluginService.UserPluginDirectory;

  [ObservableProperty]
  [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
  [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
  private bool _busy;

  [ObservableProperty]
  private bool _restartRequired;

  [ObservableProperty]
  private string _status = "Portable plugins execute with the same access as Mission Planner.";

  public PluginManagerViewModel() {
    PluginService.Changed += OnPluginsChanged;
    ReplaceSnapshot(preserveChoices: false);
  }

  [RelayCommand(CanExecute = nameof(NotBusy))]
  private async Task Refresh() {
    Busy = true;
    Status = "Scanning plugin directories…";
    try {
      await PluginService.RefreshAsync();
      ReplaceSnapshot(preserveChoices: true);
      int loaded = Entries.Count(entry => entry.State == PluginFileState.Loaded);
      Status = Entries.Count == 0
          ? "No portable plugin DLLs were found."
          : $"Found {Entries.Count} plugin file(s); {loaded} loaded.";
    } catch (Exception ex) {
      Status = "Plugin scan failed: " + ex.Message;
    } finally {
      Busy = false;
    }
  }

  [RelayCommand(CanExecute = nameof(NotBusy))]
  private void Save() {
    PluginService.SaveDisabled(Entries.Where(entry => !entry.Enabled)
        .Select(entry => entry.FileName));
    RestartRequired = PluginService.RestartRequired;
    ReplaceSnapshot(preserveChoices: false);
    Status = RestartRequired
        ? "Plugin enable/disable choices were saved. Restart is required for loaded plugins."
        : "Plugin choices are unchanged.";
  }

  [RelayCommand]
  private void OpenFolder() {
    try {
      System.IO.Directory.CreateDirectory(PluginDirectory);
      if (OperatingSystem.IsLinux()) {
        Process.Start("xdg-open", PluginDirectory);
      } else if (OperatingSystem.IsMacOS()) {
        Process.Start("open", PluginDirectory);
      } else {
        Process.Start(new ProcessStartInfo(PluginDirectory) { UseShellExecute = true });
      }
    } catch (Exception ex) {
      Status = "Could not open the plugin directory: " + ex.Message;
    }
  }

  public void Dispose() => PluginService.Changed -= OnPluginsChanged;

  private bool NotBusy() => !Busy;

  private void OnPluginsChanged() => Dispatcher.UIThread.Post(() => {
    ReplaceSnapshot(preserveChoices: true);
    RestartRequired = PluginService.RestartRequired;
  });

  private void ReplaceSnapshot(bool preserveChoices) {
    Dictionary<string, bool> choices = preserveChoices
        ? Entries.GroupBy(entry => entry.FileName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Enabled,
                StringComparer.OrdinalIgnoreCase)
        : new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
    PluginFileSnapshot[] snapshot = PluginService.Snapshot().ToArray();
    Entries.Clear();
    foreach (PluginFileSnapshot file in snapshot) {
      bool enabled = choices.TryGetValue(file.FileName, out bool choice) ? choice : file.Enabled;
      Entries.Add(new PluginListItem(file, enabled));
    }
    RestartRequired = PluginService.RestartRequired;
  }
}

public partial class PluginListItem : ObservableObject {
  internal PluginListItem(PluginFileSnapshot snapshot, bool enabled) {
    Path = snapshot.Path;
    FileName = snapshot.FileName;
    State = snapshot.State;
    Name = snapshot.Name;
    Version = snapshot.Version;
    Author = snapshot.Author;
    Error = snapshot.Error;
    _enabled = enabled;
  }

  public string Path { get; }

  public string FileName { get; }

  internal PluginFileState State { get; }

  public string Name { get; }

  public string Version { get; }

  public string Author { get; }

  public string Error { get; }

  public string StateText => State switch {
    PluginFileState.Discovered => "Ready",
    PluginFileState.Disabled => "Disabled",
    PluginFileState.Loading => "Loading",
    PluginFileState.Loaded when !Enabled => "Loaded; restart to disable",
    PluginFileState.Loaded => "Loaded",
    PluginFileState.Declined => "Declined",
    PluginFileState.Dependency => "Dependency",
    PluginFileState.Failed => "Failed",
    _ => State.ToString(),
  };

  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(StateText))]
  private bool _enabled;
}

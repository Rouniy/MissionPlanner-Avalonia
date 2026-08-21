using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlannerAvalonia.Services;

namespace MissionPlannerAvalonia.ViewModels.GCSViews.ConfigurationView;

public partial class CustomMissionCommandRow : ObservableObject {
  public CustomMissionCommandRow(MissionCommandDefinition definition) {
    Id = definition.Id;
    Name = definition.Name;
    var labels = definition.ParameterLabels.Concat(Enumerable.Repeat("", 7)).Take(7).ToArray();
    P1 = labels[0];
    P2 = labels[1];
    P3 = labels[2];
    P4 = labels[3];
    P5 = labels[4];
    P6 = labels[5];
    P7 = labels[6];
  }

  [ObservableProperty] private ushort _id;
  [ObservableProperty] private string _name = "";
  [ObservableProperty] private string _p1 = "";
  [ObservableProperty] private string _p2 = "";
  [ObservableProperty] private string _p3 = "";
  [ObservableProperty] private string _p4 = "";
  [ObservableProperty] private string _p5 = "Lat";
  [ObservableProperty] private string _p6 = "Lon";
  [ObservableProperty] private string _p7 = "Alt";

  public MissionCommandDefinition ToDefinition() =>
      new(Id, Name.Trim(), new[] { P1, P2, P3, P4, P5, P6, P7 });
}

public partial class ConfigMavCommandViewModel : ViewModelBase {
  public ObservableCollection<CustomMissionCommandRow> Commands { get; } = new();

  [ObservableProperty] private CustomMissionCommandRow? _selectedCommand;
  [ObservableProperty]
  private string _status =
      "Add commands that are missing from the MAVLink enum, or override parameter labels for known commands.";

  public ConfigMavCommandViewModel() {
    Reload();
  }

  [RelayCommand]
  private async Task Add() {
    var input = await Dialogs.InputBox("Add Mission Command", "MAV_CMD numeric ID (0..65535)");
    if (!ushort.TryParse(input, out var id)) {
      if (input != null) {
        Status = "The command ID must be an integer from 0 through 65535.";
      }
      return;
    }
    if (Commands.Any(row => row.Id == id)) {
      Status = $"Command ID {id} is already in the custom list.";
      return;
    }

    string name;
    if (Enum.IsDefined(typeof(MAVLink.MAV_CMD), id)) {
      name = ((MAVLink.MAV_CMD)id).ToString();
    } else {
      var entered = await Dialogs.InputBox(
          "Add Mission Command", "Command name (letters, digits and underscores)", "NEW_COMMAND");
      if (string.IsNullOrWhiteSpace(entered)) {
        return;
      }
      name = entered.Trim().ToUpperInvariant();
    }
    if (Commands.Any(row => string.Equals(row.Name, name, StringComparison.OrdinalIgnoreCase))) {
      Status = $"Command name '{name}' is already in the custom list.";
      return;
    }

    var row = new CustomMissionCommandRow(
        new MissionCommandDefinition(id, name, new[] { "", "", "", "", "Lat", "Lon", "Alt" }));
    Commands.Add(row);
    SelectedCommand = row;
    Status = $"Added {name}; edit its labels and press Save.";
  }

  [RelayCommand]
  private void Remove() {
    if (SelectedCommand == null) {
      return;
    }
    Commands.Remove(SelectedCommand);
    SelectedCommand = null;
    Status = "Removed from the editor; press Save to persist the change.";
  }

  [RelayCommand]
  private void Save() {
    try {
      MissionCommandCatalog.Save(Commands.Select(row => row.ToDefinition()));
      WpRow.RefreshCommandCatalog();
      Status = $"Saved {Commands.Count} custom command definitions. The Flight Planner list is updated.";
    } catch (Exception ex) {
      Status = "Cannot save: " + ex.Message;
    }
  }

  [RelayCommand]
  private void Reload() {
    Commands.Clear();
    foreach (var definition in MissionCommandCatalog.LoadDefinitions()) {
      Commands.Add(new CustomMissionCommandRow(definition));
    }
    Status = $"Loaded {Commands.Count} custom command definitions.";
  }
}

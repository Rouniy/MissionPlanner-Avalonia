using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner.Warnings;

namespace MissionPlannerAvalonia.ViewModels.GCSViews.ConfigurationView;

public partial class WarningManagerViewModel : ViewModelBase {
  public ObservableCollection<WarningRuleViewModel> Rules { get; } = new();

  [ObservableProperty]
  private string _status = "";

  public WarningManagerViewModel() {
    CustomWarning.defaultsrc = AppState.comPort.MAV.cs;
    Rebuild();
  }

  [RelayCommand]
  private void Add() {
    var warning = CreateWarning();
    lock (WarningEngine.warnings) {
      WarningEngine.warnings.Add(warning);
    }
    Rebuild();
  }

  [RelayCommand]
  private void Save() {
    WarningEngine.SaveConfig();
    Status = $"Saved {Rules.Count} warning condition(s).";
  }

  internal void AddChild(CustomWarning parent) {
    if (parent.Child == null) {
      parent.Child = CreateWarning();
    }
    Rebuild();
  }

  internal void Remove(CustomWarning target) {
    lock (WarningEngine.warnings) {
      if (!WarningEngine.warnings.Remove(target)) {
        foreach (var root in WarningEngine.warnings) {
          if (RemoveChild(root, target)) {
            break;
          }
        }
      }
    }
    Rebuild();
  }

  private static CustomWarning CreateWarning() {
    var warning = new CustomWarning();
    var source = warning.GetOptions().FirstOrDefault();
    if (source != null) {
      warning.SetField(source);
    }
    return warning;
  }

  private static bool RemoveChild(CustomWarning parent, CustomWarning target) {
    if (parent.Child == target) {
      parent.Child = target.Child;
      return true;
    }
    return parent.Child != null && RemoveChild(parent.Child, target);
  }

  private void Rebuild() {
    Rules.Clear();
    lock (WarningEngine.warnings) {
      foreach (var warning in WarningEngine.warnings) {
        AddRows(warning, 0);
      }
    }
    Status = $"{Rules.Count} warning condition(s). Changes take effect immediately; save to persist.";
  }

  private void AddRows(CustomWarning warning, int depth) {
    Rules.Add(new WarningRuleViewModel(this, warning, depth));
    if (warning.Child != null) {
      AddRows(warning.Child, depth + 1);
    }
  }
}

public partial class WarningRuleViewModel : ObservableObject {
  private readonly WarningManagerViewModel _owner;
  private readonly CustomWarning _warning;

  public WarningRuleViewModel(WarningManagerViewModel owner, CustomWarning warning, int depth) {
    _owner = owner;
    _warning = warning;
    DepthLabel = depth == 0 ? "IF" : "AND";
    Margin = new Thickness(depth * 20, 0, 0, 0);

    try {
      if (!string.IsNullOrWhiteSpace(warning.Name)) {
        warning.SetField(warning.Name);
      }
    } catch {
      var source = SourceOptions.FirstOrDefault();
      if (source != null) {
        warning.SetField(source);
      }
    }
  }

  public IReadOnlyList<string> SourceOptions { get; } = GetSourceOptions();
  public IReadOnlyList<CustomWarning.Conditional> ConditionOptions { get; } =
      Enum.GetValues<CustomWarning.Conditional>();
  public IReadOnlyList<CustomWarning.WarningType> TypeOptions { get; } =
      Enum.GetValues<CustomWarning.WarningType>();
  public IReadOnlyList<string> ColorOptions { get; } =
      Enum.GetNames<CustomWarning.WarningColors>();

  public string DepthLabel { get; }
  public Thickness Margin { get; }

  public string Source {
    get => _warning.Name;
    set {
      if (value == _warning.Name || string.IsNullOrWhiteSpace(value)) {
        return;
      }
      _warning.SetField(value);
      OnPropertyChanged();
    }
  }

  public CustomWarning.Conditional Condition {
    get => _warning.ConditionType;
    set {
      if (value == _warning.ConditionType) {
        return;
      }
      _warning.ConditionType = value;
      OnPropertyChanged();
    }
  }

  public double Threshold {
    get => _warning.Warning;
    set {
      if (value.Equals(_warning.Warning)) {
        return;
      }
      _warning.Warning = value;
      OnPropertyChanged();
    }
  }

  public CustomWarning.WarningType Type {
    get => _warning.type;
    set {
      if (value == _warning.type) {
        return;
      }
      _warning.type = value;
      OnPropertyChanged();
    }
  }

  public string Color {
    get => _warning.color ?? "NoColor";
    set {
      if (value == _warning.color) {
        return;
      }
      _warning.color = value;
      OnPropertyChanged();
    }
  }

  public int RepeatSeconds {
    get => _warning.RepeatTime;
    set {
      if (value == _warning.RepeatTime) {
        return;
      }
      _warning.RepeatTime = Math.Max(0, value);
      OnPropertyChanged();
    }
  }

  public string Text {
    get => _warning.Text;
    set {
      if (value == _warning.Text) {
        return;
      }
      _warning.Text = value;
      OnPropertyChanged();
    }
  }

  [RelayCommand]
  private void AddChild() => _owner.AddChild(_warning);

  [RelayCommand]
  private void Remove() => _owner.Remove(_warning);

  private static string[] GetSourceOptions() {
    CustomWarning.defaultsrc = AppState.comPort.MAV.cs;
    return new CustomWarning().GetOptions().OrderBy(name => name).ToArray();
  }
}

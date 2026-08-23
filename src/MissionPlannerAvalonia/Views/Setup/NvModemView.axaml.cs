using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using MissionPlannerAvalonia.Services;
using MissionPlannerAvalonia.ViewModels.Setup;

namespace MissionPlannerAvalonia.Views.Setup;

public partial class NvModemView : UserControl {
  private static readonly FilePickerFileType ParamFiles = new("Mission Planner parameters") {
    Patterns = ["*.param", "*.parm", "*.txt"],
  };

  public NvModemView() {
    InitializeComponent();
    this.FindControl<Button>("LoadParametersButton")!.Click += LoadParameters;
    this.FindControl<Button>("SaveParametersButton")!.Click += SaveParameters;
    this.FindControl<DataGrid>("ParametersGrid")!.BeginningEdit += (_, args) => {
      if (args.Row.DataContext is NvModemParameterRow { IsReadOnly: true }) {
        args.Cancel = true;
      }
    };
  }

  private NvModemViewModel? ViewModel => DataContext as NvModemViewModel;

  private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

  private async void LoadParameters(object? sender, RoutedEventArgs e) {
    TopLevel? top = TopLevel.GetTopLevel(this);
    if (top == null || ViewModel == null) {
      return;
    }

    var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions {
      Title = "Load NV modem parameters",
      AllowMultiple = false,
      FileTypeFilter = [ParamFiles, new FilePickerFileType("All files") { Patterns = ["*"] }],
    });
    string? path = files.FirstOrDefault()?.TryGetLocalPath();
    if (path == null) {
      return;
    }

    try {
      ViewModel.ImportParameterFile(await File.ReadAllTextAsync(path));
    } catch (Exception ex) {
      await Dialogs.Alert("Load NV modem parameters", ex.Message);
    }
  }

  private async void SaveParameters(object? sender, RoutedEventArgs e) {
    TopLevel? top = TopLevel.GetTopLevel(this);
    if (top == null || ViewModel == null) {
      return;
    }

    var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions {
      Title = "Save NV modem parameters",
      SuggestedFileName = ViewModel.SuggestedParameterFileName(),
      DefaultExtension = "param",
      FileTypeChoices = [ParamFiles],
    });
    string? path = file?.TryGetLocalPath();
    if (path == null || !await Dialogs.ConfirmDangerous(
            "Export NV modem parameters",
            "The export includes readable encryption key bytes and network settings. "
            + "Save it only to a trusted location and review it before sharing.",
            "EXPORT PARAMETERS")) {
      return;
    }

    try {
      await File.WriteAllTextAsync(path, ViewModel.ExportParameterFile());
    } catch (Exception ex) {
      await Dialogs.Alert("Save NV modem parameters", ex.Message);
    }
  }
}

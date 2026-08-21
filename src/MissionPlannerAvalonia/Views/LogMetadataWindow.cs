using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using MissionPlannerAvalonia.Services;

namespace MissionPlannerAvalonia.Views;

internal sealed class LogMetadataWindow : Window {
  private LogMetadataWindow(string title, DataGrid table, Control footer) {
    Title = title;
    Width = 760;
    Height = 560;
    MinWidth = 520;
    MinHeight = 320;
    WindowStartupLocation = WindowStartupLocation.CenterOwner;
    Content = new Grid {
      Margin = new Thickness(12),
      RowDefinitions = new RowDefinitions("*,Auto"),
      RowSpacing = 8,
      Children = { table, footer },
    };
    Grid.SetRow(footer, 1);
  }

  internal static void ShowMessages(Window owner, IReadOnlyList<DataFlashMessage> messages) {
    var table = Table(messages);
    table.Columns.Add(TextColumn("Time (s)", nameof(DataFlashMessage.TimeText), 120));
    table.Columns.Add(TextColumn("Message", nameof(DataFlashMessage.Message), null));
    var close = new Button { Content = "Close", HorizontalAlignment = HorizontalAlignment.Right };
    var window = new LogMetadataWindow($"Log Messages ({messages.Count})", table, close);
    close.Click += (_, _) => window.Close();
    window.Show(owner);
  }

  internal static void ShowParameters(
      Window owner, IReadOnlyList<DataFlashParameter> parameters, string suggestedName) {
    var table = Table(parameters);
    table.Columns.Add(TextColumn("Parameter", nameof(DataFlashParameter.Name), null));
    table.Columns.Add(TextColumn("Value", nameof(DataFlashParameter.Value), 180));
    table.Columns.Add(TextColumn("Default", nameof(DataFlashParameter.DefaultValue), 180));
    var save = new Button { Content = "Save .param…" };
    var close = new Button { Content = "Close" };
    var buttons = new StackPanel {
      Orientation = Orientation.Horizontal,
      Spacing = 8,
      HorizontalAlignment = HorizontalAlignment.Right,
      Children = { save, close },
    };
    var window = new LogMetadataWindow($"Log Parameters ({parameters.Count})", table, buttons);
    close.Click += (_, _) => window.Close();
    save.Click += async (_, _) => {
      var file = await window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions {
        Title = "Save parameters from log",
        SuggestedFileName = suggestedName,
        DefaultExtension = "param",
        FileTypeChoices = [
          new FilePickerFileType("Parameter file") { Patterns = ["*.param", "*.parm"] },
        ],
      });
      if (file?.TryGetLocalPath() is { } path) {
        DataFlashLog.ExportParameters(parameters, path);
      }
    };
    window.Show(owner);
  }

  private static DataGrid Table<T>(IReadOnlyList<T> rows) => new() {
    ItemsSource = rows,
    AutoGenerateColumns = false,
    IsReadOnly = true,
    CanUserSortColumns = true,
    CanUserResizeColumns = true,
    GridLinesVisibility = DataGridGridLinesVisibility.All,
  };

  private static DataGridTextColumn TextColumn(string header, string property, double? width) =>
      new() {
        Header = header,
        Binding = new Binding(property),
        IsReadOnly = true,
        Width = width.HasValue
            ? new DataGridLength(width.Value)
            : new DataGridLength(1, DataGridLengthUnitType.Star),
      };
}

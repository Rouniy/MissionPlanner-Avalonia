using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using MissionPlannerAvalonia.ViewModels;

namespace MissionPlannerAvalonia.Views;

internal sealed class ParamCompareWindow : Window {
  private ParamCompareWindow(IReadOnlyList<ParamComparisonRow> rows) {
    Title = "Compare Parameters";
    Width = 720;
    Height = 560;
    MinWidth = 520;
    MinHeight = 360;
    WindowStartupLocation = WindowStartupLocation.CenterOwner;

    var grid = new DataGrid {
      ItemsSource = rows,
      AutoGenerateColumns = false,
      CanUserSortColumns = true,
      CanUserResizeColumns = true,
      GridLinesVisibility = DataGridGridLinesVisibility.All,
      IsReadOnly = false,
    };
    grid.Columns.Add(new DataGridCheckBoxColumn {
      Header = "Use",
      Binding = new Binding(nameof(ParamComparisonRow.Use)) { Mode = BindingMode.TwoWay },
      Width = new DataGridLength(60),
    });
    grid.Columns.Add(new DataGridTextColumn {
      Header = "Parameter",
      Binding = new Binding(nameof(ParamComparisonRow.Name)),
      IsReadOnly = true,
      Width = new DataGridLength(1, DataGridLengthUnitType.Star),
    });
    grid.Columns.Add(new DataGridTextColumn {
      Header = "Current",
      Binding = new Binding(nameof(ParamComparisonRow.CurrentText)),
      IsReadOnly = true,
      Width = new DataGridLength(160),
    });
    grid.Columns.Add(new DataGridTextColumn {
      Header = "File",
      Binding = new Binding(nameof(ParamComparisonRow.FileText)),
      IsReadOnly = true,
      Width = new DataGridLength(160),
    });

    var toggleAll = new CheckBox { Content = "Use all", IsChecked = true };
    toggleAll.IsCheckedChanged += (_, _) => {
      bool use = toggleAll.IsChecked == true;
      foreach (var row in rows) {
        row.Use = use;
      }
    };
    var cancel = new Button { Content = "Cancel" };
    var stage = new Button { Content = "Stage selected", IsDefault = true };
    cancel.Click += (_, _) => Close(false);
    stage.Click += (_, _) => Close(true);

    Content = new Grid {
      Margin = new Thickness(12),
      RowDefinitions = new RowDefinitions("Auto,*,Auto"),
      RowSpacing = 10,
      Children = {
        new TextBlock {
          Text = "Choose which file values to stage. Nothing is written to the vehicle until Write Params is used.",
          TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        },
        grid,
        new Grid {
          ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
          ColumnSpacing = 8,
          Children = { toggleAll, cancel, stage },
        },
      },
    };
    Grid.SetRow(grid, 1);
    Grid.SetRow((Content as Grid)!.Children[2], 2);
    Grid.SetColumn(cancel, 1);
    Grid.SetColumn(stage, 2);
  }

  internal static Task<bool> ShowAsync(
      Window owner, IReadOnlyList<ParamComparisonRow> rows) =>
      new ParamCompareWindow(rows).ShowDialog<bool>(owner);
}

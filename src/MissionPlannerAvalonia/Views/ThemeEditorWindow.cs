using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MissionPlannerAvalonia.Services;

namespace MissionPlannerAvalonia.Views;

internal sealed class ThemeEditorWindow : Window {
  private readonly Dictionary<string, TextBox> _editors = new();

  private ThemeEditorWindow() {
    Title = "Custom Theme";
    Width = 520;
    Height = 650;
    MinWidth = 440;
    MinHeight = 440;
    WindowStartupLocation = WindowStartupLocation.CenterOwner;

    var values = ThemeService.EditableValues();
    var fields = new StackPanel { Spacing = 6 };
    foreach (var (key, label, fallback) in ThemeService.EditableColors) {
      var box = new TextBox { Text = values.TryGetValue(key, out string? value) ? value : fallback };
      _editors[key] = box;
      fields.Children.Add(new Grid {
        ColumnDefinitions = new ColumnDefinitions("180,*"),
        ColumnSpacing = 8,
        Children = {
          new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center },
          box,
        },
      });
      Grid.SetColumn(box, 1);
    }

    var status = new TextBlock { Foreground = Brushes.OrangeRed, TextWrapping = TextWrapping.Wrap };
    var cancel = new Button { Content = "Cancel", MinWidth = 90, IsCancel = true };
    var apply = new Button { Content = "Save and Apply", MinWidth = 120, IsDefault = true };
    var buttons = new StackPanel {
      Orientation = Orientation.Horizontal,
      HorizontalAlignment = HorizontalAlignment.Right,
      Spacing = 8,
      Children = { cancel, apply },
    };
    cancel.Click += (_, _) => Close(false);
    apply.Click += (_, _) => {
      var entered = new Dictionary<string, string>();
      foreach (var pair in _editors) {
        entered[pair.Key] = pair.Value.Text ?? "";
      }
      if (!ThemeService.SaveCustom(entered, out string error)) {
        status.Text = error;
        return;
      }
      Close(true);
    };

    Content = new Grid {
      Margin = new Thickness(16),
      RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto"),
      RowSpacing = 10,
      Children = {
        new TextBlock {
          Text = "Edit portable Avalonia palette colors. Alpha-prefixed #AARRGGBB values are supported.",
          TextWrapping = TextWrapping.Wrap,
        },
        new ScrollViewer { Content = fields },
        status,
        buttons,
      },
    };
    var root = (Grid)Content;
    Grid.SetRow(root.Children[1], 1);
    Grid.SetRow(status, 2);
    Grid.SetRow(buttons, 3);
  }

  internal static System.Threading.Tasks.Task<bool> ShowAsync(Window owner) =>
      new ThemeEditorWindow().ShowDialog<bool>(owner);
}

using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using MissionPlannerAvalonia.ViewModels;

namespace MissionPlannerAvalonia.Views;

public partial class SerialOutputCotView : UserControl {
  private readonly DataGrid _identityGrid;
  private SerialOutputCotViewModel? _viewModel;

  public SerialOutputCotView() {
    AvaloniaXamlLoader.Load(this);
    _identityGrid = this.FindControl<DataGrid>("IdentityGrid")!;
    _identityGrid.LostFocus += CommitIdentityEdits;
    DataContextChanged += OnDataContextChanged;
  }

  internal void CommitIdentityEdits() {
    _identityGrid.CommitEdit(DataGridEditingUnit.Cell, true);
    _identityGrid.CommitEdit(DataGridEditingUnit.Row, true);
  }

  private void CommitIdentityEdits(object? sender, RoutedEventArgs e) => CommitIdentityEdits();

  private void OnDataContextChanged(object? sender, EventArgs e) {
    if (_viewModel != null) {
      _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }
    _viewModel = DataContext as SerialOutputCotViewModel;
    if (_viewModel != null) {
      _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }
    UpdateAdvancedColumns();
  }

  private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e) {
    if (e.PropertyName == nameof(SerialOutputCotViewModel.AdvancedMode)) {
      UpdateAdvancedColumns();
    }
  }

  private void UpdateAdvancedColumns() {
    bool visible = _viewModel?.AdvancedMode ?? true;
    for (int index = 2; index < _identityGrid.Columns.Count; index++) {
      _identityGrid.Columns[index].IsVisible = visible;
    }
  }
}

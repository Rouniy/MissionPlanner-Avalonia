using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using MissionPlannerAvalonia.Services;
using MissionPlannerAvalonia.ViewModels;

namespace MissionPlannerAvalonia.Views;

public partial class TrackerHomeModuleWindow : Window {
  private readonly TrackerHomeModuleViewModel _viewModel;

  public TrackerHomeModuleWindow()
      : this(new TrackerHomeModuleViewModel()) {
  }

  internal TrackerHomeModuleWindow(TrackerHomeModuleViewModel viewModel) {
    InitializeComponent();
    _viewModel = viewModel;
    DataContext = viewModel;
    viewModel.FixAcquired += OnFixAcquired;
    Closed += OnClosed;
  }

  internal static Task<NmeaGgaFix?> ShowAsync(Window? owner) {
    if (owner == null) {
      return Task.FromResult<NmeaGgaFix?>(null);
    }
    var window = new TrackerHomeModuleWindow();
    return window.ShowDialog<NmeaGgaFix?>(owner);
  }

  private void OnFixAcquired(NmeaGgaFix fix) => Close((NmeaGgaFix?)fix);

  private void OnCancelClick(object? sender, RoutedEventArgs e) {
    _viewModel.CancelRead();
    Close((NmeaGgaFix?)null);
  }

  private void OnClosed(object? sender, EventArgs e) {
    _viewModel.FixAcquired -= OnFixAcquired;
    _viewModel.Dispose();
  }
}

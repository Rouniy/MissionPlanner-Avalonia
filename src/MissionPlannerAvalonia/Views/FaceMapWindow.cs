using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Media;
using MissionPlanner.Utilities;
using MissionPlannerAvalonia.ViewModels;

namespace MissionPlannerAvalonia.Views;

public sealed class FaceMapWindow : Window {
  private readonly FaceMapView _view = new();

  public FaceMapWindow(FaceMapViewModel viewModel) {
    Title = "Face Map";
    Width = 1120;
    Height = 900;
    MinWidth = 820;
    MinHeight = 650;
    Background = new SolidColorBrush(Color.Parse("#434445"));
    WindowStartupLocation = WindowStartupLocation.CenterOwner;
    DataContext = viewModel;
    _view.DataContext = viewModel;
    Content = _view;

    viewModel.CloseRequested += Close;
    Closed += (_, _) => {
      viewModel.SaveSettings();
      viewModel.CloseRequested -= Close;
    };
  }

  public static FaceMapViewModel OpenForPath(List<PointLatLngAlt> path,
      PointLatLngAlt home, byte frame, Action<SurveyMissionPlan> onAccept,
      Action<IReadOnlyList<PointLatLngAlt>>? onPathAccepted = null) {
    var viewModel = new FaceMapViewModel(path, home, frame);
    viewModel.PlanAccepted += onAccept;
    if (onPathAccepted != null) {
      viewModel.PathAccepted += onPathAccepted;
    }
    var window = new FaceMapWindow(viewModel);
    Window? owner = Services.Dialogs.Owner;
    if (owner != null) {
      window.Show(owner);
    } else {
      window.Show();
    }
    return viewModel;
  }
}

using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using MissionPlannerAvalonia.ViewModels;

namespace MissionPlannerAvalonia.Controls;

/// <summary>Cross-section preview equivalent to the official FaceMap picture box.</summary>
public sealed class FaceMapProfileControl : Control {
  private FaceMapViewModel? _viewModel;
  private bool _attached;

  public FaceMapProfileControl() {
    MinHeight = 150;
    DataContextChanged += (_, _) => {
      if (_attached) {
        Attach(DataContext as FaceMapViewModel);
      }
    };
  }

  protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) {
    base.OnAttachedToVisualTree(e);
    _attached = true;
    Attach(DataContext as FaceMapViewModel);
  }

  protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) {
    _attached = false;
    Attach(null);
    base.OnDetachedFromVisualTree(e);
  }

  public override void Render(DrawingContext context) {
    base.Render(context);
    Rect bounds = Bounds;
    context.FillRectangle(new SolidColorBrush(Color.Parse("#24364A")), bounds);
    if (_viewModel == null || bounds.Width < 40 || bounds.Height < 40) {
      return;
    }

    double angle = Math.Clamp(_viewModel.FaceAngle, 1, 90) * Math.PI / 180;
    double faceDepth = _viewModel.BenchHeight / Math.Tan(angle);
    double distanceX = _viewModel.DistanceFromFace *
                       Math.Cos(_viewModel.CameraPitch * Math.PI / 180);
    double distanceY = _viewModel.DistanceFromFace *
                       Math.Sin(_viewModel.CameraPitch * Math.PI / 180);
    double totalWidth = Math.Max(1, distanceX + _viewModel.BenchCount *
        (_viewModel.BermDepth + faceDepth));
    double totalHeight = Math.Max(1, _viewModel.BenchCount * _viewModel.BenchHeight +
        distanceY + _viewModel.VerticalSpacing);
    double scale = Math.Min((bounds.Width - 24) / totalWidth,
        (bounds.Height - 28) / totalHeight);
    double originX = bounds.Right - 12 -
                     _viewModel.BenchCount * (_viewModel.BermDepth + faceDepth) * scale;
    double originY = bounds.Bottom - 12;

    var face = new StreamGeometry();
    using (StreamGeometryContext geometry = face.Open()) {
      geometry.BeginFigure(new Point(0, bounds.Bottom), true);
      geometry.LineTo(new Point(0, originY));
      geometry.LineTo(new Point(originX, originY));
      double x = originX;
      double y = originY;
      for (int bench = 0; bench < _viewModel.BenchCount; bench++) {
        x += faceDepth * scale;
        y -= _viewModel.BenchHeight * scale;
        geometry.LineTo(new Point(x, y));
        x += _viewModel.BermDepth * scale;
        geometry.LineTo(new Point(x, y));
      }
      geometry.LineTo(new Point(bounds.Right, y));
      geometry.LineTo(new Point(bounds.Right, bounds.Bottom));
      geometry.EndFigure(true);
    }
    context.DrawGeometry(new SolidColorBrush(Color.Parse("#75684C")),
        new Pen(new SolidColorBrush(Color.Parse("#C7B27B")), 1.5), face);

    double increment = Math.Max(0.1, _viewModel.VerticalSpacing) * Math.Sin(angle);
    int lanes = Math.Max(1, (int)Math.Round(
        (_viewModel.BenchHeight - _viewModel.ToePointHeight) / increment) +
        _viewModel.ToePointRuns + 1);
    var flightPen = new Pen(new SolidColorBrush(Color.Parse("#FFD84D")), 1);
    var cameraBrush = new SolidColorBrush(Color.Parse("#2F81F7"));
    int toeRuns = 0;
    for (int bench = 0; bench < _viewModel.BenchCount; bench++) {
      for (int lane = 0; lane < lanes; lane++) {
        double laneHeight;
        if (toeRuns < _viewModel.ToePointRuns) {
          laneHeight = _viewModel.ToePointHeight;
          toeRuns++;
        } else {
          laneHeight = _viewModel.ToePointHeight +
                       (lane - _viewModel.ToePointRuns) * increment;
        }
        double surveyX = originX + (laneHeight / Math.Tan(angle) + bench *
            (_viewModel.BermDepth + faceDepth)) * scale;
        double surveyY = originY - (laneHeight + bench * _viewModel.BenchHeight) * scale;
        double cameraX = surveyX - distanceX * scale;
        double cameraY = surveyY - distanceY * scale;
        if (!double.IsFinite(cameraX) || !double.IsFinite(cameraY)) {
          continue;
        }
        context.DrawLine(flightPen, new Point(cameraX, cameraY),
            new Point(surveyX, surveyY));
        context.DrawEllipse(cameraBrush, new Pen(Brushes.White, 1),
            new Point(cameraX, cameraY), 3.5, 3.5);
      }
    }
  }

  private void Attach(FaceMapViewModel? viewModel) {
    if (ReferenceEquals(_viewModel, viewModel)) {
      return;
    }
    if (_viewModel != null) {
      _viewModel.PropertyChanged -= OnViewModelChanged;
    }
    _viewModel = viewModel;
    if (_viewModel != null) {
      _viewModel.PropertyChanged += OnViewModelChanged;
    }
    InvalidateVisual();
  }

  private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e) =>
      InvalidateVisual();
}

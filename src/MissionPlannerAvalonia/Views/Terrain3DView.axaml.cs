using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MissionPlannerAvalonia.Services;
using MissionPlannerAvalonia.ViewModels;

namespace MissionPlannerAvalonia.Views;

public partial class Terrain3DView : UserControl {
  public Terrain3DView() {
    InitializeComponent();
    Loaded += (_, _) => Vm?.Start();
    TerrainImage.SizeChanged += (_, args) =>
        Vm?.SetViewport(args.NewSize.Width, args.NewSize.Height);
    TerrainImage.PointerMoved += OnTerrainPointerMoved;
    TerrainImage.PointerPressed += OnTerrainPointerPressed;
    AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
  }

  private Terrain3DViewModel? Vm => DataContext as Terrain3DViewModel;

  private void OnTerrainPointerMoved(object? sender, PointerEventArgs e) {
    if (sender is Control image && Vm is { } viewModel) {
      var point = e.GetPosition(image);
      viewModel.InspectPoint(point.X, point.Y);
    }
  }

  private void OnTerrainPointerPressed(object? sender, PointerPressedEventArgs e) {
    if (sender is not Control image || Vm is not { } viewModel
        || !e.GetCurrentPoint(image).Properties.IsLeftButtonPressed) {
      return;
    }
    Focus();
    var point = e.GetPosition(image);
    _ = viewModel.SendGuidedTargetAsync(point.X, point.Y);
    e.Handled = true;
  }

  private void OnKeyDown(object? sender, KeyEventArgs e) {
    if (Vm is not { } viewModel || IsEditing(TopLevel.GetTopLevel(this)?
            .FocusManager?.GetFocusedElement() as Control)) {
      return;
    }
    Terrain3DCameraMotion? motion = e.Key switch {
      Key.W => Terrain3DCameraMotion.Forward,
      Key.S => Terrain3DCameraMotion.Backward,
      Key.A => Terrain3DCameraMotion.Left,
      Key.D => Terrain3DCameraMotion.Right,
      Key.Q => Terrain3DCameraMotion.YawLeft,
      Key.E => Terrain3DCameraMotion.YawRight,
      Key.R => Terrain3DCameraMotion.Up,
      Key.F => Terrain3DCameraMotion.Down,
      Key.Up => Terrain3DCameraMotion.PitchUp,
      Key.Down => Terrain3DCameraMotion.PitchDown,
      Key.Left => Terrain3DCameraMotion.YawLeft,
      Key.Right => Terrain3DCameraMotion.YawRight,
      _ => null,
    };
    if (motion == null) {
      return;
    }
    viewModel.MoveCamera(motion.Value);
    e.Handled = true;
  }

  private static bool IsEditing(Control? focused) {
    for (Control? control = focused; control != null; control = control.Parent as Control) {
      if (control is TextBox or NumericUpDown or ComboBox) {
        return true;
      }
    }
    return false;
  }
}

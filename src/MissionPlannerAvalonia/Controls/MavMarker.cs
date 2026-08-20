using Mapsui.Styles;

namespace MissionPlannerAvalonia.Controls;

public static class MavMarker {
  public static SymbolStyle Vehicle(double headingDeg, bool active = true) => new() {
    SymbolType = SymbolType.Triangle,
    Fill = new Brush(active ? Color.Red : Color.Gray),
    Outline = new Pen(Color.White, 2),
    SymbolScale = 0.9,
    SymbolRotation = headingDeg,
    RotateWithMap = true,
  };

  public static SymbolStyle Traffic(double headingDeg, bool threat) => new() {
    SymbolType = SymbolType.Triangle,
    Fill = new Brush(threat ? Color.Red : new Color(255, 180, 0)),
    Outline = new Pen(Color.White, 1),
    SymbolScale = 0.65,
    SymbolRotation = headingDeg,
    RotateWithMap = true,
  };

  public static SymbolStyle Camera(bool belowMinimumInterval) => new() {
    SymbolType = SymbolType.Ellipse,
    Fill = new Brush(belowMinimumInterval ? Color.Red : new Color(40, 210, 90)),
    Outline = new Pen(Color.White, 1),
    SymbolScale = 0.5,
  };
}

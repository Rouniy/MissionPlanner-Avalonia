using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Threading;
using MissionPlanner;
using MissionPlannerAvalonia.Controls;
using MissionPlannerAvalonia.ViewModels;

namespace MissionPlannerAvalonia.Views;

public sealed class MavlinkFieldGraphWindow : Window, IDisposable {
  private readonly MAVLinkInterface _mav;
  private readonly MavlinkGraphSelection _selection;
  private readonly int _history;
  private readonly object _gate = new();
  private readonly Dictionary<string, Queue<(double X, double Y)>> _series = [];
  private readonly Dictionary<string, ScottPlot.Color> _colors = [];
  private readonly Stopwatch _clock = Stopwatch.StartNew();
  private readonly DispatcherTimer _timer;
  private readonly LivePlot _plot = new();
  private bool _disposed;

  private static readonly ScottPlot.Color[] _palette = [
    ScottPlot.Colors.Red,
    ScottPlot.Colors.Green,
    ScottPlot.Colors.Blue,
    ScottPlot.Colors.Violet,
    ScottPlot.Colors.Orange,
    ScottPlot.Colors.Cyan,
  ];

  public MavlinkFieldGraphWindow(
      MAVLinkInterface mav, MavlinkGraphSelection selection, int history) {
    _mav = mav;
    _selection = selection;
    _history = Math.Clamp(history, 10, 100000);

    Title = $"MAVLink Graph — {selection.MessageName}.{selection.FieldName}";
    Width = 800;
    Height = 500;
    WindowStartupLocation = WindowStartupLocation.CenterOwner;
    _plot.SetAxisLabels(
        "Time since graph opened (s)", ResolveUnit(selection),
        $"Vehicle {selection.SysId}, component {selection.CompId}");
    Content = _plot;

    _mav.OnPacketReceived += OnPacket;
    _mav.OnPacketSent += OnPacket;
    _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
    _timer.Tick += OnTick;
    _timer.Start();
    Closed += (_, _) => Dispose();
  }

  private void OnPacket(object? sender, MAVLink.MAVLinkMessage message) {
    if (message.sysid != _selection.SysId
        || message.compid != _selection.CompId
        || message.msgid != _selection.MessageId
        || !MavlinkGraphSampleExtractor.TryRead(
            message.data, _selection.FieldName, out var values)) {
      return;
    }

    double x = _clock.Elapsed.TotalSeconds;
    lock (_gate) {
      for (int index = 0; index < values.Count; index++) {
        string label = values.Count == 1
            ? $"{_selection.MessageName}.{_selection.FieldName}"
            : $"{_selection.MessageName}.{_selection.FieldName}[{index}]";
        if (!_series.TryGetValue(label, out var points)) {
          points = new Queue<(double X, double Y)>();
          _series[label] = points;
        }
        points.Enqueue((x, values[index]));
        while (points.Count > _history) {
          points.Dequeue();
        }
      }
    }
  }

  private void OnTick(object? sender, EventArgs args) {
    KeyValuePair<string, (double[] Xs, double[] Ys)>[] snapshot;
    lock (_gate) {
      snapshot = [.. _series.Select(pair => new KeyValuePair<string, (double[], double[])>(
          pair.Key,
          (pair.Value.Select(point => point.X).ToArray(),
           pair.Value.Select(point => point.Y).ToArray())))];
    }
    foreach (var pair in snapshot) {
      if (!_colors.TryGetValue(pair.Key, out ScottPlot.Color color)) {
        color = _palette[_colors.Count % _palette.Length];
        _colors[pair.Key] = color;
      }
      _plot.SetSeries(pair.Key, pair.Value.Xs, pair.Value.Ys, color);
    }
  }

  private static string ResolveUnit(MavlinkGraphSelection selection) {
    try {
      var info = MAVLink.MAVLINK_MESSAGE_INFOS.GetMessageInfo(selection.MessageId);
      var field = info.type?.GetField(selection.FieldName);
      return field?.GetCustomAttributes<MAVLink.Units>().FirstOrDefault()?.Unit
          is { Length: > 0 } unit ? unit : "Value";
    } catch {
      return "Value";
    }
  }

  public void Dispose() {
    if (_disposed) {
      return;
    }
    _disposed = true;
    _timer.Stop();
    _timer.Tick -= OnTick;
    _mav.OnPacketReceived -= OnPacket;
    _mav.OnPacketSent -= OnPacket;
  }
}

internal static class MavlinkGraphSampleExtractor {
  internal static bool IsSupportedType(Type type) {
    Type candidate = type.IsArray ? type.GetElementType() ?? type : type;
    return candidate.IsEnum || Type.GetTypeCode(candidate) is
        TypeCode.Byte or TypeCode.SByte or TypeCode.UInt16 or TypeCode.UInt32 or TypeCode.UInt64
        or TypeCode.Int16 or TypeCode.Int32 or TypeCode.Int64 or TypeCode.Single
        or TypeCode.Double or TypeCode.Decimal;
  }

  internal static bool TryRead(object? message, string fieldName, out IReadOnlyList<double> values) {
    values = [];
    if (message == null) {
      return false;
    }

    try {
      FieldInfo? field = message.GetType().GetField(fieldName);
      if (field == null) {
        return false;
      }
      object? value = field.GetValue(message);
      if (value is IEnumerable sequence and not string) {
        var converted = new List<double>();
        foreach (object? item in sequence) {
          if (!TryConvert(item, out double number)) {
            return false;
          }
          converted.Add(number);
        }
        values = converted;
        return converted.Count > 0;
      }
      if (!TryConvert(value, out double scalar)) {
        return false;
      }
      values = [scalar];
      return true;
    } catch {
      return false;
    }
  }

  private static bool TryConvert(object? value, out double number) {
    try {
      if (value is not IConvertible convertible) {
        number = 0;
        return false;
      }
      number = convertible.ToDouble(CultureInfo.InvariantCulture);
      return double.IsFinite(number);
    } catch {
      number = 0;
      return false;
    }
  }
}

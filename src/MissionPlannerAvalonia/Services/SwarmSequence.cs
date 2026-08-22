using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using MissionPlanner.ArduPilot;

namespace MissionPlannerAvalonia.Services;

internal sealed class SwarmSequenceDocument {
  public List<SwarmSequenceLayout> Layouts { get; set; } = [];
  public List<string> Steps { get; set; } = [];
}

internal sealed class SwarmSequenceLayout {
  public string Id { get; set; } = "";
  public int DelayStart { get; set; }
  public int DelayEnd { get; set; }
  public Dictionary<int, SwarmSequenceOffset> Offset { get; set; } = [];

  internal SwarmSequenceLayout Clone(string? id = null) => new() {
    Id = id ?? Id,
    DelayStart = DelayStart,
    DelayEnd = DelayEnd,
    Offset = Offset.ToDictionary(pair => pair.Key, pair => pair.Value with { }),
  };
}

internal sealed record SwarmSequenceOffset(
    [property: JsonPropertyName("x")] double X,
    [property: JsonPropertyName("y")] double Y,
    [property: JsonPropertyName("z")] double Z);

internal static class SwarmSequenceFile {
  internal const long MaximumFileBytes = 8 * 1024 * 1024;
  private static readonly JsonSerializerOptions _json = new() {
    WriteIndented = true,
    PropertyNameCaseInsensitive = true,
    AllowTrailingCommas = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
  };

  internal static SwarmSequenceDocument Load(string path) {
    var info = new FileInfo(path);
    if (!info.Exists) {
      throw new FileNotFoundException("Sequence file was not found.", path);
    }
    if (info.Length > MaximumFileBytes) {
      throw new InvalidDataException("Sequence file exceeds the 8 MiB safety limit.");
    }
    SwarmSequenceDocument? document = JsonSerializer.Deserialize<SwarmSequenceDocument>(
        File.ReadAllText(path), _json);
    document ??= new SwarmSequenceDocument();
    Normalize(document);
    Validate(document);
    return document;
  }

  internal static void Save(string path, SwarmSequenceDocument document) {
    Normalize(document);
    Validate(document);
    string fullPath = Path.GetFullPath(path);
    string? directory = Path.GetDirectoryName(fullPath);
    if (!string.IsNullOrWhiteSpace(directory)) {
      Directory.CreateDirectory(directory);
    }
    string temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".partial";
    try {
      File.WriteAllText(temporary, JsonSerializer.Serialize(document, _json));
      File.Move(temporary, fullPath, overwrite: true);
    } finally {
      if (File.Exists(temporary)) {
        File.Delete(temporary);
      }
    }
  }

  internal static void Validate(SwarmSequenceDocument document) {
    if (document.Layouts.Count > 1000 || document.Steps.Count > 100000) {
      throw new InvalidDataException("Sequence contains too many layouts or steps.");
    }
    var ids = new HashSet<string>(StringComparer.Ordinal);
    HashSet<int>? expectedSystemIds = null;
    foreach (SwarmSequenceLayout layout in document.Layouts) {
      if (string.IsNullOrWhiteSpace(layout.Id)) {
        throw new InvalidDataException("Every layout must have a non-empty Id.");
      }
      if (!ids.Add(layout.Id)) {
        throw new InvalidDataException($"Layout Id '{layout.Id}' is duplicated.");
      }
      if (layout.Offset.Count > 255) {
        throw new InvalidDataException($"Layout '{layout.Id}' contains more than 255 vehicles.");
      }
      foreach ((int systemId, SwarmSequenceOffset offset) in layout.Offset) {
        if (systemId is < 1 or > 255) {
          throw new InvalidDataException(
              $"Layout '{layout.Id}' has invalid MAVLink system id {systemId}.");
        }
        if (!IsSafe(offset)) {
          throw new InvalidDataException(
              $"Layout '{layout.Id}' has an invalid or excessive offset for system {systemId}.");
        }
      }
      if (expectedSystemIds == null) {
        expectedSystemIds = layout.Offset.Keys.ToHashSet();
      } else if (!expectedSystemIds.SetEquals(layout.Offset.Keys)) {
        throw new InvalidDataException(
            $"Layout '{layout.Id}' does not contain the same MAVLink system ids as the first layout.");
      }
    }
    foreach (string step in document.Steps) {
      if (!ids.Contains(step)) {
        throw new InvalidDataException($"Sequence step references missing layout '{step}'.");
      }
    }
  }

  internal static bool IsSafe(SwarmSequenceOffset offset) =>
      double.IsFinite(offset.X) && double.IsFinite(offset.Y) && double.IsFinite(offset.Z) &&
      Math.Abs(offset.X) <= 100000 && Math.Abs(offset.Y) <= 100000 &&
      Math.Abs(offset.Z) <= 10000;

  private static void Normalize(SwarmSequenceDocument document) {
    document.Layouts ??= [];
    document.Steps ??= [];
    foreach (SwarmSequenceLayout layout in document.Layouts) {
      layout.Id = layout.Id?.Trim() ?? "";
      layout.Offset ??= [];
    }
    for (int i = 0; i < document.Steps.Count; i++) {
      document.Steps[i] = document.Steps[i]?.Trim() ?? "";
    }
  }
}

internal readonly record struct SwarmSequenceOrigin(double Latitude, double Longitude);

internal sealed record SwarmSequenceAssignment(int SystemId, FormationVehicleId Vehicle);

internal sealed record SwarmSequenceCommandPlan(
    FormationVehicleId Anchor,
    SwarmSequenceOrigin Origin,
    SwarmSequenceLayout Layout,
    IReadOnlyList<SwarmSequenceAssignment> Assignments);

internal sealed record SwarmSequenceCommand(
    int SystemId,
    FormationVehicleSource Vehicle,
    FollowPathPoint Target);

internal sealed record SwarmSequenceCommandResult(
    string Status,
    IReadOnlyList<SwarmSequenceCommand> Commands);

internal sealed class SwarmSequenceCommandRunner {
  private readonly Func<IReadOnlyList<FormationVehicleSource>> _snapshot;
  private readonly IFollowLeaderCommandSink _sink;

  internal SwarmSequenceCommandRunner(
      Func<IReadOnlyList<FormationVehicleSource>> snapshot,
      IFollowLeaderCommandSink sink) {
    _snapshot = snapshot;
    _sink = sink;
  }

  internal bool TryCaptureOrigin(
      FormationVehicleId anchor,
      DateTime nowUtc,
      out SwarmSequenceOrigin origin,
      out string error) {
    origin = default;
    if (!TryResolveFlightVehicle(_snapshot(), anchor, nowUtc,
            out FormationVehicleSource source, out error)) {
      error = "anchor " + error;
      return false;
    }
    if (!FormationCommandRunner.HasPosition(source.State)) {
      error = "anchor position is unavailable.";
      return false;
    }
    origin = new SwarmSequenceOrigin(source.State.cs.lat, source.State.cs.lng);
    return true;
  }

  internal SwarmSequenceCommandResult SendLayout(
      SwarmSequenceCommandPlan plan,
      DateTime nowUtc) {
    if (!TryResolvePlan(plan, nowUtc, out List<(int SystemId,
            FormationVehicleSource Vehicle, SwarmSequenceOffset Offset)> vehicles,
        out string error)) {
      throw new InvalidOperationException("Sequence step rejected: " + error);
    }

    var commands = new List<SwarmSequenceCommand>(vehicles.Count);
    foreach ((int systemId, FormationVehicleSource vehicle, SwarmSequenceOffset offset) in vehicles) {
      double distance = Math.Sqrt(offset.X * offset.X + offset.Y * offset.Y);
      double bearing = Math.Atan2(offset.X, offset.Y);
      (double latitude, double longitude) = FormationGeometry.Project(
          plan.Origin.Latitude, plan.Origin.Longitude, bearing, distance);
      commands.Add(new SwarmSequenceCommand(systemId, vehicle,
          new FollowPathPoint(latitude, longitude, offset.Z)));
    }

    _sink.RequestPositionStreams(commands.Select(command => command.Vehicle).ToArray());
    foreach (SwarmSequenceCommand command in commands) {
      _sink.SendPositionVelocity(
          command.Vehicle, command.Target, new FollowLeaderVelocity(0, 0, 0));
    }
    return new SwarmSequenceCommandResult(
        $"Sequence layout '{plan.Layout.Id}' sent to {commands.Count} vehicle(s).",
        commands);
  }

  internal bool TryResolvePlan(
      SwarmSequenceCommandPlan plan,
      DateTime nowUtc,
      out List<(int SystemId, FormationVehicleSource Vehicle, SwarmSequenceOffset Offset)> vehicles,
      out string error) {
    vehicles = [];
    if (!double.IsFinite(plan.Origin.Latitude) || !double.IsFinite(plan.Origin.Longitude) ||
        plan.Origin.Latitude is < -90 or > 90 ||
        plan.Origin.Longitude is < -180 or > 180 ||
        (Math.Abs(plan.Origin.Latitude) <= double.Epsilon &&
         Math.Abs(plan.Origin.Longitude) <= double.Epsilon)) {
      error = "captured origin is invalid.";
      return false;
    }
    try {
      SwarmSequenceFile.Validate(new SwarmSequenceDocument { Layouts = [plan.Layout] });
    } catch (InvalidDataException ex) {
      error = ex.Message;
      return false;
    }
    if (plan.Layout.Offset.Count == 0) {
      error = $"layout '{plan.Layout.Id}' has no vehicles.";
      return false;
    }
    if (plan.Assignments.Count != plan.Layout.Offset.Count) {
      error = "every layout system id must have exactly one vehicle assignment.";
      return false;
    }

    IReadOnlyList<FormationVehicleSource> sources = _snapshot();
    if (!TryResolveFlightVehicle(sources, plan.Anchor, nowUtc,
            out _, out error)) {
      error = "anchor " + error;
      return false;
    }
    var systemIds = new HashSet<int>();
    var identities = new HashSet<FormationVehicleId>();
    foreach (SwarmSequenceAssignment assignment in plan.Assignments) {
      if (!systemIds.Add(assignment.SystemId) ||
          !plan.Layout.Offset.TryGetValue(assignment.SystemId, out SwarmSequenceOffset? offset)) {
        error = $"system id {assignment.SystemId} is duplicated or absent from the layout.";
        return false;
      }
      if (!identities.Add(assignment.Vehicle)) {
        error = "one vehicle cannot be assigned to multiple layout system ids.";
        return false;
      }
      if (!TryResolveFlightVehicle(
              sources, assignment.Vehicle, nowUtc,
              out FormationVehicleSource source, out error)) {
        error = $"system {assignment.SystemId} vehicle " + error;
        return false;
      }
      if (!FormationCommandRunner.HasPosition(source.State)) {
        error = $"system {assignment.SystemId} vehicle {source.Label} position is unavailable.";
        return false;
      }
      vehicles.Add((assignment.SystemId, source, offset));
    }
    if (!systemIds.SetEquals(plan.Layout.Offset.Keys)) {
      error = "vehicle assignments do not match the selected layout.";
      return false;
    }
    vehicles.Sort((left, right) => left.SystemId.CompareTo(right.SystemId));
    error = "";
    return true;
  }

  internal static bool TryResolveFlightVehicle(
      IReadOnlyList<FormationVehicleSource> sources,
      FormationVehicleId id,
      DateTime nowUtc,
      out FormationVehicleSource source,
      out string error) {
    if (!FormationCommandRunner.TryResolveAutopilot(
            sources, id, nowUtc, out source, out error)) {
      return false;
    }
    if (source.State.cs.firmware != Firmwares.ArduCopter2) {
      error = $"{source.Label} is {source.State.cs.firmware}; Sequence flight requires ArduCopter.";
      return false;
    }
    error = "";
    return true;
  }
}

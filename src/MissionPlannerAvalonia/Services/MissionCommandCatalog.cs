using System;
using System.Collections.Generic;
using System.Linq;
using MissionPlanner.Utilities;
using Newtonsoft.Json;

namespace MissionPlannerAvalonia.Services;

public sealed record MissionCommandDefinition(ushort Id, string Name, string[] ParameterLabels);

public static class MissionCommandCatalog {
  private static readonly object _sync = new();
  private static Dictionary<string, string[]> _labels = new(StringComparer.OrdinalIgnoreCase);
  private static Dictionary<string, ushort> _extraIds = new(StringComparer.OrdinalIgnoreCase);
  private static string? _loadedLabelsJson;
  private static string? _loadedIdsJson;

  public static IReadOnlyList<MissionCommandDefinition> LoadDefinitions() {
    lock (_sync) {
      ReloadLocked();
      return _labels.Keys.Concat(_extraIds.Keys).Distinct(StringComparer.OrdinalIgnoreCase)
          .OrderBy(GetIdLocked)
          .Select(name => new MissionCommandDefinition(
              GetIdLocked(name), name,
              NormalizeLabels(_labels.TryGetValue(name, out var labels) ? labels : null)))
          .ToArray();
    }
  }

  public static string[] Names() {
    lock (_sync) {
      ReloadLocked();
      return Enum.GetNames(typeof(MAVLink.MAV_CMD))
          .Concat(_extraIds.Keys)
          .Distinct(StringComparer.OrdinalIgnoreCase)
          .OrderBy(name => TryGetIdLocked(name, out var id) ? id : ushort.MaxValue)
          .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
          .ToArray();
    }
  }

  public static bool TryGetId(string name, out ushort id) {
    lock (_sync) {
      ReloadLocked();
      return TryGetIdLocked(name, out id);
    }
  }

  public static string? GetName(ushort id) {
    if (Enum.IsDefined(typeof(MAVLink.MAV_CMD), id)) {
      return ((MAVLink.MAV_CMD)id).ToString();
    }
    lock (_sync) {
      ReloadLocked();
      return _extraIds.FirstOrDefault(item => item.Value == id).Key;
    }
  }

  public static string[]? GetLabels(ushort id) {
    lock (_sync) {
      ReloadLocked();
      var name = Enum.IsDefined(typeof(MAVLink.MAV_CMD), id)
          ? ((MAVLink.MAV_CMD)id).ToString()
          : _extraIds.FirstOrDefault(item => item.Value == id).Key;
      return name != null && _labels.TryGetValue(name, out var labels)
          ? NormalizeLabels(labels)
          : null;
    }
  }

  public static void Save(IEnumerable<MissionCommandDefinition> definitions) {
    var rows = definitions.ToArray();
    Validate(rows);
    var labels = rows.ToDictionary(
        row => row.Name.Trim(), row => NormalizeLabels(row.ParameterLabels),
        StringComparer.OrdinalIgnoreCase);
    var ids = rows.Where(row => !Enum.IsDefined(typeof(MAVLink.MAV_CMD), row.Id))
        .ToDictionary(row => row.Name.Trim(), row => row.Id, StringComparer.OrdinalIgnoreCase);

    lock (_sync) {
      Settings.Instance["PlannerExtraCommand"] = JsonConvert.SerializeObject(labels);
      Settings.Instance["PlannerExtraCommandIDs"] = JsonConvert.SerializeObject(ids);
      Settings.Instance.Save();
      _labels = labels;
      _extraIds = ids;
      _loadedLabelsJson = Settings.Instance["PlannerExtraCommand"];
      _loadedIdsJson = Settings.Instance["PlannerExtraCommandIDs"];
    }
  }

  public static void Validate(IReadOnlyCollection<MissionCommandDefinition> rows) {
    var duplicateId = rows.GroupBy(row => row.Id).FirstOrDefault(group => group.Count() > 1);
    if (duplicateId != null) {
      throw new InvalidOperationException($"Command ID {duplicateId.Key} is used more than once.");
    }
    var duplicateName = rows.GroupBy(row => row.Name.Trim(), StringComparer.OrdinalIgnoreCase)
        .FirstOrDefault(group => group.Count() > 1);
    if (duplicateName != null) {
      throw new InvalidOperationException($"Command name '{duplicateName.Key}' is used more than once.");
    }
    foreach (var row in rows) {
      if (string.IsNullOrWhiteSpace(row.Name)) {
        throw new InvalidOperationException($"Command ID {row.Id} has no name.");
      }
      if (row.Name.Any(character => !char.IsLetterOrDigit(character) && character != '_')) {
        throw new InvalidOperationException(
            $"Command name '{row.Name}' may contain only letters, digits and underscores.");
      }
      if (Enum.IsDefined(typeof(MAVLink.MAV_CMD), row.Id)
          && !string.Equals(((MAVLink.MAV_CMD)row.Id).ToString(), row.Name,
              StringComparison.OrdinalIgnoreCase)) {
        throw new InvalidOperationException(
            $"Known command ID {row.Id} must retain its MAVLink name '{(MAVLink.MAV_CMD)row.Id}'.");
      }
      if (!Enum.IsDefined(typeof(MAVLink.MAV_CMD), row.Id)
          && Enum.TryParse<MAVLink.MAV_CMD>(row.Name, true, out var namedCommand)
          && Enum.IsDefined(typeof(MAVLink.MAV_CMD), namedCommand)) {
        throw new InvalidOperationException(
            $"Custom command ID {row.Id} cannot reuse known MAVLink name '{row.Name}'.");
      }
    }
  }

  private static ushort GetIdLocked(string name) =>
      TryGetIdLocked(name, out var id) ? id : ushort.MaxValue;

  private static bool TryGetIdLocked(string name, out ushort id) {
    if (Enum.TryParse<MAVLink.MAV_CMD>(name, true, out var command)
        && Enum.IsDefined(typeof(MAVLink.MAV_CMD), command)) {
      id = (ushort)command;
      return true;
    }
    return _extraIds.TryGetValue(name, out id);
  }

  private static void ReloadLocked() {
    var labelsJson = Settings.Instance["PlannerExtraCommand"] ?? "{}";
    var idsJson = Settings.Instance["PlannerExtraCommandIDs"] ?? "{}";
    if (labelsJson == _loadedLabelsJson && idsJson == _loadedIdsJson) {
      return;
    }
    try {
      _labels = JsonConvert.DeserializeObject<Dictionary<string, string[]>>(
          labelsJson)
          ?? new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
      _labels = new Dictionary<string, string[]>(_labels, StringComparer.OrdinalIgnoreCase);
    } catch {
      _labels = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
    }
    try {
      _extraIds = JsonConvert.DeserializeObject<Dictionary<string, ushort>>(
          idsJson)
          ?? new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase);
      _extraIds = new Dictionary<string, ushort>(_extraIds, StringComparer.OrdinalIgnoreCase);
    } catch {
      _extraIds = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase);
    }
    _loadedLabelsJson = labelsJson;
    _loadedIdsJson = idsJson;
  }

  private static string[] NormalizeLabels(IReadOnlyList<string>? labels) =>
      Enumerable.Range(0, 7)
          .Select(index => index < (labels?.Count ?? 0) ? labels![index] ?? "" : "")
          .ToArray();
}

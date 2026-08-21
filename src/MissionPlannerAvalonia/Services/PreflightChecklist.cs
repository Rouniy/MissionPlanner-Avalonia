using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Serialization;

namespace MissionPlannerAvalonia.Services;

public enum PreflightCondition {
  NONE = 0,
  LT,
  LTEQ,
  EQ,
  GT,
  GTEQ,
  NEQ,
}

public sealed class PreflightChecklistDefinition {
  public PreflightChecklistDefinition? Child { get; set; }
  public string TrueColor { get; set; } = "Green";
  public string FalseColor { get; set; } = "Red";
  public string Description { get; set; } = "New check";
  public string Text { get; set; } = "{value}";
  public double TriggerValue { get; set; }
  public string Name { get; set; } = "satcount";
  public PreflightCondition ConditionType { get; set; } = PreflightCondition.GTEQ;

  internal PreflightChecklistDefinition Clone() => new() {
    TrueColor = TrueColor,
    FalseColor = FalseColor,
    Description = Description,
    Text = Text,
    TriggerValue = TriggerValue,
    Name = Name,
    ConditionType = ConditionType,
    Child = Child?.Clone(),
  };
}

internal readonly record struct PreflightChecklistEvaluation(
    string DisplayText,
    bool IsSatisfied,
    bool IsManual);

internal static partial class PreflightChecklist {
  private static readonly XmlSerializer _serializer = new(
      typeof(List<PreflightChecklistDefinition>),
      [typeof(PreflightChecklistDefinition)]);

  private static readonly Regex _parameterName = ParameterNameRegex();
  private static readonly Regex _manualExpression = ManualExpressionRegex();
  private static readonly Regex _conditionExpression = ConditionExpressionRegex();

  internal static string ConfigPath => Path.Combine(AppPaths.ConfigRoot, "checklist.xml");
  internal static string DefaultPath => Path.Combine(AppPaths.InstallRoot, "checklistDefault.xml");

  internal static List<PreflightChecklistDefinition> Load(
      string? configPath = null,
      string? defaultPath = null,
      IReadOnlyList<string>? legacyManualItems = null) {
    configPath ??= ConfigPath;
    defaultPath ??= DefaultPath;

    bool usingDefault = !File.Exists(configPath);
    string? path = !usingDefault ? configPath : File.Exists(defaultPath) ? defaultPath : null;
    List<PreflightChecklistDefinition> definitions;
    try {
      definitions = path == null ? BuiltInDefaults() : Read(path);
      if (definitions.Count == 0) {
        definitions = BuiltInDefaults();
        usingDefault = true;
      }
    } catch {
      definitions = BuiltInDefaults();
      usingDefault = true;
    }

    if (usingDefault && legacyManualItems != null) {
      ApplyLegacyManualItems(definitions, legacyManualItems);
    }
    return definitions;
  }

  internal static List<PreflightChecklistDefinition> LoadDefaults(string? defaultPath = null) {
    defaultPath ??= DefaultPath;
    try {
      return File.Exists(defaultPath) ? Read(defaultPath) : BuiltInDefaults();
    } catch {
      return BuiltInDefaults();
    }
  }

  internal static void Save(
      IReadOnlyCollection<PreflightChecklistDefinition> definitions,
      string? path = null) {
    path ??= ConfigPath;
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    using var writer = new StreamWriter(path, append: false);
    _serializer.Serialize(writer, definitions.ToList());
  }

  internal static PreflightChecklistEvaluation Evaluate(
      PreflightChecklistDefinition definition,
      object source,
      Func<string, double?> parameterLookup,
      bool manualState) {
    bool manual = definition.ConditionType == PreflightCondition.NONE;
    if (!TryGetValue(definition, source, parameterLookup, out object? value, out double numeric)) {
      return new PreflightChecklistEvaluation("Error", false, manual);
    }

    string display = FormatText(definition, value);
    bool satisfied = manual
        ? manualState
        : CheckValue(definition.ConditionType, numeric, definition.TriggerValue)
          && (definition.Child == null
              || Evaluate(definition.Child, source, parameterLookup, manualState).IsSatisfied);
    return new PreflightChecklistEvaluation(display, satisfied, manual);
  }

  internal static string ToExpression(PreflightChecklistDefinition definition) {
    var parts = new List<string>();
    for (PreflightChecklistDefinition? item = definition; item != null; item = item.Child) {
      if (item.ConditionType == PreflightCondition.NONE) {
        parts.Add(item.Text.Contains("{value}", StringComparison.Ordinal)
            ? $"manual({item.Name})"
            : "manual");
        continue;
      }

      string source = item.Name;
      if (string.Equals(source, "PARAM", StringComparison.OrdinalIgnoreCase)) {
        Match match = _parameterName.Match(item.Description ?? "");
        source = match.Success ? "PARAM:" + match.Groups[1].Value : "PARAM:?";
      }
      parts.Add($"{source} {Operator(item.ConditionType)} "
          + item.TriggerValue.ToString("0.###############", CultureInfo.InvariantCulture));
    }
    return string.Join(" && ", parts);
  }

  internal static bool TryParseExpression(
      string expression,
      string description,
      string text,
      string trueColor,
      string falseColor,
      out PreflightChecklistDefinition definition,
      out string error) {
    definition = null!;
    error = "";
    string[] parts = expression.Split("&&", StringSplitOptions.TrimEntries);
    if (parts.Length == 0 || parts.Any(string.IsNullOrWhiteSpace)) {
      error = "condition is empty";
      return false;
    }

    PreflightChecklistDefinition? root = null;
    PreflightChecklistDefinition? previous = null;
    foreach (string part in parts) {
      var item = new PreflightChecklistDefinition {
        Description = description.Trim(),
        Text = text,
        TrueColor = NormalizeColor(trueColor, "Green"),
        FalseColor = NormalizeColor(falseColor, "Red"),
      };

      Match manual = _manualExpression.Match(part);
      if (manual.Success) {
        item.ConditionType = PreflightCondition.NONE;
        item.Name = manual.Groups[1].Success ? manual.Groups[1].Value : "roll";
      } else {
        Match condition = _conditionExpression.Match(part);
        if (!condition.Success
            || !double.TryParse(condition.Groups[3].Value, NumberStyles.Float,
                CultureInfo.InvariantCulture, out double trigger)) {
          error = $"invalid condition '{part}'";
          return false;
        }

        string source = condition.Groups[1].Value;
        item.ConditionType = ParseOperator(condition.Groups[2].Value);
        item.TriggerValue = trigger;
        if (source.StartsWith("PARAM:", StringComparison.OrdinalIgnoreCase)) {
          string parameter = source[6..];
          if (parameter.Length == 0 || parameter == "?") {
            error = "PARAM condition needs a parameter name";
            return false;
          }
          item.Name = "PARAM";
          item.Description = EnsureParameterTag(item.Description, parameter);
        } else {
          if (typeof(MissionPlanner.CurrentState).GetProperty(
                  source, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase) == null) {
            error = $"CurrentState has no property named '{source}'";
            return false;
          }
          item.Name = source;
        }
      }

      if (root == null) {
        root = item;
      } else {
        previous!.Child = item;
      }
      previous = item;
    }

    definition = root!;
    return true;
  }

  private static List<PreflightChecklistDefinition> Read(string path) {
    using var reader = new StreamReader(path);
    return (List<PreflightChecklistDefinition>?)_serializer.Deserialize(reader) ?? [];
  }

  private static void ApplyLegacyManualItems(
      List<PreflightChecklistDefinition> definitions,
      IReadOnlyList<string> manualItems) {
    int firstManual = definitions.FindIndex(item =>
        item.ConditionType == PreflightCondition.NONE
        && !item.Text.Contains("{value}", StringComparison.Ordinal));
    if (firstManual >= 0) {
      definitions.RemoveRange(firstManual, definitions.Count - firstManual);
    }
    foreach (string description in manualItems.Where(item => !string.IsNullOrWhiteSpace(item))) {
      definitions.Add(Manual(description.Trim()));
    }
  }

  private static bool TryGetValue(
      PreflightChecklistDefinition definition,
      object source,
      Func<string, double?> parameterLookup,
      out object? value,
      out double numeric) {
    value = null;
    numeric = 0;
    try {
      if (string.Equals(definition.Name, "PARAM", StringComparison.OrdinalIgnoreCase)) {
        Match match = _parameterName.Match(definition.Description ?? "");
        if (!match.Success) {
          return false;
        }
        double? parameter = parameterLookup(match.Groups[1].Value);
        if (!parameter.HasValue) {
          return false;
        }
        value = numeric = parameter.Value;
        return true;
      }

      PropertyInfo? property = source.GetType().GetProperty(
          definition.Name,
          BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
      if (property == null || property.GetIndexParameters().Length != 0) {
        return false;
      }
      value = property.GetValue(source);
      if (value == null) {
        return false;
      }
      if (definition.ConditionType == PreflightCondition.NONE) {
        return true;
      }
      numeric = Convert.ToDouble(value, CultureInfo.InvariantCulture);
      return true;
    } catch {
      return false;
    }
  }

  private static string FormatText(PreflightChecklistDefinition definition, object? value) {
    string formatted = value switch {
      float number => number.ToString("0.##", CultureInfo.InvariantCulture),
      double number => number.ToString("0.##", CultureInfo.InvariantCulture),
      decimal number => number.ToString("0.##", CultureInfo.InvariantCulture),
      IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
      _ => value?.ToString() ?? "",
    };
    return (definition.Text ?? "")
        .Replace("{trigger}", definition.TriggerValue.ToString("0.##", CultureInfo.InvariantCulture),
            StringComparison.Ordinal)
        .Replace("{value}", formatted, StringComparison.Ordinal)
        .Replace("{name}", definition.Name, StringComparison.Ordinal);
  }

  private static bool CheckValue(PreflightCondition condition, double value, double trigger) =>
      condition switch {
        PreflightCondition.LT => value < trigger,
        PreflightCondition.LTEQ => value <= trigger,
        PreflightCondition.EQ => value == trigger,
        PreflightCondition.GT => value > trigger,
        PreflightCondition.GTEQ => value >= trigger,
        PreflightCondition.NEQ => value != trigger,
        _ => false,
      };

  private static string Operator(PreflightCondition condition) => condition switch {
    PreflightCondition.LT => "<",
    PreflightCondition.LTEQ => "<=",
    PreflightCondition.EQ => "==",
    PreflightCondition.GT => ">",
    PreflightCondition.GTEQ => ">=",
    PreflightCondition.NEQ => "!=",
    _ => "manual",
  };

  private static PreflightCondition ParseOperator(string value) => value switch {
    "<" => PreflightCondition.LT,
    "<=" => PreflightCondition.LTEQ,
    "==" => PreflightCondition.EQ,
    ">" => PreflightCondition.GT,
    ">=" => PreflightCondition.GTEQ,
    "!=" => PreflightCondition.NEQ,
    _ => PreflightCondition.NONE,
  };

  private static string NormalizeColor(string? value, string fallback) =>
      string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

  private static string EnsureParameterTag(string description, string parameter) {
    string tag = "{" + parameter + "}";
    Match existing = _parameterName.Match(description);
    if (existing.Success) {
      return description[..existing.Index] + tag + description[(existing.Index + existing.Length)..];
    }
    return string.IsNullOrWhiteSpace(description) ? tag : description.Trim() + " " + tag;
  }

  private static List<PreflightChecklistDefinition> BuiltInDefaults() => [
    Automatic("Verify GPS", "{value} >= 3", "gpsstatus", PreflightCondition.GTEQ, 3),
    Automatic("GPS Sat Count", "{value} Sats", "satcount", PreflightCondition.GTEQ, 5),
    Automatic("Telemetry Signal", "{value}%", "linkqualitygcs", PreflightCondition.GT, 95),
    Automatic("Battery Level", "{value} V", "battery_voltage", PreflightCondition.GT, 22),
    new PreflightChecklistDefinition {
      TrueColor = "White", FalseColor = "White", Description = "Mode", Text = "{value}",
      Name = "mode", ConditionType = PreflightCondition.NONE,
    },
    Automatic("Check Altitude", "{value} < 5m", "alt", PreflightCondition.LT, 5,
        "White", "White"),
    Manual("Tail and wings secured?"),
    Manual("All servos respond to input?"),
    Manual("All servos respond to pitch and roll?"),
    Manual("Center of gravity at indicated point?"),
    Manual("Servo linkages are secure?"),
    Manual("Camera is on and ready to fly?"),
  ];

  private static PreflightChecklistDefinition Automatic(
      string description,
      string text,
      string name,
      PreflightCondition condition,
      double trigger,
      string trueColor = "Green",
      string falseColor = "Red") => new() {
        Description = description,
        Text = text,
        Name = name,
        ConditionType = condition,
        TriggerValue = trigger,
        TrueColor = trueColor,
        FalseColor = falseColor,
      };

  private static PreflightChecklistDefinition Manual(string description) => new() {
    Description = description,
    Text = "",
    Name = "roll",
    ConditionType = PreflightCondition.NONE,
    TrueColor = "White",
    FalseColor = "White",
  };

  [GeneratedRegex(@"\{([^{}]+)\}", RegexOptions.CultureInvariant)]
  private static partial Regex ParameterNameRegex();

  [GeneratedRegex(@"^\s*manual(?:\(([A-Za-z_][A-Za-z0-9_]*)\))?\s*$",
      RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
  private static partial Regex ManualExpressionRegex();

  [GeneratedRegex(
      @"^\s*([A-Za-z_][A-Za-z0-9_]*|PARAM:[A-Za-z0-9_]+)\s*(<=|>=|==|!=|<|>)\s*(-?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?)\s*$",
      RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
  private static partial Regex ConditionExpressionRegex();
}

internal sealed class PreflightEditorRow {
  internal PreflightEditorRow(PreflightChecklistDefinition definition) {
    Description = definition.Description;
    Text = definition.Text;
    Condition = PreflightChecklist.ToExpression(definition);
    TrueColor = definition.TrueColor;
    FalseColor = definition.FalseColor;
  }

  public string Description { get; set; }
  public string Text { get; set; }
  public string Condition { get; set; }
  public string TrueColor { get; set; }
  public string FalseColor { get; set; }
}

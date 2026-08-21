using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using MissionPlanner.Log;
using MissionPlanner.Utilities;
using org.mariuszgromada.math.mxparser;

namespace MissionPlannerAvalonia.Services;

internal readonly record struct DataFlashFieldReference(
    string Text, string Type, string BaseType, string? Instance, string Field, string Argument);

/// <summary>
/// Evaluates the same graph-expression language as Mission Planner's DFLogScript while keeping
/// the latest value from each requested message type. The latter makes mixed-type expressions
/// deterministic; upstream's old evaluator indexes every field against the current record.
/// </summary>
internal static class DataFlashExpressionEvaluator {
  private static readonly Regex _fieldReference = new(
      @"(?<type>[A-Za-z_][A-Za-z0-9_]*(?:\[(?<instance>\d+)\])?)\.(?<field>[A-Za-z_][A-Za-z0-9_]*)",
      RegexOptions.Compiled);
  private static readonly Regex _quotedKey = new(
      "[\\\"'](?<key>[^\\\"']+)[\\\"']", RegexOptions.Compiled);

  internal static IReadOnlyList<(double TimeSeconds, double Value)> Evaluate(
      string path, string expression) {
    if (ContainsUpstreamSpecialFunction(expression)) {
      return EvaluateUpstreamFallback(path, expression);
    }
    var references = References(expression);
    if (references.Count == 0) {
      return EvaluateUpstreamFallback(path, expression);
    }

    var compiled = Compile(expression, references);
    var values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
    var answer = new List<(double, double)>();
    using var log = new DFLogBuffer(path);
    string[] selectors = references.Select(reference => reference.Type)
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    foreach (var item in log.GetEnumeratorType(selectors)) {
      bool updated = false;
      foreach (var reference in references) {
        if (!string.Equals(reference.BaseType, item.msgtype, StringComparison.OrdinalIgnoreCase)
            || !MatchesInstance(item, reference.Instance)) {
          continue;
        }
        if (double.TryParse(item[reference.Field], NumberStyles.Any,
                CultureInfo.InvariantCulture, out double value)) {
          values[reference.Text] = value;
          updated = true;
        }
      }
      if (!updated || values.Count != references.Count) {
        continue;
      }
      foreach (var reference in references) {
        compiled.setArgumentValue(reference.Argument, values[reference.Text]);
      }
      double result = compiled.calculate();
      if (double.IsFinite(result)) {
        answer.Add((item.timems / 1000.0, result));
      }
    }

    if (answer.Count == 0) {
      throw new InvalidOperationException($"'{expression}' produced no finite values.");
    }
    return answer;
  }

  internal static IReadOnlyList<DataFlashFieldReference> References(string expression) {
    var references = new List<DataFlashFieldReference>();
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (Match match in _fieldReference.Matches(expression)) {
      string text = match.Value;
      if (!seen.Add(text)) {
        continue;
      }
      string type = match.Groups["type"].Value;
      string? instance = match.Groups["instance"].Success
          ? match.Groups["instance"].Value
          : null;
      string baseType = instance == null ? type : type[..type.IndexOf('[', StringComparison.Ordinal)];
      references.Add(new DataFlashFieldReference(
          text, type, baseType, instance, match.Groups["field"].Value,
          "__field" + references.Count.ToString(CultureInfo.InvariantCulture)));
    }
    return references;
  }

  internal static bool CanEvaluate(
      string expression, IReadOnlyDictionary<string, string[]> formats) {
    var references = References(expression);
    bool fieldsAvailable = references.All(reference =>
        formats.TryGetValue(reference.BaseType, out var fields)
        && fields.Contains(reference.Field, StringComparer.OrdinalIgnoreCase));
    if (ContainsUpstreamSpecialFunction(expression)) {
      string[] types = KnownSpecialTypes(expression).Distinct(StringComparer.OrdinalIgnoreCase)
          .ToArray();
      return types.Length > 0 && types.All(type => formats.ContainsKey(type)) && fieldsAvailable;
    }
    return references.Count > 0 && fieldsAvailable;
  }

  private static Expression Compile(
      string expression, IReadOnlyList<DataFlashFieldReference> references) {
    var argumentNames = references.ToDictionary(
        reference => reference.Text, reference => reference.Argument,
        StringComparer.OrdinalIgnoreCase);
    string normalized = _fieldReference.Replace(expression,
        match => argumentNames[match.Value]);
    normalized = normalized.Replace("**", "^", StringComparison.Ordinal)
        .Replace("%", "#", StringComparison.Ordinal)
        .Replace(":2", string.Empty, StringComparison.Ordinal);
    normalized = _quotedKey.Replace(normalized,
        match => PackKey(match.Groups["key"].Value).ToString(CultureInfo.InvariantCulture));

    var evaluator = new Expression(normalized);
    evaluator.addFunctions(new Function("wrap_360(x) = (x+360) # 360"));
    evaluator.addFunctions(new Function("degrees(x) = x*57.295779513"));
    evaluator.addFunctions(new Function("atan2", new Atan2Function()));
    evaluator.addFunctions(new Function("lowpass", new LowPassFunction()));
    evaluator.addFunctions(new Function("delta", new DeltaFunction()));
    foreach (var reference in references) {
      evaluator.addArguments(new Argument(reference.Argument));
    }
    if (!evaluator.checkSyntax()) {
      throw new InvalidOperationException(
          $"Invalid Mission Planner graph expression '{expression}': {evaluator.getErrorMessage()}");
    }
    return evaluator;
  }

  private static bool MatchesInstance(DFLog.DFItem item, string? requested) {
    if (requested == null) {
      return true;
    }
    try {
      return string.Equals(item.instance, requested, StringComparison.Ordinal);
    } catch {
      return false;
    }
  }

  private static IReadOnlyList<(double TimeSeconds, double Value)> EvaluateUpstreamFallback(
      string path, string expression) {
    using var log = new DFLogBuffer(path);
    var values = DFLogScript.ProcessExpression(log.dflog, log, expression);
    var answer = values.Where(value => double.IsFinite(value.Item2))
        .Select(value => (value.Item1.timems / 1000.0, value.Item2)).ToArray();
    if (answer.Length == 0) {
      throw new InvalidOperationException(
          $"'{expression}' produced no finite values; required messages may be missing from the log.");
    }
    return answer;
  }

  private static IEnumerable<string> KnownSpecialTypes(string expression) {
    var matches = Regex.Matches(expression,
        @"(?:earth_accel_df|mag_heading_df|gps_velocity_df)\((?<args>[^)]*)\)");
    foreach (Match match in matches) {
      foreach (string token in match.Groups["args"].Value.Split(',')) {
        string type = token.Trim();
        if (Regex.IsMatch(type, @"^[A-Za-z_][A-Za-z0-9_]*$")) {
          yield return type;
        }
      }
    }
  }

  private static bool ContainsUpstreamSpecialFunction(string expression) =>
      expression.Contains("earth_accel_df", StringComparison.Ordinal)
      || expression.Contains("gps_velocity_df", StringComparison.Ordinal)
      || expression.Contains("mag_heading_df", StringComparison.Ordinal);

  private static ulong PackKey(string key) {
    Span<byte> bytes = stackalloc byte[8];
    Encoding.ASCII.GetBytes(key.AsSpan(0, Math.Min(key.Length, bytes.Length)), bytes);
    return BitConverter.ToUInt64(bytes);
  }

  private sealed class Atan2Function : FunctionExtension {
    private double _y;
    private double _x;
    public double calculate() => Math.Atan2(_y, _x);
    public FunctionExtension clone() => new Atan2Function();
    public string getParameterName(int argumentIndex) => argumentIndex == 0 ? "y" : "x";
    public int getParametersNumber() => 2;
    public void setParameterValue(int argumentIndex, double argumentValue) {
      if (argumentIndex == 0) {
        _y = argumentValue;
      } else {
        _x = argumentValue;
      }
    }
  }

  private sealed class LowPassFunction : FunctionExtension {
    private readonly Dictionary<double, double> _last = new();
    private double _value;
    private double _key;
    private double _factor;
    public double calculate() {
      if (!_last.TryGetValue(_key, out double previous)) {
        previous = _value;
      }
      double filtered = _factor * previous + (1.0 - _factor) * _value;
      _last[_key] = filtered;
      return filtered;
    }
    public FunctionExtension clone() => new LowPassFunction();
    public string getParameterName(int argumentIndex) =>
        argumentIndex switch { 0 => "value", 1 => "key", _ => "factor" };
    public int getParametersNumber() => 3;
    public void setParameterValue(int argumentIndex, double argumentValue) {
      if (argumentIndex == 0) {
        _value = argumentValue;
      } else if (argumentIndex == 1) {
        _key = argumentValue;
      } else {
        _factor = argumentValue;
      }
    }
  }

  private sealed class DeltaFunction : FunctionExtension {
    private readonly Dictionary<double, (double Value, double Time)> _last = new();
    private double _value;
    private double _key;
    private double _timeUsec;
    public double calculate() {
      double time = _timeUsec == 0 ? 0 : _timeUsec * 1.0e-6;
      _last.TryGetValue(_key, out var previous);
      double delta = time.Equals(previous.Time)
          ? 0
          : (_value - previous.Value) / (time - previous.Time);
      _last[_key] = (_value, time);
      return delta;
    }
    public FunctionExtension clone() => new DeltaFunction();
    public string getParameterName(int argumentIndex) =>
        argumentIndex switch { 0 => "value", 1 => "key", _ => "timeUsec" };
    public int getParametersNumber() => 3;
    public void setParameterValue(int argumentIndex, double argumentValue) {
      if (argumentIndex == 0) {
        _value = argumentValue;
      } else if (argumentIndex == 1) {
        _key = argumentValue;
      } else {
        _timeUsec = argumentValue;
      }
    }
  }
}

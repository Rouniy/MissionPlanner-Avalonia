using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace MissionPlannerAvalonia.Services;

public readonly record struct GraphCurve(string Expression, int Axis);

public readonly record struct GraphPresetAlternative(IReadOnlyList<GraphCurve> Curves);

public readonly record struct GraphPreset(
    string Name,
    string Description,
    IReadOnlyList<GraphPresetAlternative> Alternatives) {
  public IReadOnlyList<GraphCurve> Curves =>
      Alternatives.Count == 0 ? System.Array.Empty<GraphCurve>() : Alternatives[0].Curves;
}

public static class GraphPresets {
  public static List<GraphPreset> Parse(string xml) {
    var doc = XDocument.Parse(xml);
    var list = new List<GraphPreset>();
    foreach (var g in doc.Descendants("graph")) {
      var name = (string?)g.Attribute("name");
      var expressions = g.Elements("expression")
          .Select(element => element.Value.Trim())
          .Where(expression => !string.IsNullOrWhiteSpace(expression))
          .Select(expression => new GraphPresetAlternative(ParseCurves(expression)))
          .Where(alternative => alternative.Curves.Count > 0)
          .ToArray();
      if (string.IsNullOrWhiteSpace(name) || expressions.Length == 0) {
        continue;
      }
      string description = g.Elements("description").FirstOrDefault()?.Value.Trim() ?? string.Empty;
      list.Add(new GraphPreset(name!, description, expressions));
    }
    return list;
  }

  public static List<GraphCurve> ParseCurves(string expression) {
    var curves = new List<GraphCurve>();
    foreach (var token in expression.Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries)) {
      int axis = 1;
      var ex = token;
      int colon = ex.LastIndexOf(':');

      if (colon > 0 && colon == ex.Length - 2 && char.IsDigit(ex[colon + 1])) {
        axis = ex[colon + 1] - '0';
        ex = ex[..colon];
      }
      curves.Add(new GraphCurve(ex, axis));
    }
    return curves;
  }

  public static List<GraphPreset> LoadDirectory(string dir) {
    var all = new List<GraphPreset>();
    if (!Directory.Exists(dir)) {
      return all;
    }
    foreach (var file in Directory.GetFiles(dir, "*.xml").OrderBy(f => f)) {
      try {
        all.AddRange(Parse(File.ReadAllText(file)));
      } catch {

      }
    }
    return all.OrderBy(p => p.Name).ToList();
  }
}

using System.Text.RegularExpressions;

namespace MissionPlannerAvalonia.Tests;

public sealed class TempHandlerAuditTests {
  [Fact]
  public void Every_official_temp_click_handler_has_one_closed_classification() {
    string sourcePath = FindRepositoryFile("external/MissionPlanner/temp.cs");
    string auditPath = FindRepositoryFile("docs/TEMP_HANDLER_AUDIT.md");
    string source = File.ReadAllText(sourcePath);
    string audit = File.ReadAllText(auditPath);

    string[] upstream = Regex.Matches(source, @"\bvoid\s+(\w+_Click)\s*\(")
        .Select(match => match.Groups[1].Value)
        .ToArray();
    MatchCollection rows = Regex.Matches(
        audit,
        @"^\|\s*`(?<handler>\w+_Click)`\s*\|\s*`(?<status>[\w-]+)`\s*\|",
        RegexOptions.Multiline);
    string[] documented = rows.Select(match => match.Groups["handler"].Value).ToArray();
    var acceptedStatuses = new HashSet<string> {
      "ported", "replaced", "obsolete", "unsafe", "platform-specific",
    };

    Assert.Equal(67, upstream.Length);
    Assert.Equal(upstream.Length, upstream.Distinct(StringComparer.Ordinal).Count());
    Assert.Equal(documented.Length, documented.Distinct(StringComparer.Ordinal).Count());
    Assert.Equal(
        upstream.OrderBy(name => name, StringComparer.Ordinal),
        documented.OrderBy(name => name, StringComparer.Ordinal));
    Assert.All(rows.Cast<Match>(), row =>
        Assert.Contains(row.Groups["status"].Value, acceptedStatuses));
    Assert.Contains("67a3c4f22bd1b38ac499f9756902e04fa4ed8444", audit);
  }

  private static string FindRepositoryFile(string relativePath) {
    for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
         directory != null;
         directory = directory.Parent) {
      string candidate = Path.Combine(directory.FullName, relativePath);
      if (File.Exists(candidate)) {
        return candidate;
      }
    }
    throw new FileNotFoundException(relativePath);
  }
}

using MissionPlannerAvalonia.Services;

namespace MissionPlannerAvalonia.Tests;

public class PreflightChecklistTests {
  [Fact]
  public void Loads_upstream_default_checklist_schema() {
    string path = FindRepositoryFile("external/MissionPlanner/checklistDefault.xml");

    var definitions = PreflightChecklist.Load(
        configPath: path + ".missing", defaultPath: path);

    Assert.Equal(12, definitions.Count);
    Assert.Equal("gpsstatus", definitions[0].Name);
    Assert.Equal(PreflightCondition.GTEQ, definitions[0].ConditionType);
    Assert.Equal("Camera is on and ready to fly?", definitions[^1].Description);
  }

  [Fact]
  public void Parses_and_round_trips_nested_parameter_condition() {
    bool parsed = PreflightChecklist.TryParseExpression(
        "satcount >= 5 && PARAM:ARMING_CHECK != 0",
        "Arming checks", "{value}", "Green", "Red",
        out var definition, out string error);

    Assert.True(parsed, error);
    Assert.Equal("satcount", definition.Name);
    Assert.Equal(PreflightCondition.GTEQ, definition.ConditionType);
    Assert.NotNull(definition.Child);
    Assert.Equal("PARAM", definition.Child.Name);
    Assert.Contains("{ARMING_CHECK}", definition.Child.Description);
    Assert.Equal(
        "satcount >= 5 && PARAM:ARMING_CHECK != 0",
        PreflightChecklist.ToExpression(definition));
  }

  [Fact]
  public void Evaluates_nested_state_and_parameter_conditions() {
    var definition = new PreflightChecklistDefinition {
      Name = nameof(TestState.Value),
      ConditionType = PreflightCondition.GT,
      TriggerValue = 5,
      Text = "{value} > {trigger}",
      Child = new PreflightChecklistDefinition {
        Name = "PARAM",
        Description = "Required {TEST_PARAM}",
        ConditionType = PreflightCondition.EQ,
        TriggerValue = 1,
      },
    };

    var passing = PreflightChecklist.Evaluate(
        definition, new TestState { Value = 10 },
        name => name == "TEST_PARAM" ? 1 : null, manualState: false);
    var failing = PreflightChecklist.Evaluate(
        definition, new TestState { Value = 10 },
        _ => 0, manualState: false);

    Assert.True(passing.IsSatisfied);
    Assert.Equal("10 > 5", passing.DisplayText);
    Assert.False(failing.IsSatisfied);
  }

  [Fact]
  public void Manual_check_preserves_checkbox_state_and_formats_source_value() {
    var definition = new PreflightChecklistDefinition {
      Name = nameof(TestState.Mode),
      ConditionType = PreflightCondition.NONE,
      Text = "Mode: {value}",
    };

    var evaluation = PreflightChecklist.Evaluate(
        definition, new TestState { Mode = "AUTO" }, _ => null, manualState: true);

    Assert.True(evaluation.IsManual);
    Assert.True(evaluation.IsSatisfied);
    Assert.Equal("Mode: AUTO", evaluation.DisplayText);
  }

  [Fact]
  public void Saves_and_reloads_nested_xml() {
    string root = Path.Combine(
        Path.GetTempPath(), "mp-preflight-" + Guid.NewGuid().ToString("N"));
    string path = Path.Combine(root, "checklist.xml");
    try {
      var source = new List<PreflightChecklistDefinition> {
        new() {
          Description = "Nested",
          Name = "satcount",
          ConditionType = PreflightCondition.GTEQ,
          TriggerValue = 6,
          Child = new PreflightChecklistDefinition {
            Description = "{ARMING_CHECK}",
            Name = "PARAM",
            ConditionType = PreflightCondition.NEQ,
          },
        },
      };

      PreflightChecklist.Save(source, path);
      var loaded = PreflightChecklist.Load(path, path + ".missing");

      var item = Assert.Single(loaded);
      Assert.Equal("Nested", item.Description);
      Assert.NotNull(item.Child);
      Assert.Equal("PARAM", item.Child.Name);
      Assert.Equal(PreflightCondition.NEQ, item.Child.ConditionType);
    } finally {
      if (Directory.Exists(root)) {
        Directory.Delete(root, recursive: true);
      }
    }
  }

  [Fact]
  public void Falls_back_to_built_in_rules_and_migrates_legacy_manual_items() {
    string missing = Path.Combine(
        Path.GetTempPath(), "mp-preflight-missing-" + Guid.NewGuid().ToString("N"));

    var definitions = PreflightChecklist.Load(
        missing + ".xml", missing + "-default.xml", ["Custom airframe check"]);

    Assert.Equal(7, definitions.Count);
    Assert.Equal("Mode", definitions[4].Description);
    Assert.Equal(PreflightCondition.NONE, definitions[4].ConditionType);
    Assert.Equal("Custom airframe check", definitions[^1].Description);
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

  private sealed class TestState {
    public double Value { get; init; }
    public string Mode { get; init; } = "";
  }
}

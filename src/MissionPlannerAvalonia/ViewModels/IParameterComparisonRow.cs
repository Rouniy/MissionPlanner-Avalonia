namespace MissionPlannerAvalonia.ViewModels;

public interface IParameterComparisonRow {
  string Name { get; }
  string CurrentText { get; }
  string ProposedText { get; }
  bool Use { get; set; }
}

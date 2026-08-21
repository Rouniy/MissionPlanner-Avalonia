using MissionPlannerAvalonia.ViewModels;
using Xunit;

namespace MissionPlannerAvalonia.Tests;

public class BackstageLifecycleTests {
  [Fact]
  public void Switching_pages_and_leaving_screen_deactivates_active_content() {
    using var backstage = new TestBackstage();
    var first = Assert.IsType<LifecycleViewModel>(backstage.CurrentContent);
    Assert.Equal(1, first.ActivationCount);

    backstage.SelectedPage = backstage.Second;
    Assert.Equal(1, first.DeactivationCount);

    var second = Assert.IsType<LifecycleViewModel>(backstage.CurrentContent);
    Assert.Equal(1, second.ActivationCount);
    backstage.Deactivate();
    Assert.Equal(1, second.DeactivationCount);
  }

  [Fact]
  public void SelectPage_expands_group_and_selects_named_tool() {
    using var backstage = new TestBackstage();
    backstage.AdvancedGroup.IsExpanded = false;

    Assert.True(backstage.SelectPage("Advanced Tool"));
    Assert.True(backstage.AdvancedGroup.IsExpanded);
    Assert.Same(backstage.AdvancedTool, backstage.SelectedPage);
  }

  private sealed class TestBackstage : BackstageViewModel {
    public TestBackstage() {
      Add("First", () => new LifecycleViewModel());
      Second = Add("Second", () => new LifecycleViewModel());
      AdvancedGroup = Add(">> Advanced", () => new LifecycleViewModel());
      AdvancedTool = Add("Advanced Tool", () => new LifecycleViewModel(), sub: true);
      SelectFirst();
    }

    public BackstagePage Second { get; }
    public BackstagePage AdvancedGroup { get; }
    public BackstagePage AdvancedTool { get; }
  }

  private sealed class LifecycleViewModel : ViewModelBase, IActivationAware, IDeactivationAware {
    public int ActivationCount { get; private set; }
    public int DeactivationCount { get; private set; }

    public void Activate() => ActivationCount++;
    public void Deactivate() => DeactivationCount++;
  }
}

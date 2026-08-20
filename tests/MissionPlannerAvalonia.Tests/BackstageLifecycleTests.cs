using MissionPlannerAvalonia.ViewModels;
using Xunit;

namespace MissionPlannerAvalonia.Tests;

public class BackstageLifecycleTests {
  [Fact]
  public void Switching_pages_and_leaving_screen_deactivates_active_content() {
    using var backstage = new TestBackstage();
    var first = Assert.IsType<LifecycleViewModel>(backstage.CurrentContent);

    backstage.SelectedPage = backstage.Second;
    Assert.Equal(1, first.DeactivationCount);

    var second = Assert.IsType<LifecycleViewModel>(backstage.CurrentContent);
    backstage.Deactivate();
    Assert.Equal(1, second.DeactivationCount);
  }

  private sealed class TestBackstage : BackstageViewModel {
    public TestBackstage() {
      Add("First", () => new LifecycleViewModel());
      Second = Add("Second", () => new LifecycleViewModel());
      SelectFirst();
    }

    public BackstagePage Second { get; }
  }

  private sealed class LifecycleViewModel : ViewModelBase, IDeactivationAware {
    public int DeactivationCount { get; private set; }

    public void Deactivate() => DeactivationCount++;
  }
}

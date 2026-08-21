using MissionPlanner.Utilities;
using MissionPlannerAvalonia.Services;
using MissionPlannerAvalonia.Views;

namespace MissionPlannerAvalonia.Tests;

public class DisplayViewTests {
  [Theory]
  [InlineData("Basic", DisplayNames.Basic, false)]
  [InlineData("advanced", DisplayNames.Advanced, true)]
  public void AvaloniaEnumCaptionIsMigratedToUpstreamJson(
      string stored, DisplayNames expectedName, bool expectedAdvanced) {
    var profile = DisplayViewService.ResolveStoredProfile(stored, out string canonical);

    Assert.Equal(expectedName, profile.displayName);
    Assert.Equal(expectedAdvanced, profile.isAdvancedMode);
    Assert.StartsWith("{", canonical);
    Assert.True(DisplayViewExtensions.TryParse(canonical, out var roundTrip));
    Assert.Equal(expectedName, roundTrip.displayName);
  }

  [Fact]
  public void ExistingCustomJsonPreservesEveryAuthoredFlag() {
    var authored = new DisplayView().Advanced();
    authored.displayName = DisplayNames.Custom;
    authored.displaySimulation = false;
    authored.displayQuickTab = false;
    authored.displayStandardParams = true;
    authored.displayPlannerLayout = false;

    var profile = DisplayViewService.ResolveStoredProfile(
        authored.ConvertToString(), out string canonical);

    Assert.Equal(DisplayNames.Custom, profile.displayName);
    Assert.False(profile.displaySimulation);
    Assert.False(profile.displayQuickTab);
    Assert.True(profile.displayStandardParams);
    Assert.False(profile.displayPlannerLayout);
    Assert.True(DisplayViewExtensions.TryParse(canonical, out var roundTrip));
    Assert.True(roundTrip.displayStandardParams);
  }

  [Fact]
  public void InvalidStoredProfileUsesUpstreamBasicFallback() {
    var profile = DisplayViewService.ResolveStoredProfile("not a display profile", out string canonical);

    Assert.Equal(DisplayNames.Basic, profile.displayName);
    Assert.False(profile.isAdvancedMode);
    Assert.True(profile.displayQuickTab);
    Assert.True(DisplayViewExtensions.TryParse(canonical, out _));
  }

  [Fact]
  public void LegacyAdvancedFlagOverwritesStoredProfileLikeMainV2() {
    var stored = new DisplayView().Basic();
    stored.displaySimulation = false;

    var profile = DisplayViewService.ResolveStartupProfile(
        stored.ConvertToString(), hadStoredProfile: true, legacyAdvanced: true,
        out string? canonical);

    Assert.Equal(DisplayNames.Advanced, profile.displayName);
    Assert.True(profile.isAdvancedMode);
    Assert.True(profile.displaySimulation);
    Assert.False(profile.displayAdvancedParams);
    Assert.False(profile.displayStandardParams);
    Assert.True(profile.displayFullParamList);
    Assert.NotNull(canonical);
  }

  [Fact]
  public void FlightDataTabsFollowBasicAndAdvancedProfiles() {
    var basic = new DisplayView().Basic();
    var advanced = new DisplayView().Advanced();

    Assert.True(FlightDataView.ProfileAllowsTab("Quick", basic));
    Assert.False(FlightDataView.ProfileAllowsTab("Actions", basic));
    Assert.True(FlightDataView.ProfileAllowsTab("Simple Actions", basic));
    Assert.False(FlightDataView.ProfileAllowsTab("Status", basic));
    Assert.False(FlightDataView.ProfileAllowsTab("Scripts", basic));

    Assert.True(FlightDataView.ProfileAllowsTab("Quick", advanced));
    Assert.True(FlightDataView.ProfileAllowsTab("Actions", advanced));
    Assert.False(FlightDataView.ProfileAllowsTab("Simple Actions", advanced));
    Assert.True(FlightDataView.ProfileAllowsTab("Status", advanced));
    Assert.True(FlightDataView.ProfileAllowsTab("Scripts", advanced));
  }
}

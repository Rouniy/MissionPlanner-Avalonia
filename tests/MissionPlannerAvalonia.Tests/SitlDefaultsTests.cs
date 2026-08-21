using MissionPlannerAvalonia.Services;

namespace MissionPlannerAvalonia.Tests;

public class SitlDefaultsTests {
  [Fact]
  public void Parses_single_frame_defaults_from_vehicleinfo() {
    const string source = """
        class VehicleInfo(object):
            options = {
              "ArduCopter": {
                "frames": {
                  "+": {
                    "default_params_filename": "default_params/copter.parm",
                    "external": False,
                  },
                },
              },
            }
        """;

    Assert.Equal(new[] { "default_params/copter.parm" },
        SitlLauncher.ParseDefaultParameterPaths(source, "+"));
  }

  [Fact]
  public void Python_comments_do_not_strip_hash_characters_inside_strings() {
    const string source = """
        class VehicleInfo(object):
            options = {
              "ArduCopter": {
                "frames": {
                  "frame#1": { # a real Python comment
                    "default_params_filename": "default_params/copter#1.parm",
                  },
                },
              },
            }
        """;

    Assert.Equal(new[] { "default_params/copter#1.parm" },
        SitlLauncher.ParseDefaultParameterPaths(source, "frame#1"));
  }

  [Fact]
  public void Parses_ordered_multiple_frame_defaults_case_insensitively() {
    const string source = """
        options = {
          "ArduPlane": {
            "frames": {
              "QuadPlane": {
                "default_params_filename": [
                  "default_params/plane.parm",
                  "default_params/quadplane.parm",
                ],
              },
            },
          },
        }
        """;

    Assert.Equal(new[] {
      "default_params/plane.parm", "default_params/quadplane.parm",
    }, SitlLauncher.ParseDefaultParameterPaths(source, "quadplane"));
  }

  [Fact]
  public void Missing_frame_has_no_automatic_defaults() {
    Assert.Empty(SitlLauncher.ParseDefaultParameterPaths(
        "options = {\"Plane\": {\"frames\": {}}}", "unknown"));
  }
}

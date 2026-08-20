using System;
using System.IO;
using System.Threading.Tasks;
using MissionPlanner.Utilities;
using MissionPlannerAvalonia.Services;

namespace MissionPlannerAvalonia.Tests;

public class PortFixTests {
  private const string _kml = """
      <?xml version="1.0" encoding="UTF-8"?>
      <kml xmlns="http://www.opengis.net/kml/2.2">
        <Document>
          <Placemark>
            <Polygon>
              <outerBoundaryIs>
                <LinearRing>
                  <coordinates>
                    30.0,50.0,0 30.1,50.0,0 30.1,50.1,0 30.0,50.1,0 30.0,50.0,0
                  </coordinates>
                </LinearRing>
              </outerBoundaryIs>
            </Polygon>
          </Placemark>
        </Document>
      </kml>
      """;

  [Fact]
  public void NoFlyOverlay_loads_all_kml_files_from_directory() {
    var dir = Directory.CreateTempSubdirectory("nofly-test").FullName;
    try {
      File.WriteAllText(Path.Combine(dir, "zone1.kml"), _kml);
      File.WriteAllText(Path.Combine(dir, "notes.txt"), "ignored");

      var layer = NoFlyOverlay.BuildLayerFromDirectory(dir);

      Assert.NotNull(layer);
    } finally {
      Directory.Delete(dir, true);
    }
  }

  [Fact]
  public void NoFlyOverlay_returns_null_for_empty_or_missing_directory() {
    var dir = Directory.CreateTempSubdirectory("nofly-empty").FullName;
    try {
      Assert.Null(NoFlyOverlay.BuildLayerFromDirectory(dir));
    } finally {
      Directory.Delete(dir, true);
    }
    Assert.Null(NoFlyOverlay.BuildLayerFromDirectory(Path.Combine(dir, "does-not-exist")));
  }

  [Fact]
  public void Password_round_trip_validates_and_rejects() {
    var saved = Settings.Instance["password"];
    try {
      Password.EnterPassword("secret-1");
      Assert.True(Password.ValidatePassword("secret-1"));
      Assert.False(Password.ValidatePassword("wrong"));
    } finally {
      Settings.Instance["password"] = saved;
    }
  }

  [Theory]
  [InlineData(null, false)]
  [InlineData("", false)]
  [InlineData("not base64 !!!", false)]
  [InlineData("AA==", false)] // valid Base64 but not a 32-byte SHA-256 hash
  public void Password_hash_validity_rejects_non_hashes(string? stored, bool expected) {
    var saved = Settings.Instance["password"];
    try {
      Settings.Instance["password"] = stored;
      Assert.Equal(expected, ViewModels.MainWindowViewModel.HasValidPasswordHash());
    } finally {
      Settings.Instance["password"] = saved;
    }
  }

  [Fact]
  public void Password_hash_validity_accepts_real_hash() {
    var saved = Settings.Instance["password"];
    try {
      Password.EnterPassword("secret-2");
      Assert.True(ViewModels.MainWindowViewModel.HasValidPasswordHash());
    } finally {
      Settings.Instance["password"] = saved;
    }
  }

  [Fact]
  public async Task TlogPlayer_open_terminates_on_garbage_input() {
    var path = Path.Combine(Path.GetTempPath(), "garbage-" + Guid.NewGuid().ToString("N") + ".tlog");
    var bytes = new byte[64 * 1024];
    for (int i = 0; i < bytes.Length; i++) {
      bytes[i] = (byte)(i % 251);
    }
    File.WriteAllBytes(path, bytes);
    try {
      var player = new TlogPlayer();
      // Times out (and fails) if the parser stops making forward progress on malformed input.
      await Task.Run(() => player.Open(path)).WaitAsync(TimeSpan.FromSeconds(30));
      player.Close();
    } finally {
      File.Delete(path);
    }
  }

  [Fact]
  public void Upstream_message_bridge_uses_safe_noninteractive_results() {
    Assert.Equal(global::System.CustomMessageBox.DialogResult.OK,
        Dialogs.DefaultUpstreamResult(global::System.CustomMessageBox.MessageBoxButtons.OK));
    Assert.Equal(global::System.CustomMessageBox.DialogResult.Cancel,
        Dialogs.DefaultUpstreamResult(global::System.CustomMessageBox.MessageBoxButtons.YesNo));
  }

  [Fact]
  public void AppState_installs_upstream_message_callback() {
    _ = AppState.comPort;

    var result = global::System.CustomMessageBox.Show("bridge test", "Mission Planner");

    Assert.Equal(global::System.CustomMessageBox.DialogResult.OK, result);
  }

  [Fact]
  public void Px4Flow_grayscale_conversion_is_platform_neutral_bgra() {
    var bgra = Px4FlowReceiver.ToBgra(new byte[] { 0, 127, 255, 42 }, 2, 2);

    Assert.Equal(new byte[] {
      0, 0, 0, 255,
      127, 127, 127, 255,
      255, 255, 255, 255,
      42, 42, 42, 255,
    }, bgra);
  }
}

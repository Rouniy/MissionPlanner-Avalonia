using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using MissionPlanner.Mavlink;
using MissionPlanner.Utilities;
using MissionPlannerAvalonia.Services;

namespace MissionPlannerAvalonia.ViewModels.GCSViews.ConfigurationView;

public class ConfigAdvancedViewModel : ActionPageViewModel {
  public ConfigAdvancedViewModel() {
    Title = "Advanced";
    Instructions = "The following tools are for advanced configuration only — use with caution.";

    Action("Anon Log", () => _ = AnonLogAsync());

    Action("MAVLink Inspector", () => Views.MAVLinkInspectorWindow.OpenWindow());
    Action("Mavlink Mirror", () => Views.SerialPassThroughWindow.OpenWindow());
    Action("NMEA", () => Views.SerialOutputNMEAWindow.OpenWindow());
    Action("Cursor-on-Target / TAK", () => Views.SerialOutputCotWindow.OpenWindow());
    Action("Follow Me", () => Views.FollowMeWindow.OpenWindow());
    Action("Moving Base", () => Views.MovingBaseWindow.OpenWindow());
    Action("Map Tile Cache", () => Views.MapCacheWindow.OpenWindow());
    Action("MAVLink Signing", () => _ = ManageSigningAsync());
    Action("FFT", () => Views.ConfigFFTWindow.OpenWindow());
    Action("Spectrogram", () => Views.SpectrogramWindow.OpenWindow());
    Action("Warning Manager", () => Views.WarningManagerWindow.OpenWindow());
    Action("Proximity", () => Views.ProximityWindow.OpenWindow());
    Action("Param gen", () => _ = RegenerateParameterMetadataAsync());

    foreach (var (name, desc, opens) in _notPorted) {
      var n = name;
      var d = desc;
      var o = opens;
      Action(n, () => _ = NoticeAsync(n, d, o));
    }
  }

  private static readonly (string Name, string Desc, string Opens)[] _notPorted =
  {
        ("Support Proxy", "Share connection with support engineer", "SerialSupportProxy"),
    };

  private const string _parameterLocations =
      "https://raw.githubusercontent.com/ArduPilot/ardupilot/master/ArduCopter/Parameters.cpp;" +
      "https://raw.githubusercontent.com/ArduPilot/ardupilot/master/ArduPlane/Parameters.cpp;" +
      "https://raw.githubusercontent.com/ArduPilot/ardupilot/master/Rover/Parameters.cpp;" +
      "https://raw.githubusercontent.com/ArduPilot/ardupilot/master/ArduSub/Parameters.cpp;" +
      "https://raw.githubusercontent.com/ArduPilot/ardupilot/master/AntennaTracker/Parameters.cpp;" +
      "https://raw.githubusercontent.com/ArduPilot/ardupilot/ArduCopter-stable/ArduCopter/Parameters.cpp;" +
      "https://raw.githubusercontent.com/ArduPilot/ardupilot/ArduPlane-stable/ArduPlane/Parameters.cpp;" +
      "https://raw.githubusercontent.com/ArduPilot/ardupilot/Rover-stable/Rover/Parameters.cpp;" +
      "https://raw.githubusercontent.com/ArduPilot/ardupilot/ArduSub-stable/ArduSub/Parameters.cpp;";

  private async Task RegenerateParameterMetadataAsync() {
    if (!await Dialogs.Confirm(
            "Regenerate Parameter Metadata",
            "Download current master/stable ArduPilot parameter definitions and rebuild the local metadata cache?")) {
      return;
    }

    AppendLog("Parameter metadata: downloading current ArduPilot definitions…");
    try {
      await Task.Run(() => {
        ParameterMetaDataParser.GetParameterInformation(_parameterLocations);
        ParameterMetaDataRepositoryAPMpdef.GetMetaData(true);
        ParameterMetaDataRepositoryAPM.Reload();
      });
      AppendLog("Parameter metadata regenerated. Reopen parameter pages to refresh them.");
    } catch (Exception ex) {
      AppendLog("Parameter metadata generation failed: " + ex.Message);
    }
  }

  private async Task ManageSigningAsync() {
    while (true) {
      var current = CurrentSigningKeyName();
      var choice = await Dialogs.Choice(
          "MAVLink Signing",
          $"Stored keys: {MAVAuthKeys.Keys.Count}\nUsing key: {current}\n" +
          $"Signed packets: {_comPort.Mavlink2Signed}",
          "Add", "Use", "Delete", "Disable", "Close");

      switch (choice) {
        case "Add":
          await AddSigningKeyAsync();
          break;
        case "Use":
          await UseSigningKeyAsync();
          break;
        case "Delete":
          await DeleteSigningKeyAsync();
          break;
        case "Disable":
          await DisableSigningAsync();
          break;
        default:
          return;
      }
    }
  }

  private string CurrentSigningKeyName() {
    if (_comPort.MAV?.signing != true) {
      return "None/Unknown";
    }

    var name = Settings.Instance["mavlink_signing_key_name"];
    return string.IsNullOrWhiteSpace(name) ? "Enabled/Unknown" : name;
  }

  private async Task AddSigningKeyAsync() {
    var name = await Dialogs.InputBox("MAVLink Signing", "Friendly key name");
    if (string.IsNullOrWhiteSpace(name)) {
      return;
    }

    var seed = await Dialogs.InputBox(
        "MAVLink Signing",
        "Pass phrase/seed (use a unique long phrase with mixed characters)");
    if (string.IsNullOrEmpty(seed)) {
      return;
    }

    MAVAuthKeys.AddKey(name.Trim(), seed);
    MAVAuthKeys.Save();
    AppendLog($"MAVLink signing key '{name.Trim()}' saved.");
  }

  private async Task UseSigningKeyAsync() {
    if (!RequireConnection()) {
      return;
    }

    var name = await ChooseSigningKeyAsync("Use Signing Key");
    if (name == null) {
      return;
    }

    var mav = _comPort.MAV;
    _comPort.setupSigning(mav.sysid, mav.compid, "", MAVAuthKeys.Keys[name].Key);
    Settings.Instance["mavlink_signing_key_name"] = name;
    Settings.Instance.Save();
    AppendLog($"MAVLink signing enabled with key '{name}'.");
  }

  private async Task DeleteSigningKeyAsync() {
    var name = await ChooseSigningKeyAsync("Delete Signing Key");
    if (name == null || !await Dialogs.Confirm(
            "Delete Signing Key", $"Delete stored key '{name}'?")) {
      return;
    }

    MAVAuthKeys.Keys.Remove(name);
    MAVAuthKeys.Save();
    if (Settings.Instance["mavlink_signing_key_name"] == name) {
      Settings.Instance["mavlink_signing_key_name"] = "";
      Settings.Instance.Save();
    }
    AppendLog($"MAVLink signing key '{name}' deleted.");
  }

  private async Task DisableSigningAsync() {
    if (!RequireConnection() || !await Dialogs.Confirm(
            "Disable MAVLink Signing",
            "Disable signing on the connected vehicle and this link?")) {
      return;
    }

    var mav = _comPort.MAV;
    _comPort.setupSigning(mav.sysid, mav.compid, "");
    Settings.Instance["mavlink_signing_key_name"] = "";
    Settings.Instance.Save();
    AppendLog("MAVLink signing disabled.");
  }

  private static async Task<string?> ChooseSigningKeyAsync(string title) {
    var names = MAVAuthKeys.Keys.Keys.OrderBy(name => name).ToArray();
    if (names.Length == 0) {
      await Dialogs.Alert(title, "No signing keys are stored. Add one first.");
      return null;
    }

    return await Dialogs.Choice(title, "Select a stored key", names);
  }

  private async Task NoticeAsync(string name, string desc, string opens) {
    AppendLog($"{name}: not yet ported (upstream opens {opens}).");
    await Dialogs.Alert(
        $"{name} — not yet ported",
        $"{desc}\n\nIn Mission Planner this opens the \"{opens}\" window, which has not been " +
        "ported to the Avalonia GCS yet.");
  }

  private async Task AnonLogAsync() {
    var sp = Dialogs.Owner?.StorageProvider;
    if (sp == null) {
      AppendLog("Anon Log: no window available.");
      return;
    }

    await Dialogs.Alert("Anon Log", "This is beta, please confirm the output file.");

    var picked = await sp.OpenFilePickerAsync(new FilePickerOpenOptions {
      Title = "Select a log to anonymise",
      AllowMultiple = false,
      FileTypeFilter = new[]
        {
                new FilePickerFileType("tlog or bin/log") { Patterns = new[] { "*.tlog", "*.bin", "*.log" } },
                new FilePickerFileType("All files") { Patterns = new[] { "*" } },
            },
    });

    var input = picked.FirstOrDefault()?.TryGetLocalPath();
    if (input == null) {
      return;
    }

    var ext = Path.GetExtension(input).ToLowerInvariant();
    if (ext == ".bin") {
      ext = ".log";
    }

    var suggested = Path.GetFileNameWithoutExtension(input) + "-anon" + ext;
    var save = await sp.SaveFilePickerAsync(new FilePickerSaveOptions {
      Title = "Save anonymised log",
      SuggestedFileName = suggested,
      DefaultExtension = ext.TrimStart('.'),
    });

    var output = save?.TryGetLocalPath();
    if (output == null) {
      return;
    }

    AppendLog($"Anonymising {input} -> {output} …");
    try {
      await Task.Run(() => Privacy.anonymise(input, output));
      AppendLog("Anon Log: done.");
    } catch (Exception ex) {
      AppendLog("Anon Log failed: " + ex.Message);
    }
  }
}

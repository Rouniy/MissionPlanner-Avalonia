using System.Xml.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using MissionPlannerAvalonia.Services;
using MissionPlannerAvalonia.ViewModels;
using MissionPlannerAvalonia.Views;
using Newtonsoft.Json.Linq;

namespace MissionPlannerAvalonia.Tests;

public class CotEventSerializerTests {
  [Fact]
  public void Serialize_creates_invariant_cot_event() {
    var timestamp = new DateTime(2026, 8, 21, 12, 34, 56, DateTimeKind.Utc);
    var xml = CotEventSerializer.Serialize(
        "mp-1-1", "a-f-A-M-F-Q", 35.1234567, 33.7654321, 123.45, 270.5, 12.25,
        "Copter-1", timestampUtc: timestamp);
    var root = XDocument.Parse(xml).Root!;

    Assert.Equal("event", root.Name.LocalName);
    Assert.Equal("mp-1-1", root.Attribute("uid")?.Value);
    Assert.Equal("2026-08-21T12:34:56.000Z", root.Attribute("time")?.Value);
    Assert.Equal("35.1234567", root.Element("point")?.Attribute("lat")?.Value);
    Assert.Equal("33.7654321", root.Element("point")?.Attribute("lon")?.Value);
    Assert.Equal("Copter-1", root.Element("detail")?.Element("contact")?.Attribute("callsign")?.Value);
  }

  [Fact]
  public void Serialize_emits_upstream_advanced_identity_elements() {
    var xml = CotEventSerializer.Serialize(
        "UAS-42", "a-f-A-M-F-Q", 35, 33, 123, 270, 12,
        callsign: "Global-42", timestampUtc: new DateTime(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc),
        identity: new CotIdentityDetail(
            IncludeTakv: true,
            Callsign: "Falcon 42",
            Endpoint: "10.0.0.42:4242:tcp",
            Vmf: "VMF-42"));
    var detail = XDocument.Parse(xml).Root!.Element("detail")!;

    Assert.Equal(new[] { "takv", "contact", "uid", "track" },
        detail.Elements().Select(element => element.Name.LocalName));
    Assert.NotNull(detail.Element("takv"));
    Assert.Equal("Falcon 42", detail.Element("contact")?.Attribute("callsign")?.Value);
    Assert.Equal("10.0.0.42:4242:tcp", detail.Element("contact")?.Attribute("endpoint")?.Value);
    Assert.Equal("VMF-42", detail.Element("uid")?.Attribute("vmf")?.Value);
  }

  [Fact]
  public void Serialize_omits_blank_advanced_elements_and_keeps_global_callsign() {
    var xml = CotEventSerializer.Serialize(
        "mp-1-1", "a-f-A-M-F-Q", 35, 33, 123, 270, 12,
        callsign: "Copter-1",
        identity: new CotIdentityDetail(Callsign: "", Endpoint: null, Vmf: ""));
    var detail = XDocument.Parse(xml).Root!.Element("detail")!;

    Assert.Null(detail.Element("takv"));
    Assert.Null(detail.Element("uid"));
    Assert.Equal("Copter-1", detail.Element("contact")?.Attribute("callsign")?.Value);
    Assert.Null(detail.Element("contact")?.Attribute("endpoint"));
  }

  [Fact]
  public void Identity_rows_round_trip_the_official_cotuid_grid_format() {
    var rows = new[] {
      new CotIdentityRow {
        SystemId = "42",
        Uid = "UAS-42",
        IncludeTakv = true,
        ContactCallsign = "Falcon 42",
        ContactEndpoint = "10.0.0.42:4242:tcp",
        Vmf = "VMF-42",
      },
      new CotIdentityRow { SystemId = "7", Uid = "UAS-7" },
    };

    string json = SerialOutputCotViewModel.SerializeIdentityRows(rows);
    var grid = JArray.Parse(json);
    Assert.Equal(2, grid.Count);
    Assert.Equal(6, Assert.IsType<JArray>(grid[0]).Count);

    var restored = SerialOutputCotViewModel.ParseIdentityRows(json);
    Assert.Equal(2, restored.Count);
    Assert.Equal("42", restored[0].SystemId);
    Assert.Equal("UAS-42", restored[0].Uid);
    Assert.True(restored[0].IncludeTakv);
    Assert.Equal("Falcon 42", restored[0].ContactCallsign);
    Assert.Equal("10.0.0.42:4242:tcp", restored[0].ContactEndpoint);
    Assert.Equal("VMF-42", restored[0].Vmf);
    Assert.Equal("UAS-7", restored[1].Uid);
  }

  [Fact]
  public void Identity_uid_override_and_fallback_match_runtime_mapping() {
    Assert.Equal("UAS-42", SerialOutputCotViewModel.ResolveEventUid(
        "MissionPlanner", 42, 1, hasIdentityRow: true, identityUid: "UAS-42"));
    Assert.Equal("MissionPlanner-42-100", SerialOutputCotViewModel.ResolveEventUid(
        "MissionPlanner", 42, 100, hasIdentityRow: false, identityUid: null));
    Assert.Equal("", SerialOutputCotViewModel.ResolveEventUid(
        "MissionPlanner", 42, 1, hasIdentityRow: true, identityUid: null));
  }

  [Fact]
  public void Identity_grid_preserves_official_text_null_and_out_of_range_cells() {
    Assert.Empty(SerialOutputCotViewModel.ParseIdentityRows("not json"));
    const string json = "[[\"custom\",\"uid-a\",null,null,\"*:4242:tcp\",null],"
        + "[999,\"uid-b\",false,\"Callsign\",null,\"VMF\"]]";

    var rows = SerialOutputCotViewModel.ParseIdentityRows(json);
    Assert.Equal(2, rows.Count);
    Assert.Equal("custom", rows[0].SystemId);
    Assert.Null(rows[0].ContactCallsign);
    Assert.Equal("*:4242:tcp", rows[0].ContactEndpoint);
    Assert.Equal("999", rows[1].SystemId);
    Assert.Equal("VMF", rows[1].Vmf);
  }

  [AvaloniaFact]
  public void Cot_view_exposes_one_editable_six_column_identity_grid() {
    var view = new SerialOutputCotView();
    var grid = view.FindControl<DataGrid>("IdentityGrid");

    Assert.NotNull(grid);
    Assert.Equal(6, grid.Columns.Count);
    Assert.All(grid.Columns, column => Assert.False(column.IsReadOnly));
    view.CommitIdentityEdits();
  }
}

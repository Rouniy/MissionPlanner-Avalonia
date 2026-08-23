using System.Runtime.InteropServices;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using MissionPlanner;
using MissionPlannerAvalonia.Services;
using MissionPlannerAvalonia.ViewModels.Setup;
using MissionPlannerAvalonia.Views.Setup;

namespace MissionPlannerAvalonia.Tests;

public class NvModemTests {
  [Fact]
  public void Registers_exact_skycomm_message_layouts_and_crc_extras() {
    NvModemMavlinkDialect.Register();

    Assert.Equal(28, Marshal.SizeOf<NvRxStatMessage>());
    Assert.Equal(78, Marshal.SizeOf<Nv5LinkStatusMessage>());
    Assert.Equal(103, Marshal.SizeOf<Nv5RtspConfigMessage>());
    Assert.Equal(9, Marshal.SizeOf<Nv5RtspConfigAckMessage>());
    AssertMessage(NvModemMessageIds.NvRxStat, 49, 28, 28, typeof(NvRxStatMessage));
    AssertMessage(NvModemMessageIds.Nv5LinkStatus, 165, 77, 78,
        typeof(Nv5LinkStatusMessage));
    AssertMessage(NvModemMessageIds.Nv5RtspConfig, 127, 103, 103,
        typeof(Nv5RtspConfigMessage));
    AssertMessage(NvModemMessageIds.Nv5RtspConfigAck, 193, 9, 9,
        typeof(Nv5RtspConfigAckMessage));
  }

  [Fact]
  public void Parses_custom_nv5_packet_through_the_shared_mission_planner_parser() {
    NvModemMavlinkDialect.Register();
    var expected = new Nv5LinkStatusMessage {
      SampleMs = 1000,
      FrequencyHz = 868_000_000,
      TxRadioBytes = 125_000,
      PacketRssiDbmX10 = -873,
      PacketSnrDbX10 = 42,
      Channel = 2,
      RadioChip = 0,
      Role = 2,
      Modulation = 1,
      Flags = 0xc7,
      LinkQuality = 97,
      TxState = 1,
    };

    MAVLink.MAVLinkMessage packet = Packet(
        NvModemMessageIds.Nv5LinkStatus, expected, systemId: 41, componentId: 68);
    Nv5LinkStatusMessage actual = packet.ToStructure<Nv5LinkStatusMessage>();

    Assert.Equal((uint)NvModemMessageIds.Nv5LinkStatus, packet.msgid);
    Assert.Equal(41, packet.sysid);
    Assert.Equal(868_000_000u, actual.FrequencyHz);
    Assert.Equal(-873, actual.PacketRssiDbmX10);
    Assert.Equal(2, actual.Channel);
    Assert.Equal(97, actual.LinkQuality);
  }

  [Theory]
  [InlineData(MAVLink.MAV_PARAM_TYPE.UINT8, 255d)]
  [InlineData(MAVLink.MAV_PARAM_TYPE.INT8, -128d)]
  [InlineData(MAVLink.MAV_PARAM_TYPE.UINT16, 65535d)]
  [InlineData(MAVLink.MAV_PARAM_TYPE.INT16, -32768d)]
  [InlineData(MAVLink.MAV_PARAM_TYPE.UINT32, 4294967295d)]
  [InlineData(MAVLink.MAV_PARAM_TYPE.INT32, -2147483648d)]
  public void Preserves_bytewise_mavlink_integer_parameter_encoding(
      MAVLink.MAV_PARAM_TYPE type, double expected) {
    float wire = NvModemParameterCodec.Encode(expected, (byte)type);

    Assert.Equal(expected, NvModemParameterCodec.Decode(wire, (byte)type));
  }

  [Fact]
  public void Carries_nv5settings_descriptions_and_corrected_nv4_apply_parameter() {
    Assert.True(NvModemCatalog.IsNv4Signature("REFRESH_SETTING"));
    Assert.False(NvModemCatalog.IsNv4Signature("REFRESH_SETTINGS"));
    Assert.True(NvModemCatalog.IsReadOnly("REFRESH_SETTING"));
    Assert.Contains("writes it automatically",
        NvModemCatalog.Description("REFRESH_SETTING"), StringComparison.OrdinalIgnoreCase);
    Assert.Contains("868000 = 868 MHz", NvModemCatalog.Description("CH1_FREQ_KHZ"));
    Assert.Contains("0=receiver", NvModemCatalog.Description("CH2_ROLE"));
    Assert.Equal("Teensy · RFM/SX1278",
        NvModemCatalog.HardwareModel(NvModemGeneration.Nv4, 99));
  }

  [Fact]
  public void Keeps_same_system_component_devices_separate_by_existing_mavlink_link() {
    var transport = new FakeTransport();
    using var viewModel = new NvModemViewModel(transport, () => DateTime.UtcNow,
        startTimer: false);
    var udp = new NvModemLink(new MAVLinkInterface(), "udp://0.0.0.0:14550");
    var serial = new NvModemLink(new MAVLinkInterface(), "serial:/dev/ttyUSB0:115200");
    var status = new Nv5LinkStatusMessage { Channel = 1, RadioChip = 0, Role = 1 };

    viewModel.HandlePacket(udp, Packet(NvModemMessageIds.Nv5LinkStatus, status, 7, 68));
    viewModel.HandlePacket(serial, Packet(NvModemMessageIds.Nv5LinkStatus, status, 7, 68));

    Assert.Equal(2, viewModel.Devices.Count);
    Assert.Contains(viewModel.Devices, item => item.Label.Contains("udp://", StringComparison.Ordinal));
    Assert.Contains(viewModel.Devices, item => item.Label.Contains("serial:", StringComparison.Ordinal));
  }

  [Fact]
  public void Clears_parameter_rows_immediately_when_switching_devices_and_reuses_target_link() {
    var transport = new FakeTransport();
    DateTime now = DateTime.UtcNow;
    using var viewModel = new NvModemViewModel(transport, () => now, startTimer: false);
    var first = new NvModemLink(new MAVLinkInterface(), "UDP first");
    var second = new NvModemLink(new MAVLinkInterface(), "TCP second");
    var status = new Nv5LinkStatusMessage { Channel = 1, RadioChip = 0, Role = 0 };
    viewModel.HandlePacket(first, Packet(NvModemMessageIds.Nv5LinkStatus, status, 1, 68));
    viewModel.HandlePacket(first, ParameterPacket("MODEM_PROFILE", 7, 1, 2, 1, 68));
    Assert.Single(viewModel.Parameters);
    viewModel.HandlePacket(second, Packet(NvModemMessageIds.Nv5LinkStatus, status, 2, 68));

    transport.Sent.Clear();
    viewModel.SelectedDevice = viewModel.Devices.Single(item =>
        item.Label.Contains("TCP second", StringComparison.Ordinal));

    Assert.Empty(viewModel.Parameters);
    Assert.Contains(transport.Sent, sent => ReferenceEquals(sent.Link, second)
        && sent.Packet is MAVLink.mavlink_param_request_list_t request
        && request.target_system == 2 && request.target_component == 68);
    Assert.DoesNotContain(transport.Sent, sent => ReferenceEquals(sent.Link, first));
  }

  [Fact]
  public void Nv5_key_write_accepts_protected_minus_one_ack_and_stays_on_discovery_link() {
    var transport = new FakeTransport();
    using var viewModel = new NvModemViewModel(transport, () => DateTime.UtcNow,
        startTimer: false);
    var source = new NvModemLink(new MAVLinkInterface(), "UDP NV5");
    var status = new Nv5LinkStatusMessage { Channel = 1, RadioChip = 0, Role = 1 };
    viewModel.HandlePacket(source, Packet(NvModemMessageIds.Nv5LinkStatus, status, 9, 68));
    const int count = 18;
    viewModel.HandlePacket(source, ParameterPacket("MODEM_PROFILE", 7, 5, count, 9, 68));
    viewModel.HandlePacket(source, ParameterPacket("CH1_MOD", 1, 5, count, 9, 68));
    for (int index = 0; index < 16; index++) {
      viewModel.HandlePacket(source, ParameterPacket($"CH1_KEY{index:00}",
          65 + index, 6, count, 9, 68));
    }
    transport.Sent.Clear();
    viewModel.KeyText = "ABCDEFGHIJKLMNOP";

    viewModel.SetKeyCommand.Execute(null);
    for (int index = 0; index < 16; index++) {
      FakeTransport.SentPacket sent = transport.Sent[^1];
      var write = Assert.IsType<MAVLink.mavlink_param_set_t>(sent.Packet);
      Assert.Same(source, sent.Link);
      Assert.Equal($"CH1_KEY{index:00}", NvModemParameterCodec.Name(write.param_id));
      viewModel.HandlePacket(source, ParameterPacket($"CH1_KEY{index:00}", -1, 6,
          count, 9, 68));
    }

    Assert.False(viewModel.IsBusy);
    Assert.Equal("ABCDEFGHIJKLMNOP", viewModel.KeyText);
    Assert.Equal(16, transport.Sent.Count(sent => sent.Packet is MAVLink.mavlink_param_set_t));
  }

  [Fact]
  public void Nv4_key_transaction_finishes_with_singular_refresh_setting_on_same_link() {
    var transport = new FakeTransport();
    using var viewModel = new NvModemViewModel(transport, () => DateTime.UtcNow,
        startTimer: false);
    var source = new NvModemLink(new MAVLinkInterface(), "UDP NV4");
    const int count = 10;
    viewModel.HandlePacket(source, Packet(NvModemMessageIds.NvRxStat,
        new NvRxStatMessage(), 1, 16));
    viewModel.HandlePacket(source, ParameterPacket("HW_VERSION", 4, 5, count, 1, 16));
    for (int index = 1; index <= 8; index++) {
      viewModel.HandlePacket(source, ParameterPacket($"ENC_KEY_BYTE{index}", index, 6,
          count, 1, 16));
    }
    viewModel.HandlePacket(source, ParameterPacket("REFRESH_SETTING", 0, 5,
        count, 1, 16));
    transport.Sent.Clear();
    viewModel.KeyText = "ABCDEFGHIJKLMNOPQRSTUVWXYZ012345";

    viewModel.SetKeyCommand.Execute(null);
    for (int writeIndex = 0; writeIndex < 9; writeIndex++) {
      var write = Assert.IsType<MAVLink.mavlink_param_set_t>(transport.Sent[^1].Packet);
      string name = NvModemParameterCodec.Name(write.param_id);
      Assert.Same(source, transport.Sent[^1].Link);
      viewModel.HandlePacket(source, ParameterPacket(name,
          NvModemParameterCodec.Decode(write.param_value, write.param_type), write.param_type,
          count, 1, 16));
    }

    string[] writtenNames = [.. transport.Sent
        .Where(sent => sent.Packet is MAVLink.mavlink_param_set_t)
        .Select(sent => NvModemParameterCodec.Name(
            ((MAVLink.mavlink_param_set_t)sent.Packet).param_id))];
    Assert.Equal("REFRESH_SETTING", writtenNames[^1]);
    Assert.DoesNotContain("REFRESH_SETTINGS", writtenNames);
    Assert.False(viewModel.IsBusy);
  }

  [Fact]
  public void Parameter_file_roundtrip_includes_described_values_and_rtsp_path() {
    var transport = new FakeTransport();
    using var viewModel = new NvModemViewModel(transport, () => DateTime.UtcNow,
        startTimer: false);
    var source = new NvModemLink(new MAVLinkInterface(), "TCP NV5");
    var status = new Nv5LinkStatusMessage { Channel = 1, RadioChip = 0, Role = 0 };
    viewModel.HandlePacket(source, Packet(NvModemMessageIds.Nv5LinkStatus, status, 3, 68));
    viewModel.HandlePacket(source, ParameterPacket("MODEM_PROFILE", 7, 5, 2, 3, 68));
    viewModel.HandlePacket(source, ParameterPacket("CH1_FREQ_KHZ", 868000, 5, 2, 3, 68));

    Assert.True(viewModel.ImportParameterFile("CH1_FREQ_KHZ,915000\n#NV5_RTSP_PATH,/cam/main\n"));
    string exported = viewModel.ExportParameterFile();

    Assert.Contains("CH1_FREQ_KHZ,915000", exported);
    Assert.Contains("#NV5_RTSP_PATH,/cam/main", exported);
    Assert.Contains("key-byte values", exported, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("\r\r\n", exported);
    Assert.True(viewModel.HasPendingChanges);
  }

  [Fact]
  public void Late_rtsp_read_does_not_overwrite_a_locally_staged_path() {
    var transport = new FakeTransport();
    using var viewModel = new NvModemViewModel(transport, () => DateTime.UtcNow,
        startTimer: false);
    var source = new NvModemLink(new MAVLinkInterface(), "UDP NV5");
    var status = new Nv5LinkStatusMessage { Channel = 1, RadioChip = 0, Role = 0 };
    viewModel.HandlePacket(source, Packet(NvModemMessageIds.Nv5LinkStatus, status, 4, 68));
    viewModel.RtspPath = "/operator/staged";

    viewModel.HandlePacket(source, Packet(NvModemMessageIds.Nv5RtspConfig,
        RtspPacket("/device/current"), 4, 68));

    Assert.Equal("/operator/staged", viewModel.RtspPath);
    Assert.True(viewModel.HasPendingChanges);
  }

  [Fact]
  public void Silent_parameter_read_retries_then_stops_without_blocking_the_view_model() {
    var transport = new FakeTransport();
    DateTime now = DateTime.UtcNow;
    using var viewModel = new NvModemViewModel(transport, () => now, startTimer: false);
    var source = new NvModemLink(new MAVLinkInterface(), "UDP silent modem");
    var status = new Nv5LinkStatusMessage { Channel = 1, RadioChip = 0, Role = 0 };
    viewModel.HandlePacket(source, Packet(NvModemMessageIds.Nv5LinkStatus, status, 5, 68));
    transport.Sent.Clear();

    now += TimeSpan.FromMilliseconds(2100);
    viewModel.ServiceTransactions();
    now += TimeSpan.FromMilliseconds(2100);
    viewModel.ServiceTransactions();
    now += TimeSpan.FromMilliseconds(3100);
    viewModel.ServiceTransactions();

    Assert.Equal(2, transport.Sent.Count(sent =>
        sent.Packet is MAVLink.mavlink_param_request_list_t));
    Assert.False(viewModel.IsBusy);
    Assert.StartsWith("Error:", viewModel.Status, StringComparison.Ordinal);
    Assert.Contains("Press Refresh selected", viewModel.Status, StringComparison.Ordinal);
  }

  [Fact]
  public void Factory_preset_is_staged_locally_with_the_nv5settings_lr2021_defaults() {
    var transport = new FakeTransport();
    using var viewModel = new NvModemViewModel(transport, () => DateTime.UtcNow,
        startTimer: false);
    var source = new NvModemLink(new MAVLinkInterface(), "TCP NV5");
    var status = new Nv5LinkStatusMessage { Channel = 1, RadioChip = 0, Role = 2 };
    viewModel.HandlePacket(source, Packet(NvModemMessageIds.Nv5LinkStatus, status, 6, 68));
    viewModel.HandlePacket(source, ParameterPacket("MODEM_PROFILE", 7, 5, 4, 6, 68));
    viewModel.HandlePacket(source, ParameterPacket("CH1_CHIP", 0, 5, 4, 6, 68));
    viewModel.HandlePacket(source, ParameterPacket("CH1_MOD", 0, 5, 4, 6, 68));
    viewModel.HandlePacket(source, ParameterPacket("CH1_FRAME", 64, 5, 4, 6, 68));
    transport.Sent.Clear();

    viewModel.StageRadioPresetCommand.Execute("factory");
    string staged = viewModel.ExportParameterFile();

    Assert.Contains("CH1_MOD,1", staged);
    Assert.Contains("CH1_FRAME,240", staged);
    Assert.Contains("CH1_FLRC_RATE,1300000", staged);
    Assert.True(viewModel.HasPendingChanges);
    Assert.Empty(transport.Sent);
  }

  [AvaloniaFact]
  public void Nv_modem_view_and_navigation_entry_are_available() {
    var transport = new FakeTransport();
    using var viewModel = new NvModemViewModel(transport, () => DateTime.UtcNow,
        startTimer: false);
    var view = new NvModemView { DataContext = viewModel };

    Assert.NotNull(view.FindControl<DataGrid>("ParametersGrid"));
    Assert.NotNull(view.FindControl<Button>("LoadParametersButton"));
    Assert.NotNull(view.FindControl<Button>("SaveParametersButton"));
    using var setup = new MissionPlannerAvalonia.ViewModels.SetupViewModel();
    int sik = setup.Pages.ToList().FindIndex(page => page.Header == "Sik Radio");
    int nv = setup.Pages.ToList().FindIndex(page => page.Header == "NV Modem");
    Assert.Equal(sik + 1, nv);
  }

  private static void AssertMessage(uint id, byte crc, uint minimum, uint length, Type type) {
    MAVLink.message_info info = Assert.Single(MAVLink.MAVLINK_MESSAGE_INFOS,
        candidate => candidate.msgid == id);
    Assert.Equal(crc, info.crc);
    Assert.Equal(minimum, info.minlength);
    Assert.Equal(length, info.length);
    Assert.Equal(type, info.type);
  }

  private static MAVLink.MAVLinkMessage ParameterPacket(
      string name, double value, byte type, ushort count, byte systemId, byte componentId) =>
      Packet((uint)MAVLink.MAVLINK_MSG_ID.PARAM_VALUE,
          new MAVLink.mavlink_param_value_t {
            param_id = NvModemParameterCodec.NameBytes(name),
            param_value = NvModemParameterCodec.Encode(value, type),
            param_type = type,
            param_count = count,
            param_index = 0,
          }, systemId, componentId);

  private static Nv5RtspConfigMessage RtspPacket(string path) {
    byte[] bytes = new byte[96];
    Encoding.Latin1.GetBytes(path).CopyTo(bytes, 0);
    return new Nv5RtspConfigMessage {
      Operation = 2,
      Path = bytes,
    };
  }

  private static MAVLink.MAVLinkMessage Packet(
      uint id, object payload, byte systemId, byte componentId) {
    NvModemMavlinkDialect.Register();
    var generator = new MAVLink.MavlinkParse();
    byte[] bytes = generator.GenerateMAVLinkPacket20(
        (MAVLink.MAVLINK_MSG_ID)id, payload, false, systemId, componentId);
    return new MAVLink.MavlinkParse().ReadPacket(new MemoryStream(bytes))!;
  }

  private sealed class FakeTransport : INvModemMavlinkTransport {
    internal sealed record SentPacket(
        NvModemLink Link, object Packet, byte SystemId, byte ComponentId);

    internal List<NvModemLink> Links { get; } = [];
    internal List<SentPacket> Sent { get; } = [];

    public event Action<NvModemLink, MAVLink.MAVLinkMessage>? PacketReceived {
      add { }
      remove { }
    }

    public event Action? LinksChanged {
      add { }
      remove { }
    }

    public IReadOnlyList<NvModemLink> Snapshot() => Links;

    public bool Send(NvModemLink source, object packet, byte systemId, byte componentId) {
      Sent.Add(new SentPacket(source, packet, systemId, componentId));
      return true;
    }

    public void Dispose() { }
  }
}

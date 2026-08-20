using MissionPlannerAvalonia.Services;

namespace MissionPlannerAvalonia.Tests;

public class JoystickControlTests {
  [Fact]
  public void Rc_override_contains_mapped_values_and_upstream_ignore_sentinels() {
    var mapped = new bool[18];
    var values = new short[18];
    mapped[0] = true;
    mapped[2] = true;
    mapped[8] = true;
    values[0] = 1100;
    values[2] = 1750;
    values[8] = 1900;

    var packet = JoystickControlPackets.BuildRcOverride(7, 1,
        new JoystickOutputSnapshot(false, mapped, values));

    Assert.Equal((byte)7, packet.target_system);
    Assert.Equal((byte)1, packet.target_component);
    Assert.Equal((ushort)1100, packet.chan1_raw);
    Assert.Equal(ushort.MaxValue, packet.chan2_raw);
    Assert.Equal((ushort)1750, packet.chan3_raw);
    Assert.Equal((ushort)0, packet.chan10_raw);
    Assert.Equal((ushort)1900, packet.chan9_raw);
  }

  [Fact]
  public void Rc_override_preserves_mapped_uint16_max_sentinel() {
    var mapped = new bool[18];
    var values = new short[18];
    mapped[0] = true;
    values[0] = -1;

    var packet = JoystickControlPackets.BuildRcOverride(1, 1,
        new JoystickOutputSnapshot(false, mapped, values));

    Assert.Equal(ushort.MaxValue, packet.chan1_raw);
  }

  [Fact]
  public void Manual_control_targets_vehicle_and_uses_first_four_mapped_channels() {
    var mapped = new bool[18];
    var values = new short[18];
    for (int i = 0; i < 4; i++) {
      mapped[i] = true;
    }
    values[0] = -500;
    values[1] = 250;
    values[2] = 750;
    values[3] = 1000;

    var packet = JoystickControlPackets.BuildManualControl(42,
        new JoystickOutputSnapshot(true, mapped, values));

    Assert.Equal((byte)42, packet.target);
    Assert.Equal((short)-500, packet.x);
    Assert.Equal((short)250, packet.y);
    Assert.Equal((short)750, packet.z);
    Assert.Equal((short)1000, packet.r);
  }

  [Fact]
  public void Sitl_datagram_is_eight_little_endian_rc_channels() {
    var values = new short[18];
    values[0] = 1000;
    values[1] = 1500;
    values[2] = 2000;
    values[7] = 1234;

    var packet = JoystickControlPackets.BuildSitlDatagram(
        new JoystickOutputSnapshot(false, new bool[18], values));

    Assert.Equal(16, packet.Length);
    Assert.Equal(new byte[] { 0xe8, 0x03 }, packet[0..2]);
    Assert.Equal(new byte[] { 0xdc, 0x05 }, packet[2..4]);
    Assert.Equal(new byte[] { 0xd0, 0x07 }, packet[4..6]);
    Assert.Equal(new byte[] { 0xd2, 0x04 }, packet[14..16]);
  }
}

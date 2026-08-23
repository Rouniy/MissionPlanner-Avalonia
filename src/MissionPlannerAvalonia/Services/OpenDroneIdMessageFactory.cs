using System;
using System.Text;

namespace MissionPlannerAvalonia.Services;

#pragma warning disable CS0612 // MAVLink marks the current Open Drone ID wire structs obsolete.

internal sealed record OpenDroneIdConfiguration(
    MAVLink.MAV_ODID_ID_TYPE UasIdType,
    string UasId,
    MAVLink.MAV_ODID_UA_TYPE UaType,
    MAVLink.MAV_ODID_DESC_TYPE DescriptionType,
    string Description,
    ushort AreaCount,
    ushort AreaRadiusM,
    float AreaCeilingM,
    float AreaFloorM,
    MAVLink.MAV_ODID_CATEGORY_EU CategoryEu,
    MAVLink.MAV_ODID_CLASS_EU ClassEu,
    MAVLink.MAV_ODID_CLASSIFICATION_TYPE ClassificationType,
    MAVLink.MAV_ODID_OPERATOR_LOCATION_TYPE OperatorLocationType,
    MAVLink.MAV_ODID_OPERATOR_ID_TYPE OperatorIdType,
    string OperatorId);

internal enum OpenDroneIdExtendedMessage {
  BasicId,
  System,
  SelfId,
  OperatorId,
}

internal static class OpenDroneIdMessageFactory {
  internal const float UnknownAltitudeM = -1000;
  internal const int UasIdLength = 20;
  internal const int DescriptionLength = 23;
  internal const int OperatorIdLength = 20;
  private static readonly DateTimeOffset Epoch2019 =
      new(2019, 1, 1, 0, 0, 0, TimeSpan.Zero);

  internal static bool TryValidate(OpenDroneIdConfiguration configuration, out string error) {
    if (!Enum.IsDefined(configuration.UasIdType)
        || !Enum.IsDefined(configuration.UaType)
        || !Enum.IsDefined(configuration.DescriptionType)
        || !Enum.IsDefined(configuration.CategoryEu)
        || !Enum.IsDefined(configuration.ClassEu)
        || !Enum.IsDefined(configuration.ClassificationType)
        || !Enum.IsDefined(configuration.OperatorLocationType)
        || !Enum.IsDefined(configuration.OperatorIdType)) {
      error = "One or more Open Drone ID enum values are invalid.";
      return false;
    }
    if (!TryValidateAscii(configuration.UasId, UasIdLength, "UAS ID", out error)
        || !TryValidateAscii(
            configuration.Description, DescriptionLength, "Self ID description", out error)
        || !TryValidateAscii(
            configuration.OperatorId, OperatorIdLength, "Operator ID", out error)) {
      return false;
    }
    if (configuration.AreaCount == 0) {
      error = "Aircraft count must be at least one.";
      return false;
    }
    if (!ValidAltitude(configuration.AreaCeilingM)
        || !ValidAltitude(configuration.AreaFloorM)) {
      error = "Operation ceiling and floor must be finite values or -1000 for unknown.";
      return false;
    }
    if (configuration.AreaCeilingM != UnknownAltitudeM
        && configuration.AreaFloorM != UnknownAltitudeM
        && configuration.AreaFloorM > configuration.AreaCeilingM) {
      error = "Operation floor cannot be above the ceiling.";
      return false;
    }
    error = "";
    return true;
  }

  internal static MAVLink.mavlink_open_drone_id_basic_id_t BasicId(
      byte systemId, OpenDroneIdConfiguration configuration) {
    return MAVLink.mavlink_open_drone_id_basic_id_t.PopulateXMLOrder(
        systemId, BroadcastComponent, EmptyIdOrMac(), (byte)configuration.UasIdType,
        (byte)configuration.UaType, FixedAscii(configuration.UasId, UasIdLength));
  }

  internal static MAVLink.mavlink_open_drone_id_system_t System(
      byte systemId,
      OpenDroneIdConfiguration configuration,
      NmeaGgaFix? fix,
      DateTimeOffset now) {
    bool fresh = fix.HasValue;
    NmeaGgaFix position = fix.GetValueOrDefault();
    return MAVLink.mavlink_open_drone_id_system_t.PopulateXMLOrder(
        systemId,
        BroadcastComponent,
        EmptyIdOrMac(),
        (byte)configuration.OperatorLocationType,
        (byte)configuration.ClassificationType,
        fresh ? DegreesE7(position.Latitude, latitude: true) : 0,
        fresh ? DegreesE7(position.Longitude, latitude: false) : 0,
        configuration.AreaCount,
        configuration.AreaRadiusM,
        configuration.AreaCeilingM,
        configuration.AreaFloorM,
        (byte)configuration.CategoryEu,
        (byte)configuration.ClassEu,
        fresh ? (float)position.GeodeticAltitudeM : UnknownAltitudeM,
        Timestamp2019(now));
  }

  internal static MAVLink.mavlink_open_drone_id_system_update_t SystemUpdate(
      byte systemId, NmeaGgaFix fix, DateTimeOffset now) =>
      MAVLink.mavlink_open_drone_id_system_update_t.PopulateXMLOrder(
          systemId,
          BroadcastComponent,
          DegreesE7(fix.Latitude, latitude: true),
          DegreesE7(fix.Longitude, latitude: false),
          (float)fix.GeodeticAltitudeM,
          Timestamp2019(now));

  internal static MAVLink.mavlink_open_drone_id_self_id_t SelfId(
      byte systemId, OpenDroneIdConfiguration configuration) {
    return MAVLink.mavlink_open_drone_id_self_id_t.PopulateXMLOrder(
        systemId, BroadcastComponent, EmptyIdOrMac(), (byte)configuration.DescriptionType,
        FixedAscii(configuration.Description, DescriptionLength));
  }

  internal static MAVLink.mavlink_open_drone_id_operator_id_t OperatorId(
      byte systemId, OpenDroneIdConfiguration configuration) {
    return MAVLink.mavlink_open_drone_id_operator_id_t.PopulateXMLOrder(
        systemId, BroadcastComponent, EmptyIdOrMac(), (byte)configuration.OperatorIdType,
        FixedAscii(configuration.OperatorId, OperatorIdLength));
  }

  internal static object Extended(
      OpenDroneIdExtendedMessage kind,
      byte systemId,
      OpenDroneIdConfiguration configuration,
      NmeaGgaFix? fix,
      DateTimeOffset now) => kind switch {
        OpenDroneIdExtendedMessage.BasicId => BasicId(systemId, configuration),
        OpenDroneIdExtendedMessage.System => System(systemId, configuration, fix, now),
        OpenDroneIdExtendedMessage.SelfId => SelfId(systemId, configuration),
        OpenDroneIdExtendedMessage.OperatorId => OperatorId(systemId, configuration),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
      };

  internal static uint Timestamp2019(DateTimeOffset now) {
    double seconds = (now.ToUniversalTime() - Epoch2019).TotalSeconds;
    return seconds <= 0 ? 0 : seconds >= uint.MaxValue ? uint.MaxValue : (uint)seconds;
  }

  internal static byte[] FixedAscii(string? value, int length) {
    var result = new byte[length];
    if (string.IsNullOrEmpty(value)) {
      return result;
    }
    Encoding.ASCII.GetBytes(value.AsSpan(), result);
    return result;
  }

  private static byte BroadcastComponent =>
      (byte)MAVLink.MAV_COMPONENT.MAV_COMP_ID_ALL;

  private static byte[] EmptyIdOrMac() => new byte[20];

  private static bool TryValidateAscii(
      string? value, int maxLength, string label, out string error) {
    value ??= "";
    if (value.Length > maxLength) {
      error = $"{label} must not exceed {maxLength} ASCII characters.";
      return false;
    }
    foreach (char character in value) {
      if (character is < (char)0x20 or > (char)0x7e) {
        error = $"{label} must contain printable ASCII characters only.";
        return false;
      }
    }
    error = "";
    return true;
  }

  private static bool ValidAltitude(float value) => float.IsFinite(value);

  private static int DegreesE7(double degrees, bool latitude) {
    double limit = latitude ? 90 : 180;
    if (!double.IsFinite(degrees) || degrees < -limit || degrees > limit) {
      throw new ArgumentOutOfRangeException(nameof(degrees));
    }
    return checked((int)Math.Round(degrees * 1e7, MidpointRounding.AwayFromZero));
  }
}

internal sealed class OpenDroneIdSendScheduler {
  internal static readonly TimeSpan SystemUpdateInterval = TimeSpan.FromSeconds(1);
  internal static readonly TimeSpan ExtendedInterval = TimeSpan.FromSeconds(2.5);
  internal static readonly TimeSpan MaxGpsAge = TimeSpan.FromSeconds(5);
  internal static readonly TimeSpan ArmStatusTimeout = TimeSpan.FromSeconds(5);

  private DateTimeOffset _lastSystemUpdate = DateTimeOffset.MinValue;
  private DateTimeOffset _lastExtended = DateTimeOffset.MinValue;
  private int _extendedIndex;

  internal OpenDroneIdScheduledMessage? Next(
      DateTimeOffset now, bool moduleDetected, bool freshGps) {
    if (!moduleDetected) {
      return null;
    }
    if (freshGps && now - _lastSystemUpdate >= SystemUpdateInterval) {
      _lastSystemUpdate = now;
      return new OpenDroneIdScheduledMessage(SystemUpdate: true, default);
    }
    if (now - _lastExtended < ExtendedInterval) {
      return null;
    }
    _lastExtended = now;
    var kind = (OpenDroneIdExtendedMessage)(_extendedIndex++ % 4);
    return new OpenDroneIdScheduledMessage(SystemUpdate: false, kind);
  }
}

internal readonly record struct OpenDroneIdScheduledMessage(
    bool SystemUpdate, OpenDroneIdExtendedMessage ExtendedKind);

#pragma warning restore CS0612

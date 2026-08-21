using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using MissionPlanner.Utilities;
using Newtonsoft.Json;

namespace MissionPlannerAvalonia.Services;

public static class DeveloperToolParsers {
  private static readonly char[] _separators = { ' ', '\t', '\r', '\n', ',', ';', ':', '-' };

  public static byte[] ParseBytes(string input) {
    if (string.IsNullOrWhiteSpace(input)) {
      throw new FormatException("Enter at least one byte.");
    }

    var trimmed = input.Trim();
    bool hasSeparators = trimmed.IndexOfAny(_separators) >= 0;
    if (!hasSeparators) {
      var compact = trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
          ? trimmed[2..]
          : trimmed;
      if (compact.Length == 0 || compact.Length % 2 != 0 || !compact.All(Uri.IsHexDigit)) {
        throw new FormatException("Compact hexadecimal input must contain an even number of hex digits.");
      }

      return Enumerable.Range(0, compact.Length / 2)
          .Select(index => byte.Parse(compact.AsSpan(index * 2, 2), NumberStyles.HexNumber,
              CultureInfo.InvariantCulture))
          .ToArray();
    }

    var result = new List<byte>();
    foreach (var token in trimmed.Split(_separators, StringSplitOptions.RemoveEmptyEntries)) {
      bool explicitHex = token.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
      var digits = explicitHex ? token[2..] : token;
      var style = explicitHex || digits.Any(char.IsLetter)
          ? NumberStyles.HexNumber
          : NumberStyles.Integer;
      if (!byte.TryParse(digits, style, CultureInfo.InvariantCulture, out var value)) {
        throw new FormatException($"'{token}' is not a byte (0..255 or 00..FF). ");
      }
      result.Add(value);
    }

    if (result.Count == 0) {
      throw new FormatException("Enter at least one byte.");
    }
    return result.ToArray();
  }

  public static string DecodeMavlinkPacket(string input) {
    var bytes = ParseBytes(input);
    var packet = new MAVLink.MavlinkParse().ReadPacket(new MemoryStream(bytes));
    if (packet == null || packet.Length == 0) {
      throw new InvalidDataException("The input does not contain a complete MAVLink packet.");
    }

    return $"{packet.msgtypename} (message {packet.msgid}, system {packet.sysid}, " +
           $"component {packet.compid}, {packet.Length} bytes)\n" +
           packet.ToJSON(Formatting.Indented);
  }

  public static string DecodeHardwareId(string input, string parameterName = "") {
    var trimmed = input.Trim();
    uint value;
    if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) {
      if (!uint.TryParse(trimmed[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value)) {
        throw new FormatException("The hardware ID is not a valid hexadecimal UInt32 value.");
      }
    } else if (!uint.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) {
      throw new FormatException("The hardware ID is not a valid UInt32 value.");
    }

    return new Device.DeviceStructure(parameterName.Trim().ToUpperInvariant(), value).ToString();
  }
}

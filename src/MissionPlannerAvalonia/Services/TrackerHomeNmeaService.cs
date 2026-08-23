using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MissionPlannerAvalonia.Services;

internal enum TrackerHomeNmeaTransport {
  Serial,
  TcpClient,
  TcpHost,
  UdpListener,
  Gpsd,
}

internal sealed record TrackerHomeNmeaOptions(
    TrackerHomeNmeaTransport Transport,
    string SerialPort,
    int BaudRate,
    string Host,
    int Port);

/// <summary>
/// Portable replacement for the official TrackerHome plugin's Windows-only Garmin USB reader.
/// It deliberately obtains one validated GGA fix and releases the device immediately.
/// </summary>
internal sealed class TrackerHomeNmeaService {
  internal const int MaximumLineLength = 1024;

  private readonly Func<string, int, SerialPort> _serialFactory;

  internal TrackerHomeNmeaService(Func<string, int, SerialPort>? serialFactory = null) {
    _serialFactory = serialFactory ?? ((port, baud) => new SerialPort(port, baud));
  }

  internal async Task<NmeaGgaFix> ReadFixAsync(
      TrackerHomeNmeaOptions options,
      Action<string>? lineReceived,
      CancellationToken cancellationToken) {
    Validate(options);
    return options.Transport switch {
      TrackerHomeNmeaTransport.Serial =>
          await ReadSerialAsync(options, lineReceived, cancellationToken).ConfigureAwait(false),
      TrackerHomeNmeaTransport.TcpClient =>
          await ReadTcpClientAsync(options, gpsd: false, lineReceived, cancellationToken)
              .ConfigureAwait(false),
      TrackerHomeNmeaTransport.Gpsd =>
          await ReadTcpClientAsync(options, gpsd: true, lineReceived, cancellationToken)
              .ConfigureAwait(false),
      TrackerHomeNmeaTransport.TcpHost =>
          await ReadTcpHostAsync(options, lineReceived, cancellationToken).ConfigureAwait(false),
      TrackerHomeNmeaTransport.UdpListener =>
          await ReadUdpAsync(options, lineReceived, cancellationToken).ConfigureAwait(false),
      _ => throw new ArgumentOutOfRangeException(nameof(options)),
    };
  }

  private async Task<NmeaGgaFix> ReadSerialAsync(
      TrackerHomeNmeaOptions options,
      Action<string>? lineReceived,
      CancellationToken cancellationToken) {
    using SerialPort serial = _serialFactory(options.SerialPort, options.BaudRate);
    serial.ReadTimeout = 750;
    serial.NewLine = "\n";
    using CancellationTokenRegistration registration = cancellationToken.Register(() => {
      try {
        serial.Close();
      } catch {
      }
    });
    await Task.Run(serial.Open, cancellationToken).ConfigureAwait(false);

    string lastError = "No NMEA GGA sentence was received.";
    while (true) {
      cancellationToken.ThrowIfCancellationRequested();
      string? line;
      try {
        line = await Task.Run(serial.ReadLine, cancellationToken).ConfigureAwait(false);
      } catch (TimeoutException) {
        continue;
      } catch (Exception) when (cancellationToken.IsCancellationRequested) {
        throw new OperationCanceledException(cancellationToken);
      }
      if (TryAcceptLine(line, lineReceived, ref lastError, out NmeaGgaFix fix)) {
        return fix;
      }
    }
  }

  private static async Task<NmeaGgaFix> ReadTcpClientAsync(
      TrackerHomeNmeaOptions options,
      bool gpsd,
      Action<string>? lineReceived,
      CancellationToken cancellationToken) {
    using var client = new TcpClient();
    await client.ConnectAsync(options.Host, options.Port, cancellationToken).ConfigureAwait(false);
    using NetworkStream stream = client.GetStream();
    if (gpsd) {
      byte[] watch = Encoding.ASCII.GetBytes(
          "?WATCH={\"enable\":true,\"json\":false,\"nmea\":true};\n");
      await stream.WriteAsync(watch, cancellationToken).ConfigureAwait(false);
      await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
    return await ReadStreamAsync(stream, lineReceived, cancellationToken).ConfigureAwait(false);
  }

  private static async Task<NmeaGgaFix> ReadTcpHostAsync(
      TrackerHomeNmeaOptions options,
      Action<string>? lineReceived,
      CancellationToken cancellationToken) {
    var listener = new TcpListener(IPAddress.Any, options.Port);
    listener.Start();
    try {
      using TcpClient client = await listener.AcceptTcpClientAsync(cancellationToken)
          .ConfigureAwait(false);
      return await ReadStreamAsync(client.GetStream(), lineReceived, cancellationToken)
          .ConfigureAwait(false);
    } finally {
      listener.Stop();
    }
  }

  private static async Task<NmeaGgaFix> ReadUdpAsync(
      TrackerHomeNmeaOptions options,
      Action<string>? lineReceived,
      CancellationToken cancellationToken) {
    using var udp = new UdpClient(options.Port);
    string lastError = "No NMEA GGA sentence was received.";
    while (true) {
      UdpReceiveResult datagram = await udp.ReceiveAsync(cancellationToken).ConfigureAwait(false);
      if (datagram.Buffer.Length > 64 * 1024) {
        lastError = "NMEA UDP datagram is too large.";
        continue;
      }
      string text = Encoding.ASCII.GetString(datagram.Buffer);
      foreach (string line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)) {
        if (TryAcceptLine(line, lineReceived, ref lastError, out NmeaGgaFix fix)) {
          return fix;
        }
      }
    }
  }

  private static async Task<NmeaGgaFix> ReadStreamAsync(
      Stream stream,
      Action<string>? lineReceived,
      CancellationToken cancellationToken) {
    using var reader = new StreamReader(
        stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false,
        bufferSize: 1024, leaveOpen: true);
    string lastError = "No NMEA GGA sentence was received.";
    while (true) {
      string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
      if (line == null) {
        throw new EndOfStreamException(lastError);
      }
      if (TryAcceptLine(line, lineReceived, ref lastError, out NmeaGgaFix fix)) {
        return fix;
      }
    }
  }

  private static bool TryAcceptLine(
      string? line,
      Action<string>? lineReceived,
      ref string lastError,
      out NmeaGgaFix fix) {
    fix = default;
    if (string.IsNullOrWhiteSpace(line)) {
      return false;
    }
    string trimmed = line.Trim();
    if (trimmed.Length > MaximumLineLength) {
      lastError = "NMEA line exceeds the 1024-character safety limit.";
      return false;
    }
    lineReceived?.Invoke(trimmed);
    if (NmeaGgaParser.TryParse(trimmed, out fix, out string error)) {
      return true;
    }
    if (trimmed.Contains("GGA", StringComparison.OrdinalIgnoreCase)) {
      lastError = error;
    }
    return false;
  }

  private static void Validate(TrackerHomeNmeaOptions options) {
    if (options.Transport == TrackerHomeNmeaTransport.Serial) {
      if (string.IsNullOrWhiteSpace(options.SerialPort)) {
        throw new ArgumentException("Select a serial GPS device.", nameof(options));
      }
      if (options.BaudRate is < 300 or > 4_000_000) {
        throw new ArgumentOutOfRangeException(
            nameof(options), "Serial baud rate is outside the supported range.");
      }
      return;
    }
    if (options.Port is < IPEndPoint.MinPort or > IPEndPoint.MaxPort) {
      throw new ArgumentOutOfRangeException(
          nameof(options), "Network port must be between 1 and 65535.");
    }
    if ((options.Transport is TrackerHomeNmeaTransport.TcpClient
        or TrackerHomeNmeaTransport.Gpsd)
        && string.IsNullOrWhiteSpace(options.Host)) {
      throw new ArgumentException("Enter the remote NMEA host.", nameof(options));
    }
  }
}

internal readonly record struct TrackerHomeAltitude(
    double Metres, string Source, bool UsedGpsFallback);

internal static class TrackerHomeLocationResolver {
  internal static TrackerHomeAltitude Resolve(
      NmeaGgaFix fix, MissionPlanner.Utilities.srtm.altresponce terrain) {
    if ((terrain.currenttype is MissionPlanner.Utilities.srtm.tiletype.valid
        or MissionPlanner.Utilities.srtm.tiletype.ocean)
        && double.IsFinite(terrain.alt)) {
      string source = string.IsNullOrWhiteSpace(terrain.altsource)
          ? "local terrain"
          : terrain.altsource;
      return new TrackerHomeAltitude(terrain.alt, source, UsedGpsFallback: false);
    }
    if (double.IsFinite(fix.AltitudeM)) {
      return new TrackerHomeAltitude(
          fix.AltitudeM, "GPS GGA mean-sea-level altitude", UsedGpsFallback: true);
    }
    throw new InvalidOperationException(
        "Neither local terrain data nor the GPS supplied a finite altitude.");
  }

  internal static string CoordinateText(NmeaGgaFix fix) => string.Format(
      CultureInfo.InvariantCulture, "{0:0.0000000}, {1:0.0000000}",
      fix.Latitude, fix.Longitude);
}

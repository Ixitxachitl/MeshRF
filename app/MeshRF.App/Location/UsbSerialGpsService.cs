// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.IO;
using System.IO.Ports;

namespace MeshRF.App.Location;

public sealed record GpsSerialOptions(string? PortName, int? BaudRate);

public sealed record GpsFix(
    double Latitude,
    double Longitude,
    string PortName,
    int BaudRate,
    DateTimeOffset TimestampUtc);

/// <summary>
/// Auto-detects an NMEA GPS receiver on any available COM port and streams the
/// most recent fix. Intended for simple USB GPS dongles such as VK-162 units.
/// </summary>
public sealed class UsbSerialGpsService : IDisposable
{
    private static readonly int[] s_baudRates = [9600, 4800, 38400, 19200, 57600];
    private static readonly TimeSpan s_probeWindow = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan s_streamTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan s_scanRetryDelay = TimeSpan.FromSeconds(2);

    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private SerialPort? _activePort;
    private GpsSerialOptions _options = new(null, null);

    public event Action<string>? StatusChanged;
    public event Action<GpsFix>? FixReceived;

    public void UpdateOptions(GpsSerialOptions options)
    {
        lock (_gate)
        {
            _options = options;
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_cts is not null) return;
            _cts = new CancellationTokenSource();
            _runTask = Task.Run(() => RunAsync(_cts.Token));
        }
    }

    public void Stop()
    {
        CancellationTokenSource? cts;
        Task? runTask;
        lock (_gate)
        {
            cts = _cts;
            runTask = _runTask;
            _cts = null;
            _runTask = null;
        }

        if (cts is null) return;

        try { cts.Cancel(); } catch { }
        CloseActivePort();

        try { runTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        cts.Dispose();
    }

    public void Restart()
    {
        Stop();
        Start();
    }

    public void Dispose() => Stop();

    private async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var options = GetOptions();
            var ports = ResolvePorts(options);

            if (ports.Length == 0)
            {
                PublishStatus(string.IsNullOrWhiteSpace(options.PortName)
                    ? "USB GPS: no serial ports found."
                    : $"USB GPS: configured port {options.PortName} not found.");
                await DelayAsync(s_scanRetryDelay, token).ConfigureAwait(false);
                continue;
            }

            var baudRates = ResolveBaudRates(options);
            PublishStatus(BuildScanStatus(options, ports.Length, baudRates));

            bool sawGps = false;
            foreach (var portName in ports)
            {
                foreach (var baudRate in baudRates)
                {
                    if (token.IsCancellationRequested) return;

                    bool matched = TryConsumePort(portName, baudRate, token);
                    if (matched)
                    {
                        sawGps = true;
                        break;
                    }
                }

                if (sawGps) break;
            }

            if (!sawGps)
            {
                PublishStatus(BuildRetryStatus(options));
                await DelayAsync(s_scanRetryDelay, token).ConfigureAwait(false);
            }
        }
    }

    private GpsSerialOptions GetOptions()
    {
        lock (_gate)
        {
            return _options;
        }
    }

    private static string[] ResolvePorts(GpsSerialOptions options)
    {
        var availablePorts = SerialPort.GetPortNames()
                                       .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                                       .ToArray();
        if (string.IsNullOrWhiteSpace(options.PortName))
            return availablePorts;

        return availablePorts
            .Where(name => string.Equals(name, options.PortName.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    private static int[] ResolveBaudRates(GpsSerialOptions options)
    {
        if (options.BaudRate is int baudRate && baudRate > 0)
            return [baudRate];
        return s_baudRates;
    }

    private static string BuildScanStatus(GpsSerialOptions options, int portCount, IReadOnlyList<int> baudRates)
    {
        if (!string.IsNullOrWhiteSpace(options.PortName) && options.BaudRate is int baudRate)
            return $"USB GPS: probing {options.PortName.Trim()} @ {baudRate} baud...";
        if (!string.IsNullOrWhiteSpace(options.PortName))
            return $"USB GPS: probing {options.PortName.Trim()} at common GPS baud rates...";
        if (options.BaudRate is int forcedBaud)
            return $"USB GPS: scanning {portCount} serial port(s) at {forcedBaud} baud...";
        return $"USB GPS: scanning {portCount} serial port(s)...";
    }

    private static string BuildRetryStatus(GpsSerialOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.PortName) && options.BaudRate is int baudRate)
            return $"USB GPS: no NMEA stream found on {options.PortName.Trim()} @ {baudRate}; retrying...";
        if (!string.IsNullOrWhiteSpace(options.PortName))
            return $"USB GPS: no NMEA stream found on {options.PortName.Trim()}; retrying...";
        if (options.BaudRate is int forcedBaud)
            return $"USB GPS: no NMEA stream found at {forcedBaud} baud; retrying...";
        return "USB GPS: no NMEA stream found; retrying...";
    }

    private bool TryConsumePort(string portName, int baudRate, CancellationToken token)
    {
        SerialPort? port = null;
        bool sawNmea = false;
        bool sawFix = false;
        bool announcedNmea = false;
        var probeDeadline = DateTime.UtcNow + s_probeWindow;
        var staleDeadline = DateTime.UtcNow + s_streamTimeout;

        try
        {
            port = new SerialPort(portName, baudRate)
            {
                NewLine = "\n",
                ReadTimeout = 1000,
                DtrEnable = true,
                RtsEnable = true,
            };

            port.Open();
            SetActivePort(port);
            PublishStatus($"USB GPS: probing {portName} @ {baudRate} baud...");

            while (!token.IsCancellationRequested)
            {
                try
                {
                    var line = port.ReadLine().Trim();
                    if (!LooksLikeNmeaSentence(line))
                        continue;

                    sawNmea = true;
                    staleDeadline = DateTime.UtcNow + s_streamTimeout;

                    if (!announcedNmea)
                    {
                        PublishStatus($"USB GPS: NMEA detected on {portName} @ {baudRate}; waiting for GPS fix...");
                        announcedNmea = true;
                    }

                    if (!TryParseNmeaFix(line, out var latitude, out var longitude))
                        continue;

                    sawFix = true;
                    staleDeadline = DateTime.UtcNow + s_streamTimeout;
                    PublishStatus($"USB GPS: receiving fixes from {portName} @ {baudRate} baud.");
                    FixReceived?.Invoke(new GpsFix(
                        latitude,
                        longitude,
                        portName,
                        baudRate,
                        DateTimeOffset.UtcNow));
                }
                catch (TimeoutException)
                {
                    var now = DateTime.UtcNow;
                    if (!sawNmea && now >= probeDeadline) return false;
                    if (sawNmea && now >= staleDeadline)
                        throw new IOException(sawFix
                            ? $"Timed out waiting for GPS data on {portName}."
                            : $"Timed out waiting for additional NMEA data on {portName}.");
                }
            }
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            PublishStatus(sawNmea
                ? $"USB GPS: lost {portName} ({ex.Message})."
                : $"USB GPS: {portName} @ {baudRate} did not match ({ex.Message}).");
        }
        finally
        {
            if (port is not null)
            {
                ClearActivePort(port);
                try { port.Dispose(); } catch { }
            }
        }

        return sawNmea;
    }

    private static bool LooksLikeNmeaSentence(string line) =>
        !string.IsNullOrWhiteSpace(line) && line[0] == '$' && line.Length >= 6;

    private static bool TryParseNmeaFix(string line, out double latitude, out double longitude)
    {
        latitude = 0;
        longitude = 0;

        if (!LooksLikeNmeaSentence(line))
            return false;

        var checksumIndex = line.IndexOf('*');
        var content = checksumIndex >= 0 ? line[..checksumIndex] : line;
        var fields = content.Split(',');
        if (fields.Length < 6) return false;

        var sentenceType = fields[0];
        if (sentenceType.EndsWith("RMC", StringComparison.Ordinal))
        {
            if (fields.Length < 7 || !string.Equals(fields[2], "A", StringComparison.OrdinalIgnoreCase))
                return false;

            return TryParseCoordinate(fields[3], fields[4], 2, out latitude) &&
                   TryParseCoordinate(fields[5], fields[6], 3, out longitude);
        }

        if (sentenceType.EndsWith("GGA", StringComparison.Ordinal))
        {
            if (fields.Length < 7 || !int.TryParse(fields[6], NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var fixQuality) || fixQuality <= 0)
                return false;

            return TryParseCoordinate(fields[2], fields[3], 2, out latitude) &&
                   TryParseCoordinate(fields[4], fields[5], 3, out longitude);
        }

        return false;
    }

    private static bool TryParseCoordinate(string value, string hemisphere, int degreeDigits, out double coordinate)
    {
        coordinate = 0;
        if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(hemisphere))
            return false;
        if (value.Length <= degreeDigits)
            return false;
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var raw))
            return false;

        var degrees = Math.Floor(raw / 100);
        var minutes = raw - (degrees * 100);
        coordinate = degrees + (minutes / 60.0);
        if (hemisphere.Equals("S", StringComparison.OrdinalIgnoreCase) ||
            hemisphere.Equals("W", StringComparison.OrdinalIgnoreCase))
            coordinate = -coordinate;

        return true;
    }

    private void PublishStatus(string status) => StatusChanged?.Invoke(status);

    private static async Task DelayAsync(TimeSpan delay, CancellationToken token)
    {
        try { await Task.Delay(delay, token).ConfigureAwait(false); } catch (OperationCanceledException) { }
    }

    private void SetActivePort(SerialPort port)
    {
        lock (_gate)
        {
            _activePort = port;
        }
    }

    private void ClearActivePort(SerialPort port)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_activePort, port))
                _activePort = null;
        }
    }

    private void CloseActivePort()
    {
        SerialPort? port;
        lock (_gate)
        {
            port = _activePort;
            _activePort = null;
        }

        if (port is null) return;

        try { port.Close(); } catch { }
    }
}
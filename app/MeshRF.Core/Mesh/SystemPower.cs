// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MeshRF.Mesh;

/// <summary>
/// The host machine's battery, reported as this node's own device metrics. A
/// laptop running MeshRF is as much a battery-powered node as a handheld, so
/// its charge belongs on the mesh the same way.
/// </summary>
/// <remarks>
/// Every probe is best-effort: an absent battery, a locked-down WMI, or a
/// machine that simply has no such counter all read as "unknown" rather than
/// failing. A desktop on mains reports exactly that, which is the honest
/// answer and the one firmware expects.
/// </remarks>
public static partial class SystemPower
{
    /// <summary>Meshtastic reads a battery level above 100 as "externally
    /// powered", so this is the value to send when running on mains.</summary>
    public const byte MainsPoweredLevel = 101;

    /// <summary>
    /// Reads the host's power state. <paramref name="batteryPct"/> and
    /// <paramref name="voltageV"/> are null when unknown, which is different
    /// from zero — a battery that reads 0% is flat, one that reads nothing has
    /// not been found.
    /// </summary>
    public static void Read(out bool acOnline, out byte? batteryPct, out float? voltageV)
    {
        acOnline = true;   // A machine with no battery at all is on mains.
        batteryPct = null;
        voltageV = null;

        try
        {
            if (OperatingSystem.IsWindows()) ReadWindows(out acOnline, out batteryPct, out voltageV);
            else if (OperatingSystem.IsLinux()) ReadLinux(out acOnline, out batteryPct, out voltageV);
            else if (OperatingSystem.IsMacOS()) ReadMacOs(out acOnline, out batteryPct, out voltageV);
        }
        catch
        {
            // Telemetry is never worth an exception reaching the caller.
        }
    }

    /// <summary>
    /// The battery level to put on the wire: the mains sentinel when running on
    /// AC, otherwise the charge. Falls back to <paramref name="previousPct"/>
    /// so a machine whose battery cannot be read keeps reporting the last known
    /// figure rather than dropping to a misleading zero.
    /// </summary>
    public static byte BatteryLevelForWire(bool acOnline, byte? batteryPct, byte? previousPct = null) =>
        acOnline ? MainsPoweredLevel : batteryPct ?? previousPct ?? 0;

    // --- Windows ----------------------------------------------------------

    [SupportedOSPlatform("windows")]
    private static void ReadWindows(out bool acOnline, out byte? batteryPct, out float? voltageV)
    {
        acOnline = true;
        batteryPct = null;
        voltageV = null;

        if (GetSystemPowerStatus(out var status))
        {
            acOnline = status.ACLineStatus == 1;
            // 255 means the API does not know; anything else is a percentage.
            if (status.BatteryLifePercent <= 100) batteryPct = status.BatteryLifePercent;
        }

        // No Win32 call reports battery voltage, so this is the WMI class that
        // does. Some systems disable it, hence the separate try.
        try
        {
            using var searcher = new ManagementObjectSearcher("root\\WMI", "SELECT Voltage FROM BatteryStatus");
            foreach (var battery in searcher.Get().OfType<ManagementObject>())
            {
                using (battery)
                {
                    if (battery["Voltage"] is not { } raw) continue;
                    if (!uint.TryParse(raw.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var mv))
                        continue;
                    if (!IsPlausibleMillivolts(mv)) continue;
                    voltageV = mv / 1000f;
                    break;
                }
            }
        }
        catch
        {
            // WMI battery classes are commonly disabled; voltage stays unknown.
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte Reserved;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemPowerStatus(out SystemPowerStatus systemPowerStatus);

    // --- Linux ------------------------------------------------------------

    /// <summary>
    /// sysfs, which every mainstream distribution exposes: each supply under
    /// /sys/class/power_supply declares its type, and batteries carry capacity
    /// and voltage_now (in microvolts).
    /// </summary>
    private static void ReadLinux(out bool acOnline, out byte? batteryPct, out float? voltageV)
    {
        acOnline = true;
        batteryPct = null;
        voltageV = null;

        const string root = "/sys/class/power_supply";
        if (!Directory.Exists(root)) return;

        bool sawBattery = false;
        bool sawMainsOnline = false;
        bool sawMains = false;

        foreach (var supply in Directory.EnumerateDirectories(root))
        {
            var type = ReadTrimmed(Path.Combine(supply, "type"));
            if (string.Equals(type, "Mains", StringComparison.OrdinalIgnoreCase))
            {
                sawMains = true;
                if (ReadTrimmed(Path.Combine(supply, "online")) == "1") sawMainsOnline = true;
            }
            else if (string.Equals(type, "Battery", StringComparison.OrdinalIgnoreCase) && !sawBattery)
            {
                sawBattery = true;
                if (byte.TryParse(ReadTrimmed(Path.Combine(supply, "capacity")),
                                  NumberStyles.Integer, CultureInfo.InvariantCulture, out var pct) && pct <= 100)
                    batteryPct = pct;

                if (uint.TryParse(ReadTrimmed(Path.Combine(supply, "voltage_now")),
                                  NumberStyles.Integer, CultureInfo.InvariantCulture, out var uv))
                {
                    var mv = uv / 1000;
                    if (IsPlausibleMillivolts(mv)) voltageV = mv / 1000f;
                }
            }
        }

        // With a battery but no mains supply listed, treat a discharging
        // machine as off mains rather than claiming AC we cannot see.
        acOnline = sawMains ? sawMainsOnline : !sawBattery;
    }

    private static string ReadTrimmed(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path).Trim() : string.Empty; }
        catch { return string.Empty; }
    }

    // --- macOS ------------------------------------------------------------

    /// <summary>
    /// pmset for charge and AC, ioreg for voltage — macOS has no sysfs and no
    /// managed API for either. Both are read-only queries that exit promptly.
    /// </summary>
    private static void ReadMacOs(out bool acOnline, out byte? batteryPct, out float? voltageV)
    {
        acOnline = true;
        batteryPct = null;
        voltageV = null;

        var batt = RunTool("/usr/bin/pmset", "-g batt");
        if (batt.Length > 0)
        {
            acOnline = batt.Contains("AC Power", StringComparison.OrdinalIgnoreCase);
            var match = MacBatteryPercentRegex().Match(batt);
            if (match.Success &&
                byte.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pct)
                && pct <= 100)
                batteryPct = pct;
        }

        var ioreg = RunTool("/usr/sbin/ioreg", "-rn AppleSmartBattery");
        if (ioreg.Length > 0)
        {
            var match = MacVoltageRegex().Match(ioreg);
            if (match.Success &&
                uint.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mv)
                && IsPlausibleMillivolts(mv))
                voltageV = mv / 1000f;
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"(\d{1,3})%")]
    private static partial System.Text.RegularExpressions.Regex MacBatteryPercentRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"""Voltage""\s*=\s*(\d+)")]
    private static partial System.Text.RegularExpressions.Regex MacVoltageRegex();

    private static string RunTool(string path, string arguments)
    {
        try
        {
            if (!File.Exists(path)) return string.Empty;
            using var p = Process.Start(new ProcessStartInfo(path, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p is null) return string.Empty;

            string output = p.StandardOutput.ReadToEnd();
            // Bounded so a wedged tool cannot stall a telemetry send.
            if (!p.WaitForExit(2000)) { try { p.Kill(entireProcessTree: true); } catch { } return string.Empty; }
            return output;
        }
        catch { return string.Empty; }
    }

    // --- shared -----------------------------------------------------------

    /// <summary>
    /// A single-cell lithium sits near 3.7 V and a laptop pack near 11-12 V, so
    /// anything outside 1-20 V is a field holding something other than what we
    /// asked for — design voltage in a different unit, or a sentinel.
    /// </summary>
    private static bool IsPlausibleMillivolts(uint mv) => mv is >= 1000 and <= 20000;
}

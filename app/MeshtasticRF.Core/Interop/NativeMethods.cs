// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;

namespace MeshtasticRF.Interop;

/// <summary>
/// P/Invoke surface for the native MeshtasticRF.Native DLL (built from
/// native/bridge). Mirrors native/bridge/include/mrf/c_api.h.
/// </summary>
internal static partial class NativeMethods
{
    private const string Dll = "MeshtasticRF.Native";

    [LibraryImport(Dll, EntryPoint = "mrf_abi_version")]
    public static partial uint AbiVersion();

    [LibraryImport(Dll, EntryPoint = "mrf_core_create")]
    public static partial nint CoreCreate();

    [LibraryImport(Dll, EntryPoint = "mrf_core_destroy")]
    public static partial void CoreDestroy(nint core);

    [LibraryImport(Dll, EntryPoint = "mrf_core_start_rx")]
    public static partial int CoreStartRx(nint core, int preset, ulong centerFreqHz);

    [LibraryImport(Dll, EntryPoint = "mrf_core_stop")]
    public static partial void CoreStop(nint core);

    [LibraryImport(Dll, EntryPoint = "mrf_core_set_gains")]
    public static partial void CoreSetGains(nint core, byte lnaDb, byte vgaDb, int ampEnable);

    [LibraryImport(Dll, EntryPoint = "mrf_core_is_running")]
    public static partial int CoreIsRunning(nint core);

    [LibraryImport(Dll, EntryPoint = "mrf_core_start_capture", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int CoreStartCapture(nint core, string path);

    [LibraryImport(Dll, EntryPoint = "mrf_core_stop_capture")]
    public static partial void CoreStopCapture(nint core);

    [LibraryImport(Dll, EntryPoint = "mrf_core_is_capturing")]
    public static partial int CoreIsCapturing(nint core);

    [StructLayout(LayoutKind.Sequential)]
    public struct SignalStats
    {
        public float RssiDbfs;
        public float PeakDbfs;
        public float DcRe;
        public float DcIm;
        public ulong TotalSamples;
    }

    [LibraryImport(Dll, EntryPoint = "mrf_core_get_signal_stats")]
    public static partial void CoreGetSignalStats(nint core, out SignalStats stats);

    [LibraryImport(Dll, EntryPoint = "mrf_core_spectrum_size")]
    public static partial uint CoreSpectrumSize(nint core);

    [LibraryImport(Dll, EntryPoint = "mrf_core_sample_rate_hz")]
    public static partial uint CoreSampleRateHz(nint core);

    [LibraryImport(Dll, EntryPoint = "mrf_core_pull_spectrum")]
    public static unsafe partial uint CorePullSpectrum(nint core, float* outDbfs, uint capacity);

    [LibraryImport(Dll, EntryPoint = "mrf_core_pull_packet_spectrogram")]
    public static unsafe partial uint CorePullPacketSpectrogram(nint core, float* outDbfs, uint nTime, uint nFreq);

    [LibraryImport(Dll, EntryPoint = "mrf_core_get_device_name")]
    public static unsafe partial uint CoreGetDeviceName(nint core, byte* buf, uint capacity);

    [LibraryImport(Dll, EntryPoint = "mrf_core_get_device_status")]
    public static unsafe partial uint CoreGetDeviceStatus(nint core, byte* buf, uint capacity);

    [LibraryImport(Dll, EntryPoint = "mrf_core_pull_event")]
    public static unsafe partial uint CorePullEvent(nint core, byte* buf, uint capacity);
}



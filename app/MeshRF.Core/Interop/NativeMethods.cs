// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;

namespace MeshRF.Interop;

/// <summary>
/// P/Invoke surface for the native MeshRF.Native DLL (built from
/// native/bridge). Mirrors native/bridge/include/mrf/c_api.h.
/// </summary>
internal static partial class NativeMethods
{
    private const string Dll = "MeshRF.Native";

    [LibraryImport(Dll, EntryPoint = "mrf_abi_version")]
    public static partial uint AbiVersion();

    [LibraryImport(Dll, EntryPoint = "mrf_core_create")]
    public static partial nint CoreCreate();

    [LibraryImport(Dll, EntryPoint = "mrf_core_destroy")]
    public static partial void CoreDestroy(nint core);

    [LibraryImport(Dll, EntryPoint = "mrf_core_start_rx")]
    public static partial int CoreStartRx(nint core, int preset, ulong centerFreqHz);

    [LibraryImport(Dll, EntryPoint = "mrf_core_start_rx_params")]
    public static partial int CoreStartRxParams(nint core, byte sf, uint bwHz, byte cr, ulong centerFreqHz);

    [LibraryImport(Dll, EntryPoint = "mrf_core_stop")]
    public static partial void CoreStop(nint core);

    [LibraryImport(Dll, EntryPoint = "mrf_core_set_device")]
    public static partial int CoreSetDevice(nint core, int kind);

    [LibraryImport(Dll, EntryPoint = "mrf_core_set_rx_device")]
    public static partial int CoreSetRxDevice(nint core, int kind);

    [LibraryImport(Dll, EntryPoint = "mrf_core_set_tx_device")]
    public static partial int CoreSetTxDevice(nint core, int kind);

    [LibraryImport(Dll, EntryPoint = "mrf_core_get_device_kind")]
    public static partial int CoreGetDeviceKind(nint core);

    [LibraryImport(Dll, EntryPoint = "mrf_core_get_rx_device_kind")]
    public static partial int CoreGetRxDeviceKind(nint core);

    [LibraryImport(Dll, EntryPoint = "mrf_core_get_tx_device_kind")]
    public static partial int CoreGetTxDeviceKind(nint core);

    [LibraryImport(Dll, EntryPoint = "mrf_core_device_available")]
    public static partial int CoreDeviceAvailable(nint core, int kind);

    [LibraryImport(Dll, EntryPoint = "mrf_core_set_sx1262_board")]
    public static partial int CoreSetSx1262Board(nint core, int board);

    [LibraryImport(Dll, EntryPoint = "mrf_core_get_sx1262_board")]
    public static partial int CoreGetSx1262Board(nint core);

    [LibraryImport(Dll, EntryPoint = "mrf_core_set_sx1262_serial", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int CoreSetSx1262Serial(nint core, string serial);

    [LibraryImport(Dll, EntryPoint = "mrf_core_get_sx1262_serial")]
    public static unsafe partial uint CoreGetSx1262Serial(nint core, byte* buf, uint capacity);

    [LibraryImport(Dll, EntryPoint = "mrf_core_list_sx1262_serials")]
    public static unsafe partial uint CoreListSx1262Serials(nint core, byte* buf, uint capacity);

    [LibraryImport(Dll, EntryPoint = "mrf_core_set_tx_power_dbm")]
    public static partial void CoreSetTxPowerDbm(nint core, int dbm);

    [LibraryImport(Dll, EntryPoint = "mrf_core_get_tx_power_dbm")]
    public static partial int CoreGetTxPowerDbm(nint core);

    [LibraryImport(Dll, EntryPoint = "mrf_core_tx_power_range")]
    public static unsafe partial void CoreTxPowerRange(nint core, int* minDbm, int* maxDbm);

    [LibraryImport(Dll, EntryPoint = "mrf_core_set_gains")]
    public static partial void CoreSetGains(nint core, byte lnaDb, byte vgaDb, int ampEnable);

    [LibraryImport(Dll, EntryPoint = "mrf_core_set_device_option", StringMarshalling = StringMarshalling.Utf8)]
    public static partial void CoreSetDeviceOption(nint core, string key, int value);

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

    [LibraryImport(Dll, EntryPoint = "mrf_core_spectrum_center_hz")]
    public static partial ulong CoreSpectrumCenterHz(nint core);

    [LibraryImport(Dll, EntryPoint = "mrf_core_pull_spectrum")]
    public static unsafe partial uint CorePullSpectrum(nint core, float* outDbfs, uint capacity);

    [LibraryImport(Dll, EntryPoint = "mrf_core_spectrum_frame_count")]
    public static partial ulong CoreSpectrumFrameCount(nint core);

    [LibraryImport(Dll, EntryPoint = "mrf_core_spectrum_history_frame_rate_hz")]
    public static partial uint CoreSpectrumHistoryFrameRateHz(nint core);

    [LibraryImport(Dll, EntryPoint = "mrf_core_pull_spectrum_frames")]
    public static unsafe partial uint CorePullSpectrumFrames(
        nint core,
        ulong afterFrameIdx,
        uint maxCount,
        float* outFrames,
        uint outFramesLen);

    [LibraryImport(Dll, EntryPoint = "mrf_core_pull_packet_spectrogram")]
    public static unsafe partial uint CorePullPacketSpectrogram(nint core, float* outDbfs, uint nTime, uint nFreq);

    [LibraryImport(Dll, EntryPoint = "mrf_core_get_device_name")]
    public static unsafe partial uint CoreGetDeviceName(nint core, byte* buf, uint capacity);

    [LibraryImport(Dll, EntryPoint = "mrf_core_get_tx_device_name")]
    public static unsafe partial uint CoreGetTxDeviceName(nint core, byte* buf, uint capacity);

    [LibraryImport(Dll, EntryPoint = "mrf_core_get_device_status")]
    public static unsafe partial uint CoreGetDeviceStatus(nint core, byte* buf, uint capacity);

    [LibraryImport(Dll, EntryPoint = "mrf_core_pull_event")]
    public static unsafe partial uint CorePullEvent(nint core, byte* buf, uint capacity);

    [LibraryImport(Dll, EntryPoint = "mrf_core_can_transmit")]
    public static partial int CoreCanTransmit(nint core);

    [LibraryImport(Dll, EntryPoint = "mrf_core_transmit")]
    public static unsafe partial int CoreTransmit(nint core, int preset, ulong centerFreqHz,
        byte* payload, uint payloadLen, byte txvgaGainDb, int ampEnable);
}



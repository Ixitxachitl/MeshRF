// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF.Interop;

namespace MeshRF;

/// <summary>
/// Mirrors <c>mrf::modem::Preset</c> in <c>native/core/include/mrf/modem/Preset.h</c>.
/// Order MUST match exactly.
/// </summary>
public enum LoraPreset
{
    ShortTurbo = 0,
    ShortFast,
    ShortSlow,
    MediumFast,
    MediumSlow,
    LongTurbo,
    LongFast,
    LongModerate,
    LongSlow,
    LiteFast,
    LiteSlow,
    NarrowFast,
    NarrowSlow,
    TinyFast,
    TinySlow,
    MediumTurbo,
}

/// <summary>Immutable snapshot of receiver signal statistics.</summary>
public readonly record struct SignalStatsSnapshot(
    float RssiDbfs,
    float PeakDbfs,
    float DcRe,
    float DcIm,
    ulong TotalSamples);

/// <summary>
/// Selectable radio backend. Mirrors <c>mrf::hal::DeviceKind</c> in
/// <c>native/core/include/mrf/hal/RadioDevice.h</c>. Values are part of the
/// C ABI — keep them in sync.
/// </summary>
public enum RadioDeviceKind
{
    Auto = 0,
    HackRf = 1,
    RtlSdr = 2,
    Null = 3,
}

/// <summary>
/// Managed wrapper around the native <c>mrf::Core</c> facade.
///
/// Every access to <see cref="_handle"/> is guarded by <see cref="_lock"/>: a
/// read lock is held for the full duration of any native call, and
/// <see cref="Dispose"/> takes the write lock before destroying the handle.
/// This prevents a background thread from entering the native library with a
/// handle that <see cref="Dispose"/> is concurrently freeing (use-after-free).
/// </summary>
public sealed class MeshtasticCore : IDisposable
{
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);
    private nint _handle;
    private bool _disposed;

    public MeshtasticCore()
    {
        _handle = NativeMethods.CoreCreate();
        if (_handle == 0)
            throw new InvalidOperationException("Failed to create native core");
    }

    /// <summary>
    /// Human-readable name of the RX radio backend in use (e.g. "HackRF One",
    /// "RTL-SDR" or "(none)" if no RX device is selected/available). Re-read from the
    /// native core each access so it reflects the latest <see cref="SetDevice"/>.
    /// </summary>
    public string DeviceName => ReadDeviceName();

    /// <summary>Human-readable name of the selected TX backend.</summary>
    public string TxDeviceName => ReadTxDeviceName();

    /// <summary>
    /// Diagnostic string from the most recent device-open attempt.
    /// </summary>
    public string DeviceStatus => ReadDeviceStatus();

    /// <summary>True if a real RX radio backend is in use.</summary>
    public bool HasRealRadio
    {
        get
        {
            var name = DeviceName;
            return !string.IsNullOrEmpty(name) &&
                   !name.StartsWith("null", StringComparison.OrdinalIgnoreCase) &&
                   !name.Equals("(none)", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Select the RX radio backend used for the next <see cref="StartRx"/>. Reopens
    /// the device immediately (so <see cref="DeviceName"/>/<see cref="DeviceStatus"/>
    /// reflect the choice) when RX is stopped. Returns false if RX is running.
    /// </summary>
    public bool SetDevice(RadioDeviceKind kind)
    {
        return SetRxDevice(kind);
    }

    /// <summary>Select the RX radio backend used for the next <see cref="StartRx"/>.</summary>
    public bool SetRxDevice(RadioDeviceKind kind)
    {
        _lock.EnterReadLock();
        try
        {
            if (_disposed) return false;
            return NativeMethods.CoreSetRxDevice(_handle, (int)kind) == 0;
        }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>Select the TX radio backend. HackRF can transmit; RTL-SDR cannot.</summary>
    public bool SetTxDevice(RadioDeviceKind kind)
    {
        _lock.EnterReadLock();
        try
        {
            if (_disposed) return false;
            return NativeMethods.CoreSetTxDevice(_handle, (int)kind) == 0;
        }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>The RX backend that actually opened (may differ from the request).</summary>
    public RadioDeviceKind DeviceKind
    {
        get
        {
            _lock.EnterReadLock();
            try
            {
                return _disposed ? RadioDeviceKind.Null : (RadioDeviceKind)NativeMethods.CoreGetRxDeviceKind(_handle);
            }
            finally { _lock.ExitReadLock(); }
        }
    }

    /// <summary>The TX backend that actually opened/selected.</summary>
    public RadioDeviceKind TxDeviceKind
    {
        get
        {
            _lock.EnterReadLock();
            try
            {
                return _disposed ? RadioDeviceKind.Null : (RadioDeviceKind)NativeMethods.CoreGetTxDeviceKind(_handle);
            }
            finally { _lock.ExitReadLock(); }
        }
    }

    /// <summary>
    /// True if the given backend's runtime library can be loaded (so the user
    /// could select it). Does not require hardware to be connected.
    /// </summary>
    public bool IsDeviceAvailable(RadioDeviceKind kind)
    {
        _lock.EnterReadLock();
        try
        {
            return !_disposed && NativeMethods.CoreDeviceAvailable(_handle, (int)kind) != 0;
        }
        finally { _lock.ExitReadLock(); }
    }

    private string ReadDeviceName()
    {
        _lock.EnterReadLock();
        try
        {
            if (_disposed) return "(none)";
            unsafe
            {
                const int cap = 128;
                byte* buf = stackalloc byte[cap];
                var n = NativeMethods.CoreGetDeviceName(_handle, buf, cap);
                if (n == 0) return "(none)";
                int len = (int)Math.Min(n, (uint)(cap - 1));
                return System.Text.Encoding.UTF8.GetString(buf, len);
            }
        }
        finally { _lock.ExitReadLock(); }
    }

    private string ReadTxDeviceName()
    {
        _lock.EnterReadLock();
        try
        {
            if (_disposed) return "(none)";
            unsafe
            {
                const int cap = 128;
                byte* buf = stackalloc byte[cap];
                var n = NativeMethods.CoreGetTxDeviceName(_handle, buf, cap);
                if (n == 0) return "(none)";
                int len = (int)Math.Min(n, (uint)(cap - 1));
                return System.Text.Encoding.UTF8.GetString(buf, len);
            }
        }
        finally { _lock.ExitReadLock(); }
    }

    private string ReadDeviceStatus()
    {
        _lock.EnterReadLock();
        try
        {
            if (_disposed) return string.Empty;
            unsafe
            {
                const int cap = 4096;
                byte* buf = stackalloc byte[cap];
                var n = NativeMethods.CoreGetDeviceStatus(_handle, buf, cap);
                if (n == 0) return string.Empty;
                int len = (int)Math.Min(n, (uint)(cap - 1));
                return System.Text.Encoding.UTF8.GetString(buf, len);
            }
        }
        finally { _lock.ExitReadLock(); }
    }

    public bool IsRunning
    {
        get
        {
            _lock.EnterReadLock();
            try { return !_disposed && NativeMethods.CoreIsRunning(_handle) != 0; }
            finally { _lock.ExitReadLock(); }
        }
    }

    public void StartRx(LoraPreset preset, ulong centerFreqHz)
    {
        _lock.EnterReadLock();
        try
        {
            ThrowIfDisposed();
            var rc = NativeMethods.CoreStartRx(_handle, (int)preset, centerFreqHz);
            if (rc != 0)
                throw new InvalidOperationException($"mrf_core_start_rx failed (rc={rc})");
        }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>
    /// Start the receiver with explicit modem parameters rather than a preset.
    /// LDRO is applied automatically when the symbol time is ≥ 16 ms.
    /// </summary>
    public void StartRxParams(byte sf, uint bwHz, byte cr, ulong centerFreqHz)
    {
        _lock.EnterReadLock();
        try
        {
            ThrowIfDisposed();
            var rc = NativeMethods.CoreStartRxParams(_handle, sf, bwHz, cr, centerFreqHz);
            if (rc != 0)
                throw new InvalidOperationException($"mrf_core_start_rx_params failed (rc={rc})");
        }
        finally { _lock.ExitReadLock(); }
    }

    public void Stop()
    {
        _lock.EnterReadLock();
        try
        {
            if (_disposed) return;
            NativeMethods.CoreStop(_handle);
        }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>
    /// True if the selected TX radio backend can transmit (HackRF only).
    /// </summary>
    public bool CanTransmit
    {
        get
        {
            _lock.EnterReadLock();
            try { return !_disposed && NativeMethods.CoreCanTransmit(_handle) != 0; }
            finally { _lock.ExitReadLock(); }
        }
    }

    /// <summary>
    /// Transmit a LoRa burst carrying <paramref name="payload"/> (the fully
    /// framed/encrypted on-air bytes from <c>MeshEncoder</c>) for the given
    /// <paramref name="preset"/>, centered on <paramref name="centerFreqHz"/>.
    /// HackRF only. If TX shares the RX HackRF, RX is paused for the burst and
    /// resumed afterwards; separate RX/TX devices can run full duplex. Blocks
    /// until the burst has been streamed. Returns true on success, false if
    /// the device cannot transmit or modulation failed.
    /// </summary>
    public bool Transmit(LoraPreset preset, ulong centerFreqHz,
                         ReadOnlySpan<byte> payload,
                         byte txvgaGainDb = 30, bool ampEnable = false)
    {
        _lock.EnterReadLock();
        try
        {
            ThrowIfDisposed();
            if (payload.IsEmpty) return false;
            unsafe
            {
                fixed (byte* p = payload)
                {
                    return NativeMethods.CoreTransmit(_handle, (int)preset, centerFreqHz,
                        p, (uint)payload.Length, txvgaGainDb, ampEnable ? 1 : 0) != 0;
                }
            }
        }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>True while an IQ capture is in progress.</summary>
    public bool IsCapturing
    {
        get
        {
            _lock.EnterReadLock();
            try { return !_disposed && NativeMethods.CoreIsCapturing(_handle) != 0; }
            finally { _lock.ExitReadLock(); }
        }
    }

    /// <summary>
    /// Begin capturing the decimated modem-input IQ stream (interleaved
    /// float32 I/Q, ".cf32") to <paramref name="path"/>. Safe to call while
    /// RX is running. Returns true if the file was opened.
    /// </summary>
    public bool StartCapture(string path)
    {
        _lock.EnterReadLock();
        try
        {
            if (_disposed) return false;
            return NativeMethods.CoreStartCapture(_handle, path) != 0;
        }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>Stop and flush any in-progress IQ capture.</summary>
    public void StopCapture()
    {
        _lock.EnterReadLock();
        try
        {
            if (_disposed) return;
            NativeMethods.CoreStopCapture(_handle);
        }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>Live update of receiver gains. Safe to call any time.</summary>
    public void SetGains(byte lnaDb, byte vgaDb, bool ampEnable)
    {
        _lock.EnterReadLock();
        try
        {
            if (_disposed) return;
            NativeMethods.CoreSetGains(_handle, lnaDb, vgaDb, ampEnable ? 1 : 0);
        }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>
    /// Set a device-specific option that doesn't fit the HackRF gain model.
    /// Recognised keys (RTL-SDR): "adc_agc" and "bias_tee" (value 0/1). Unknown
    /// keys are ignored by the backend. Cached across stop/start.
    /// </summary>
    public void SetDeviceOption(string key, int value)
    {
        _lock.EnterReadLock();
        try
        {
            if (_disposed) return;
            NativeMethods.CoreSetDeviceOption(_handle, key, value);
        }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>
    /// Enable or disable the single-pole IIR DC blocker that runs on the raw
    /// zero-IF baseband before the spectrum and modem. Default is enabled; only
    /// turn off for diagnostic/calibration purposes.
    /// </summary>
    public void SetDcBlock(bool enable) =>
        SetDeviceOption("dc_block", enable ? 1 : 0);

    /// <summary>
    /// Latest signal statistics (RSSI, peak, residual DC). Cheap; safe to
    /// poll at UI rates.
    /// </summary>
    public SignalStatsSnapshot GetSignalStats()
    {
        _lock.EnterReadLock();
        try
        {
            ThrowIfDisposed();
            NativeMethods.CoreGetSignalStats(_handle, out var s);
            return new SignalStatsSnapshot(
                s.RssiDbfs, s.PeakDbfs, s.DcRe, s.DcIm, s.TotalSamples);
        }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>FFT size of the running spectrum analyzer. 0 if RX is stopped.</summary>
    public int SpectrumSize
    {
        get
        {
            _lock.EnterReadLock();
            try { return _disposed ? 0 : (int)NativeMethods.CoreSpectrumSize(_handle); }
            finally { _lock.ExitReadLock(); }
        }
    }

    /// <summary>
    /// Device sample rate in Hz of the running pipeline. This equals the full
    /// span of the spectrum/waterfall (DC at the tuned center frequency).
    /// 0 if RX is stopped.
    /// </summary>
    public uint SampleRateHz
    {
        get
        {
            _lock.EnterReadLock();
            try { return _disposed ? 0u : NativeMethods.CoreSampleRateHz(_handle); }
            finally { _lock.ExitReadLock(); }
        }
    }

    /// <summary>
    /// Actual center frequency of the displayed spectrum in Hz. Because the
    /// radio is offset-tuned, this is the channel frequency plus the LO offset
    /// (~500 kHz). Use this for frequency-axis labels. 0 if RX is stopped.
    /// </summary>
    public ulong SpectrumCenterHz
    {
        get
        {
            _lock.EnterReadLock();
            try { return _disposed ? 0ul : NativeMethods.CoreSpectrumCenterHz(_handle); }
            finally { _lock.ExitReadLock(); }
        }
    }

    /// <summary>
    /// Copies the latest dBFS spectrum frame into <paramref name="buffer"/>.
    /// Returns the number of bins written, or 0 if no frame is available or
    /// the buffer is too small. Bins are FFT-shifted (DC at index N/2).
    /// </summary>
    public int PullSpectrum(Span<float> buffer)
    {
        _lock.EnterReadLock();
        try
        {
            if (_disposed || buffer.IsEmpty) return 0;
            unsafe
            {
                fixed (float* p = buffer)
                {
                    return (int)NativeMethods.CorePullSpectrum(
                        _handle, p, (uint)buffer.Length);
                }
            }
        }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>
    /// Monotonic count of spectrum FFT frames produced since RX started. One
    /// frame corresponds to <see cref="SpectrumSize"/> received samples, so the
    /// delta between two reads is proportional to elapsed received-signal time.
    /// Use this to advance the waterfall in step with received data rather than
    /// the UI refresh rate. 0 if RX is stopped.
    /// </summary>
    public ulong SpectrumFrameCount
    {
        get
        {
            _lock.EnterReadLock();
            try { return _disposed ? 0ul : NativeMethods.CoreSpectrumFrameCount(_handle); }
            finally { _lock.ExitReadLock(); }
        }
    }

    /// <summary>
    /// Effective frame rate (Hz) of the spectrum history stream used by
    /// <see cref="PullSpectrumFrames"/> and <see cref="SpectrumFrameCount"/>.
    /// This reflects native history decimation at high sample rates, so it may
    /// be lower than <see cref="SampleRateHz"/> / <see cref="SpectrumSize"/>.
    /// 0 if RX is stopped.
    /// </summary>
    public uint SpectrumHistoryFrameRateHz
    {
        get
        {
            _lock.EnterReadLock();
            try { return _disposed ? 0u : NativeMethods.CoreSpectrumHistoryFrameRateHz(_handle); }
            finally { _lock.ExitReadLock(); }
        }
    }

    /// <summary>
    /// Extracts up to <paramref name="maxCount"/> individual spectrum frames
    /// from the rolling history, starting after <paramref name="afterFrameIdx"/>.
    /// Fills <paramref name="buffer"/> row-major with each spectrum_size() floats
    /// per frame. The buffer must hold at least maxCount * spectrum_size() floats.
    /// Returns the number of frames actually extracted (0 to maxCount).
    /// </summary>
    public int PullSpectrumFrames(Span<float> buffer, ulong afterFrameIdx, int maxCount)
    {
        _lock.EnterReadLock();
        try
        {
            if (_disposed || maxCount <= 0) return 0;
            if (buffer.Length < maxCount * (int)NativeMethods.CoreSpectrumSize(_handle)) return 0;
            unsafe
            {
                fixed (float* p = buffer)
                {
                    return (int)NativeMethods.CorePullSpectrumFrames(
                        _handle, afterFrameIdx, (uint)maxCount, p, (uint)buffer.Length);
                }
            }
        }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>
    /// Computes a high-time-resolution spectrogram of the most recent ~150 ms
    /// of modem-rate IQ, cropped to the LoRa channel. Fills <paramref
    /// name="buffer"/> row-major as <paramref name="nTime"/> rows of <paramref
    /// name="nFreq"/> dBFS values (low->high freq left->right). The buffer must
    /// hold at least nTime*nFreq floats. Returns the number of rows written, or
    /// 0 if not enough IQ history is available.
    /// </summary>
    public int PullPacketSpectrogram(Span<float> buffer, int nTime, int nFreq)
    {
        _lock.EnterReadLock();
        try
        {
            if (_disposed || nTime <= 0 || nFreq <= 0) return 0;
            if (buffer.Length < nTime * nFreq) return 0;
            unsafe
            {
                fixed (float* p = buffer)
                {
                    return (int)NativeMethods.CorePullPacketSpectrogram(
                        _handle, p, (uint)nTime, (uint)nFreq);
                }
            }
        }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>
    /// Pops the next queued demodulator event (e.g. preamble-detect log
    /// line). Returns null if none are queued.
    /// </summary>
    public string? PullEvent()
    {
        _lock.EnterReadLock();
        try
        {
            if (_disposed) return null;
            unsafe
            {
                const int cap = 4096;
                byte* buf = stackalloc byte[cap];
                var n = NativeMethods.CorePullEvent(_handle, buf, cap);
                if (n == 0) return null;
                int len = (int)Math.Min(n, (uint)(cap - 1));
                return System.Text.Encoding.UTF8.GetString(buf, len);
            }
        }
        finally { _lock.ExitReadLock(); }
    }

    public void Dispose()
    {
        bool didDispose = false;
        _lock.EnterWriteLock();
        try
        {
            if (!_disposed)
            {
                _disposed = true;
                if (_handle != 0)
                {
                    NativeMethods.CoreDestroy(_handle);
                    _handle = 0;
                }
                didDispose = true;
            }
        }
        finally { _lock.ExitWriteLock(); }

        // Only the call that actually performed the transition disposes the
        // lock itself, so a concurrent/duplicate Dispose() (or the finalizer
        // racing an explicit Dispose()) never double-disposes it.
        if (didDispose)
        {
            GC.SuppressFinalize(this);
            _lock.Dispose();
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(MeshtasticCore));
    }

    ~MeshtasticCore() => Dispose();
}

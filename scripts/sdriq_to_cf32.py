"""Convert an SDRangel .sdriq baseband recording into our interleaved float32
.cf32 replay format (and report what it contains).

.sdriq header (little-endian, 32 bytes total in current SDRangel):
  u32  sampleRate   (Hz)
  u64  centerFreq   (Hz)
  u64  startTimeStamp (ms since epoch)
  u32  sampleSize   (bits per I or Q component: 16 or 24)
  u32  filler/CRC   (varies by version)
Some builds use a slightly different layout; we sniff sampleSize and fall
back gracefully.  After the header the body is interleaved I,Q samples of
`sampleSize` bits each (16-bit -> int16, 24-bit -> packed into int32 frames).

Usage:
  python scripts/sdriq_to_cf32.py <in.sdriq> <out.cf32>
"""
import sys
import struct
import numpy as np
    src = sys.argv[1]
    dst = sys.argv[2]
    with open(src, "rb") as f:
        blob = f.read()

    sr = struct.unpack_from("<I", blob, 0)[0]
    cf = struct.unpack_from("<Q", blob, 4)[0]
    ts = struct.unpack_from("<Q", blob, 12)[0]
    samp_size = struct.unpack_from("<I", blob, 20)[0]
    header = 32

    print(f"sdriq: sampleRate={sr} Hz  centerFreq={cf} Hz  "
          f"startTs={ts}  sampleSize={samp_size} bits")
    if sr == 0 or sr > 100_000_000 or samp_size not in (16, 24):
        print("  WARNING: header looks off; dumping first 32 bytes:")
        print("  " + blob[:32].hex(" "))
        # Heuristic: assume 16-bit, sr from arg if header is bad.
        if len(sys.argv) > 3:
            sr = int(sys.argv[3])
            samp_size = 16
            print(f"  overriding sr={sr} samp_size=16")

    body = blob[header:]
    if samp_size == 16:
        iq = np.frombuffer(body, dtype="<i2").astype(np.float32) / 32768.0
    elif samp_size == 24:
        # 24-bit stored in 32-bit little-endian words by SDRangel.
        iq = np.frombuffer(body, dtype="<i4").astype(np.float32) / (1 << 23)
    else:
        raise SystemExit(f"unsupported sampleSize {samp_size}")

    n_pairs = len(iq) // 2
    iq = iq[: n_pairs * 2]
    cplx = (iq[0::2] + 1j * iq[1::2]).astype(np.complex64)

    # Optional resample to a target rate (4th arg), e.g. 1000000 so the modem's
    # integer os=4 (fs=BW*4=1MHz) assumption holds when the recording is 1.2MHz.
    if len(sys.argv) > 3:
        target = int(sys.argv[3])
        if target != sr:
            n_in = len(cplx)
            n_out = int(round(n_in * target / sr))
            # FFT (Fourier) resample preserves the spectrum exactly for a
            # band-limited LoRa frame; done in one shot (a few M points is fine).
            spec = np.fft.fft(cplx)
            if n_out < n_in:  # decimate: keep low+high freq halves
                keep = n_out
                half = keep // 2
                new = np.zeros(keep, dtype=complex)
                new[:half] = spec[:half]
                new[half:] = spec[n_in - (keep - half):]
            else:  # interpolate: zero-pad in frequency
                new = np.zeros(n_out, dtype=complex)
                half = n_in // 2
                new[:half] = spec[:half]
                new[n_out - (n_in - half):] = spec[half:]
            cplx = (np.fft.ifft(new) * (n_out / n_in)).astype(np.complex64)
            print(f"  resampled {sr} -> {target} Hz ({n_in} -> {len(cplx)} samples)")
            sr = target

    out = np.empty(len(cplx) * 2, dtype=np.float32)
    out[0::2] = cplx.real
    out[1::2] = cplx.imag
    out.tofile(dst)
    dur = len(cplx) / sr if sr else 0
    print(f"Wrote {dst}: {len(cplx)} IQ samples ({dur:.2f}s @ {sr} Hz), "
          f"peak={np.max(np.abs(out)):.4f}")
    print("Replay with:  MRF_IQ_REPLAY=<out.cf32> "
          "mrf_core_tests.exe --gtest_filter=*ReplayCapturedIq")


if __name__ == "__main__":
    main()

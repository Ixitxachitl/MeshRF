#!/usr/bin/env python3
"""Offline analysis of a captured .cf32 stream (SF9/BW250k).

Captures are recorded at the DEVICE rate (2.4 MHz by default, matching the live
HackRF/SDRangel setup). This script resamples to the modem working rate
(1 MHz = BW*OS, os=4) before scanning so the os=4 dechirp math holds. Override
the source rate with a second CLI arg, e.g. `analyze_capture.py cap.cf32 1000000`
for an older 1 MHz capture.

Scans for LoRa preambles by dechirping with a reference downchirp and looking
for a stable FFT bin across consecutive symbols. Measures the true CFO/STO and
dumps the de-chirped header symbols — ground truth, independent of the C++ sync
state machine.

Usage: python scripts/analyze_capture.py <capture.cf32> [src_rate_hz]
"""
import sys
from math import gcd
import numpy as np

SF = 9
N = 1 << SF              # 512
BW = 250_000
OS = 4                   # modem oversampling -> fs = 1 MHz
FS = BW * OS
SPS = N * OS             # samples per symbol at os=4 = 2048
SRC_RATE_DEFAULT = 2_400_000  # device capture rate


def resample_to_modem(iq, src_rate):
    """Rational-resample interleaved complex IQ from src_rate to FS (1 MHz).

    Uses scipy.signal.resample_poly when available (fast polyphase); otherwise
    falls back to a numpy polyphase implementation. No-op when src_rate == FS.
    """
    if src_rate == FS:
        return iq
    g = gcd(int(src_rate), FS)
    up = FS // g
    down = src_rate // g
    try:
        from scipy.signal import resample_poly
        return resample_poly(iq, up, down).astype(np.complex64)
    except Exception:
        # Polyphase via FFT-free upsample/filter/downsample on the (large)
        # array would be slow; do a straightforward zero-stuff + FIR + decimate.
        big = max(up, down)
        ntaps = 12 * big + 1
        n = np.arange(ntaps) - (ntaps - 1) / 2
        fc = 0.5 / big
        h = np.sinc(2 * fc * n) * np.hamming(ntaps)
        h = h / h.sum() * up
        ups = np.zeros(len(iq) * up, dtype=np.complex64)
        ups[::up] = iq
        filt = np.convolve(ups, h.astype(np.float32))
        return filt[::down].astype(np.complex64)


def load(path, src_rate=SRC_RATE_DEFAULT):
    raw = np.fromfile(path, dtype=np.float32)
    iq = (raw[0::2] + 1j * raw[1::2]).astype(np.complex64)
    if src_rate != FS:
        print(f"Resampling {len(iq)} samples {src_rate/1e6:.3f} MHz -> "
              f"{FS/1e6:.1f} MHz ...")
        iq = resample_to_modem(iq, src_rate)
    return iq.astype(np.complex64)


def ref_chirps():
    """Reference up/down chirps at os=4, matching MeshtasticRx phase convention
    phase = 2*pi*(i^2/(2N) - 0.5 i) sampled at os resolution."""
    n = np.arange(SPS)
    t = n / OS                      # chip index, fractional
    # base upchirp instantaneous freq sweeps -N/2..N/2 over the symbol
    phase = 2.0 * np.pi * (t * t / (2.0 * N) - 0.5 * t)
    up = np.exp(1j * phase).astype(np.complex64)
    down = np.conj(up)
    return up, down


def symbol_fft(block, down):
    """Dechirp one symbol-length block (SPS samples) and return the N-bin FFT
    magnitude (decimate os by summing aliases via N-point FFT of decimated)."""
    d = block * down
    # decimate by OS taking the coherent sum (matches modem in_down center)
    dec = d.reshape(-1, OS).mean(axis=1) if len(d) == SPS else None
    if dec is None:
        return None
    sp = np.fft.fft(dec, N)
    return np.abs(sp)


def scan_preamble(iq):
    """Slide over the capture; at each symbol-spaced offset dechirp with the
    downchirp and record the argmax bin. A preamble shows ~8+ consecutive
    symbols with the SAME bin (the base upchirp dechirps to a constant tone)."""
    up, down = ref_chirps()
    n_sym = (len(iq) - SPS) // SPS
    bins = np.full(n_sym, -1, dtype=int)
    peaks = np.zeros(n_sym)
    for k in range(n_sym):
        block = iq[k * SPS:(k + 1) * SPS]
        sp = symbol_fft(block, down)
        if sp is None:
            continue
        b = int(np.argmax(sp))
        bins[k] = b
        peaks[k] = sp[b] / (np.mean(sp) + 1e-9)
    return bins, peaks


def find_preamble_runs(bins, peaks, min_run=6, tol=1):
    """Find runs where the dechirped bin is stable (preamble candidate)."""
    runs = []
    i = 0
    n = len(bins)
    while i < n:
        if peaks[i] < 4.0:
            i += 1
            continue
        j = i + 1
        while j < n and abs(bins[j] - bins[i]) <= tol and peaks[j] >= 4.0:
            j += 1
        if j - i >= min_run:
            runs.append((i, j, int(np.median(bins[i:j])), float(np.mean(peaks[i:j]))))
        i = j if j > i else i + 1
    return runs


def main():
    if len(sys.argv) < 2:
        print("usage: analyze_capture.py <capture.cf32> [src_rate_hz]")
        return
    src_rate = int(sys.argv[2]) if len(sys.argv) > 2 else SRC_RATE_DEFAULT
    iq = load(sys.argv[1], src_rate)
    print(f"Loaded {len(iq)} samples = {len(iq)/FS:.2f}s @ {FS/1e6:.1f} MHz")
    power = 20 * np.log10(np.abs(iq) + 1e-12)
    print(f"Power: mean {np.mean(power):.1f} dBFS, max {np.max(power):.1f} dBFS")

    bins, peaks = scan_preamble(iq)
    runs = find_preamble_runs(bins, peaks)
    print(f"\nFound {len(runs)} preamble-like runs (stable dechirp bin, peak>4x):")
    for (i, j, b, pk) in runs:
        t = i * SPS / FS
        print(f"  t={t:7.3f}s  sym[{i}:{j}] ({j-i} syms) bin={b:3d} peak={pk:.1f}x")
        # Estimate fractional offset by quadratic interpolation around the run.

    # Coarse: report the histogram of stable bins to see the dominant alignment.
    if runs:
        print("\nFirst run detailed bins:")
        i, j, b, pk = runs[0]
        seg = max(0, i - 2)
        for k in range(seg, min(len(bins), j + 12)):
            t = k * SPS / FS
            mark = "<-pre" if i <= k < j else ""
            print(f"   sym {k:4d} t={t:7.3f}s bin={bins[k]:3d} peak={peaks[k]:5.1f}x {mark}")


if __name__ == "__main__":
    main()

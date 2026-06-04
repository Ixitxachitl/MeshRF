#!/usr/bin/env python3
"""Compare average power spectra of two .cf32 captures around DC.

Both our app capture (offset-tuned + DC-blocked, LoRa mixed to DC) and an
SDRangel .sdriq->cf32 recording (tuned centred, LoRa at DC) should show a flat
~250 kHz LoRa channel centred on DC. This dumps the averaged PSD so we can spot:
  - a DC notch (our DC blocker eating the chirp centre)
  - band-edge rolloff / asymmetry (offset tuning pushing LoRa toward the HackRF
    analog baseband-filter skirt)
  - a frequency offset (LoRa not actually at DC)

Usage: python scripts/spec_compare.py <fileA.cf32> <rateA> <fileB.cf32> <rateB>
"""
import sys
import numpy as np

NFFT = 4096


def avg_psd(path, rate, max_frames=400):
    # Read a bounded slice (interleaved float32 I/Q).
    raw = np.fromfile(path, dtype=np.float32, count=NFFT * 2 * max_frames)
    iq = raw[0::2] + 1j * raw[1::2]
    nframes = len(iq) // NFFT
    iq = iq[: nframes * NFFT].reshape(nframes, NFFT)
    win = np.hanning(NFFT)
    acc = np.zeros(NFFT)
    for f in range(nframes):
        sp = np.fft.fftshift(np.fft.fft(iq[f] * win))
        acc += np.abs(sp) ** 2
    acc /= nframes
    psd_db = 10 * np.log10(acc + 1e-12)
    psd_db -= psd_db.max()
    freqs = np.fft.fftshift(np.fft.fftfreq(NFFT, d=1.0 / rate))
    return freqs, psd_db


def summarize(name, path, rate):
    freqs, psd = avg_psd(path, rate)
    binhz = rate / NFFT
    # LoRa channel is +/-125 kHz around DC.
    inband = np.abs(freqs) <= 125_000
    dc = np.argmin(np.abs(freqs))
    # Power in +/-2 bins around DC vs the in-band median (DC-notch detector).
    dc_region = psd[dc - 2 : dc + 3].mean()
    inband_med = np.median(psd[inband])
    left = psd[(freqs >= -125_000) & (freqs <= -100_000)].mean()
    right = psd[(freqs >= 100_000) & (freqs <= 125_000)].mean()
    print(f"== {name} ({rate/1e6:.3f} MHz, bin={binhz:.0f} Hz) ==")
    print(f"   in-band(+/-125k) median : {inband_med:6.2f} dB")
    print(f"   DC +/-2 bins            : {dc_region:6.2f} dB  "
          f"(notch={inband_med - dc_region:+.2f} dB)")
    print(f"   lower edge (-125..-100k): {left:6.2f} dB")
    print(f"   upper edge (+100..+125k): {right:6.2f} dB  "
          f"(asym={left - right:+.2f} dB)")
    # Where is the peak energy (is LoRa actually at DC?)
    pk = freqs[np.argmax(psd)]
    print(f"   spectral peak           : {pk/1e3:+.1f} kHz")
    # Coarse shape: power at +/-200k (out of band) vs in-band.
    oob = psd[(np.abs(freqs) >= 200_000) & (np.abs(freqs) <= 300_000)].mean()
    print(f"   out-of-band (200-300k)  : {oob:6.2f} dB  "
          f"(rejection={inband_med - oob:+.2f} dB)\n")


if __name__ == "__main__":
    if len(sys.argv) != 5:
        print(__doc__)
        sys.exit(1)
    summarize("A", sys.argv[1], float(sys.argv[2]))
    summarize("B", sys.argv[3], float(sys.argv[4]))

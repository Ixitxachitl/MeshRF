#!/usr/bin/env python3
"""Save a spectrogram PNG of a time window of a .cf32 capture.

Usage: python scripts/spectrogram.py <capture.cf32> [t_start_s] [t_end_s]
"""
import sys
import numpy as np
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt

FS = 1_000_000

path = sys.argv[1]
t0 = float(sys.argv[2]) if len(sys.argv) > 2 else 8.30
t1 = float(sys.argv[3]) if len(sys.argv) > 3 else 8.70

raw = np.fromfile(path, dtype=np.float32)
iq = raw[0::2] + 1j * raw[1::2]
a = int(t0 * FS)
b = int(t1 * FS)
seg = iq[a:b]
print(f"segment {t0}-{t1}s = {len(seg)} samples")

nfft = 512
hop = 128
win = np.hanning(nfft)
frames = (len(seg) - nfft) // hop
spec = np.zeros((nfft, frames), dtype=np.float32)
for i in range(frames):
    chunk = seg[i * hop:i * hop + nfft] * win
    spec[:, i] = np.fft.fftshift(20 * np.log10(np.abs(np.fft.fft(chunk)) + 1e-9))

plt.figure(figsize=(16, 6))
extent = [t0, t1, -FS / 2 / 1e3, FS / 2 / 1e3]
plt.imshow(spec, aspect="auto", origin="lower", extent=extent,
           cmap="viridis", vmax=spec.max(), vmin=spec.max() - 60)
plt.xlabel("time (s)")
plt.ylabel("freq (kHz)")
plt.title("Capture spectrogram")
plt.colorbar(label="dB")
out = "spectrogram.png"
plt.savefig(out, dpi=110, bbox_inches="tight")
print(f"saved {out}")

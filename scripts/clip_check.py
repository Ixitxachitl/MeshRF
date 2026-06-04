"""Check a .cf32 capture for clipping / overdrive, which corrupts LoRa header
FEC even when the chirps still look clean in a spectrogram."""
import sys
import numpy as np

path = sys.argv[1] if len(sys.argv) > 1 else "capture_20260603_095048.cf32"
raw = np.fromfile(path, dtype=np.float32)
i = raw[0::2]
q = raw[1::2]
mag = np.sqrt(i * i + q * q)

# Focus on the frame region (t ~ 8.32 s @ 1 MHz) AND whole file.
fs = 1_000_000
print(f"File: {path}  {len(i)} samples ({len(i)/fs:.2f}s)")
for label, sl in [("whole", slice(None)),
                  ("frame 8.30-8.45s", slice(int(8.30*fs), int(8.45*fs)))]:
    ii, qq, mm = i[sl], q[sl], mag[sl]
    n = len(mm)
    if n == 0:
        continue
    # how many samples ride at/above various thresholds
    for thr in (0.99, 0.95, 0.90):
        ci = np.sum(np.abs(ii) >= thr)
        cq = np.sum(np.abs(qq) >= thr)
        print(f"  [{label}] |I|>={thr}: {ci} ({100*ci/n:.3f}%)  "
              f"|Q|>={thr}: {cq} ({100*cq/n:.3f}%)")
    print(f"  [{label}] max|I|={np.max(np.abs(ii)):.3f} max|Q|={np.max(np.abs(qq)):.3f} "
          f"max|mag|={np.max(mm):.3f} mean|mag|={np.mean(mm):.4f}")
    # DC offset (LO leakage) — a big DC term corrupts symbols near bin 0.
    print(f"  [{label}] DC: I_mean={np.mean(ii):+.4f} Q_mean={np.mean(qq):+.4f}")

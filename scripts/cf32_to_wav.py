"""Convert an interleaved float32 .cf32 IQ capture into a stereo 16-bit WAV
that SDRangel's File Input (WAV) can open: left channel = I, right = Q,
sample rate = IQ rate.

Usage:
  python scripts/cf32_to_wav.py <in.cf32> <out.wav> [sample_rate_hz]
"""
import sys
import struct
import numpy as np


def main():
    src = sys.argv[1]
    dst = sys.argv[2]
    fs = int(sys.argv[3]) if len(sys.argv) > 3 else 1_000_000

    raw = np.fromfile(src, dtype=np.float32)
    i = raw[0::2]
    q = raw[1::2]
    n = min(len(i), len(q))
    i, q = i[:n], q[:n]

    # Scale so the loudest sample maps near full-scale int16 without clipping.
    peak = float(np.max(np.abs(np.concatenate([i, q])))) or 1.0
    scale = 32000.0 / peak
    il = np.clip(np.round(i * scale), -32768, 32767).astype(np.int16)
    ql = np.clip(np.round(q * scale), -32768, 32767).astype(np.int16)

    inter = np.empty(n * 2, dtype=np.int16)
    inter[0::2] = il
    inter[1::2] = ql
    data = inter.tobytes()

    # Minimal 16-bit PCM stereo WAV header.
    byte_rate = fs * 2 * 2  # sr * channels * bytes/sample
    block_align = 2 * 2
    with open(dst, "wb") as f:
        f.write(b"RIFF")
        f.write(struct.pack("<I", 36 + len(data)))
        f.write(b"WAVE")
        f.write(b"fmt ")
        f.write(struct.pack("<I", 16))        # fmt chunk size
        f.write(struct.pack("<H", 1))         # PCM
        f.write(struct.pack("<H", 2))         # channels (I,Q)
        f.write(struct.pack("<I", fs))        # sample rate
        f.write(struct.pack("<I", byte_rate))
        f.write(struct.pack("<H", block_align))
        f.write(struct.pack("<H", 16))        # bits/sample
        f.write(b"data")
        f.write(struct.pack("<I", len(data)))
        f.write(data)

    print(f"Wrote {dst}: {n} IQ samples, {fs} Hz, stereo int16, peak={peak:.4f}")


if __name__ == "__main__":
    main()

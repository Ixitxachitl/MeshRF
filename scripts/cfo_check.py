"""Validate the up/down CFO-STO decomposition on a synthetic frame with a
known carrier offset, using the exact gen_symbol / DOWN convention from
ref_decode.py."""
import numpy as np
import ref_decode as R

N, OS, SPS, FS, BW = R.N, R.OS, R.SPS, R.FS, R.BW
DOWN = R.DOWN
UP = np.conj(DOWN)


def fold_argmax(d):
    mag = np.abs(np.fft.fft(d))
    folded = mag[0:N] + mag[N:2*N] + mag[2*N:3*N] + mag[3*N:4*N]
    return int(np.argmax(folded)), float(np.max(folded))


def build(cfo_bins, sto_bins):
    parts = [R.gen_symbol(0.0) for _ in range(8)]
    parts.append(R.gen_symbol(0.0, down=True))
    parts.append(R.gen_symbol(0.0, down=True))
    iq = np.concatenate(parts).astype(np.complex64)
    # apply integer STO by rolling whole samples
    iq = np.roll(iq, sto_bins * OS)
    # apply CFO: cfo_bins * binwidth, binwidth = BW/N
    f = cfo_bins * BW / N
    n = np.arange(len(iq))
    iq = iq * np.exp(1j * 2 * np.pi * f * n / FS).astype(np.complex64)
    return iq


for cfo in [0, 5, 28, -28, 40]:
    sto = 17
    iq = build(cfo, sto)
    # preamble window 2 (index 2), SFD window 8
    ub, _ = fold_argmax(iq[2*SPS:3*SPS] * DOWN)
    db, _ = fold_argmax(iq[8*SPS:9*SPS] * UP)
    cfo_est = ((ub - db) // 2) % N
    cfo_signed = cfo_est if cfo_est < N//2 else cfo_est - N
    sto_est = ((ub + db) // 2) % N
    print(f"true cfo={cfo:+3d} sto={sto}: up_bin={ub:3d} down_bin={db:3d} "
          f"-> cfo_est={cfo_signed:+3d} sto_est={sto_est}")

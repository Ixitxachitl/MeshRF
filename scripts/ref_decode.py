#!/usr/bin/env python3
"""Independent reference decode of the clean frame in a .cf32 capture.

Brute-forces the header-start sample and integer CFO around the detected
preamble, demodulates 8 header symbols with an oversampled (os=4) folded-FFT
demod, and runs the EXACT FEC port (symbol_to_bits / deinterleave /
hamming_decode / header_crc5) used by the C++ modem. Reports any alignment
that yields a sane explicit header (parity-ok, has-crc, sane length/CR).

This is the ground-truth test: if NO alignment decodes here, the captured
frame itself is anomalous; if one does, the C++ front-end sync is misaligned.

Usage: python scripts/ref_decode.py <capture.cf32> [anchor_symbol_index]
"""
import sys
import numpy as np

SF = 9
N = 1 << SF            # 512
OS = 4
SPS = N * OS           # 2048
BW = 250_000
FS = BW * OS


# ----------------------------- FEC port -------------------------------------
def to_gray(v):
    return v ^ (v >> 1)


def symbol_to_bits(symbol_value, sf):
    spread = 4
    nb_eff = 1 << (sf - 2)
    reduced = ((symbol_value + spread // 2 - 1) // spread) % nb_eff
    return to_gray(reduced)


def deinterleave(symbols_bits, sf_app, cr_app):
    cws = [0] * sf_app
    for k in range(cr_app):
        for m in range(sf_app):
            i = (m + k) % sf_app
            bit = (symbols_bits[k] >> m) & 1
            cws[i] |= bit << k
    return cws


def hamming_decode_cr4(cw):
    b = [(cw >> i) & 1 for i in range(8)]
    p0 = b[0] ^ b[1] ^ b[2] ^ b[4]
    p1 = b[1] ^ b[2] ^ b[3] ^ b[5]
    p2 = b[0] ^ b[1] ^ b[3] ^ b[6]
    p3 = b[0] ^ b[2] ^ b[3] ^ b[7]
    parity = (p0 << 0) | (p1 << 1) | (p2 << 2) | (p3 << 3)
    table = {0xD: 1, 0x7: 2, 0xB: 4, 0xE: 8}
    if parity in table:
        cw ^= table[parity]
    return cw & 0xF


def header_crc5(length, fec_info):
    a = [(length >> (4 + i)) & 1 for i in range(4)]
    bb = [(length >> i) & 1 for i in range(4)]
    c = [(fec_info >> i) & 1 for i in range(4)]
    return (((a[0] ^ a[1] ^ a[2] ^ a[3]) << 4)
            | ((a[3] ^ bb[1] ^ bb[2] ^ bb[3] ^ c[0]) << 3)
            | ((a[2] ^ bb[0] ^ bb[3] ^ c[1] ^ c[3]) << 2)
            | ((a[1] ^ bb[0] ^ bb[2] ^ c[0] ^ c[1] ^ c[2]) << 1)
            | ((a[0] ^ bb[1] ^ c[0] ^ c[1] ^ c[2] ^ c[3]) << 0))


def gray_demap(symbol_value, sf, ppm):
    diff = sf - ppm
    if diff <= 0:
        return to_gray(symbol_value)
    offset = (1 << diff) // 2
    reduced = (symbol_value + offset) >> diff
    return to_gray(reduced)


def hamming_decode_cr(cw, cr):
    if cr >= 4:
        return hamming_decode_cr4(cw)
    if cr == 3:
        b = [(cw >> i) & 1 for i in range(7)]
        p0 = b[0] ^ b[1] ^ b[2] ^ b[4]
        p1 = b[1] ^ b[2] ^ b[3] ^ b[5]
        p2 = b[0] ^ b[1] ^ b[3] ^ b[6]
        parity = (p0 << 0) | (p1 << 1) | (p2 << 2)
        table = {0x5: 1, 0x7: 2, 0x3: 4, 0x6: 8}
        if parity in table:
            cw ^= table[parity]
        return cw & 0xF
    return cw & 0xF  # cr 1,2 detection only


WHITEN = [
    0xFF, 0xFE, 0xFC, 0xF8, 0xF0, 0xE1, 0xC2, 0x85, 0x0B, 0x17, 0x2F, 0x5E, 0xBC, 0x78, 0xF1, 0xE3,
    0xC6, 0x8D, 0x1A, 0x34, 0x68, 0xD0, 0xA0, 0x40, 0x80, 0x01, 0x02, 0x04, 0x08, 0x11, 0x23, 0x47,
    0x8E, 0x1C, 0x38, 0x71, 0xE2, 0xC4, 0x89, 0x12, 0x25, 0x4B, 0x97, 0x2E, 0x5C, 0xB8, 0x70, 0xE0,
    0xC0, 0x81, 0x03, 0x06, 0x0C, 0x19, 0x32, 0x64, 0xC9, 0x92, 0x24, 0x49, 0x93, 0x26, 0x4D, 0x9B,
    0x37, 0x6E, 0xDC, 0xB9, 0x72, 0xE4, 0xC8, 0x90, 0x20, 0x41, 0x82, 0x05, 0x0A, 0x15, 0x2B, 0x56,
    0xAD, 0x5B, 0xB6, 0x6D, 0xDA, 0xB5, 0x6B, 0xD6, 0xAC, 0x59, 0xB2, 0x65, 0xCB, 0x96, 0x2C, 0x58,
    0xB0, 0x61, 0xC3, 0x87, 0x0F, 0x1F, 0x3E, 0x7D, 0xFB, 0xF6, 0xED, 0xDB, 0xB7, 0x6F, 0xDE, 0xBD,
    0x7A, 0xF5, 0xEB, 0xD7, 0xAE, 0x5D, 0xBA, 0x74, 0xE8, 0xD1, 0xA2, 0x44, 0x88, 0x10, 0x21, 0x43,
    0x86, 0x0D, 0x1B, 0x36, 0x6C, 0xD8, 0xB1, 0x63, 0xC7, 0x8F, 0x1E, 0x3C, 0x79, 0xF3, 0xE7, 0xCE,
    0x9C, 0x39, 0x73, 0xE6, 0xCC, 0x98, 0x31, 0x62, 0xC5, 0x8B, 0x16, 0x2D, 0x5A, 0xB4, 0x69, 0xD2,
    0xA4, 0x48, 0x91, 0x22, 0x45, 0x8A, 0x14, 0x29, 0x52, 0xA5, 0x4A, 0x95, 0x2A, 0x54, 0xA9, 0x53,
    0xA7, 0x4E, 0x9D, 0x3B, 0x77, 0xEE, 0xDD, 0xBB, 0x76, 0xEC, 0xD9, 0xB3, 0x67, 0xCF, 0x9E, 0x3D,
    0x7B, 0xF7, 0xEF, 0xDF, 0xBF, 0x7E, 0xFD, 0xFA, 0xF4, 0xE9, 0xD3, 0xA6, 0x4C, 0x99, 0x33, 0x66,
    0xCD, 0x9A, 0x35, 0x6A, 0xD4, 0xA8, 0x51, 0xA3, 0x46, 0x8C, 0x18, 0x30, 0x60, 0xC1, 0x83, 0x07,
    0x0E, 0x1D, 0x3A, 0x75, 0xEA, 0xD5, 0xAA, 0x55, 0xAB, 0x57, 0xAF, 0x5F, 0xBE, 0x7C, 0xF9, 0xF2,
    0xE5, 0xCA, 0x94, 0x28, 0x50, 0xA1, 0x42, 0x84, 0x09, 0x13, 0x27, 0x4F, 0x9F, 0x3F, 0x7F]


def crc16gr(data):
    crc = 0
    for byte in data:
        b = byte
        for _ in range(8):
            top = ((crc & 0x8000) >> 8) ^ (b & 0x80)
            if top != 0:
                crc = ((crc << 1) ^ 0x1021) & 0xFFFF
            else:
                crc = (crc << 1) & 0xFFFF
            b = (b << 1) & 0xFF
    return crc & 0xFFFF


def decode_header(syms8):
    sf_app = SF - 2          # 7
    cr_app = 8
    sym_bits = [symbol_to_bits(s, SF) for s in syms8]
    cws = deinterleave(sym_bits, sf_app, cr_app)
    nib = [hamming_decode_cr4(c) for c in cws]
    if len(nib) < 5:
        return None
    length = ((nib[0] & 0xF) << 4) | (nib[1] & 0xF)
    fec_info = nib[2] & 0xF
    got_chk = ((nib[3] & 0xF) << 4) | (nib[4] & 0xF)
    expected = header_crc5(length, fec_info)
    has_crc = (fec_info & 1) != 0
    cr = (fec_info >> 1) & 0x07
    parity_ok = (got_chk == expected)
    sane = parity_ok and has_crc and 1 <= cr <= 4 and 16 <= length <= 255
    leak = [hamming_decode_cr4(c) & 0xF for c in cws[5:]]
    return dict(length=length, cr=cr, has_crc=has_crc, parity_ok=parity_ok,
                sane=sane, nib=nib, chk=got_chk, exp=expected, leak=leak)


def payload_symbol_count(length, cr, has_crc):
    sf = SF
    eff = sf  # no LDRO at SF9/BW250
    num = 8 * length - 4 * sf + 28 + 16 * (1 if has_crc else 0)
    den = 4 * eff
    blocks = (num + den - 1) // den
    if blocks < 0:
        blocks = 0
    return blocks * (cr + 4)


def decode_payload(pay_syms, length, cr, has_crc, leak):
    ppm = SF
    cw_len = cr + 4
    sym_bits = [gray_demap(((s - 1) % N + N) % N, SF, ppm) for s in pay_syms]
    nblocks = len(sym_bits) // cw_len
    nibbles = list(leak)
    for b in range(nblocks):
        block = sym_bits[b * cw_len:(b + 1) * cw_len]
        cws = deinterleave(block, ppm, cw_len)
        for cw in cws:
            nibbles.append(hamming_decode_cr(cw, cr) & 0xF)
    raw = []
    for i in range(0, len(nibbles) - 1, 2):
        raw.append(((nibbles[i + 1] & 0xF) << 4) | (nibbles[i] & 0xF))
    if length < 2 or len(raw) < length + 2:
        return False, raw
    dw = list(raw)
    for i in range(length):
        dw[i] ^= WHITEN[i % 255]
    crc = crc16gr(dw[:length - 2])
    crc ^= dw[length - 1]
    crc ^= dw[length - 2] << 8
    crc &= 0xFFFF
    rx = dw[length] | (dw[length + 1] << 8)
    return crc == rx, dw


# --------------------------- front end --------------------------------------
def downchirp_os():
    n = np.arange(SPS)
    k = n / OS
    phase = 2.0 * np.pi * (k * k / (2.0 * N) - 0.5 * k)
    return np.exp(-1j * phase).astype(np.complex64)


DOWN = downchirp_os()


def demod(block, cfo_corr):
    d = (block * cfo_corr) * DOWN
    sp = np.fft.fft(d)
    mag = np.abs(sp)
    folded = mag[0:N] + mag[N:2 * N] + mag[2 * N:3 * N] + mag[3 * N:4 * N]
    return int(np.argmax(folded)), folded


def est_frac_cfo(iq, pre_start, nsym=6):
    """Fractional CFO from preamble: average residual of the dechirped peak."""
    fracs = []
    for s in range(nsym):
        block = iq[pre_start + s * SPS: pre_start + (s + 1) * SPS]
        if len(block) < SPS:
            break
        d = block * DOWN
        sp = np.fft.fft(d)
        mag = np.abs(sp)
        folded = mag[0:N] + mag[N:2 * N] + mag[2 * N:3 * N] + mag[3 * N:4 * N]
        b = int(np.argmax(folded))
        # parabolic interpolation around b (folded)
        l = folded[(b - 1) % N]
        r = folded[(b + 1) % N]
        c = folded[b]
        denom = (l - 2 * c + r)
        delta = 0.5 * (l - r) / denom if denom != 0 else 0.0
        fracs.append(delta)
    return float(np.median(fracs)) if fracs else 0.0


def from_gray(g):
    g ^= g >> 8
    g ^= g >> 4
    g ^= g >> 2
    g ^= g >> 1
    return g


def hamming_encode(nib, cr):
    d0 = nib & 1
    d1 = (nib >> 1) & 1
    d2 = (nib >> 2) & 1
    d3 = (nib >> 3) & 1
    p1 = d3 ^ d2 ^ d1
    p2 = d3 ^ d2 ^ d0
    p3 = d3 ^ d1 ^ d0
    p4 = d2 ^ d1 ^ d0
    cw = (p1 << 7) | (p2 << 6) | (p3 << 5) | (p4 << 4) | (d3 << 3) | (d2 << 2) | (d1 << 1) | d0
    if cr == 1:
        cw &= 0x1F
    elif cr == 2:
        cw &= 0x3F
    elif cr == 3:
        cw &= 0x7F
    return cw


def interleave(codewords, sf_app, cr_app):
    syms = [0] * cr_app
    for k in range(cr_app):
        for m in range(sf_app):
            i = (m + k) % sf_app
            bit = (codewords[i] >> k) & 1
            syms[k] |= bit << m
    return syms


def make_header_bins(length, cr, crc):
    sf_app = SF - 2
    cr_app = 8
    fec = (cr << 1) | (1 if crc else 0)
    chk = header_crc5(length, fec)
    nib = [0] * sf_app
    nib[0] = (length >> 4) & 0xF
    nib[1] = length & 0xF
    nib[2] = fec & 0xF
    nib[3] = (chk >> 4) & 0xF
    nib[4] = chk & 0xF
    cws = [hamming_encode(n, 4) for n in nib]
    sym_bits = interleave(cws, sf_app, cr_app)
    bins = [(from_gray(b) << 2) % N for b in sym_bits]
    return bins


def selftest():
    print("Header FEC round-trip self-test:")
    ok = True
    for (length, cr, crc) in [(20, 1, True), (29, 4, True), (255, 2, True),
                              (16, 3, True), (64, 1, True)]:
        bins = make_header_bins(length, cr, crc)
        res = decode_header(bins)
        good = (res is not None and res['length'] == length and res['cr'] == cr
                and res['has_crc'] == crc and res['parity_ok'])
        ok = ok and good
        print(f"  len={length} cr={cr} crc={crc} -> bins={bins} "
              f"decoded len={res['length']} cr={res['cr']} crc={res['has_crc']} "
              f"parity={res['parity_ok']} {'OK' if good else 'FAIL'}")
    print("SELFTEST", "PASS" if ok else "FAIL")


def gen_symbol(v, down=False):
    out = np.empty(SPS, dtype=np.complex64)
    slope = -1.0 if down else 1.0
    phase = 0.0
    for m in range(SPS):
        out[m] = np.cos(phase) + 1j * np.sin(phase)
        t = m / OS
        ph = (v + slope * t) % N
        f = (ph - N / 2.0) / N
        phase += 2.0 * np.pi * f / OS
    return out


def iq_selftest(length=29, cr=4, crc=True, cfo_hz=0.0):
    """Build a synthetic os=4 frame, run the demod+decoder end-to-end."""
    bins = make_header_bins(length, cr, crc)
    parts = []
    up0 = gen_symbol(0.0)
    for _ in range(10):
        parts.append(up0)
    parts.append(gen_symbol(16.0))
    parts.append(gen_symbol(88.0))
    down0 = gen_symbol(0.0, down=True)
    parts.append(down0)
    parts.append(down0)
    parts.append(down0[:SPS // 4])
    header_start = sum(len(p) for p in parts)
    for b in bins:
        parts.append(gen_symbol(float(b)))
    iq = np.concatenate(parts).astype(np.complex64)
    if cfo_hz:
        n = np.arange(len(iq))
        iq = iq * np.exp(1j * 2 * np.pi * cfo_hz * n / FS).astype(np.complex64)
    print(f"IQ self-test: synth frame len={length} cr={cr}, header at sample "
          f"{header_start} (sym {header_start/SPS:.2f}), bins={bins}")
    # Demod the 8 header symbols at the known start (no CFO sweep needed when
    # cfo_hz=0) and decode.
    nfrac = np.arange(SPS)
    cc = np.ones(SPS, dtype=np.complex64)
    syms = []
    for i in range(8):
        s0 = header_start + i * SPS
        b, _ = demod(iq[s0:s0 + SPS], cc)
        syms.append(b)
    res = decode_header(syms)
    print(f"  got syms={syms}")
    print(f"  decoded len={res['length']} cr={res['cr']} crc={res['has_crc']} "
          f"parity={res['parity_ok']}")
    good = res['length'] == length and res['cr'] == cr and res['parity_ok']
    print("IQ-SELFTEST", "PASS" if good else "FAIL")


def main():
    if len(sys.argv) > 1 and sys.argv[1] == "--selftest":
        selftest()
        return
    if len(sys.argv) > 1 and sys.argv[1] == "--iqtest":
        iq_selftest()
        return
    path = sys.argv[1]
    raw = np.fromfile(path, dtype=np.float32)
    iq = (raw[0::2] + 1j * raw[1::2]).astype(np.complex64)
    print(f"Loaded {len(iq)} samples = {len(iq)/FS:.2f}s")

    anchor = int(sys.argv[2]) if len(sys.argv) > 2 else 4064  # preamble sym idx
    pre_sample = anchor * SPS
    frac = est_frac_cfo(iq, pre_sample)
    print(f"Preamble anchor sym={anchor} sample={pre_sample}  frac_cfo_bin={frac:+.3f}")

    # --- CFO/STO decomposition via preamble up-bin and SFD down-bin ----------
    # Up-chirp dechirp (multiply by DOWN) over preamble -> up_bin = STO + CFO.
    # Down-chirp dechirp (multiply by conj(DOWN)=UP) over SFD -> down_bin =
    # STO - CFO.  Then CFO = (up-down)/2, STO = (up+down)/2  (mod N).
    UP = np.conj(DOWN)

    def up_bin_at(s0):
        d = iq[s0:s0 + SPS] * DOWN
        mag = np.abs(np.fft.fft(d))
        folded = mag[0:N] + mag[N:2*N] + mag[2*N:3*N] + mag[3*N:4*N]
        return int(np.argmax(folded)), float(np.max(folded))

    def down_bin_at(s0):
        d = iq[s0:s0 + SPS] * UP
        mag = np.abs(np.fft.fft(d))
        folded = mag[0:N] + mag[N:2*N] + mag[2*N:3*N] + mag[3*N:4*N]
        return int(np.argmax(folded)), float(np.max(folded))

    # Measure up_bin across the preamble (stable region).
    ub, up_peak = up_bin_at(pre_sample)
    # The SFD downchirps are ~12.25 symbols after preamble start in our frame
    # model, but find them: scan a window of symbols and pick where down_bin
    # gives the strongest, most-stable peak.
    print("\n-- SFD search (down-chirp dechirp) --")
    best = None
    for off in range(8, 16):
        s0 = pre_sample + off * SPS
        db, dpk = down_bin_at(s0)
        ubh, upk = up_bin_at(s0)
        kind = "DOWN" if dpk > upk else "up"
        print(f"  +{off}sym: down_bin={db:3d} peak={dpk:8.0f} | "
              f"up_bin={ubh:3d} peak={upk:8.0f} -> {kind}-chirp")
        if best is None or dpk > best[1]:
            if dpk > upk:  # only consider where downchirp dominates (= SFD)
                best = (db, dpk, off)
    print(f"\nPreamble up_bin = {ub} (peak {up_peak:.0f})")
    if best is not None:
        db, dpk, off = best
        cfo = ((ub - db) // 2) % N
        sto = ((ub + db) // 2) % N
        cfo_signed = cfo if cfo < N // 2 else cfo - N
        print(f"SFD down_bin   = {db} (peak {dpk:.0f}) at +{off}sym")
        print(f"=> CFO = (up-down)/2 = {cfo} bins ({cfo_signed} signed) "
              f"= {cfo_signed * BW / N:.0f} Hz")
        print(f"=> STO = (up+down)/2 = {sto} bins ({sto * OS} samples)")
    if len(sys.argv) > 3 and sys.argv[3] == "--synconly":
        return

    # Build a fractional-CFO correction vector (applied per symbol window).
    nfrac = np.arange(SPS)
    cfo_corr = np.exp(-1j * 2.0 * np.pi * (frac) * (nfrac / OS) / N).astype(np.complex64)

    # Brute force: header start sample over ~6 symbols after preamble, integer
    # CFO bin -8..8.  Header starts roughly 4.25 symbols after the last
    # preamble upchirp; search wide to be safe.
    base = pre_sample  # search from preamble onward
    hits = []
    crc_hits = []
    frac_grid = [f / 20.0 for f in range(-10, 11)]  # -0.5 .. 0.5 step 0.05
    # Integer-CFO is applied as a circular shift of the 2048-pt dechirped
    # spectrum (equivalent to a pre-dechirp exponential), which is the only
    # correct way to remove a LARGE CFO (~256 bins here).  Post-fold bin
    # subtraction is WRONG for large CFO because of the os=4 alias folding.
    cfo_candidates = (list(range(-40, 41)) +
                      list(range(244, 269)) +
                      list(range(-268, -243)))
    def spec_of(block, cc):
        return np.fft.fft((block * cc) * DOWN)

    def folded_bin(sp, shift):
        mag = np.abs(np.roll(sp, shift))
        folded = mag[0:N] + mag[N:2*N] + mag[2*N:3*N] + mag[3*N:4*N]
        return int(np.argmax(folded))

    for start in range(base + 11 * SPS, base + 14 * SPS, OS):
        for fr in frac_grid:
            cc = np.exp(-1j * 2.0 * np.pi * fr * (nfrac / OS) / N).astype(np.complex64)
            # Dechirped spectra of the 8 header symbols ONCE for this (start,fr).
            hdr_sp = []
            ok = True
            for i in range(8):
                s0 = start + i * SPS
                if s0 + SPS > len(iq):
                    ok = False
                    break
                hdr_sp.append(spec_of(iq[s0:s0 + SPS], cc))
            if not ok:
                break
            pay_sp = None  # dechirp payload lazily only if a header is sane
            for cfo_int in cfo_candidates:
                try:
                    # Apply integer CFO as a spectrum roll, then fold.
                    syms = [folded_bin(sp, cfo_int) for sp in hdr_sp]
                    res = decode_header(syms)
                    if res is None or not res['sane']:
                        continue
                    hits.append((start, cfo_int, fr, syms, res))
                    n_pay = payload_symbol_count(res['length'], res['cr'], res['has_crc'])
                    if n_pay <= 0 or n_pay > 400:
                        continue
                    if pay_sp is None or len(pay_sp) < n_pay:
                        pay_sp = []
                        for i in range(n_pay):
                            s0 = start + (8 + i) * SPS
                            if s0 + SPS > len(iq):
                                break
                            pay_sp.append(spec_of(iq[s0:s0 + SPS], cc))
                    if len(pay_sp) < n_pay:
                        continue
                    pay = [folded_bin(sp, cfo_int) for sp in pay_sp[:n_pay]]
                    crc_ok, dw = decode_payload(pay, res['length'], res['cr'],
                                                res['has_crc'], res['leak'])
                    if crc_ok:
                        crc_hits.append((start, cfo_int, fr, syms, res, dw))
                except Exception as e:
                    sys.stderr.write(f"ERR start={start} fr={fr} cfo={cfo_int}: {e}\n")
                    continue

    print(f"\nBrute force: {len(hits)} sane-header decode(s), "
          f"{len(crc_hits)} with VALID payload CRC16")
    lines = [f"Brute force: {len(hits)} sane-header, {len(crc_hits)} CRC16-OK"]
    for (start, cfo_int, fr, syms, res, dw) in crc_hits[:10]:
        sym_off = (start - pre_sample) / SPS
        payload = bytes(dw[:res['length']])
        s = (f"  *** CRC-OK start=+{sym_off:.3f}sym cfo_int={cfo_int:+d} "
             f"frac={fr:+.2f} len={res['length']} cr={res['cr']} "
             f"bytes={payload.hex()} syms={syms}")
        print(s)
        lines.append(s)
    if not crc_hits:
        print("  No payload CRC16 passed.")
        lines.append("  No payload CRC16 passed.")
        for (start, cfo_int, fr, syms, res) in hits[:25]:
            sym_off = (start - pre_sample) / SPS
            s = (f"   sane-hdr start=+{sym_off:.3f}sym cfo_int={cfo_int:+d} "
                 f"frac={fr:+.2f} len={res['length']} cr={res['cr']} syms={syms}")
            print(s)
            lines.append(s)
    with open("ref_result.txt", "w") as fh:
        fh.write("\n".join(lines) + "\n")


if __name__ == "__main__":
    main()

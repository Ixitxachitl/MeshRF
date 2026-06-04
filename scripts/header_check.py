#!/usr/bin/env python3
"""Brute-force whether a captured 8-symbol LoRa header is recoverable.

Replicates MeshtasticRx::decode_header_ exactly (symbol_to_bits -> deinterleave
-> hamming_decode -> header_crc5) and sweeps global rotations, sub-bin phase,
and small per-symbol perturbations to see if ANY transform yields a sane header.
"""
import itertools

SF = 9
N = 1 << SF                  # 512
SF_APP = SF - 2              # 7
CR_APP = 8
NB_EFF = 1 << (SF - 2)       # 128

# Confirmed-identical frames 3 & 4 (same packet, two receptions):
HSYM = [226, 51, 281, 458, 491, 139, 225, 213]


def to_gray(v):
    return v ^ (v >> 1)


def symbol_to_bits(raw):
    spread = 4
    reduced = ((raw + spread // 2 - 1) // spread) % NB_EFF
    return to_gray(reduced)


def deinterleave(sym_bits):
    cws = [0] * SF_APP
    for k in range(CR_APP):
        for m in range(SF_APP):
            i = (m + k) % SF_APP
            bit = (sym_bits[k] >> m) & 1
            cws[i] |= bit << k
    return cws


def hamming_decode4(cw):
    b = [(cw >> i) & 1 for i in range(8)]
    p0 = b[0] ^ b[1] ^ b[2] ^ b[4]
    p1 = b[1] ^ b[2] ^ b[3] ^ b[5]
    p2 = b[0] ^ b[1] ^ b[3] ^ b[6]
    p3 = b[0] ^ b[2] ^ b[3] ^ b[7]
    parity = (p0 << 0) | (p1 << 1) | (p2 << 2) | (p3 << 3)
    flip = {0xD: 1, 0x7: 2, 0xB: 4, 0xE: 8}.get(parity & 0xF, 0)
    return (cw ^ flip) & 0xF


def header_crc5(length, fec_info):
    a0 = (length >> 4) & 1; a1 = (length >> 5) & 1
    a2 = (length >> 6) & 1; a3 = (length >> 7) & 1
    b0 = (length >> 0) & 1; b1 = (length >> 1) & 1
    b2 = (length >> 2) & 1; b3 = (length >> 3) & 1
    c0 = (fec_info >> 0) & 1; c1 = (fec_info >> 1) & 1
    c2 = (fec_info >> 2) & 1; c3 = (fec_info >> 3) & 1
    return (((a0 ^ a1 ^ a2 ^ a3) << 4)
            | ((a3 ^ b1 ^ b2 ^ b3 ^ c0) << 3)
            | ((a2 ^ b0 ^ b3 ^ c1 ^ c3) << 2)
            | ((a1 ^ b0 ^ b2 ^ c0 ^ c1 ^ c2) << 1)
            | ((a0 ^ b1 ^ c0 ^ c1 ^ c2 ^ c3) << 0))


def decode(raw8):
    sym_bits = [symbol_to_bits(r % N) for r in raw8]
    cws = deinterleave(sym_bits)
    nibs = [hamming_decode4(c) for c in cws]
    length = ((nibs[0] & 0xF) << 4) | (nibs[1] & 0xF)
    fec = nibs[2] & 0xF
    chk = ((nibs[3] & 0xF) << 4) | (nibs[4] & 0xF)
    has_crc = (fec & 1) != 0
    cr = (fec >> 1) & 0x7
    ok = (chk == header_crc5(length, fec))
    sane = ok and has_crc and 1 <= cr <= 4 and 16 <= length <= 255
    return sane, length, cr, has_crc, ok, nibs


def show(label, raw8):
    sane, length, cr, has_crc, ok, nibs = decode(raw8)
    tag = "SANE" if sane else ("crc_ok" if ok else "----")
    print(f"{label:34s} {tag:6s} len={length:3d} cr=4/{4+cr} crc={'on' if has_crc else 'off':3s} "
          f"nibs={''.join(f'{n:X}' for n in nibs)}")
    return sane


print(f"# header symbols: {HSYM}\n")

# 1) Global integer rotation on the reduced grid (delta), full sweep.
print("== global delta sweep (raw += 4*d, i.e. one reduced bin) ==")
hits = 0
for d in range(-NB_EFF, NB_EFF):
    if decode([(s + 4 * d) % N for s in HSYM])[0]:
        show(f"delta(reduced)={d}", [(s + 4 * d) % N for s in HSYM]); hits += 1
print(f"  hits={hits}\n")

# 2) Sub-bin phase: add p in 0..3 raw bins before reduction (with delta).
print("== sub-bin phase p=0..3 x delta sweep ==")
hits = 0
for p in range(4):
    for d in range(-NB_EFF, NB_EFF):
        cand = [(s + p + 4 * d) % N for s in HSYM]
        if decode(cand)[0]:
            show(f"phase={p} delta={d}", cand); hits += 1
print(f"  hits={hits}\n")

# 3) Per-symbol +/-1 raw brute force (3^8 = 6561) at best global delta.
print("== per-symbol perturb in {-1,0,1} raw, x global delta (-4..4) ==", flush=True)
hits = 0
for d in range(-4, 5):
    base = [(s + 4 * d) % N for s in HSYM]
    for combo in itertools.product((-1, 0, 1), repeat=8):
        cand = [(base[i] + combo[i]) % N for i in range(8)]
        if decode(cand)[0]:
            show(f"d={d} perturb={combo}", cand); hits += 1
            if hits > 20:
                break
    if hits > 20:
        break
print(f"  hits={hits}", flush=True)

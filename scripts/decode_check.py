#!/usr/bin/env python3
"""Replay captured payload symbols through SDRangel's exact algorithm.

Usage: python decode_check.py
Edit the SYMBOLS list to match the diagnostic line from the modem log.
"""

# Captured frame:
#   preamble: SF9 BW250k cfo=-32.2k peak=24.6dB
#   header[OK] len=29 cr=4/5 crc=on
#   payload[BAD] len=29 crc=CAC0/1FCF FFDEFBFFD95FE5476C7F172AE21F00F3
#   sym=...
PAYLOAD_SYMBOLS = [
    103, 142, 122, 48, 34, 4, 284, 392, 115, 124, 434, 436, 298, 256, 445,
    226, 200, 256, 445, 328, 2, 405, 507, 509, 331, 276, 472, 415, 228, 131,
    403, 91, 230, 460, 360
]
LEAK_NIBBLES = [0x0, 0x0]  # bytes[0]=0x00 in pre-dewhiten dump => leak nibbles = lo=0, hi=0
SF = 9
PPM = 9            # LDRO=off
NB_PARITY = 1      # CR=4/5
CW_LEN = 4 + NB_PARITY
PACKET_LEN = 29

WHITENING = [
    0xFF, 0xFE, 0xFC, 0xF8, 0xF0, 0xE1, 0xC2, 0x85, 0x0B, 0x17, 0x2F, 0x5E, 0xBC, 0x78, 0xF1, 0xE3,
    0xC6, 0x8D, 0x1A, 0x34, 0x68, 0xD0, 0xA0, 0x40, 0x80, 0x01, 0x02, 0x04, 0x08, 0x11, 0x23, 0x47,
    0x8E, 0x1C, 0x38, 0x71, 0xE2, 0xC4, 0x89, 0x12, 0x25, 0x4B,
]

def gray(v):
    return v ^ (v >> 1)

def diagonal_deinterleave_sx(symbols, nb_sym_bits, nb_parity):
    """Exact port of SDRangel's diagonalDeinterleaveSx."""
    cw_len = 4 + nb_parity
    nblocks = len(symbols) // cw_len
    cws = [0] * (nblocks * nb_sym_bits)
    for x in range(nblocks):
        cw_off = x * nb_sym_bits
        sym_off = x * cw_len
        for i in range(cw_len):
            sym = symbols[sym_off + i]
            for j in range(nb_sym_bits):
                bit = (sym >> (nb_sym_bits - 1 - j)) & 1
                row = ((i - j - 1) % nb_sym_bits + nb_sym_bits) % nb_sym_bits
                cws[cw_off + row] |= bit << (cw_len - 1 - i)
    return cws

def check_parity54(b):
    """SDRangel decodeCodewordHard for crApp=1 -> nibble extraction."""
    cw_len = 5
    cw = [(b >> (cw_len - 1 - i)) & 1 for i in range(cw_len)]
    nb = [cw[3], cw[2], cw[1], cw[0]]
    return (nb[0] << 3) | (nb[1] << 2) | (nb[2] << 1) | nb[3]

def crc16gr(data):
    crc = 0
    for B in data:
        b = B
        for _ in range(8):
            if (((crc & 0x8000) >> 8) ^ (b & 0x80)) != 0:
                crc = ((crc << 1) ^ 0x1021) & 0xFFFF
            else:
                crc = (crc << 1) & 0xFFFF
            b = (b << 1) & 0xFF
    return crc

# ---- Process payload only ---------------------------------------------------
N = 1 << SF

def run(label, transform):
    syms_x = [transform(s) for s in PAYLOAD_SYMBOLS]
    gray_syms = [gray(s) for s in syms_x]
    cws = diagonal_deinterleave_sx(gray_syms, PPM, NB_PARITY)
    nibbles = [check_parity54(c) for c in cws]
    all_n = LEAK_NIBBLES + nibbles
    raw = []
    for i in range(0, len(all_n) - 1, 2):
        raw.append(((all_n[i+1] & 0xF) << 4) | (all_n[i] & 0xF))
    deciphered = list(raw)
    for i in range(min(PACKET_LEN, len(deciphered))):
        deciphered[i] ^= WHITENING[i % len(WHITENING)]
    crc_ok = "?"
    if PACKET_LEN >= 2 and len(deciphered) >= PACKET_LEN + 2:
        crc = crc16gr(deciphered[:PACKET_LEN - 2])
        crc ^= deciphered[PACKET_LEN - 1]
        crc ^= deciphered[PACKET_LEN - 2] << 8
        rx_crc = deciphered[PACKET_LEN] | (deciphered[PACKET_LEN + 1] << 8)
        crc_ok = "OK" if (crc & 0xFFFF) == rx_crc else f"BAD({crc&0xFFFF:04X}/{rx_crc:04X})"
    print(f"{label:30s} dewhitened={''.join(f'{b:02X}' for b in deciphered[:PACKET_LEN+2])} crc={crc_ok}")

# Try a battery of likely transforms.
run("identity",            lambda s: s)
run("(s-1) mod N",         lambda s: (s - 1) % N)
run("(s+1) mod N",         lambda s: (s + 1) % N)
run("(s-2) mod N",         lambda s: (s - 2) % N)
run("(s+2) mod N",         lambda s: (s + 2) % N)
# LDRO-style /4 alone (header treatment) just for completeness
run("((s+1)/4) mod N/4",   lambda s: ((s + 1) // 4) % (N // 4))
run("(s-1)/4",             lambda s: ((s - 1) % N) // 4)

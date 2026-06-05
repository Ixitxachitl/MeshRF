#!/usr/bin/env python3
"""Diff OUR LoRa TX encoder against SDRangel's modmeshtastic encoder.

Both encoders are replicated faithfully from source:
  * OURS:      native/core/src/modem/LoraEncoder.cpp + LoraDecoder.cpp
  * SDRangel:  plugins/channeltx/modmeshtastic/meshtasticmodencoderlora.{h,cpp}
               + meshtasticmodsource.cpp (encodeSymbol)

We encode the SAME on-air frame both ways and print the symbol sequences
side by side, flagging the first divergence. SDRangel TX is the proven-good
reference (decodes on OpenWebRX+), so any difference is a bug in OUR encoder.

Frame under test is the known-good frame OpenWebRX+ decoded from SDRangel:
  Dest:ffffffff SRC:11111111 MID:00001234 ... len:25 crc:ok
"""

# The 25-byte on-air frame (16-byte L1 header + 9-byte ciphertext).
FRAME = bytes([
    0xff, 0xff, 0xff, 0xff,   # to
    0x11, 0x11, 0x11, 0x11,   # from
    0x34, 0x12, 0x00, 0x00,   # id (0x00001234 LE)
    0x66,                     # flags (hop_limit=6, hop_start=3)
    0x08,                     # channel hash
    0x00,                     # next_hop
    0xf0,                     # relay_node
    0xa9, 0x27, 0xe2, 0xce, 0x03, 0x0d, 0x7e, 0xbe, 0xc3,  # ciphertext
])

SF = 9
N = 1 << SF          # 512
CR = 1               # MediumFast FEC 4/5  -> nbParityBits=1, cr_index=1
HAS_CRC = True


# ===========================================================================
# Shared helpers
# ===========================================================================
def to_gray(v):
    return v ^ (v >> 1)


def from_gray(g):
    g ^= g >> 8
    g ^= g >> 4
    g ^= g >> 2
    g ^= g >> 1
    return g


# ===========================================================================
# SDRangel reference (meshtasticmodencoderlora.h / .cpp + source)
# ===========================================================================
def sdr_encodeHamming84sx(x):
    d0 = (x >> 0) & 1
    d1 = (x >> 1) & 1
    d2 = (x >> 2) & 1
    d3 = (x >> 3) & 1
    b = x & 0xf
    b |= (d0 ^ d1 ^ d2) << 4
    b |= (d1 ^ d2 ^ d3) << 5
    b |= (d0 ^ d1 ^ d3) << 6
    b |= (d0 ^ d2 ^ d3) << 7
    return b


def sdr_encodeParity54(b):
    x = b ^ (b >> 2)
    x = x ^ (x >> 1)
    return (b & 0xf) | ((x << 4) & 0x10)


def sdr_crc16sx(crc, poly):
    for _ in range(8):
        if crc & 0x8000:
            crc = ((crc << 1) ^ poly) & 0xFFFF
        else:
            crc = (crc << 1) & 0xFFFF
    return crc


def sdr_xsum8(t):
    t ^= t >> 4
    t ^= t >> 2
    t ^= t >> 1
    return t & 1


def sdr_dataChecksum(data):
    res = 0
    v = 0xff
    for byte in data:
        crc = sdr_crc16sx(res, 0x1021)
        v = (sdr_xsum8(v & 0xB8) | (v << 1)) & 0xFF
        res = crc ^ byte
    res ^= v
    v = (sdr_xsum8(v & 0xB8) | (v << 1)) & 0xFF
    res ^= (v << 8)
    return res & 0xFFFF


def sdr_headerChecksum(h):
    a0 = (h[0] >> 4) & 1
    a1 = (h[0] >> 5) & 1
    a2 = (h[0] >> 6) & 1
    a3 = (h[0] >> 7) & 1
    b0 = (h[0] >> 0) & 1
    b1 = (h[0] >> 1) & 1
    b2 = (h[0] >> 2) & 1
    b3 = (h[0] >> 3) & 1
    c0 = (h[1] >> 0) & 1
    c1 = (h[1] >> 1) & 1
    c2 = (h[1] >> 2) & 1
    c3 = (h[1] >> 3) & 1
    res = (a0 ^ a1 ^ a2 ^ a3) << 4
    res |= (a3 ^ b1 ^ b2 ^ b3 ^ c0) << 3
    res |= (a2 ^ b0 ^ b3 ^ c1 ^ c3) << 2
    res |= (a1 ^ b0 ^ b2 ^ c0 ^ c1 ^ c2) << 1
    res |= a0 ^ b1 ^ c0 ^ c1 ^ c2 ^ c3
    return res


_WHITEN_SEQ = [
    0x0102291EA751AAFF, 0xD24B050A8D643A17, 0x5B279B671120B8F4, 0x032B37B9F6FB55A2,
    0x994E0F87E95E2D16, 0x7CBCFC7631984C26, 0x281C8E4F0DAEF7F9, 0x1741886EB7733B15,
]
_WHITEN_LEN = 510
_OFS0 = [6, 4, 2, 0, -112, -114, -302, -34]
_OFS1 = [6, 4, 2, 0, -360]


def sdr_computeWhitening(buffer, bit_ofs, nb_parity):
    ofs = _OFS1 if nb_parity == 1 else _OFS0
    for j in range(len(buffer)):
        x = 0
        for i in range(4 + nb_parity):
            t = (ofs[i] + j + bit_ofs + _WHITEN_LEN) % _WHITEN_LEN
            if _WHITEN_SEQ[t >> 6] & (1 << (t & 0x3F)):
                x |= 1 << i
        buffer[j] ^= x
    return buffer


def sdr_diagonalInterleave(codewords, nb_symbol_bits, nb_parity):
    cw_len = 4 + nb_parity
    nblocks = len(codewords) // nb_symbol_bits
    symbols = [0] * (nblocks * cw_len)
    for xblk in range(nblocks):
        cw_off = xblk * nb_symbol_bits
        sym_off = xblk * cw_len
        for k in range(cw_len):
            for m in range(nb_symbol_bits):
                i = (m + k + nb_symbol_bits) % nb_symbol_bits
                bit = (codewords[cw_off + i] >> k) & 1
                symbols[sym_off + k] |= bit << m
    return symbols


def sdr_encodeFec(nb_parity, dofs_start, data, count):
    """Returns (codewords, next_dofs)."""
    cws = []
    dofs = dofs_start
    for _ in range(count):
        byte_idx = dofs // 2
        byte_val = data[byte_idx] if byte_idx < len(data) else 0
        nib = (byte_val >> 4) if (dofs % 2 == 1) else (byte_val & 0xf)
        if nb_parity == 1:
            cws.append(sdr_encodeParity54(nib))
        elif nb_parity == 4:
            cws.append(sdr_encodeHamming84sx(nib))
        else:
            cws.append(nib)
        dofs += 1
    return cws, dofs


def sdr_encode(frame, sf, cr, has_crc):
    """Faithful port of MeshtasticModEncoderLoRa::encodeBytes + encodeSymbol."""
    nb_parity = cr
    payload_nb_symbol_bits = sf            # no LDRO at SF9/BW250
    header_nb_symbol_bits = sf - 2
    header_codewords = 5
    header_symbols = 8

    # addChecksum: CRC over the data, appended LE.
    crc = sdr_dataChecksum(frame)
    bytes_ = list(frame) + [crc & 0xff, (crc >> 8) & 0xff]

    payload_nibble_count = len(bytes_) * 2
    first_block_codewords = header_nb_symbol_bits
    header_size = header_codewords
    payload_in_first = min(payload_nibble_count, first_block_codewords - header_size)
    remaining_nibbles = payload_nibble_count - payload_in_first
    remaining_codewords = ((remaining_nibbles + payload_nb_symbol_bits - 1)
                           // payload_nb_symbol_bits) * payload_nb_symbol_bits \
        if remaining_nibbles > 0 else 0

    codewords = []
    dofs = 0

    # Header nibbles (NOT whitened).
    payload_size = len(bytes_) - (2 if has_crc else 0)
    hdr = [payload_size & 0xff,
           (1 if has_crc else 0) | (nb_parity << 1),
           0]
    hdr[2] = sdr_headerChecksum(hdr)
    codewords.append(sdr_encodeHamming84sx(hdr[0] >> 4))
    codewords.append(sdr_encodeHamming84sx(hdr[0] & 0xf))
    codewords.append(sdr_encodeHamming84sx(hdr[1] & 0xf))
    codewords.append(sdr_encodeHamming84sx(hdr[2] >> 4))
    codewords.append(sdr_encodeHamming84sx(hdr[2] & 0xf))

    # First block leak payload (4/8 FEC), then sx1272-whiten those codewords.
    leak_count = first_block_codewords - header_size
    if leak_count > 0:
        leak_cws, dofs = sdr_encodeFec(4, dofs, bytes_, leak_count)
        sdr_computeWhitening(leak_cws, 0, 4)
        codewords.extend(leak_cws)

    # Remaining payload blocks (payload FEC), sx1272-whiten with bitOfs=leak_count.
    if remaining_codewords > 0:
        pay_cws, dofs = sdr_encodeFec(nb_parity, dofs, bytes_, remaining_codewords)
        sdr_computeWhitening(pay_cws, leak_count, nb_parity)
        codewords.extend(pay_cws)

    # Interleave: header block then payload blocks.
    symbols = sdr_diagonalInterleave(codewords[:first_block_codewords],
                                     header_nb_symbol_bits, 4)
    if remaining_codewords > 0:
        symbols += sdr_diagonalInterleave(codewords[first_block_codewords:],
                                          payload_nb_symbol_bits, nb_parity)

    # Gray decode.
    symbols = [from_gray(s) for s in symbols]

    # encodeSymbol: rawSymbol = (deWidth*baseSymbol + 1) % N, header forced DE>=2.
    out = []
    for idx, sym in enumerate(symbols):
        header_symbol = idx < header_symbols
        de_bits = 0
        if header_symbol and de_bits < 2:
            de_bits = 2
        de_width = 1 << de_bits
        symbol_range = max(1, N // de_width)
        base = sym % symbol_range
        out.append((de_width * base + 1) % N)
    return out, crc, hdr


# ===========================================================================
# OUR encoder (native/core/src/modem/LoraEncoder.cpp + LoraDecoder.cpp)
# ===========================================================================
def our_hamming_encode(nib, cr):
    # PATCHED to SDRangel layout for verification.
    if cr == 1:
        return sdr_encodeParity54(nib)
    if cr == 4:
        return sdr_encodeHamming84sx(nib)
    d0 = nib & 1
    d1 = (nib >> 1) & 1
    d2 = (nib >> 2) & 1
    d3 = (nib >> 3) & 1
    p1 = d3 ^ d2 ^ d1
    p2 = d3 ^ d2 ^ d0
    p3 = d3 ^ d1 ^ d0
    p4 = d2 ^ d1 ^ d0
    cw = (p1 << 7) | (p2 << 6) | (p3 << 5) | (p4 << 4) | (d3 << 3) | (d2 << 2) | (d1 << 1) | d0
    if cr == 2:
        cw &= 0x3F
    elif cr == 3:
        cw &= 0x7F
    return cw


def our_interleave(codewords, sf_app, cr_app):
    symbols = [0] * cr_app
    for k in range(cr_app):
        for m in range(sf_app):
            i = (m + k) % sf_app
            bit = (codewords[i] >> k) & 1
            symbols[k] |= bit << m
    return symbols


def our_header_crc5(length, fec_info):
    a0 = (length >> 4) & 1
    a1 = (length >> 5) & 1
    a2 = (length >> 6) & 1
    a3 = (length >> 7) & 1
    b0 = (length >> 0) & 1
    b1 = (length >> 1) & 1
    b2 = (length >> 2) & 1
    b3 = (length >> 3) & 1
    c0 = (fec_info >> 0) & 1
    c1 = (fec_info >> 1) & 1
    c2 = (fec_info >> 2) & 1
    c3 = (fec_info >> 3) & 1
    return (((a0 ^ a1 ^ a2 ^ a3) << 4)
            | ((a3 ^ b1 ^ b2 ^ b3 ^ c0) << 3)
            | ((a2 ^ b0 ^ b3 ^ c1 ^ c3) << 2)
            | ((a1 ^ b0 ^ b2 ^ c0 ^ c1 ^ c2) << 1)
            | ((a0 ^ b1 ^ c0 ^ c1 ^ c2 ^ c3) << 0))


def our_crc16gr(data):
    crc = 0
    for byte in data:
        b = byte
        for _ in range(8):
            top = ((crc & 0x8000) >> 8) ^ (b & 0x80)
            if top:
                crc = ((crc << 1) ^ 0x1021) & 0xFFFF
            else:
                crc = (crc << 1) & 0xFFFF
            b = (b << 1) & 0xFF
    return crc & 0xFFFF


# gr-lora 255-byte whitening table (kWhiteningSeq).
from importlib import util as _util  # noqa: E402
import os as _os  # noqa: E402
import sys as _sys  # noqa: E402
_spec = _util.spec_from_file_location(
    "ref_decode", _os.path.join(_os.path.dirname(__file__), "ref_decode.py"))
_ref = _util.module_from_spec(_spec)
_sys.modules["ref_decode"] = _ref
_spec.loader.exec_module(_ref)
OUR_WHITEN = _ref.WHITEN


def our_header_symbol(bits, sf):
    reduced = from_gray(bits)
    return ((reduced << 2) + 1) % (1 << sf)   # +1 fix applied


def our_payload_symbol(bits, sf, ppm):
    reduced = from_gray(bits)
    diff = sf - ppm
    if diff <= 0:
        return (reduced + 1) % (1 << sf)
    return ((reduced << diff) + 1) % (1 << sf)


def our_encode(frame, sf, cr, has_crc, ldro=False):
    sf_app = sf - 2
    pl = len(frame)

    # CRC over plaintext, appended unwhitened LE.
    stream = list(frame)
    crc = 0
    if has_crc and pl >= 2:
        crc = sdr_dataChecksum(frame)   # PATCHED to SDRangel CRC

    # Build raw byte stream = data + CRC + padding bytes, then table-whiten ALL.
    ppm = sf - (2 if ldro else 0)
    cw_len = cr + 4
    crc_flag = 1 if has_crc else 0
    num = 8 * pl - 4 * sf + 28 + 16 * crc_flag
    den = 4 * (sf - (2 if ldro else 0))
    blocks = (num + den - 1) // den
    if blocks < 0:
        blocks = 0
    leak_count = sf_app - 5
    total_nibbles_needed = leak_count + blocks * ppm
    total_bytes_needed = (total_nibbles_needed + 1) // 2

    raw = list(frame)
    if has_crc:
        raw.append(crc & 0xFF)
        raw.append((crc >> 8) & 0xFF)
    while len(raw) < total_bytes_needed:
        raw.append(0)  # padding bytes
    stream = [raw[i] ^ OUR_WHITEN[i % 255] for i in range(len(raw))]

    # Nibble stream (low nibble first).
    nibbles = []
    for b in stream:
        nibbles.append(b & 0x0F)
        nibbles.append((b >> 4) & 0x0F)

    # Header block.
    fec_info = (cr << 1) | (1 if has_crc else 0)
    chk = our_header_crc5(pl, fec_info)
    header_nibbles = [0] * sf_app
    header_nibbles[0] = (pl >> 4) & 0x0F
    header_nibbles[1] = pl & 0x0F
    header_nibbles[2] = fec_info & 0x0F
    header_nibbles[3] = (chk >> 4) & 0x0F
    header_nibbles[4] = chk & 0x0F
    consumed = 0
    for i in range(leak_count):
        header_nibbles[5 + i] = nibbles[consumed] if consumed < len(nibbles) else 0
        consumed += 1

    symbols = []
    cws = [our_hamming_encode(header_nibbles[i], 4) for i in range(sf_app)]
    sym_bits = our_interleave(cws, sf_app, 8)
    for b in sym_bits:
        symbols.append(our_header_symbol(b, sf))

    # Payload blocks.
    for _ in range(blocks):
        block_cws = []
        for _i in range(ppm):
            nib = nibbles[consumed] if consumed < len(nibbles) else 0
            block_cws.append(our_hamming_encode(nib, cr))
            consumed += 1
        sym_bits = our_interleave(block_cws, ppm, cw_len)
        for b in sym_bits:
            symbols.append(our_payload_symbol(b, sf, ppm))

    return symbols, crc, [pl & 0xff, fec_info, chk]


# ===========================================================================
# Compare
# ===========================================================================
def main():
    sdr_syms, sdr_crc, sdr_hdr = sdr_encode(FRAME, SF, CR, HAS_CRC)
    our_syms, our_crc, our_hdr = our_encode(FRAME, SF, CR, HAS_CRC)

    print(f"frame len      = {len(FRAME)} bytes")
    print(f"SDR  CRC       = 0x{sdr_crc:04X}   hdr(len,fec,chk)={sdr_hdr}")
    print(f"OUR  CRC       = 0x{our_crc:04X}   hdr(len,fec,chk)={our_hdr}")
    print(f"SDR  symbols   = {len(sdr_syms)}")
    print(f"OUR  symbols   = {len(our_syms)}")
    print()

    n = max(len(sdr_syms), len(our_syms))
    first_diff = None
    print(f"{'idx':>3} {'SDR':>5} {'OUR':>5}  {'sect':<7} diff")
    for i in range(n):
        s = sdr_syms[i] if i < len(sdr_syms) else None
        o = our_syms[i] if i < len(our_syms) else None
        sect = "header" if i < 8 else "payload"
        mark = ""
        if s != o:
            mark = "  <-- DIFF"
            if first_diff is None:
                first_diff = i
        ss = f"{s:5d}" if s is not None else "  -- "
        oo = f"{o:5d}" if o is not None else "  -- "
        print(f"{i:3d} {ss} {oo}  {sect:<7}{mark}")

    print()
    if first_diff is None and len(sdr_syms) == len(our_syms):
        print("RESULT: symbol sequences are IDENTICAL. Our TX matches SDRangel.")
    else:
        print(f"RESULT: first divergence at symbol index {first_diff} "
              f"({'header' if (first_diff or 0) < 8 else 'payload'}).")
        print("CRC match:", sdr_crc == our_crc)


if __name__ == "__main__":
    main()

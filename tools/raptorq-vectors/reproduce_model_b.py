#!/usr/bin/env python3
"""One-off independent reproduction of tests/Packet.Mailcast.Tests/Vectors/model-b-v1.json (issue #142).

Uses libzstd (python-zstandard, which should report 1.5.7) and the Rust raptorq crate, not Packet.Mailcast.
It starts from the file's inputs only. canonicalise() below is a separate implementation written from
docs/canonical-form.md and the #142 decisions (not a translation of the C#), and it must reproduce
canonicalBodyHex, envelopeHex and canonicalBulletinHex. From those it checks the compressed object octets
with dictionary 1 and 0, object IDs and long hashes, the OTI (RFC 6330 layout, built here), the type 5
envelope object, the whole frames with their CRC, the directory entry line, and (through
`cargo run -- model-b`) every listed symbol against the Rust raptorq crate.

    python3 tools/raptorq-vectors/reproduce_model_b.py [vectors.json]
"""
import hashlib
import json
import datetime
import math
import os
import re
import struct
import subprocess
import sys
import zlib

import zstandard

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
VECTORS = sys.argv[1] if len(sys.argv) > 1 else os.path.join(ROOT, "tests", "Packet.Mailcast.Tests", "Vectors", "model-b-v1.json")
DICTIONARY = os.path.join(ROOT, "src", "Packet.Mailcast", "Dictionaries", "gb7rdg-1.zdict")

failures = 0
checked = 0


def check(label, ours, theirs):
    global failures, checked
    checked += 1
    if ours != theirs:
        failures += 1
        print("MISMATCH", label)


def compress(data, use_dictionary):
    # The plain libzstd call a C head end would make: level 19, one thread, checksum, content size and
    # dictionary ID on. (Building explicit ZstdCompressionParameters instead picks different tuning for
    # inputs over 128 KB and so does not match; the level form is what ZSTD_compress_usingDict does.)
    flags = dict(level=19, write_checksum=True, write_content_size=True, write_dict_id=True, threads=0)
    if use_dictionary:
        with open(DICTIONARY, "rb") as f:
            zdict = zstandard.ZstdCompressionDict(f.read())
        return zstandard.ZstdCompressor(dict_data=zdict, **flags).compress(data)
    return zstandard.ZstdCompressor(**flags).compress(data)


def object_hash(dictionary, obj):
    h = hashlib.sha256(struct.pack(">H", dictionary) + obj).digest()
    return h[:8].hex(), h[8:16].hex()


def frame(object_id, dictionary, oti, esi, symbol):
    head = bytes([2, 0]) + bytes.fromhex(object_id) + struct.pack(">H", dictionary) + oti + esi.to_bytes(3, "big")
    body = head + symbol
    return body + struct.pack(">I", zlib.crc32(body) & 0xFFFFFFFF)


# ---------------------------------------------------------------------------------------------
# Canonicalisation, written from docs/canonical-form.md and the #142 decisions.

BLANK = b" \t"


def split_lines(data):
    """[(start, text_end, next_start)] for lines ended by CR LF, LF or a lone CR."""
    out, start, i = [], 0, 0
    while i < len(data):
        if data[i] in b"\r\n":
            nxt = i + 2 if data[i:i + 2] == b"\r\n" else i + 1
            out.append((start, i, nxt))
            start = i = nxt
        else:
            i += 1
    if start < len(data):
        out.append((start, len(data), len(data)))
    return out


def blank(b):
    return len(b.strip(BLANK)) == 0


R_LINE = re.compile(rb"R:[0-9]{6}/[0-9]{4}[Zz]? ")
ORIGIN = [  # each block: list of patterns, then whether the block needs a blank line before it
    ([rb"From: \S+@\S+", rb"To  : \S+@\S+", rb""], True),
    ([rb"Date: [A-Z][a-z]{2}, [0-9]{1,2} [A-Z][a-z]{2} [0-9]{4} [0-9]{2}:[0-9]{2}:[0-9]{2} [+-][0-9]{4}", rb"From: \S+ <\S+>", rb"To: \S+",
      rb"Subject:( .*)?", rb"Message-ID: \S+", rb""], False),
]
BCM_FROM = re.compile(rb"From: \S+ @ \S+( \([^()]*\))?")
BCM_TO = re.compile(rb"To:   \S+ @ \S+ ?")


def origin_block_end(texts, i, blank_before):
    """Index after an origin block starting at line i, or None."""
    def ok(k, pat):
        return k < len(texts) and re.fullmatch(pat, texts[k]) is not None
    for pats, needs_blank in ORIGIN:
        if needs_blank and not blank_before:
            continue
        if all(ok(i + n, pat) for n, pat in enumerate(pats)):
            return i + len(pats)
    if ok(i, BCM_FROM) and ok(i + 1, BCM_TO):
        k = i + 2
        if ok(k, rb"X-Info: .*"):
            k += 1
        if ok(k, rb""):
            return k + 1
    return None


def first_content_line(texts):
    i, routed = 0, False
    while True:
        blank_before = False
        while i < len(texts) and blank(texts[i]):
            i += 1
            blank_before = True
        if i >= len(texts):
            return i
        t = texts[i]
        if R_LINE.match(t):
            while i < len(texts) and R_LINE.match(texts[i]):
                i += 1
            routed = True
        elif t.startswith(b"Path: "):
            i += 1
            while i < len(texts) and texts[i][:1] in (b" ", b"\t"):
                i += 1
            routed = True
        else:
            end = origin_block_end(texts, i, blank_before) if routed else None
            if end is None:
                return i
            i = end
            while i < len(texts) and blank(texts[i]):
                i += 1
            return i


def leb128(n):
    out = bytearray()
    while n >= 0x80:
        out.append((n & 0x7F) | 0x80)
        n >>= 7
    out.append(n)
    return bytes(out)


def canonicalise(original, title):
    """Returns (canonical body, envelope octets, line ending style 0 LF, 1 CR LF, 2 CR, 3 Raw, top)."""
    lines = split_lines(original)
    texts = [original[a:b] for a, b, _ in lines]
    first = first_content_line(texts)
    last = len(lines) - 1
    while last >= first and blank(texts[last]):
        last -= 1
    if last < first:
        style, body, top, bottom = 0, b"", original, b""
    else:
        kinds = set()
        for k in range(first, last):
            a, b, n = lines[k]
            seg = original[b:n]
            kinds.add({b"\r\n": 1, b"\n": 0, b"\r": 2}[seg])
        style = 3 if len(kinds) > 1 else (kinds.pop() if kinds else 0)
        top = original[:lines[first][0]]
        bottom = original[lines[last][1]:]
        if style == 3:
            body = original[lines[first][0]:lines[last][1]] + b"\n"
        else:
            body = b"".join(t + b"\n" for t in texts[first:last + 1])
    envelope = bytes([1, style]) + b"".join(leb128(len(x)) + x for x in (top, bottom, title))
    return body, envelope, style, top


def shared_title(title):
    t = title[:59].replace(b"\r", b" ").replace(b"\n", b" ")
    return t.rstrip(BLANK)


def oldest_route_date(top):
    """Date of the bottom-most R: line in the top, else the epoch."""
    found = None
    for line in re.split(rb"[\r\n]", top):
        m = re.match(rb"R:([0-9]{2})([0-9]{2})([0-9]{2})/([0-9]{2})([0-9]{2})[Zz]? ", line)
        if m:
            found = m
    if found is None:
        return "1970-01-01T00:00:00Z"
    y, mo, d, h, mi = (int(g) for g in found.groups())
    try:
        return datetime.datetime(2000 + y, mo, d, h, mi).strftime("%Y-%m-%dT%H:%M:%SZ")
    except ValueError:
        return "1970-01-01T00:00:00Z"


def canonical_bulletin(inp, body, style, top):
    title = shared_title(bytes.fromhex(inp["titleHex"]))
    text = body if style == 3 else body.replace(b"\n", b"\r\n")
    head = "Type: %s\nFrom: %s\nTo: %s\nAt: %s\nBid: %s\nDate: %s\nTitle: " % (
        inp["type"], inp["from"], inp["to"], inp["at"], inp["bid"], oldest_route_date(top))
    return head.encode("latin-1") + title + b"\nCanonical: 1\n\n" + text


def oti(length, piece=240):
    """RFC 6330 common OTI: 40 bit transfer length, reserved, 16 bit symbol size, Z, N, Al."""
    return length.to_bytes(5, "big") + bytes([0]) + struct.pack(">H", piece) + bytes([1]) + struct.pack(">H", 1) + bytes([4])


def main():
    doc = json.load(open(VECTORS))
    print("libzstd", zstandard.ZSTD_VERSION, "python-zstandard", zstandard.__version__)
    check("zstd version", zstandard.ZSTD_VERSION, (1, 5, 7))
    for case in doc["cases"]:
        name = case["name"]
        e = case["expected"]
        inp = case["input"]
        body, envelope, style, top = canonicalise(bytes.fromhex(inp["messageTextHex"]), bytes.fromhex(inp["titleHex"]))
        bulletin = canonical_bulletin(inp, body, style, top)
        check(f"{name} canonicalBodyHex", body.hex(), e["canonicalBodyHex"])
        check(f"{name} envelopeHex", envelope.hex(), e["envelopeHex"])
        check(f"{name} canonicalBulletinHex", bulletin.hex(), e["canonicalBulletinHex"])
        header = dict(l.split(": ", 1) for l in bulletin.split(b"\n\n", 1)[0].decode("latin-1").split("\n"))

        objects = {
            "bulletinDictionary1": (1, 1, bulletin),
            "bulletinDictionary0": (1, 0, bulletin),
            "envelopeObjectDictionary1": (5, 1, envelope),
        }
        built = {}
        for key, (kind, dictionary, content) in objects.items():
            obj = bytes([kind]) + compress(content, dictionary == 1)
            built[key] = (dictionary, obj)
            expected = e[key]
            check(f"{name} {key} object octets", obj.hex(), expected["objectHex"])
            oid, long_hash = object_hash(dictionary, obj)
            check(f"{name} {key} id", oid, expected["id"])
            check(f"{name} {key} long hash", long_hash, expected["longHash"])
            check(f"{name} {key} transfer length", len(obj), expected["transferLength"])
            check(f"{name} {key} OTI", oti(len(obj)).hex(), expected["otiHex"])
            check(f"{name} {key} piece size", struct.unpack(">H", bytes.fromhex(expected["otiHex"])[6:8])[0], 240)
            check(f"{name} {key} source symbols", expected["sourceSymbols"], math.ceil(len(obj) / 240))

            # Whole frames, rebuilt from the header fields, the listed symbol and the CRC.
            symbols = {int(s.split(":")[0]): bytes.fromhex(s.split(":")[1]) for s in expected.get("symbols", [])}
            for f in expected.get("frames", []):
                esi_text, hexed = f.split(":")
                esi = int(esi_text)
                # ESI 0 is a listed symbol; the first repair symbol K is listed as well.
                check(f"{name} {key} frame {esi}", frame(oid, dictionary, bytes.fromhex(expected["otiHex"]), esi, symbols[esi]).hex(), hexed)

        # The directory line: the bulletin's entry with its hash and the envelope link.
        b_id, b_hash = object_hash(*built["bulletinDictionary1"])
        e_id, e_hash = object_hash(*built["envelopeObjectDictionary1"])
        line = "\t".join([b_id, "1", str(len(bulletin)), header["Bid"], header["Title"], "type=1", "hash=" + b_hash, f"env={e_id};{e_hash}"])
        # The file stores the line as JSON text decoded from Latin-1 characters.
        check(f"{name} directory line", line, e["directoryEntryLine"])

    cargo = subprocess.run(
        ["cargo", "run", "--release", "--locked", "--quiet", "--manifest-path", os.path.join(HERE, "Cargo.toml"), "--", "model-b", VECTORS],
        capture_output=True, text=True)
    print(cargo.stdout.strip())
    if cargo.returncode != 0:
        print(cargo.stderr.strip())
    check("raptorq crate symbols", cargo.returncode, 0)

    print(f"{checked} checks, {failures} mismatches")
    sys.exit(1 if failures else 0)


main()

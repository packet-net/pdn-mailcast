#!/usr/bin/env python3
"""One-off independent reproduction of tests/Packet.Mailcast.Tests/Vectors/model-b-v1.json (issue #142).

Uses libzstd (python-zstandard, which should report 1.5.7) and the Rust raptorq crate, not Packet.Mailcast.
It starts from the file's inputs and canonical octets (the canonical rules are not reimplemented) and checks
the compressed object octets with dictionary 1 and 0, object IDs and long hashes, the type 5 envelope object,
the whole frames with their CRC, the directory entry line, and (through `cargo run -- model-b`) every symbol.

    python3 tools/raptorq-vectors/reproduce_model_b.py [vectors.json]
"""
import hashlib
import json
import os
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
    params = zstandard.ZstdCompressionParameters.from_level(
        19, write_checksum=True, write_content_size=True, write_dict_id=True, threads=0)
    if use_dictionary:
        with open(DICTIONARY, "rb") as f:
            zdict = zstandard.ZstdCompressionDict(f.read())
        c = zstandard.ZstdCompressor(compression_params=params, dict_data=zdict)
    else:
        c = zstandard.ZstdCompressor(compression_params=params)
    return c.compress(data)


def object_hash(dictionary, obj):
    h = hashlib.sha256(struct.pack(">H", dictionary) + obj).digest()
    return h[:8].hex(), h[8:16].hex()


def frame(object_id, dictionary, oti, esi, symbol):
    head = bytes([2, 0]) + bytes.fromhex(object_id) + struct.pack(">H", dictionary) + oti + esi.to_bytes(3, "big")
    body = head + symbol
    return body + struct.pack(">I", zlib.crc32(body) & 0xFFFFFFFF)


def main():
    doc = json.load(open(VECTORS))
    print("libzstd", zstandard.ZSTD_VERSION, "python-zstandard", zstandard.__version__)
    check("zstd version", zstandard.ZSTD_VERSION, (1, 5, 7))
    for case in doc["cases"]:
        name = case["name"]
        e = case["expected"]
        bulletin = bytes.fromhex(e["canonicalBulletinHex"])
        envelope = bytes.fromhex(e["envelopeHex"])
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

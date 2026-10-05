//! Interop vectors for Mailcast.RaptorQ, made with the independent `raptorq` crate.
//!
//! `cargo run --release -- generate <out.json>` writes the vectors that the C# tests check:
//! for each case, the encoding symbols the crate produces for a list of ESIs, a set of symbols
//! the crate's decoder rebuilt the object from, and (where one turns up) a set of exactly K
//! symbols the crate's decoder could not use, which our decoder must also reject. A symbol is
//! written as "sbn:esi:hex"; the undecodable set is a comma-separated list of ESIs in source block 0.
//!
//! `cargo run --release -- check <ours.json>` reads symbols made by Mailcast.RaptorQ
//! (tools/RaptorQ.InteropExport) and checks that the crate makes the same bytes for the same
//! ESIs and that the crate's decoder rebuilds each object from our symbols.
//!
//! Object contents come from a xorshift32 generator so they need not be stored: start with
//! x = seed, and for each byte do x ^= x << 13; x ^= x >> 17; x ^= x << 5; byte = x & 0xff.

use raptorq::{
    Decoder, EncodingPacket, Encoder, ObjectTransmissionInformation, PayloadId,
    SourceBlockDecoder, SourceBlockEncoder,
};
use serde_json::{json, Value};
use std::collections::BTreeSet;

struct Case {
    name: &'static str,
    transfer_length: u64,
    symbol_size: u16,
    source_blocks: u8,
    sub_blocks: u16,
    alignment: u8,
    seed: u32,
    /// Extra symbols over K in the decode set; 0 means exactly K.
    overhead: u32,
    /// Whether to include every source ESI and the first repair ESIs in the byte-match list.
    all_source: bool,
}

const CASES: &[Case] = &[
    Case { name: "k1-short", transfer_length: 7, symbol_size: 8, source_blocks: 1, sub_blocks: 1, alignment: 1, seed: 1, overhead: 0, all_source: true },
    Case { name: "k1-full", transfer_length: 64, symbol_size: 64, source_blocks: 1, sub_blocks: 1, alignment: 4, seed: 2, overhead: 0, all_source: true },
    Case { name: "k10-exact", transfer_length: 160, symbol_size: 16, source_blocks: 1, sub_blocks: 1, alignment: 4, seed: 3, overhead: 0, all_source: true },
    Case { name: "k25-padded", transfer_length: 990, symbol_size: 40, source_blocks: 1, sub_blocks: 1, alignment: 4, seed: 4, overhead: 0, all_source: true },
    Case { name: "k22-t940", transfer_length: 20000, symbol_size: 940, source_blocks: 1, sub_blocks: 1, alignment: 4, seed: 5, overhead: 1, all_source: false },
    Case { name: "k101", transfer_length: 1207, symbol_size: 12, source_blocks: 1, sub_blocks: 1, alignment: 4, seed: 6, overhead: 0, all_source: false },
    Case { name: "k300", transfer_length: 2400, symbol_size: 8, source_blocks: 1, sub_blocks: 1, alignment: 4, seed: 7, overhead: 2, all_source: false },
    Case { name: "k1000", transfer_length: 3999, symbol_size: 4, source_blocks: 1, sub_blocks: 1, alignment: 4, seed: 8, overhead: 0, all_source: false },
    Case { name: "k2500", transfer_length: 5000, symbol_size: 2, source_blocks: 1, sub_blocks: 1, alignment: 1, seed: 9, overhead: 1, all_source: false },
    Case { name: "z2-n2", transfer_length: 10000, symbol_size: 64, source_blocks: 2, sub_blocks: 2, alignment: 8, seed: 10, overhead: 1, all_source: false },
    Case { name: "z3-n5-uneven", transfer_length: 5003, symbol_size: 48, source_blocks: 3, sub_blocks: 5, alignment: 4, seed: 11, overhead: 0, all_source: false },
];

fn object_bytes(seed: u32, len: u64) -> Vec<u8> {
    let mut x = seed;
    (0..len)
        .map(|_| {
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            (x & 0xff) as u8
        })
        .collect()
}

/// Selection randomness for the tool itself (not part of the vectors' contract).
struct Rng(u64);
impl Rng {
    fn next(&mut self) -> u64 {
        // splitmix64
        self.0 = self.0.wrapping_add(0x9E3779B97F4A7C15);
        let mut z = self.0;
        z = (z ^ (z >> 30)).wrapping_mul(0xBF58476D1CE4E5B9);
        z = (z ^ (z >> 27)).wrapping_mul(0x94D049BB133111EB);
        z ^ (z >> 31)
    }
    fn below(&mut self, n: u64) -> u64 {
        self.next() % n
    }
}

fn hex(data: &[u8]) -> String {
    data.iter().map(|b| format!("{:02x}", b)).collect()
}

fn unhex(s: &str) -> Vec<u8> {
    (0..s.len())
        .step_by(2)
        .map(|i| u8::from_str_radix(&s[i..i + 2], 16).unwrap())
        .collect()
}

/// A symbol is written as "sbn:esi:hex".
fn packet_json(p: &EncodingPacket) -> Value {
    Value::String(format!(
        "{}:{}:{}",
        p.payload_id().source_block_number(),
        p.payload_id().encoding_symbol_id(),
        hex(p.data())
    ))
}

fn packet_from_json(v: &Value) -> EncodingPacket {
    let parts: Vec<&str> = v.as_str().unwrap().split(':').collect();
    EncodingPacket::new(
        PayloadId::new(parts[0].parse().unwrap(), parts[1].parse().unwrap()),
        unhex(parts[2]),
    )
}

/// Encoding symbol for any ESI from one block's encoder.
fn symbol_for(block: &SourceBlockEncoder, k: u32, esi: u32) -> EncodingPacket {
    if esi < k {
        block.source_packets()[esi as usize].clone()
    } else {
        block.repair_packets(esi - k, 1).remove(0)
    }
}

fn block_sizes(config: &ObjectTransmissionInformation) -> Vec<u32> {
    let kt = config.transfer_length().div_ceil(config.symbol_size() as u64) as u32;
    let (kl, ks, zl, zs) = raptorq::partition(kt, config.source_blocks());
    let mut sizes = vec![kl; zl as usize];
    sizes.extend(vec![ks; zs as usize]);
    sizes
}

/// A random decode set for one block: some source symbols, topped up with random repair symbols.
fn random_set(rng: &mut Rng, block: &SourceBlockEncoder, k: u32, count: u32) -> Vec<EncodingPacket> {
    let mut esis = BTreeSet::new();
    let keep_source = rng.below(k as u64 + 1) as u32;
    let mut source: Vec<u32> = (0..k).collect();
    for i in (1..source.len()).rev() {
        let j = rng.below(i as u64 + 1) as usize;
        source.swap(i, j);
    }
    for &esi in source.iter().take(keep_source.min(count) as usize) {
        esis.insert(esi);
    }
    while (esis.len() as u32) < count {
        esis.insert(k + rng.below(20000) as u32);
    }
    esis.iter().map(|&esi| symbol_for(block, k, esi)).collect()
}

fn generate(path: &str) {
    let mut cases = vec![];
    for case in CASES {
        let config = ObjectTransmissionInformation::new(
            case.transfer_length,
            case.symbol_size,
            case.source_blocks,
            case.sub_blocks,
            case.alignment,
        );
        let data = object_bytes(case.seed, case.transfer_length);
        let encoder = Encoder::new(&data, config);
        let blocks = encoder.get_block_encoders();
        let sizes = block_sizes(&config);
        assert_eq!(blocks.len(), sizes.len());
        let mut rng = Rng(case.seed as u64 * 1000 + 17);

        // Byte-match list.
        let mut symbols = vec![];
        for (sbn, block) in blocks.iter().enumerate() {
            let k = sizes[sbn];
            let mut esis = BTreeSet::new();
            if case.all_source {
                esis.extend(0..k);
                esis.extend(k..k + 20);
            } else {
                esis.extend([0, 1, k - 1, k, k + 1, k + 2, k + 5, k + 17]);
            }
            esis.extend([k + 100, 1000, 40000, 65536 + k, 1 << 20, (1 << 24) - 1]);
            for _ in 0..4 {
                esis.insert(rng.below(1 << 24) as u32);
            }
            for &esi in &esis {
                if esi < (1 << 24) {
                    symbols.push(packet_json(&symbol_for(block, k, esi)));
                }
            }
        }

        // A decode set the crate decodes, across all blocks of the object.
        let mut decodable = vec![];
        let mut found = false;
        for _attempt in 0..1000 {
            let mut set = vec![];
            for (sbn, block) in blocks.iter().enumerate() {
                set.extend(random_set(&mut rng, block, sizes[sbn], sizes[sbn] + case.overhead));
            }
            let mut decoder = Decoder::new(config);
            let mut result = None;
            for p in &set {
                if result.is_none() {
                    result = decoder.decode(p.clone());
                }
            }
            if result.as_deref() == Some(&data[..]) {
                decodable = set;
                found = true;
                break;
            }
        }
        assert!(found, "no decodable set for {}", case.name);

        // A set of exactly K symbols for block 0 that the crate cannot decode, if one turns up.
        let mut undecodable = Value::Null;
        if case.source_blocks == 1 {
            let k = sizes[0];
            for _attempt in 0..5000 {
                let set = random_set(&mut rng, &blocks[0], k, k);
                if set.iter().all(|p| p.payload_id().encoding_symbol_id() < k) {
                    continue; // all source symbols always decode
                }
                let mut decoder = SourceBlockDecoder::new(0, &config, k as u64 * case.symbol_size as u64);
                if decoder.decode(set.clone()).is_none() {
                    // Rank depends only on which ESIs were received, so the bytes are not needed.
                    let esis: Vec<String> =
                        set.iter().map(|p| p.payload_id().encoding_symbol_id().to_string()).collect();
                    undecodable = Value::String(esis.join(","));
                    break;
                }
            }
        }

        eprintln!(
            "{}: blocks {:?}, {} byte-match symbols, decode set {}, undecodable set {}",
            case.name,
            sizes,
            symbols.len(),
            decodable.len(),
            if undecodable.is_null() { "none found" } else { "found" }
        );

        cases.push(json!({
            "name": case.name,
            "transferLength": case.transfer_length,
            "symbolSize": case.symbol_size,
            "sourceBlocks": case.source_blocks,
            "subBlocks": case.sub_blocks,
            "alignment": case.alignment,
            "seed": case.seed,
            "oti": hex(&config.serialize()),
            "symbols": symbols,
            "decodable": decodable.iter().map(packet_json).collect::<Vec<_>>(),
            "undecodable": undecodable,
        }));
    }

    let doc = json!({
        "generator": "tools/raptorq-vectors, raptorq crate 2.0.1 (https://github.com/cberner/raptorq)",
        "objectBytes": "xorshift32: x = seed; per byte x ^= x << 13; x ^= x >> 17; x ^= x << 5; byte = x & 0xff",
        "cases": cases,
    });
    std::fs::write(path, serde_json::to_string_pretty(&doc).unwrap() + "\n").unwrap();
}

fn check(path: &str) {
    let doc: Value = serde_json::from_str(&std::fs::read_to_string(path).unwrap()).unwrap();
    let mut failures = 0;
    for case in doc["cases"].as_array().unwrap() {
        let name = case["name"].as_str().unwrap();
        let oti: [u8; 12] = unhex(case["oti"].as_str().unwrap()).try_into().unwrap();
        let config = ObjectTransmissionInformation::deserialize(&oti);
        let data = object_bytes(case["seed"].as_u64().unwrap() as u32, config.transfer_length());
        let encoder = Encoder::new(&data, config);
        let blocks = encoder.get_block_encoders();
        let sizes = block_sizes(&config);

        let mut mismatches = 0;
        let symbols = case["symbols"].as_array().unwrap();
        for v in symbols {
            let ours = packet_from_json(v);
            let sbn = ours.payload_id().source_block_number() as usize;
            let theirs = symbol_for(&blocks[sbn], sizes[sbn], ours.payload_id().encoding_symbol_id());
            if theirs.data() != ours.data() {
                mismatches += 1;
            }
        }

        let mut decoder = Decoder::new(config);
        let mut result = None;
        let set = case["decodable"].as_array().unwrap();
        for v in set {
            if result.is_none() {
                result = decoder.decode(packet_from_json(v));
            }
        }
        let decoded = result.as_deref() == Some(&data[..]);
        let ok = mismatches == 0 && decoded;
        if !ok {
            failures += 1;
        }
        println!(
            "{}: {} symbols compared, {} differ; crate decoded our {} symbols: {}",
            name,
            symbols.len(),
            mismatches,
            set.len(),
            if decoded { "yes" } else { "NO" }
        );
    }
    if failures > 0 {
        println!("FAILED: {} case(s)", failures);
        std::process::exit(1);
    }
    println!("all cases match");
}

fn main() {
    let args: Vec<String> = std::env::args().collect();
    match (args.get(1).map(String::as_str), args.get(2)) {
        (Some("generate"), Some(path)) => generate(path),
        (Some("check"), Some(path)) => check(path),
        _ => {
            eprintln!("usage: raptorq-vectors generate <out.json> | check <ours.json>");
            std::process::exit(2);
        }
    }
}

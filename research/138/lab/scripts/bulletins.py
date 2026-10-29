#!/usr/bin/env python3 -I
"""The test bulletins. Each is (key, title bytes, body bytes, ending) where body is sent verbatim
over the telnet session (so the line terminators in it are exactly what the BBS sees on the wire)
and ending is b"/EX\r" (normal) or b"\x1a" (Ctrl-Z straight after the last byte, no newline).
Running this file writes bulletins/<key>.{title,body,ending} so the inputs are on record."""
import os, sys
CR = b"\r"          # what a telnet client sends for Enter
def lines(*ls, t=CR): return b"".join(l + t for l in ls)
LONG = lambda n, c=b"x": (c * n)
FIFTY = b"0123456789abcdefghijklmnopqrstuvwxyz ABCDEFGHIJKLMNOPQRSTUV."  # 61 chars
B = {}
def add(key, body, title=None, ending=b"/EX\r"):
    B[key] = (title if title is not None else ("lab " + key).encode(), body, ending)

add("t01_plain", lines(b"Plain bulletin.", b"Second line.", b"Third line."))
add("t02_from_line", lines(b"From the desk of the lab", b"From nobody Thu Oct 29 20:00:00 2026", b"  From indented", b"from lower", b"End."))
add("t03_from_to_top", lines(b"From: G0FAKE@GB7XXX.#1.GBR.EURO", b"To  : ALL@WW", b"Body after fake header lines."))
add("t04_trailing_ws", lines(b"trailing space   ", b"trailing tab\t", b"\ttab at start", b"mid\ttab", b"space-only line follows", b"   ", b"end."))
add("t05_blank_first", lines(b"", b"first line above is blank", b"", b"", b"two blank lines above this one."))
add("t06_long_lines", lines(b"L79 " + LONG(75), b"L80 " + LONG(76), b"L100 " + LONG(96), b"L255 " + LONG(250), b"L256 " + LONG(251), b"L300 " + LONG(296), b"L600 " + LONG(596, b"abcdefghi "), b"end."))
add("t07_title40", lines(b"title is 40 chars exactly."), title=b"T40 " + b"a" * 36)
add("t08_title60", lines(b"title is 60 chars exactly."), title=b"T60 " + b"b" * 56)
add("t09_title100", lines(b"title is 100 chars."), title=b"T100 " + b"c" * 95)
add("t10_latin1", lines(b"latin1: caf\xe9 na\xefve \xa3 \xb0 \xfe \xc0\xdf end", b"high: " + bytes(range(0x80, 0xa0)).replace(b"\xff", b"") , b"end."), title=b"caf\xe9 title \xa3")
add("t11_utf8", lines("utf8: café naïve £ ° € — – … 日本".encode(), b"end."), title="café £ ° — title".encode())
add("t12_no_final_newline", lines(b"line one", b"line two") + b"last line without newline", ending=b"\x1a")
add("t13_ends_blank_lines", lines(b"text", b"", b"", b""))
add("t14_crlf_body", lines(b"crlf one", b"crlf two", b"crlf three", t=b"\r\n"))
add("t15_lf_body", lines(b"lf one", b"lf two", b"lf three", t=b"\n"))
add("t16_dot_line", lines(b"before", b".", b"after", b"..", b"after2"))
add("t17_r_lines", lines(b"R:261029/1200Z 1@GB7FAKE.#1.GBR.EURO LinBPQ6.0.25", b"R:261029/1100Z 7@GB7FAK2.#2.GBR.EURO [FBB] $:F", b"", b"body after R lines.", b"R:261029/1000Z mid-body R line 9@X.#3.GBR.EURO", b"end."))
add("t18_very_long", b"".join(lines(b"%04d " % i + FIFTY) for i in range(1, 801)))   # about 53 kB
add("t19_gt_from_and_misc", lines(b">From already escaped", b"~ tilde line", b"/EX-ish line: /EXIT", b"# hash", b"end."))
add("t20_ctrl_chars", lines(b"bell:\x07 esc:\x1b[0m ff:\x0c nul-free", b"end."))
if __name__ == "__main__":
    out = sys.argv[1]; os.makedirs(out, exist_ok=True)
    for k, (t, b, e) in B.items():
        for suf, d in (("title", t), ("body", b), ("ending", e)):
            open(f"{out}/{k}.{suf}", "wb").write(d)
    print(len(B), "bulletins written to", out)

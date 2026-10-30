# Canonical form of a bulletin (issue #141, Model B)

Copies of one bulletin held by different BBSes differ at the edges, not in the middle: every forwarding hop prepends routing lines, some BBSes add an origin block, and they end lines differently. `CanonicalForm.Canonicalise` (in `Packet.Mailcast`) splits a copy into a **canonical body**, which is the same for every station that holds the bulletin, and a small per-station **envelope** holding exactly what was taken off. `CanonicalForm.Rebuild(canonical, envelope)` returns the original bytes and title exactly, for any input, including inputs no rule matches. A rule therefore only has to be good at making copies match, not safe. This is a library only; nothing sends or receives it yet (that is #142, #143, #145).

Evidence for each rule is in `research/138/README.md` and `research/138/lab/FINDINGS.md`.

Bytes are handled one to one (Latin-1), and only space and tab count as blank: ANSI escapes, 8-bit bytes, NBSP, NEL and UTF-8 sequences are content, wherever they sit.

## What is removed from the top

Working down from the first line, repeating until none applies:

1. Blank lines (nothing, or only spaces and tabs).
2. A run of routing lines `R:yymmdd/hhmm[Zz] ...` (the zone letter is optional because Sally writes `R:261024/2145 @:...`, seen in the corpus) (LinBPQ, FBB `@:` form, XRouter and openBCM alike).
3. A `Path: ...` line and the lines after it that start with a space or tab (FBB's reader output).
4. Once routing has been seen, one origin block, after which the top stops:
   - FBB: a blank line, `From: CALL@HROUTE`, `To  : X@ROUTE` (two spaces before the colon), blank line.
   - XRouter: `Date: Thu, 29 Oct 2026 23:58:25 +0000`, `From: a <b>`, `To: x`, `Subject: ...`, `Message-ID: ...`, blank line.
   - openBCM: `From: X @ Y` (optionally followed by ` (Name)`), `To:   X @ Y` (three spaces after the colon, an optional trailing space), an optional `X-Info: ...`, blank line. openBCM's title overflow line is not recognised: every copy keeps it in the body, so it already matches.

Anything that does not match exactly stays, so an author's own `From:` line, a `To:` with one space, or an `R:` line after the body has started is never touched. A leading `R:` block typed by the author cannot be told from routing and is removed; it is in the envelope, so nothing is lost.

## What is removed from the bottom

Trailing blank lines (nothing, or only spaces and tabs), and the line ending of the last content line. Trailing spaces on content lines, tabs, long lines and 8-bit bytes are kept.

## Line endings

Lines end in CR LF, LF or a lone CR. If every break between body lines is the same kind, the canonical body uses LF and the envelope records the style. If they are mixed, the style is `Raw`: the body is left byte for byte as it was (it will not combine with other copies, which costs airtime and breaks nothing). The middle of the body is never changed otherwise; JNOS's `>From` escaping and 127-column folding are not undone.

## The canonical body

The content lines, each followed by one LF, so it is empty or ends with LF.

## The envelope

`BulletinEnvelope.Serialise()` gives a deterministic byte string, version 1:

| Field | Size | Meaning |
| --- | --- | --- |
| version | 1 byte | `1`. A reader rejects any other value. A rule that changes the middle of a body needs a new version. |
| line ending | 1 byte | 0 LF, 1 CR LF, 2 CR, 3 Raw |
| top | LEB128 length, then bytes | exactly what was removed above the canonical body |
| bottom | LEB128 length, then bytes | exactly what was removed below it (starts with the last content line's ending) |
| title | LEB128 length, then bytes | the full title, Latin-1 |

Lengths are unsigned LEB128 (7 bits per byte, low group first, high bit set when more follow), in their shortest form: a reader rejects redundant zero groups, so an envelope has exactly one encoding and one hash. A typical envelope is a few hundred bytes (routing lines of about 55 bytes each). Rebuild is: top, then the canonical body with its final LF dropped and the remaining LFs turned back into the recorded line ending, then bottom.

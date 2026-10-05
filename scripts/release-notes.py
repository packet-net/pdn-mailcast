#!/usr/bin/env python3
"""Release notes from first-parent history: one bullet per merged PR or direct commit.

Copied from pdn-soundmodem's scripts/release-notes.py; only the closing line differs.

Usage: scripts/release-notes.py <tag> [<previous-tag>]   (previous defaults to the tag before
<tag> in version order). Writes Markdown to stdout. No network: a merge commit's body line
is the PR title, a direct commit's subject is its own title. Conventional prefixes
(feat/fix/docs/test/chore...) decide the section and are stripped from the bullet; a
bare area prefix ("ms110d:", "Survey:") stays as the bullet's lead word.
"""
import re
import subprocess
import sys


def git(*args):
    return subprocess.run(["git", *args], check=True, capture_output=True, text=True).stdout


def version_key(tag):
    parts = re.findall(r"\d+", tag)
    return tuple(int(p) for p in parts) + (0,) * (4 - len(parts))


def previous_tag(tag):
    tags = sorted(git("tag", "-l", "v*").split(), key=version_key)
    if tag not in tags:
        return tags[-1] if tags else None
    i = tags.index(tag)
    return tags[i - 1] if i > 0 else None


SECTIONS = [
    ("feat", "New"),
    ("fix", "Fixes"),
    ("perf", "Performance"),
    ("test", "Tests and instruments"),
    ("docs", "Documentation"),
    ("chore", "Housekeeping"),
    ("build", "Housekeeping"),
    ("ci", "Housekeeping"),
    ("style", "Housekeeping"),
    ("refactor", "Housekeeping"),
]
PREFIX = re.compile(r"^(?P<type>[A-Za-z]+)(?:\((?P<scope>[^)]+)\))?(?P<bang>!)?:\s*(?P<rest>.+)$")


def classify(title):
    m = PREFIX.match(title)
    if not m:
        return "Changes", title
    kind = m.group("type").lower()
    section = dict(SECTIONS).get(kind)
    if section is None:
        # An area prefix rather than a conventional type ("ms110d: G1d closes ..."):
        # keep it as the lead word, file under Changes.
        return "Changes", title
    rest = m.group("rest")
    scope = m.group("scope")
    rest = rest[0].upper() + rest[1:]
    return section, f"{scope}: {rest}" if scope else rest


def commit_exists(ref):
    return subprocess.run(
        ["git", "rev-parse", "--verify", "--quiet", f"{ref}^{{commit}}"],
        capture_output=True, text=True).returncode == 0


def entries(prev, tag):
    # On a tag push the tag is already there. On a workflow_dispatch it is not: release.yml
    # calls this BEFORE `gh release create`, which is what creates the tag, so `prev..tag`
    # names a commit that does not exist and git exits 128 (pdn-soundmodem learned this on
    # its first dispatched release). HEAD is the right end of the range because it is the
    # commit the release will be tagged at, `gh release create --target "$GITHUB_SHA"`.
    end = tag if commit_exists(tag) else "HEAD"
    rng = f"{prev}..{end}" if prev else end
    raw = git("log", "--first-parent", "--format=%H%x1f%h%x1f%s%x1f%b%x1e", rng)
    out = []
    for rec in raw.split("\x1e"):
        rec = rec.strip("\n")
        if not rec.strip():
            continue
        _sha, short, subject, body = (rec.split("\x1f") + ["", "", "", ""])[:4]
        m = re.match(r"Merge pull request #(\d+) from \S+", subject)
        if m:
            title = next((l.strip() for l in body.splitlines() if l.strip()), subject)
            ref = f"#{m.group(1)}"
        else:
            title = subject.strip()
            ref = short
            squashed = re.search(r"\s*\(#(\d+)\)$", title)
            if squashed:
                title = title[: squashed.start()]
                ref = f"#{squashed.group(1)}"
        out.append((title, ref))
    return out


def main():
    if len(sys.argv) < 2:
        sys.exit(__doc__)
    tag = sys.argv[1]
    prev = sys.argv[2] if len(sys.argv) > 2 else previous_tag(tag)
    groups = {}
    for title, ref in entries(prev, tag):
        section, text = classify(title)
        groups.setdefault(section, []).append(f"- {text} ({ref})")
    order = ["New", "Fixes", "Performance", "Changes", "Tests and instruments", "Documentation", "Housekeeping"]
    lines = []
    if prev:
        lines.append(f"Changes since {prev}.")
        lines.append("")
    for section in order:
        if section in groups:
            lines.append(f"### {section}")
            lines.extend(groups[section])
            lines.append("")
    if not any(section in groups for section in order):
        lines.append("No recorded changes.")
        lines.append("")
    lines.append("Packages: `pdn-mailcast-receiver` and `pdn-mailcast-headend` as `.deb` for amd64, arm64 and armhf, and the M0LTE.RaptorQ NuGet package, are attached; verify downloads against `SHA256SUMS`. To run a receiver, see [src/Mailcast.Receiver/README.md](https://github.com/packet-net/pdn-mailcast/blob/main/src/Mailcast.Receiver/README.md); for the head end, [docs/headend.md](https://github.com/packet-net/pdn-mailcast/blob/main/docs/headend.md).")
    print("\n".join(lines))


if __name__ == "__main__":
    main()

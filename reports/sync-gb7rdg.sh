#!/bin/sh
# Runs on gb7rdg-node from mailcast-report-archive.timer: copies new daily reports from GB7RDG's
# mail store into this checkout and pushes them to main. The reports workflow then regenerates the
# tables. Setup is in reports/README.md, "Automatic copying from GB7RDG".
set -eu
cd "$(dirname "$0")/.."
git pull -q --rebase origin main
out=$(python3 reports/import-gb7rdg.py local)
echo "$out"
if [ -z "$(git status --porcelain -- reports/daily)" ]; then
    exit 0
fi
git add -- reports/daily
added=$(echo "$out" | sed -n 's/^added \(.*\)\.txt$/\1/p' | tr '\n' ' ')
git -c user.name="GB7RDG report archive" -c user.email="gb7rdg-node@users.noreply.github.com" \
    commit -q -m "Reports: import from GB7RDG" -m "Added: $added"
git push -q origin HEAD:main

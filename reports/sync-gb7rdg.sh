#!/bin/sh
# Runs on gb7rdg-node from mailcast-report-archive.timer: copies new daily reports from GB7RDG's
# mail store into this checkout and pushes them to main. The reports workflow then regenerates the
# tables. Setup is in reports/README.md, "Automatic copying from GB7RDG".
set -eu
cd "$(dirname "$0")/.."
# Every git command needs an identity: a pull that replays a commit left by a rejected push
# commits too.
export GIT_AUTHOR_NAME="GB7RDG report archive" GIT_AUTHOR_EMAIL="gb7rdg-node@users.noreply.github.com"
export GIT_COMMITTER_NAME="$GIT_AUTHOR_NAME" GIT_COMMITTER_EMAIL="$GIT_AUTHOR_EMAIL"
# A run that died mid-rebase would otherwise stop every later one.
if [ -d .git/rebase-merge ] || [ -d .git/rebase-apply ]; then
    git rebase --abort
fi
git pull -q --rebase origin main
out=$(python3 reports/import-gb7rdg.py local)
echo "$out"
if [ -z "$(git status --porcelain -- reports/daily)" ]; then
    exit 0
fi
git add -- reports/daily
added=$(echo "$out" | sed -n 's/^added \(.*\)\.txt$/\1/p' | tr '\n' ' ')
git commit -q -m "Reports: import from GB7RDG" -m "Added: $added"
git push -q origin HEAD:main

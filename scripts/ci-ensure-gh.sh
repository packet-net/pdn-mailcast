#!/usr/bin/env bash
# ci-ensure-gh.sh: put the GitHub CLI on PATH for the rest of the job when the runner has none.
#
# Some self-hosted runners have no gh and give us no root to install it, so this fetches GitHub's
# own release tarball into the job's temp directory, checks it against the pinned SHA-256 and adds
# it to $GITHUB_PATH. A runner that already has gh is left alone.
set -euo pipefail

if command -v gh > /dev/null 2>&1; then
  exit 0
fi

version=2.103.3
case "$(uname -m)" in
  x86_64) arch=amd64 sha=4c806978ffce1cec70853302a56d70ac6da4f19bae7adca4e1a8ab00633dd91d ;;
  aarch64) arch=arm64 sha=ee32df2323c8847f5fac6cb6b4b69ca75828e9329a8f22f677021f7b3306ce63 ;;
  *) echo "::error::no gh on this runner, and no pinned gh build for $(uname -m)"; exit 1 ;;
esac

name="gh_${version}_linux_${arch}"
dir="${RUNNER_TEMP:?}/gh"
mkdir -p "$dir"
curl -fsSL --retry 3 -o "$dir/$name.tar.gz" "https://github.com/cli/cli/releases/download/v${version}/$name.tar.gz"
echo "$sha  $dir/$name.tar.gz" | sha256sum -c --quiet
tar -xzf "$dir/$name.tar.gz" -C "$dir"
echo "$dir/$name/bin" >> "${GITHUB_PATH:?}"
echo "gh $version fetched into $dir, as this runner has none"

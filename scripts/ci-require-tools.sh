#!/usr/bin/env bash
# ci-require-tools.sh: fail with one clear line per missing tool, naming the apt package.
#
#   scripts/ci-require-tools.sh dpkg-deb:dpkg readelf:binutils docker
#
# Each argument is <command> or <command>:<package>. Workflows call this on self-hosted runners
# for the tools no setup action provides. It never installs anything: that needs root, and a
# runner is not assumed to give us passwordless sudo. A job that finds something missing says
# what to install and stops, rather than failing 20 minutes in with a bare "command not found".
set -u
missing=0
for spec in "$@"; do
  cmd="${spec%%:*}"
  pkg="${spec#*:}"
  if ! command -v "$cmd" > /dev/null 2>&1; then
    echo "::error::this runner has no '$cmd': install the '$pkg' package (apt-get install $pkg)"
    missing=1
  fi
done
# docker on PATH is not docker usable: the daemon must answer for this account.
for spec in "$@"; do
  if [ "${spec%%:*}" = docker ] && command -v docker > /dev/null 2>&1 && ! docker info > /dev/null 2>&1; then
    echo "::error::docker is installed but this account cannot reach the daemon: start docker and add the runner user to the 'docker' group"
    missing=1
  fi
done
exit "$missing"

#!/usr/bin/env bash
#
# build-receiver-zip.sh: publish the receiver self-contained for Windows and zip it, the
# Windows counterpart of build-receiver-deb.sh.
#
#   scripts/build-receiver-zip.sh <version>
#   e.g. scripts/build-receiver-zip.sh 0.1.0
#
# Produces artifacts/pdn-mailcast-receiver_<version>_win-x64.zip holding one folder,
# pdn-mailcast-receiver/, with:
#   pdn-mailcast-receiver.exe       the self-contained single-file binary
#   *.dll                           anything publish leaves beside it
#   receiver.example.json           the example config, with Windows' forms and no stateDirectory
#   install-service.ps1             makes the Windows service
#   README.txt, README.md, LICENSE
# It runs anywhere with the .NET SDK and python3 (for the zip), so release.yml builds it on Linux.
set -euo pipefail
umask 022

version="${1:?usage: build-receiver-zip.sh <version>}"
rid=win-x64
python="${PYTHON:-python3}"

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
proj="$root/src/Mailcast.Receiver/Mailcast.Receiver.csproj"
pub="$root/artifacts/receiver/$rid"
stage="$root/artifacts/zip/pdn-mailcast-receiver"
out="$root/artifacts/pdn-mailcast-receiver_${version}_${rid}.zip"

# As the .deb: self-contained single file, no ICU, no symbols, no ReadyToRun, no trimming.
echo "==> publish $rid"
rm -rf "$pub"
dotnet publish "$proj" -c Release -r "$rid" --self-contained true \
  -p:Version="$version" \
  -p:PublishSingleFile=true \
  -p:InvariantGlobalization=true \
  -p:DebugType=none \
  -p:DebugSymbols=false \
  -v minimal -o "$pub"

exe="$pub/pdn-mailcast-receiver.exe"
[ -f "$exe" ] || { echo "expected the binary $exe, but it was not produced" >&2; exit 1; }

echo "==> stage"
rm -rf "$stage"
mkdir -p "$stage"
find "$pub" -maxdepth 1 -type f ! -name '*.xml' ! -name '*.pdb' -exec cp {} "$stage/" \;
cp "$root/packaging/windows/README.txt" "$root/packaging/windows/install-service.ps1" "$stage/"
cp "$root/src/Mailcast.Receiver/README.md" "$stage/README.md"
cp "$root/LICENSE" "$stage/LICENSE"

# The example as Windows reads it: a Windows sound card and path, and the state directory
# left to its Windows default (C:\ProgramData\pdn-mailcast\state) rather than /var/lib.
example="$root/src/Mailcast.Receiver/receiver.example.json"
sed \
  -e 's|^  //   "plughw:CARD=Device,DEV=0"       a sound card on your radio, set to USB on "dialKHz"|  //   "soundcard:USB Audio Device"     a sound card on your radio by its Windows name, set to USB on "dialKHz"|' \
  -e 's|^  //   "wav:/path/to/recording.wav"     a recording, for testing|  //   "wav:C:\\\\path\\\\recording.wav"     a recording, for testing|' \
  -e '/^  "stateDirectory": /d' \
  "$example" > "$stage/receiver.example.json"
# Each change must have happened: the example's wording moving would otherwise go unnoticed.
grep -q 'soundcard:USB Audio Device' "$stage/receiver.example.json" || { echo "the example's sound card line has changed: update this script" >&2; exit 1; }
grep -q 'wav:C:' "$stage/receiver.example.json" || { echo "the example's recording line has changed: update this script" >&2; exit 1; }
! grep -q 'stateDirectory' "$stage/receiver.example.json" || { echo "the example's stateDirectory line has changed: update this script" >&2; exit 1; }
# That leaves a comma after "archive", which the receiver reads (it allows trailing commas).

echo "==> zip"
rm -f "$out"
(cd "$root/artifacts/zip" && "$python" -m zipfile -c "$out" pdn-mailcast-receiver)
echo "built $out"

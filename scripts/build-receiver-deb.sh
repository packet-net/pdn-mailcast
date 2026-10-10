#!/usr/bin/env bash
#
# build-receiver-deb.sh: publish the receiver self-contained for one RID and package it as a
# Debian .deb, the way pdn-bbs's scripts/build-deb.sh packages pdn-bbs.
#
#   scripts/build-receiver-deb.sh <rid> <version>
#   e.g. scripts/build-receiver-deb.sh linux-x64 0.1.0
#
# Produces artifacts/pdn-mailcast-receiver_<version>_<arch>.deb holding:
#   usr/lib/pdn-mailcast/pdn-mailcast-receiver       the self-contained single-file binary
#   usr/bin/pdn-mailcast-receiver                    a link to it
#   lib/systemd/system/pdn-mailcast-receiver.service
#   usr/share/pdn-mailcast/receiver.example.json     postinst copies it to /etc/pdn-mailcast/receiver.json
#   usr/share/doc/pdn-mailcast-receiver/             README.md and copyright
# The config is made by postinst rather than shipped, because the service rewrites it when
# settings are saved on its page. State lives in /var/lib/pdn-mailcast and is never shipped.
set -euo pipefail
umask 022

rid="${1:?usage: build-receiver-deb.sh <rid> <version>}"
version="${2:?usage: build-receiver-deb.sh <rid> <version>}"

case "$rid" in
  linux-x64)   arch=amd64 ;;
  linux-arm64) arch=arm64 ;;
  linux-arm)   arch=armhf ;;
  *) echo "unknown rid: $rid (want linux-x64, linux-arm64 or linux-arm)" >&2; exit 2 ;;
esac

command -v readelf >/dev/null || { echo "readelf not found: install binutils" >&2; exit 3; }

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
proj="$root/src/Mailcast.Receiver/Mailcast.Receiver.csproj"
pub="$root/artifacts/receiver/$rid"
stage="$root/artifacts/deb/receiver-$rid"
out="$root/artifacts/pdn-mailcast-receiver_${version}_${arch}.deb"

# Self-contained single file, no ICU, no symbols. No ReadyToRun and no trimming, as pdn-bbs.
# Unlike pdn-bbs, the .NET runtime's native libraries stay beside the binary rather than inside
# it: inside, they are extracted at start-up into the user's home directory, which a system
# user does not have.
echo "==> publish $rid"
rm -rf "$pub"
dotnet publish "$proj" -c Release -r "$rid" --self-contained true \
  -p:Version="$version" \
  -p:PublishSingleFile=true \
  -p:InvariantGlobalization=true \
  -p:DebugType=none \
  -p:DebugSymbols=false \
  -v minimal -o "$pub"

bin="$pub/pdn-mailcast-receiver"
[ -f "$bin" ] || { echo "expected the binary $bin, but it was not produced" >&2; exit 1; }

echo "==> stage the package for $arch"
rm -rf "$stage"
install -d "$stage/DEBIAN" "$stage/usr/lib/pdn-mailcast" "$stage/usr/bin" "$stage/lib/systemd/system" \
  "$stage/usr/share/pdn-mailcast" "$stage/usr/share/doc/pdn-mailcast-receiver"
install -m 0755 "$bin" "$stage/usr/lib/pdn-mailcast/pdn-mailcast-receiver"
# Anything else publish left beside the binary (native libraries that will not bundle).
find "$pub" -maxdepth 1 -type f ! -name pdn-mailcast-receiver ! -name '*.xml' ! -name '*.pdb' -exec install -m 0644 {} "$stage/usr/lib/pdn-mailcast/" \;
ln -s ../lib/pdn-mailcast/pdn-mailcast-receiver "$stage/usr/bin/pdn-mailcast-receiver"
install -m 0644 "$root/packaging/receiver/pdn-mailcast-receiver.service" "$stage/lib/systemd/system/"
install -m 0644 "$root/src/Mailcast.Receiver/receiver.example.json" "$stage/usr/share/pdn-mailcast/"
install -m 0644 "$root/src/Mailcast.Receiver/README.md" "$stage/usr/share/doc/pdn-mailcast-receiver/README.md"
cat > "$stage/usr/share/doc/pdn-mailcast-receiver/copyright" <<EOF
Format: https://www.debian.org/doc/packaging-manuals/copyright-format/1.0/
Upstream-Name: pdn-mailcast
Source: https://github.com/packet-net/pdn-mailcast

Files: *
Copyright: packet-net contributors
License: AGPL-3.0-only
 The receiver bundles pdn-soundmodem (GPL-3.0-or-later) and the .NET runtime (MIT).
 See https://github.com/packet-net/pdn-mailcast/blob/main/LICENSE

Files: usr/lib/pdn-mailcast/pdn-mailcast-receiver (embedded ground bounce data, issue #89)
Copyright: GeoNames (towns); Flanders Marine Institute (sea names); Met Office (shipping
 forecast areas, Crown copyright)
License: CC-BY-4.0 and OGL-UK-3.0
 Towns: GeoNames, https://www.geonames.org/, CC BY 4.0 (https://creativecommons.org/licenses/by/4.0/).
 Sea names: Flanders Marine Institute (2018). IHO Sea Areas, version 3,
 https://doi.org/10.14284/323, CC BY 4.0.
 Shipping forecast areas: Met Office, National Meteorological Library and Archive, Factsheet 8
 "The Shipping Forecast", Table 1. Crown copyright, Open Government Licence v3.0
 (https://www.nationalarchives.gov.uk/doc/open-government-licence/version/3/).
 Full credits, and how the data was built and simplified: docs/receiver.md.
EOF

# Library floors, read from the ELF files in the package rather than assumed: the .NET host's
# glibc floor moves between releases and runtime identifiers, and an understated Depends lets
# apt install a binary that cannot start.
max_version() {
  local family="$1" best="" v f
  while IFS= read -r f; do
    [ "$(od -An -tx1 -N4 "$f" 2>/dev/null | tr -d ' \n')" = "7f454c46" ] || continue
    v="$(readelf --version-info "$f" 2>/dev/null | grep -oE "${family}_[0-9][0-9.]*" | sed "s/^${family}_//" | sort -uV | tail -1)"
    [ -n "$v" ] && best="$(printf '%s\n%s\n' "$best" "$v" | sort -uV | tail -1)"
  done < <(find "$stage/usr/lib/pdn-mailcast" -type f)
  printf '%s' "$best"
}
glibc="$(max_version GLIBC)"
glibcxx="$(max_version GLIBCXX)"
[ -n "$glibc" ] || { echo "could not read a glibc floor from the binary" >&2; exit 4; }
# libssl: HTTPS and WebSockets to a web SDR. .NET loads it at run time, so readelf cannot see it.
depends="libc6 (>= $glibc), libgcc-s1, libssl3 | libssl3t64 | libssl1.1, libasound2 | libasound2t64, adduser"
if [ -n "$glibcxx" ]; then
  # libstdc++ names its symbol versions by C++ ABI; the GCC release that first shipped each.
  case "$glibcxx" in
    3.4.2[0-5]) gcc=8 ;;
    3.4.2[6-8]) gcc=10 ;;
    3.4.29) gcc=11 ;;
    3.4.30) gcc=12 ;;
    3.4.3[12]) gcc=13 ;;
    3.4.33) gcc=14 ;;
    *) echo "unknown GLIBCXX_$glibcxx: extend the table in $0" >&2; exit 4 ;;
  esac
  depends="$depends, libstdc++6 (>= $gcc)"
fi
echo "    depends: $depends"

size="$(du -k -s --exclude=DEBIAN "$stage" | cut -f1)"
sed -e "s/@ARCH@/$arch/" -e "s/@VERSION@/$version/" -e "s/@SIZE@/$size/" -e "s/@DEPENDS@/$depends/" \
  "$root/packaging/receiver/control.in" > "$stage/DEBIAN/control"
for s in postinst prerm postrm; do
  install -m 0755 "$root/packaging/receiver/$s" "$stage/DEBIAN/$s"
  sh -n "$stage/DEBIAN/$s"
done

echo "==> build the .deb"
mkdir -p "$root/artifacts"
# root:root files without fakeroot, and xz rather than zstd so bullseye's dpkg can unpack it.
dpkg-deb --build --root-owner-group -Zxz "$stage" "$out"
echo "==> built $out"
dpkg-deb --info "$out"
dpkg-deb --contents "$out" | awk '{print $1, $6, $7, $8}'

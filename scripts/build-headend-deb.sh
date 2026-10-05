#!/usr/bin/env bash
#
# build-headend-deb.sh: publish the head end self-contained for one RID and package it as a
# Debian .deb, laid out like the receiver's (scripts/build-receiver-deb.sh).
#
#   scripts/build-headend-deb.sh <rid> <version>
#   e.g. scripts/build-headend-deb.sh linux-x64 0.1.0
#
# Produces artifacts/pdn-mailcast-headend_<version>_<arch>.deb holding:
#   usr/lib/pdn-mailcast-headend/pdn-mailcast-headend     the self-contained single-file binary
#   usr/bin/pdn-mailcast-headend                          a link to it
#   lib/systemd/system/pdn-mailcast-headend.service
#   usr/share/pdn-mailcast-headend/headend.example.json   postinst copies it to /etc/pdn-mailcast-headend/headend.json
#   usr/share/doc/pdn-mailcast-headend/                   headend.md and copyright
# Its own directory under /usr/lib, not the receiver's, so the two packages never ship the same
# file and both can be installed on one machine. State lives in /var/lib/pdn-mailcast-headend.
set -euo pipefail
umask 022

rid="${1:?usage: build-headend-deb.sh <rid> <version>}"
version="${2:?usage: build-headend-deb.sh <rid> <version>}"

case "$rid" in
  linux-x64)   arch=amd64 ;;
  linux-arm64) arch=arm64 ;;
  linux-arm)   arch=armhf ;;
  *) echo "unknown rid: $rid (want linux-x64, linux-arm64 or linux-arm)" >&2; exit 2 ;;
esac

command -v readelf >/dev/null || { echo "readelf not found: install binutils" >&2; exit 3; }
command -v dpkg-deb >/dev/null || { echo "dpkg-deb not found: this needs a Debian-family host" >&2; exit 3; }

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
proj="$root/src/Mailcast.HeadEnd/Mailcast.HeadEnd.csproj"
pub="$root/artifacts/headend/$rid"
stage="$root/artifacts/deb/headend-$rid"
out="$root/artifacts/pdn-mailcast-headend_${version}_${arch}.deb"
lib="usr/lib/pdn-mailcast-headend"
doc="usr/share/doc/pdn-mailcast-headend"

echo "==> publish $rid"
rm -rf "$pub"
dotnet publish "$proj" -c Release -r "$rid" --self-contained true \
  -p:Version="$version" \
  -p:PublishSingleFile=true \
  -p:InvariantGlobalization=true \
  -p:DebugType=none \
  -p:DebugSymbols=false \
  -p:GenerateDocumentationFile=false \
  -v minimal -o "$pub"

bin="$pub/pdn-mailcast-headend"
[ -f "$bin" ] || { echo "expected the binary $bin, but it was not produced" >&2; exit 1; }

echo "==> stage the package for $arch"
rm -rf "$stage"
install -d "$stage/DEBIAN" "$stage/$lib" "$stage/usr/bin" "$stage/lib/systemd/system" \
  "$stage/usr/share/pdn-mailcast-headend" "$stage/$doc"
install -m 0755 "$bin" "$stage/$lib/pdn-mailcast-headend"
# Native libraries publish leaves beside the binary; .NET looks for them next to its real path.
find "$pub" -maxdepth 1 -type f ! -name pdn-mailcast-headend ! -name '*.xml' ! -name '*.pdb' -exec install -m 0644 {} "$stage/$lib/" \;
ln -s ../lib/pdn-mailcast-headend/pdn-mailcast-headend "$stage/usr/bin/pdn-mailcast-headend"
install -m 0644 "$root/packaging/headend/pdn-mailcast-headend.service" "$stage/lib/systemd/system/"
install -m 0644 "$root/packaging/headend/headend.example.json" "$stage/usr/share/pdn-mailcast-headend/"
install -m 0644 "$root/docs/headend.md" "$stage/$doc/headend.md"
cat > "$stage/$doc/copyright" <<COPYRIGHT
Format: https://www.debian.org/doc/packaging-manuals/copyright-format/1.0/
Upstream-Name: pdn-mailcast
Source: https://github.com/packet-net/pdn-mailcast

Files: *
Copyright: packet-net contributors
License: AGPL-3.0-only
 The head end bundles pdn-soundmodem (GPL-3.0-or-later), M0LTE.Flex and the .NET runtime (MIT).
 See https://github.com/packet-net/pdn-mailcast/blob/main/LICENSE
COPYRIGHT

# Library floors, read from the ELF files in the package rather than assumed (see the receiver's
# script and pdn-soundmodem's for why every ELF and not just the executable).
max_version() {
  local family="$1" best="" v f
  while IFS= read -r f; do
    [ "$(od -An -tx1 -N4 "$f" 2>/dev/null | tr -d ' \n')" = "7f454c46" ] || continue
    v="$(readelf --version-info "$f" 2>/dev/null | grep -oE "${family}_[0-9][0-9.]*" | sed "s/^${family}_//" | sort -uV | tail -1)"
    [ -n "$v" ] && best="$(printf '%s\n%s\n' "$best" "$v" | sort -uV | tail -1)"
  done < <(find "$stage/$lib" -type f)
  printf '%s' "$best"
}
glibc="$(max_version GLIBC)"
glibcxx="$(max_version GLIBCXX)"
[ -n "$glibc" ] || { echo "could not read a glibc floor from the binary" >&2; exit 4; }
depends="libc6 (>= $glibc), libgcc-s1, adduser"
if [ -n "$glibcxx" ]; then
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
  "$root/packaging/headend/control.in" > "$stage/DEBIAN/control"
for s in postinst prerm postrm; do
  install -m 0755 "$root/packaging/headend/$s" "$stage/DEBIAN/$s"
  sh -n "$stage/DEBIAN/$s"
done

echo "==> build the .deb"
mkdir -p "$root/artifacts"
# root:root files without fakeroot, and xz rather than zstd so bullseye's dpkg can unpack it.
dpkg-deb --build --root-owner-group -Zxz "$stage" "$out"
echo "==> built $out"
dpkg-deb --info "$out"
dpkg-deb --contents "$out" | awk '{print $1, $6, $7, $8}'

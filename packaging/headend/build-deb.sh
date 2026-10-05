#!/usr/bin/env bash
# Builds the pdn-mailcast-headend .deb for one architecture.
#
#   packaging/headend/build-deb.sh <version> [amd64|arm64|armhf] [outdir]
#
# Produces <outdir>/pdn-mailcast-headend_<version>_<arch>.deb: a self-contained single-file build
# (no .NET runtime needed on the target) in /usr/lib/pdn-mailcast-headend with a symlink in
# /usr/bin, a systemd unit, and an example config seeded to /etc/pdn-mailcast-headend/headend.json
# on first install. Laid out like pdn-soundmodem's package (its packaging/build-deb.sh), whose
# reasons apply here too: native shims stay beside the binary, and the library floors in Depends
# are read from the binaries rather than assumed.
set -euo pipefail
umask 022

VERSION="${1:?usage: build-deb.sh <version> [arch] [outdir]}"
ARCH="${2:-amd64}"

case "$ARCH" in
  amd64) RID=linux-x64 ;;
  arm64) RID=linux-arm64 ;;
  armhf) RID=linux-arm ;;
  *) echo "unsupported arch $ARCH" >&2; exit 2 ;;
esac

command -v dpkg-deb >/dev/null || { echo "dpkg-deb not found - this needs a Debian-family host" >&2; exit 3; }
command -v readelf >/dev/null || { echo "readelf not found - install binutils" >&2; exit 3; }

HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
OUTDIR="${3:-$ROOT/artifacts}"
STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT

NAME=pdn-mailcast-headend
PKGDIR=/usr/lib/$NAME
DOCDIR=/usr/share/doc/$NAME
DATADIR=/usr/share/$NAME
UNITDIR=/usr/lib/systemd/system

dotnet publish "$ROOT/src/Mailcast.HeadEnd/Mailcast.HeadEnd.csproj" \
  --configuration Release \
  --runtime "$RID" \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:Version="$VERSION" \
  -p:DebugType=none \
  -p:GenerateDocumentationFile=false \
  --output "$STAGE/publish"

mkdir -p "$STAGE/root$PKGDIR" "$STAGE/root/usr/bin" "$STAGE/root$UNITDIR" \
         "$STAGE/root$DATADIR" "$STAGE/root$DOCDIR" "$STAGE/root/DEBIAN"

install -m 0755 "$STAGE/publish/$NAME" "$STAGE/root$PKGDIR/$NAME"
for so in "$STAGE"/publish/*.so; do
  [ -e "$so" ] || continue
  install -m 0644 "$so" "$STAGE/root$PKGDIR/$(basename "$so")"
done
ln -s "..${PKGDIR#/usr}/$NAME" "$STAGE/root/usr/bin/$NAME"
install -m 0644 "$HERE/$NAME.service" "$STAGE/root$UNITDIR/$NAME.service"
# Under /usr/share, not /usr/share/doc, which Debian allows to be stripped; postinst reads it.
install -m 0644 "$HERE/headend.example.json" "$STAGE/root$DATADIR/headend.example.json"
install -m 0644 "$ROOT/docs/headend.md" "$STAGE/root$DOCDIR/headend.md"
cat > "$STAGE/root$DOCDIR/copyright" <<COPYRIGHT
Format: https://www.debian.org/doc/packaging-manuals/copyright-format/1.0/
Upstream-Name: pdn-mailcast
Source: https://github.com/packet-net/pdn-mailcast

Files: *
Copyright: packet-net contributors
License: AGPL-3.0-only
 The full text is at https://www.gnu.org/licenses/agpl-3.0.txt and in the source repository.
COPYRIGHT

case "${SOURCE_DATE_EPOCH:-}" in
  ''|*[!0-9]*) CHANGELOG_DATE="$(date -R)" ;;
  *)           CHANGELOG_DATE="$(date -R --date="@$SOURCE_DATE_EPOCH")" ;;
esac
cat > "$STAGE/changelog.Debian" <<CHANGELOG
$NAME ($VERSION) unstable; urgency=medium

  * Release $VERSION. See https://github.com/packet-net/pdn-mailcast/releases

 -- Tom Fanning M0LTE <tom@m0lte.uk>  $CHANGELOG_DATE
CHANGELOG
gzip -9n -c "$STAGE/changelog.Debian" > "$STAGE/root$DOCDIR/changelog.Debian.gz"
chmod 0644 "$STAGE/root$DOCDIR/changelog.Debian.gz"

INSTALLED_SIZE="$(du -k -s --exclude=DEBIAN "$STAGE/root" | cut -f1)"

# The highest GLIBC_ and GLIBCXX_ symbol versions any shipped ELF needs (see pdn-soundmodem's
# packaging for why every ELF, not just the executable).
elf_files() {
  find "$STAGE/root" -type f -print | while IFS= read -r f; do
    [ "$(od -An -tx1 -N4 "$f" 2>/dev/null | tr -d ' \n')" = "7f454c46" ] && printf '%s\n' "$f"
  done
}
max_needed() {
  local family="$1" max="" v f
  while IFS= read -r f; do
    [ -n "$f" ] || continue
    v="$(readelf --version-info "$f" 2>/dev/null | awk '/Version needs section/,0' \
      | grep -oE "${family}_[0-9][0-9.]*" | sed "s/^${family}_//" | sort -uV | tail -1)"
    [ -n "$v" ] && max="$(printf '%s\n%s\n' "$max" "$v" | sort -uV | tail -1)"
  done <<ELFS
$(elf_files)
ELFS
  printf '%s' "$max"
}
GLIBC_MIN="$(max_needed GLIBC)"
GLIBCXX_MIN="$(max_needed GLIBCXX)"
[ -n "$GLIBC_MIN" ] || { echo "could not read a GLIBC floor from the staged package" >&2; exit 4; }
DEPENDS="libc6 (>= $GLIBC_MIN), libgcc-s1, adduser"
if [ -n "$GLIBCXX_MIN" ]; then
  case "$GLIBCXX_MIN" in
    3.4|3.4.[0-9]|3.4.1[0-9]|3.4.2[01]) STDCXX_MIN=5 ;;
    3.4.22) STDCXX_MIN=6 ;;
    3.4.23|3.4.24) STDCXX_MIN=7 ;;
    3.4.25) STDCXX_MIN=8 ;;
    3.4.26) STDCXX_MIN=9 ;;
    3.4.27|3.4.28) STDCXX_MIN=10 ;;
    3.4.29) STDCXX_MIN=11 ;;
    3.4.30) STDCXX_MIN=12 ;;
    3.4.31|3.4.32) STDCXX_MIN=13 ;;
    3.4.33) STDCXX_MIN=14 ;;
    3.4.34) STDCXX_MIN=15 ;;
    *) echo "unknown GLIBCXX_$GLIBCXX_MIN - extend the table in $0" >&2; exit 4 ;;
  esac
  DEPENDS="$DEPENDS, libstdc++6 (>= $STDCXX_MIN)"
fi
echo "floors for $ARCH: $DEPENDS"

cat > "$STAGE/root/DEBIAN/control" <<CONTROL
Package: $NAME
Version: $VERSION
Architecture: $ARCH
Maintainer: Tom Fanning M0LTE <tom@m0lte.uk>
Installed-Size: $INSTALLED_SIZE
Depends: $DEPENDS
Recommends: pdn-soundmodem
Section: hamradio
Priority: optional
Homepage: https://github.com/packet-net/pdn-mailcast
Description: Daily one-way HF broadcast of packet BBS bulletins (head end)
 Takes bulletins from the station's BBS as a forwarding partner and, once a
 day, broadcasts them through the station's own pdn-soundmodem: a transmit
 lease, a calibration tone, then RaptorQ-coded MS110D bursts that receivers
 rebuild into bulletins and hand to their own BBS.
 .
 Ships a systemd unit, enabled on install. It will not start until
 /etc/pdn-mailcast-headend/headend.json has the station's API key; see
 /usr/share/doc/pdn-mailcast-headend/headend.md.
 .
 AGPL-3.0.
CONTROL

cat > "$STAGE/root/DEBIAN/postinst" <<'POSTINST'
#!/bin/sh
set -e

EXAMPLE=/usr/share/pdn-mailcast-headend/headend.example.json
CONFIG=/etc/pdn-mailcast-headend/headend.json

case "$1" in
  configure)
    if ! getent passwd pdn-mailcast-headend >/dev/null; then
        adduser --system --no-create-home --group pdn-mailcast-headend
    fi
    install -d -m 0750 -o root -g pdn-mailcast-headend /etc/pdn-mailcast-headend
    if [ ! -e "$CONFIG" ] && [ -f "$EXAMPLE" ]; then
        # It will hold the station's API key and the BBS password, so not world-readable.
        install -m 0640 -o root -g pdn-mailcast-headend "$EXAMPLE" "$CONFIG"
        echo "pdn-mailcast-headend: seeded $CONFIG from the example."
    fi
    echo "pdn-mailcast-headend: set station.apiKey (and the BBS login) in $CONFIG, then"
    echo "                      systemctl restart pdn-mailcast-headend."
    ;;
esac

if [ "$1" = "configure" ] || [ "$1" = "abort-upgrade" ] || [ "$1" = "abort-deconfigure" ] || [ "$1" = "abort-remove" ]; then
    if command -v deb-systemd-helper >/dev/null; then
        deb-systemd-helper unmask 'pdn-mailcast-headend.service' >/dev/null || true
        if deb-systemd-helper --quiet was-enabled 'pdn-mailcast-headend.service'; then
            deb-systemd-helper enable 'pdn-mailcast-headend.service' >/dev/null || true
        else
            deb-systemd-helper update-state 'pdn-mailcast-headend.service' >/dev/null || true
        fi
    fi
    if [ -d /run/systemd/system ] && command -v systemctl >/dev/null; then
        systemctl --system daemon-reload >/dev/null || true
        if [ -n "${2:-}" ]; then _action=restart; else _action=start; fi
        if command -v deb-systemd-invoke >/dev/null; then
            deb-systemd-invoke "$_action" 'pdn-mailcast-headend.service' >/dev/null || true
        fi
    fi
fi

exit 0
POSTINST

cat > "$STAGE/root/DEBIAN/prerm" <<'PRERM'
#!/bin/sh
set -e
if [ -d /run/systemd/system ] && [ "$1" = "remove" ] && command -v deb-systemd-invoke >/dev/null; then
    deb-systemd-invoke stop 'pdn-mailcast-headend.service' >/dev/null || true
fi
exit 0
PRERM

cat > "$STAGE/root/DEBIAN/postrm" <<'POSTRM'
#!/bin/sh
set -e
if [ -d /run/systemd/system ] && command -v systemctl >/dev/null; then
    systemctl --system daemon-reload >/dev/null || true
fi
if [ "$1" = "remove" ] && command -v deb-systemd-helper >/dev/null; then
    deb-systemd-helper mask 'pdn-mailcast-headend.service' >/dev/null || true
fi
if [ "$1" = "purge" ]; then
    if command -v deb-systemd-helper >/dev/null; then
        deb-systemd-helper purge 'pdn-mailcast-headend.service' >/dev/null || true
        deb-systemd-helper unmask 'pdn-mailcast-headend.service' >/dev/null || true
    fi
    # Seeded by postinst rather than shipped, so dpkg does not remove it. The state under
    # /var/lib/pdn-mailcast-headend (bulletins and their ESIs) is kept, as with any operator data.
    rm -f /etc/pdn-mailcast-headend/headend.json
    rmdir --ignore-fail-on-non-empty /etc/pdn-mailcast-headend 2>/dev/null || true
    if getent passwd pdn-mailcast-headend >/dev/null; then
        deluser --system --quiet pdn-mailcast-headend >/dev/null 2>&1 || true
    fi
fi
exit 0
POSTRM

chmod 0755 "$STAGE/root/DEBIAN/postinst" "$STAGE/root/DEBIAN/prerm" "$STAGE/root/DEBIAN/postrm"
for s in postinst prerm postrm; do sh -n "$STAGE/root/DEBIAN/$s"; done

mkdir -p "$OUTDIR"
DEB="$OUTDIR/${NAME}_${VERSION}_${ARCH}.deb"
# xz, so Debian 11's dpkg can unpack it (dpkg-deb now defaults to zstd).
dpkg-deb --build --root-owner-group -Zxz "$STAGE/root" "$DEB"
echo "built: $DEB"

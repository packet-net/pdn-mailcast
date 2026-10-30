# LinFBB 7.0.11 + ve7fet AX.25 userland (ax25ipd, kissattach), built as in M0LTE/f6fbb-interop
# build/20-build-in-chroot.sh. Run with --network host --privileged: AF_AX25 only works in the
# init network namespace, and the kernel needs the ax25 module loaded on the host.
FROM debian:trixie
ENV DEBIAN_FRONTEND=noninteractive
RUN apt-get update && apt-get install -y --no-install-recommends build-essential autoconf automake libtool pkg-config m4 zlib1g-dev libncurses-dev git curl ca-certificates iproute2 procps python3 kmod && rm -rf /var/lib/apt/lists/*
WORKDIR /build
RUN git clone --depth 1 https://github.com/ve7fet/linuxax25.git
RUN curl -fSL --retry 3 -o fbb.tar.gz https://downloads.sourceforge.net/project/linfbb/fbb-7.0.11.tar.gz && tar xzf fbb.tar.gz
RUN for c in libax25 ax25tools ax25apps; do cd /build/linuxax25/$c && ./autogen.sh && ./configure --prefix=/usr --sysconfdir=/etc --localstatedir=/var CFLAGS="-g -O2 -fcommon" && make -j"$(nproc)" && make install || exit 1; done; ldconfig
RUN cd /build/fbb-7.0.11 && ./configure --disable-x-utils CFLAGS="-g -O2 -fcommon -Wno-error=implicit-function-declaration -Wno-error=implicit-int -Wno-error=incompatible-pointer-types -Wno-error=int-conversion -Wno-error=return-mismatch -Wno-error=format-security" && make -j"$(nproc)" && make install && make installconf && ldconfig

# openBCM 1.x ("old-school" port, oe5hpm/openBCM), 32-bit Linux build. Pin the commit with --build-arg REF=.
FROM debian:12 AS build
RUN dpkg --add-architecture i386 && apt-get update && apt-get install -y --no-install-recommends libcrypt-dev:i386 build-essential gcc-multilib g++-multilib git ca-certificates make && rm -rf /var/lib/apt/lists/*
ARG REF=HEAD
RUN git clone https://github.com/oe5hpm/openBCM /src && cd /src && git checkout $REF && git rev-parse HEAD > /COMMIT
WORKDIR /src
RUN make 2>&1 | tail -30; ls -la out-x86_64/ bcm 2>&1 | head

FROM debian:12-slim
RUN dpkg --add-architecture i386 && apt-get update && apt-get install -y --no-install-recommends libc6:i386 libstdc++6:i386 libcrypt1:i386 tmux procps && rm -rf /var/lib/apt/lists/*
COPY --from=build /src/out-x86_64/bcm /usr/local/bin/bcm
COPY --from=build /COMMIT /COMMIT

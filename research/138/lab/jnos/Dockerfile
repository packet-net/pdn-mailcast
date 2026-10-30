# JNOS 2.0q (VE4KLM) built from the Rhizomatica/jnos2 GitHub mirror. Pin the commit with --build-arg REF=.
FROM debian:12 AS build
RUN apt-get update && apt-get install -y --no-install-recommends build-essential libncurses-dev libssl-dev git ca-certificates make && rm -rf /var/lib/apt/lists/*
ARG REF=HEAD
RUN git clone https://github.com/Rhizomatica/jnos2 /src && cd /src && git checkout $REF && git rev-parse HEAD > /src/COMMIT
WORKDIR /src/2.0q
RUN make clean >/dev/null 2>&1 || true; find . -name '*.o' -delete; rm -f *.a
RUN ./configure && make -j8 2>&1 | tail -30; ls -la jnos
FROM debian:12-slim
RUN apt-get update && apt-get install -y --no-install-recommends libncurses6 libssl3 procps tmux iproute2 python3 tcpdump && rm -rf /var/lib/apt/lists/*
COPY --from=build /src/2.0q/jnos /usr/local/bin/jnos
COPY --from=build /src/COMMIT /COMMIT

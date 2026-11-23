# A real LinBPQ for the scenarios that share a radio with one: the binary from the image this
# repo's own tests use, with the libraries it was built against beside it.
FROM m0lte/linbpq@sha256:b01155258c50c0a3f039bc7e3db92e669af505fa95328470980d977d54dd6903 AS linbpq
RUN mkdir /bundle && cp /usr/local/bin/linbpq /bundle/ \
 && ldd /usr/local/bin/linbpq | awk '$3 ~ /^\// {print $3}' | grep -vE '/(libc|libm)\.so' | xargs -I{} cp -L {} /bundle/

# Bench image: the released packages from the packet-net apt repo, as a listener or
# head end operator installs them, plus Hamlib's dummy rig and Python for the simulated air.
FROM debian:13
RUN apt-get update && apt-get install -y --no-install-recommends ca-certificates curl gnupg \
 && curl -fsSL https://packet-net.github.io/apt/pubkey.asc | gpg --dearmor -o /usr/share/keyrings/packet-net.gpg \
 && echo "deb [signed-by=/usr/share/keyrings/packet-net.gpg] https://packet-net.github.io/apt ./" > /etc/apt/sources.list.d/packet-net.list \
 && apt-get update \
 && apt-get install -y --no-install-recommends pdn-soundmodem pdn-mailcast-headend pdn-mailcast-receiver \
      libhamlib-utils alsa-utils python3 python3-numpy python3-scipy procps iproute2 netcat-openbsd jq \
 && rm -rf /var/lib/apt/lists/*
COPY --from=linbpq /bundle /opt/linbpq
ENTRYPOINT ["/repo/tools/bench/run.sh"]

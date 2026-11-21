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
ENTRYPOINT ["/repo/tools/bench/run.sh"]

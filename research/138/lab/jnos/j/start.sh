#!/bin/sh
# runs inside the JNOS container before JNOS: once JNOS has created tun0, configure the kernel side and start the relay
(
  for i in $(seq 1 100); do ip link show tun0 >/dev/null 2>&1 && break; sleep 0.2; done
  ip addr add 10.77.0.1/24 dev tun0; ip link set tun0 up mtu 1500
  ip addr add 10.77.0.11/32 dev lo; ip addr add 10.77.0.12/32 dev lo
  python3 /jnos/relay.py >/jnos/relay.log 2>&1
) &
cd /jnos
exec tmux new-session -s j -x 200 -y 50 "jnos -d /jnos -f /jnos/autoexec.nos"

#!/bin/bash
# Starts FBB, then sets up the receiver's login through the sysop console, as docs/receiver.md's
# Linux FBB section tells a sysop to: the user Q0CAST, with the B (BBS) and M (modem and
# telnet) flags and a password.
set -e
cd /var/ax25/fbb

# On a first start xfbbd asks before creating each of its empty files; yes answers them.
yes Y | xfbbd &
fbb=$!

# The console's port, 3286, answers once xfbbd is ready.
until (exec 3<>/dev/tcp/127.0.0.1/3286) 2>/dev/null; do
    kill -0 "$fbb"
    sleep 0.5
done

# The sysop's first connection asks for a name, a city, a home BBS and a zip code. EU G0SYS,
# N (do not delete) and P then turn paging off for the sysop, so that messages read whole.
fbb-console Sysop Reading GB7FBB RG1 "EU G0SYS" N P ""
# EU makes the user (Y); B and M set its flags and W its password. An empty line ends the edit.
fbb-console "EU Q0CAST" Y B M "W mailcast12" ""
echo "mailcast: Q0CAST set up"

wait "$fbb"

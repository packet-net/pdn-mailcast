#!/bin/sh
# A receiver hook that only says when it ran.
echo "$(date -u +%H:%M:%S) hook $1 ran: MAILCAST_HOOK=$MAILCAST_HOOK MAILCAST_SLOT_UTC=$MAILCAST_SLOT_UTC MAILCAST_BEFORE_OK=${MAILCAST_BEFORE_OK:-}" >>/out/hooks.log

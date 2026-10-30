#!/bin/bash
# kick.sh - ask JNOS to poll GB7LAA (reverse forward, receive LAA's bulletins) and then forward to GB7LAB
docker exec lab138j-laj-1 tmux send-keys -t j "mbox ki Gb7laa" Enter
sleep 25
docker exec lab138j-laj-1 tmux send-keys -t j "mbox ki Gb7lab" Enter

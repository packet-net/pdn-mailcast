# Two minutes into the slot, hand the listener's packet modem one frame to send.
sleep $(( $1 + 120 - $(date -u +%s) ))
echo "$(date -u +%H:%M:%S) a KISS frame is handed to the listener's packet modem"
python3 - <<'PY'
import socket, time
def address(call, last):
    return bytes(b << 1 for b in call.ljust(6).encode()) + bytes([0x60 | (1 if last else 0)])
frame = address("BENCH", False) + address("G0ABC", True) + b"\x03\xf0" + b"queued during the mailcast window"
with socket.create_connection(("127.0.0.1", 8105)) as kiss:
    kiss.sendall(b"\xc0\x00" + frame + b"\xc0")
    time.sleep(1)
PY

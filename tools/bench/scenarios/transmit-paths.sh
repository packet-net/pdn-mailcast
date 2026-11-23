# Attempts to key the listener's radio around one slot ($1 is the slot's start):
#   75 s before: six long packet frames, so the transmitter is busy when the radio is due to move
#   90 s in:     the ARDOP modem is told to call a station
#   150 s in:    one more packet frame (in a slot that completes, the radio is back by then)
python3 - $1 <<'PY'
import socket, sys, time
slot = int(sys.argv[1])
def at(offset, what):
    time.sleep(max(0, slot + offset - time.time()))
    print(time.strftime("%H:%M:%S", time.gmtime()), what, flush=True)
def address(call, last):
    return bytes(b << 1 for b in call.ljust(6).encode()) + bytes([0x60 | (1 if last else 0)])
def frame(text):
    return b"\xc0\x00" + address("BENCH", False) + address("G0ABC", True) + b"\x03\xf0" + text + b"\xc0"
at(-75, "six 200 byte KISS frames are handed to the packet modem")
with socket.create_connection(("127.0.0.1", 8105)) as kiss:
    for n in range(6):
        kiss.sendall(frame(b"frame %d before the window " % n + b"x" * 170))
    time.sleep(1)
at(90, "the ARDOP modem is told: MYCALL G0ABC, ARQBW 500MAX, ARQCALL M0XYZ 5")
ardop = socket.create_connection(("127.0.0.1", 8515)); ardop.settimeout(0.5)
for command in (b"INITIALIZE", b"MYCALL G0ABC", b"ARQBW 500MAX", b"ARQCALL M0XYZ 5"):
    ardop.sendall(command + b"\r"); time.sleep(0.3)
def answers(seconds):
    end = time.time() + seconds
    while time.time() < end:
        try:
            data = ardop.recv(4096)
            if data:
                print(time.strftime("%H:%M:%S", time.gmtime()), "ARDOP says:", data.decode(errors="replace").replace("\r", " | "), flush=True)
        except socket.timeout:
            pass
answers(55)
at(150, "one more KISS frame is handed to the packet modem")
with socket.create_connection(("127.0.0.1", 8105)) as kiss:
    kiss.sendall(frame(b"queued during the mailcast window")); time.sleep(1)
answers(600)
PY

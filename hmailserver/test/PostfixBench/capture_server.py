#!/usr/bin/env python3
"""Minimal SMTP sink that records the exact bytes a client transmits.

Advertises PIPELINING/8BITMIME/SIZE like hMailServer does, accepts one
message per connection, and writes the raw DATA payload (everything between
the 354 and the terminating dot, dot INCLUDED) to a file per session, plus a
hex dump of the final 48 bytes - the part that answers "what does Postfix
actually put on the wire before the terminating dot".
"""
import socket
import sys
import threading

PORT = 8025
OUT_PREFIX = sys.argv[1] if len(sys.argv) > 1 else "/tmp/capture"

def serve_one(conn, index):
    conn.sendall(b"220 capture ESMTP\r\n")
    buf = b""
    in_data = False
    payload = b""

    def find_terminator():
        # The terminator is not necessarily the LAST thing in the buffer:
        # Postfix pipelines QUIT (or the next MAIL) into the same segment as
        # the terminating dot. Search rather than endswith, and hand whatever
        # follows back to the command loop. Most-specific spelling first.
        for spelling in (b"\r\n.\r\n", b"\n.\r\n", b"\n.\n"):
            idx = payload.find(spelling)
            if idx >= 0:
                return idx + len(spelling)
        return -1

    def finish_message(end):
        nonlocal in_data, buf, payload
        message = payload[:end]
        with open(f"{OUT_PREFIX}-{index}.bin", "wb") as f:
            f.write(message)
        tail = message[-48:]
        print(f"SESSION {index}: {len(message)} bytes, tail hex: {tail.hex()}", flush=True)
        conn.sendall(b"250 2.0.0 queued\r\n")
        buf = payload[end:]   # pipelined bytes after the dot are commands
        in_data = False

    while True:
        if in_data:
            # Check BEFORE waiting for more bytes: with PIPELINING the whole
            # message including the terminator can already be sitting in the
            # buffer from the segment that carried the DATA command - waiting
            # on recv() first is exactly the wait-for-bytes-that-already-came
            # deadlock this bench exists to prove was fixed in the server.
            end = find_terminator()
            if end >= 0:
                finish_message(end)
                continue
            chunk = conn.recv(65536)
            if not chunk:
                return payload
            payload += chunk
            print(f"S{index} data +{len(chunk)} = {len(payload)} tail={payload[-16:].hex()}", flush=True)
            continue

        chunk = conn.recv(65536)
        if not chunk:
            return payload
        buf += chunk
        while b"\r\n" in buf and not in_data:
            line, buf = buf.split(b"\r\n", 1)
            cmd = line.upper()
            print(f"S{index} cmd: {line[:60]!r}", flush=True)
            if cmd.startswith(b"EHLO"):
                conn.sendall(b"250-capture\r\n250-PIPELINING\r\n250-SIZE 20480000\r\n250-8BITMIME\r\n250 OK\r\n")
            elif cmd.startswith(b"MAIL") or cmd.startswith(b"RCPT"):
                conn.sendall(b"250 2.1.0 OK\r\n")
            elif cmd.startswith(b"DATA"):
                conn.sendall(b"354 OK, send.\r\n")
                in_data = True
                payload = buf  # anything pipelined after DATA is already payload
                buf = b""
            elif cmd.startswith(b"QUIT"):
                conn.sendall(b"221 bye\r\n")
                return payload
            else:
                conn.sendall(b"250 OK\r\n")

def main():
    srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    srv.bind(("127.0.0.1", PORT))
    srv.listen(5)
    print(f"capture listening on {PORT}", flush=True)
    index = 0

    def worker(conn, index):
        # One thread per connection: Postfix opens several sessions in
        # parallel, and a serial accept loop starves all but the first of
        # their greeting - which reads back as "lost connection while
        # receiving the initial server greeting" in the postfix log.
        try:
            serve_one(conn, index)
        except Exception as e:
            print(f"SESSION {index} error: {e}", flush=True)
        finally:
            conn.close()

    while True:
        conn, _ = srv.accept()
        index += 1
        threading.Thread(target=worker, args=(conn, index), daemon=True).start()

if __name__ == "__main__":
    main()

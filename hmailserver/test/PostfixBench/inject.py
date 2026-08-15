#!/usr/bin/env python3
"""Injects test messages into the local Postfix (port 2525) with precisely
controlled line endings, the way a misbehaving internet sender would.

Cases:
  1 crlf     - fully CRLF-correct body
  2 barelf   - final body line ends with bare LF before the terminator
               (the exact shape from discussion #18: ...text\n.\r\n)
  3 midlf    - bare LF in the middle of the body, proper CRLF terminator
  4 params   - CRLF-correct, pipelined MAIL/RCPT/DATA in one segment,
               with SIZE and BODY=8BITMIME parameters (katip's EHLO shape)
"""
import socket
import sys
import time

HOST, PORT = "127.0.0.2", 2525
RCPT = "test@pipelining.example.test"

def build(case):
    headers = (b"From: bench@example.org\r\n"
               b"To: " + RCPT.encode() + b"\r\n"
               b"Subject: bench case " + case.encode() + b"\r\n"
               b"\r\n")
    if case == "crlf":
        body = b"line one\r\nline two\r\nlast line\r\n"
    elif case == "barelf":
        body = b"line one\r\nline two\r\nlast line ends bare\n"
    elif case == "midlf":
        body = b"line one\nbare middle\r\nlast line\r\n"
    else:  # params
        body = b"pipelined message body\r\n" * 40
    return headers + body

def run(case):
    msg = build(case)
    s = socket.create_connection((HOST, PORT), timeout=30)
    r = s.recv(4096)
    transcript = [("<", r)]

    def send(b):
        transcript.append((">", b))
        s.sendall(b)

    def recv():
        r = s.recv(4096)
        transcript.append(("<", r))
        return r

    send(b"EHLO bench.example.org\r\n"); recv()

    size = len(msg)
    if case == "params":
        # Pipelined: everything up to and including DATA in one segment.
        send(b"MAIL FROM:<bench@example.org> SIZE=" + str(size).encode() +
             b" BODY=8BITMIME\r\nRCPT TO:<" + RCPT.encode() + b">\r\nDATA\r\n")
        recv()  # 250/250/354 possibly split
        time.sleep(0.3)
    else:
        send(b"MAIL FROM:<bench@example.org>\r\n"); recv()
        send(b"RCPT TO:<" + RCPT.encode() + b">\r\n"); recv()
        send(b"DATA\r\n"); recv()

    # The message. For barelf the final line has NO \r before its \n, so the
    # terminator on the wire is ...bare\n.\r\n - byte-for-byte the #18 shape.
    send(msg + b".\r\n")
    final = recv()
    send(b"QUIT\r\n")
    try:
        recv()
    except Exception:
        pass
    s.close()

    ok = b"250" in final
    print(f"CASE {case}: postfix accepted={ok}")
    for d, b in transcript:
        printable = b.decode("latin-1").replace("\r", "\\r").replace("\n", "\\n")
        if len(printable) > 160:
            printable = printable[:80] + " ... " + printable[-60:]
        print(f"  {d} {printable}")
    return ok

if __name__ == "__main__":
    cases = sys.argv[1:] or ["crlf", "barelf", "midlf", "params"]
    results = {c: run(c) for c in cases}
    print("SUMMARY:", results)

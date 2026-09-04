#!/usr/bin/env python3
# Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
# SPDX-License-Identifier: AGPL-3.0-or-later

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
VALID_CASES = ("crlf", "barelf", "midlf", "params")

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
    def recv_smtp_reply():
        data = b""
        code = None
        while True:
            chunk = s.recv(4096)
            if not chunk:
                break
            data += chunk

            lines = data.split(b"\r\n")
            complete_lines = lines[:-1]
            if not complete_lines:
                continue

            for line in complete_lines:
                if len(line) >= 4 and line[:3].isdigit():
                    if code is None:
                        code = line[:3]
                    if line[:3] == code and line[3:4] == b" ":
                        return data

            if code is None and data.endswith(b"\r\n"):
                return data
        return data

    r = recv_smtp_reply()
    transcript = [("<", r)]

    def send(b):
        transcript.append((">", b))
        s.sendall(b)

    def recv():
        r = recv_smtp_reply()
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
    except Exception as quit_error:
        # The server may close the socket without answering QUIT; that is not
        # part of what this case measures, so it is noted rather than fatal.
        print(f"CASE {case}: no reply to QUIT ({quit_error})")
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
    cases = sys.argv[1:] or list(VALID_CASES)
    invalid = [c for c in cases if c not in VALID_CASES]
    if invalid:
        print(
            "ERROR: unsupported case name(s): "
            + ", ".join(invalid)
            + ". Supported cases: "
            + ", ".join(VALID_CASES),
            file=sys.stderr,
        )
        sys.exit(2)
    results = {c: run(c) for c in cases}
    print("SUMMARY:", results)

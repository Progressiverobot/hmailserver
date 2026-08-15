#!/bin/bash
# Configures the WSL Postfix as a PMG-shaped relay for the bench. Idempotent.
# Run inside WSL as root, from this directory. See README.md.
set -e

# Postfix must not think 127.0.0.1 is its own address, or it refuses the
# delivery as a loop. Does not survive a WSL restart, hence re-run here.
ip addr add 127.0.0.2/8 dev lo 2>/dev/null || true

# The smtpd listener moves to 127.0.0.2:2525: port 25 would collide with
# hMailServer in mirrored networking's shared port space.
sed -i 's/^smtp      inet/2525      inet/' /etc/postfix/master.cf 2>/dev/null || true

postconf -e "myhostname = pmg-sim.lab"
postconf -e "mydestination ="
postconf -e "relay_domains = pipelining.example.test"
postconf -e "transport_maps = hash:/etc/postfix/transport"
postconf -e "inet_interfaces = 127.0.0.2"
postconf -e "mynetworks = 127.0.0.0/8"
postconf -e "smtpd_relay_restrictions = permit_mynetworks, reject_unauth_destination"
# An older PMG accepts bare-LF input; modern Postfix normalizes it to CRLF on
# output either way (measured), but the acceptance keeps the injection honest.
postconf -e "smtpd_forbid_bare_newline = no"
# Short backoffs: the WSL clock trap (README) otherwise turns every deferred
# retry into a multi-minute wait.
postconf -e "minimal_backoff_time = 5s"
postconf -e "maximal_backoff_time = 10s"
postconf -e "queue_run_delay = 5s"

echo "pipelining.example.test smtp:[127.0.0.1]:25" > /etc/postfix/transport
postmap /etc/postfix/transport

systemctl restart postfix
sleep 2
ss -ltn | grep -q 2525 && echo "postfix listening on 127.0.0.2:2525" || {
   echo "postfix failed to bind 2525" >&2
   exit 1
}

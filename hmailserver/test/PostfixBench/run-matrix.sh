#!/bin/bash
# Full matrix: inject all four cases, then flush patiently until the WSL
# clock-skew window passes (qmgr skips queue files stamped "in the future"),
# and report exactly what Postfix transmitted.
BENCH="$(dirname "$0")"
postsuper -d ALL > /dev/null 2>&1
rm -f /tmp/capture-*.bin

python3 "$BENCH/inject.py" 2>&1 | grep -E "CASE|SUMMARY"

for i in $(seq 1 18); do
   sleep 10
   # The WSL VM's clocks disagree by minutes and oscillate, and qmgr skips any
   # queue file whose mtime it considers future. Backdating the queue files
   # makes them due on every clock in the building.
   find /var/spool/postfix/incoming /var/spool/postfix/active /var/spool/postfix/deferred \
        -type f -exec touch -d "10 minutes ago" {} + 2>/dev/null
   postqueue -f 2>/dev/null
   sleep 5
   sent=$(grep -c 'status=sent' /var/log/mail.log)
   sessions=$(grep -c SESSION /tmp/capture.log)
   echo "t+$((i*15))s sessions=$sessions sent=$sent"
   [ "$sent" -ge 4 ] && break
done

echo "=== capture sessions (tail hex shows the wire bytes before the dot) ==="
grep SESSION /tmp/capture.log
echo "=== delivery results ==="
grep 'status=sent' /var/log/mail.log | tail -6
mailq | tail -1

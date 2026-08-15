#!/bin/bash
# Stage 2: postfix (real, PIPELINING) -> hMailServer at [127.0.0.1]:25.
# Counts only NEW deliveries after this run's marker.
BENCH="$(dirname "$0")"
postsuper -d ALL > /dev/null 2>&1
MARK=$(wc -l < /var/log/mail.log)

python3 "$BENCH/inject.py" 2>&1 | grep -E "CASE|SUMMARY"

for i in $(seq 1 18); do
   sleep 10
   find /var/spool/postfix/incoming /var/spool/postfix/active /var/spool/postfix/deferred \
        -type f -exec touch -d "10 minutes ago" {} + 2>/dev/null
   postqueue -f 2>/dev/null
   sleep 5
   sent=$(tail -n +$MARK /var/log/mail.log | grep -c ']:25.*status=sent')
   other=$(tail -n +$MARK /var/log/mail.log | grep -cE 'status=(bounced|deferred)')
   echo "t+$((i*15))s sent=$sent bounced_or_deferred=$other"
   [ "$sent" -ge 4 ] && break
   [ "$other" -ge 4 ] && break
done

echo "=== delivery lines ==="
tail -n +$MARK /var/log/mail.log | grep -E 'status=' | tail -8
mailq | tail -1

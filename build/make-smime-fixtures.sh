#!/usr/bin/env bash
# S/MIME fixtures made with OpenSSL, the independent implementation the page's
# module is checked against: a CA, an RSA leaf and an EC leaf for
# alice@example.test (emailAddress in the subject and an rfc822Name SAN), the
# keys as PKCS#8 (clear, PBES2-encrypted) and as a modern PKCS#12, then
# messages OpenSSL signed (detached and opaque) and encrypted for each leaf.
set -euo pipefail
out="$1"
mkdir -p "$out"
cd "$out"
export MSYS_NO_PATHCONV=1

openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:2048 -out ca-key.pem 2>/dev/null
openssl req -x509 -new -key ca-key.pem -days 3650 -subj '/CN=Fixture CA/O=hMailServer tests' -out ca.pem 2>/dev/null

cat > leaf.cnf <<'EOF'
[req]
distinguished_name = dn
[dn]
[ext]
basicConstraints = CA:FALSE
keyUsage = digitalSignature, keyEncipherment
extendedKeyUsage = emailProtection
subjectAltName = email:alice@example.test
[ext_ec]
basicConstraints = CA:FALSE
keyUsage = digitalSignature
extendedKeyUsage = emailProtection
subjectAltName = email:erin@example.test
EOF

openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:2048 -out alice-key.pem 2>/dev/null
openssl req -new -key alice-key.pem -subj '/CN=Alice Example/emailAddress=alice@example.test' -out alice.csr 2>/dev/null
openssl x509 -req -in alice.csr -CA ca.pem -CAkey ca-key.pem -CAcreateserial -days 3650 -extfile leaf.cnf -extensions ext -out alice.pem 2>/dev/null
openssl pkcs8 -topk8 -in alice-key.pem -out alice-key-encrypted.pem -v2 aes-256-cbc -v2prf hmacWithSHA256 -passout pass:fixture-pass 2>/dev/null
openssl pkcs12 -export -in alice.pem -inkey alice-key.pem -certfile ca.pem -name 'Alice Example' -passout pass:fixture-pass -out alice.p12 2>/dev/null
openssl pkcs12 -export -in alice.pem -inkey alice-key.pem -certfile ca.pem -name 'Alice Example' -passout pass:fixture-pass -legacy -out alice-legacy.p12 2>/dev/null || true

openssl genpkey -algorithm EC -pkeyopt ec_paramgen_curve:P-256 -out erin-key.pem 2>/dev/null
openssl req -new -key erin-key.pem -subj '/CN=Erin Example/emailAddress=erin@example.test' -out erin.csr 2>/dev/null
openssl x509 -req -in erin.csr -CA ca.pem -CAkey ca-key.pem -CAcreateserial -days 3650 -extfile leaf.cnf -extensions ext_ec -out erin.pem 2>/dev/null

# LF endings: OpenSSL writes the canonical CRLF form into what it signs.
printf 'Content-Type: text/plain; charset=utf-8\nContent-Transfer-Encoding: 8bit\n\nA signed message from the fixtures.\nSecond line, with caf\xc3\xa9.\n' > body.txt
openssl cms -sign -in body.txt -signer alice.pem -inkey alice-key.pem -certfile ca.pem -md sha256 -out signed-detached.eml -from alice@example.test -to bob@example.test -subject 'Signed, detached' 2>/dev/null
openssl cms -sign -in body.txt -signer alice.pem -inkey alice-key.pem -certfile ca.pem -md sha256 -nodetach -out signed-opaque.eml -from alice@example.test -to bob@example.test -subject 'Signed, opaque' 2>/dev/null
openssl cms -sign -in body.txt -signer erin.pem -inkey erin-key.pem -certfile ca.pem -md sha256 -out signed-ec.eml -from erin@example.test -to bob@example.test -subject 'Signed by an EC key' 2>/dev/null
openssl cms -encrypt -in body.txt -aes256 -out encrypted.eml -from bob@example.test -to alice@example.test -subject 'Encrypted for Alice' alice.pem 2>/dev/null
openssl cms -encrypt -in body.txt -aes128 -out encrypted-aes128.eml -from bob@example.test -to alice@example.test -subject 'Encrypted with AES-128' alice.pem 2>/dev/null
openssl cms -sign -in body.txt -signer alice.pem -inkey alice-key.pem -certfile ca.pem -md sha256 -out signed-then.eml 2>/dev/null
openssl cms -encrypt -in signed-then.eml -aes256 -out signed-encrypted.eml -from alice@example.test -to alice@example.test -subject 'Signed then encrypted' alice.pem 2>/dev/null
# On Windows OpenSSL writes these in text mode, which turns the CRLF it
# canonicalised into CR CR LF; the messages are put back to what it signed.
for f in signed-detached.eml signed-opaque.eml signed-ec.eml encrypted.eml encrypted-aes128.eml signed-then.eml signed-encrypted.eml; do
  perl -pi -e 's/\r\r\n/\r\n/g' "$f"
done
# A message whose body was changed after signing: the signature must fail.
sed 's/A signed message/A changed message/' signed-detached.eml > tampered.eml

# The certificates as DER too, and the leaf's serial and fingerprint for the checks.
openssl x509 -in alice.pem -outform DER -out alice.der
openssl x509 -in ca.pem -outform DER -out ca.der
openssl x509 -in erin.pem -outform DER -out erin.der
openssl x509 -in alice.pem -noout -serial -fingerprint -sha256 > alice.facts
openssl pkcs8 -topk8 -nocrypt -in alice-key.pem -outform DER -out alice-key.p8
ls -la

// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// Runs the page's S/MIME module (PortalSmime.js) in Node against fixtures
// OpenSSL made - certificates, keys in three forms, messages signed detached,
// opaque and by an EC key, messages encrypted with AES-256 and AES-128, one
// signed then encrypted, one tampered with - and hands what the module signs
// and encrypts to OpenSSL to verify and decrypt, so both directions are
// checked against an implementation that is not this one.
//
// Usage: node build/check-portal-smime.js [module.js] [fixtures dir]
// Needs Node 20 or later (Web Crypto) and openssl on the PATH.
'use strict';
const fs = require('fs');
const os = require('os');
const path = require('path');
const { execFileSync } = require('child_process');

const modulePath = process.argv[2] || path.join(__dirname, '..', 'hmailserver', 'source', 'Server', 'Common', 'Util', 'PortalSmime.js');
const smime = require(path.resolve(modulePath));
// The fixtures are made fresh by OpenSSL (build/make-smime-fixtures.sh) unless a
// directory holding them is given - they hold private keys, so none is committed.
let fixtures = process.argv[3] || '';
let madeFixtures = false;
if (!fixtures) {
  fixtures = fs.mkdtempSync(path.join(os.tmpdir(), 'hm-smime-fixtures-'));
  execFileSync('bash', [path.join(__dirname, 'make-smime-fixtures.sh'), fixtures], { stdio: ['ignore', 'ignore', 'inherit'] });
  madeFixtures = true;
}

let failures = 0;
let checks = 0;
function check(condition, what) {
  checks++;
  if (condition) { console.log('  ok   ' + what); } else { failures++; console.log('  FAIL ' + what); }
}
function read(name) { return new Uint8Array(fs.readFileSync(path.join(fixtures, name))); }
function text(bytes) { return Buffer.from(bytes).toString('utf8'); }
const PASSWORD = 'fixture-pass';
// body.txt is written with LF endings; what OpenSSL signed and encrypted is its canonical CRLF form.
const BODY_CONTENT = 'A signed message from the fixtures.\r\nSecond line, with caf\u00e9.\r\n';

function openssl(args, input) {
  try {
    return { ok: true, out: execFileSync('openssl', args, { input: input, stdio: ['pipe', 'pipe', 'pipe'] }).toString('utf8') };
  } catch (e) {
    return { ok: false, out: (e.stdout ? e.stdout.toString() : '') + (e.stderr ? e.stderr.toString() : '') };
  }
}

(async function () {
  console.log('== certificates');
  const alice = smime.parseCertificate(read('alice.der'));
  const facts = text(read('alice.facts'));
  const serial = (facts.match(/serial=([0-9A-F]+)/i) || [])[1].toLowerCase();
  const fp = (facts.match(/Fingerprint=([0-9A-F:]+)/i) || [])[1].replace(/:/g, '').toLowerCase();
  check(alice.emails.indexOf('alice@example.test') >= 0, 'the leaf names alice@example.test (subject emailAddress and SAN): ' + alice.emails.join(', '));
  check(alice.serialHex === serial, 'the serial matches OpenSSL: ' + alice.serialHex);
  check((await smime.fingerprint(alice)) === fp, 'the SHA-256 fingerprint matches OpenSSL');
  check(alice.keyType === 'rsa' && alice.rsa.bytes === 256, 'an RSA-2048 key');
  check(alice.subject.map.CN === 'Alice Example' && alice.issuer.map.CN === 'Fixture CA', 'subject and issuer read: ' + alice.subject.text + ' / ' + alice.issuer.text);
  check(!alice.isCA && alice.keyUsage && alice.keyUsage.digitalSignature && alice.keyUsage.keyEncipherment, 'not a CA; signs and enciphers');
  check(alice.extendedKeyUsage.indexOf('1.3.6.1.5.5.7.3.4') >= 0, 'extended key usage emailProtection');
  check(alice.notAfter.getTime() > Date.now(), 'validity read: until ' + alice.notAfter.toISOString().slice(0, 10));
  const ca = smime.parseCertificate(read('ca.der'));
  check(ca.isCA, 'the CA is a CA');
  const erin = smime.parseCertificate(read('erin.der'));
  check(erin.keyType === 'ec' && erin.curve === 'P-256', 'the EC leaf is P-256');

  console.log('== keys');
  const clear = await smime.importAny(read('alice-key.pem'), '');
  check(clear.keys.length === 1 && clear.keys[0].key.type === 'rsa', 'PKCS#8 PEM imported');
  check(smime.keyMatchesCertificate(clear.keys[0].key, alice), 'the key matches the certificate');
  const encrypted = await smime.importAny(read('alice-key-encrypted.pem'), PASSWORD);
  check(encrypted.keys.length === 1 && smime.keyMatchesCertificate(encrypted.keys[0].key, alice), 'PBES2-encrypted PKCS#8 PEM imported with the password');
  let wrong = '';
  try { await smime.importAny(read('alice-key-encrypted.pem'), 'nope'); } catch (e) { wrong = e.message; }
  check(wrong === 'wrong password', 'a wrong password is named: ' + wrong);
  const p12 = await smime.importAny(read('alice.p12'), PASSWORD);
  check(p12.keys.length === 1 && p12.keys[0].certificate && p12.keys[0].certificate.emails[0] === 'alice@example.test', 'PKCS#12 imported: the key is paired with its certificate');
  check(p12.certificates.length === 2, 'PKCS#12 carries the chain: ' + p12.certificates.map(function (c) { return c.name; }).join(', '));
  if (fs.existsSync(path.join(fixtures, 'alice-legacy.p12'))) {
    let legacy = '';
    try { await smime.importAny(read('alice-legacy.p12'), PASSWORD); } catch (e) { legacy = e.message; }
    check(/legacy/.test(legacy), 'a legacy (3DES/RC2) PKCS#12 is refused with advice: ' + legacy);
  }
  const der8 = await smime.importAny(read('alice-key.p8'), '');
  check(der8.keys.length === 1 && smime.keyMatchesCertificate(der8.keys[0].key, alice), 'DER PKCS#8 imported');
  const pemCert = await smime.importAny(read('alice.pem'), '');
  check(pemCert.certificates.length === 1 && pemCert.keys.length === 0, 'a PEM certificate alone imports');
  const erinKey = (await smime.importAny(read('erin-key.pem'), '')).keys[0].key;
  check(erinKey.type === 'ec' && smime.keyMatchesCertificate(erinKey, erin), 'the EC key matches the EC certificate');

  console.log('== wrapping');
  const wrapped = await smime.wrapPrivateKey(clear.keys[0].key.pkcs8, 'account-password');
  const unwrapped = await smime.unwrapPrivateKey(wrapped, 'account-password');
  check(Buffer.compare(Buffer.from(unwrapped), Buffer.from(clear.keys[0].key.pkcs8)) === 0, 'a wrapped key unwraps to the same PKCS#8');
  let bad = '';
  try { await smime.unwrapPrivateKey(wrapped, 'other'); } catch (e) { bad = e.message; }
  check(bad === 'wrong password', 'unwrapping with another password fails: ' + bad);

  console.log('== verifying what OpenSSL signed');
  const own = [{ key: clear.keys[0].key, certificate: alice }];
  const detached = await smime.readMessage(read('signed-detached.eml'), own);
  check(detached.signed && detached.signed.valid, 'detached signature valid: ' + (detached.signed && detached.signed.error));
  check(detached.signed && detached.signed.signer && detached.signed.signer.emails[0] === 'alice@example.test', 'signed by alice');
  check(detached.signed && detached.signed.certificates.length === 2, 'the message carries the leaf and the CA');
  check(detached.content && detached.content.text === BODY_CONTENT, 'the text is the fixture body: ' + JSON.stringify(detached.content && detached.content.text.slice(0, 40)));
  check(detached.signed && detached.signed.signingTime instanceof Date, 'the signing time is read');
  const opaque = await smime.readMessage(read('signed-opaque.eml'), own);
  check(opaque.signed && opaque.signed.valid && opaque.content.text === BODY_CONTENT, 'opaque signature valid and its content read');
  const ec = await smime.readMessage(read('signed-ec.eml'), own);
  check(ec.signed && ec.signed.valid && ec.signed.signer.emails[0] === 'erin@example.test', 'an ECDSA P-256 signature verifies: ' + (ec.signed && ec.signed.error));
  const tampered = await smime.readMessage(read('tampered.eml'), own);
  check(tampered.signed && !tampered.signed.valid && /changed/.test(tampered.signed.error), 'a changed body fails: ' + (tampered.signed && tampered.signed.error));

  console.log('== decrypting what OpenSSL encrypted');
  const enc = await smime.readMessage(read('encrypted.eml'), own);
  check(enc.encrypted && enc.encrypted.ok && enc.content.text === BODY_CONTENT, 'AES-256-CBC + RSAES-PKCS1-v1_5 decrypted: ' + (enc.encrypted && enc.encrypted.error));
  const enc128 = await smime.readMessage(read('encrypted-aes128.eml'), own);
  check(enc128.encrypted && enc128.encrypted.ok && enc128.content.text === BODY_CONTENT, 'AES-128-CBC decrypted');
  const both = await smime.readMessage(read('signed-encrypted.eml'), own);
  check(both.encrypted && both.encrypted.ok && both.signed && both.signed.valid && both.content.text === BODY_CONTENT, 'signed then encrypted: decrypted, then the signature verified; layers ' + both.layers.join(' > '));
  const notMine = await smime.readMessage(read('encrypted.eml'), [{ key: erinKey, certificate: erin }]);
  check(notMine.encrypted && !notMine.encrypted.ok && /not encrypted for/.test(notMine.encrypted.error), 'another key cannot open it: ' + (notMine.encrypted && notMine.encrypted.error));

  console.log('== OpenSSL verifying and decrypting what the module made');
  const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'hm-smime-'));
  const signer = { key: clear.keys[0].key, certificate: alice, chain: [ca] };
  const signedText = await smime.composeMessage({ headers: { from: 'alice@example.test', to: 'bob@example.test', subject: 'Signed on the page, with café' }, text: 'Hello Bob,\r\n\r\nSigned in the browser.\r\n', html: '<p>Hello <b>Bob</b>,</p><p>Signed in the browser.</p>', attachments: [{ name: 'note.txt', type: 'text/plain', data: Buffer.from('an attachment').toString('base64') }], sign: signer });
  fs.writeFileSync(path.join(tmp, 'page-signed.eml'), signedText, 'binary');
  let r = openssl(['cms', '-verify', '-in', path.join(tmp, 'page-signed.eml'), '-CAfile', path.join(fixtures, 'ca.pem'), '-out', path.join(tmp, 'page-signed-content.txt')]);
  check(r.ok, 'openssl cms -verify accepts the page\'s detached signature' + (r.ok ? '' : ': ' + r.out.trim()));
  const verifiedContent = r.ok ? fs.readFileSync(path.join(tmp, 'page-signed-content.txt'), 'binary') : '';
  check(/Signed in the browser/.test(verifiedContent) && /multipart\/mixed/.test(verifiedContent) && /name="note.txt"/.test(verifiedContent), 'the signed content is the text, the HTML and the attachment');
  const back = await smime.readMessage(smime.latin1Bytes(signedText), own);
  check(back.signed && back.signed.valid && back.content.text === 'Hello Bob,\r\n\r\nSigned in the browser.\r\n' && back.content.attachments.length === 1 && text(back.content.attachments[0].bytes) === 'an attachment', 'the module reads its own signed message back');
  const ecSigned = await smime.composeMessage({ headers: { from: 'erin@example.test', to: 'bob@example.test', subject: 'EC' }, text: 'EC signed', sign: { key: erinKey, certificate: erin, chain: [ca] } });
  fs.writeFileSync(path.join(tmp, 'page-ec.eml'), ecSigned, 'binary');
  r = openssl(['cms', '-verify', '-in', path.join(tmp, 'page-ec.eml'), '-CAfile', path.join(fixtures, 'ca.pem'), '-out', path.join(tmp, 'page-ec-content.txt')]);
  check(r.ok, 'openssl cms -verify accepts the page\'s ECDSA signature' + (r.ok ? '' : ': ' + r.out.trim()));

  const encryptedText = await smime.composeMessage({ headers: { from: 'bob@example.test', to: 'alice@example.test', subject: 'Encrypted on the page' }, text: 'For Alice only.\r\n', encrypt: [alice] });
  fs.writeFileSync(path.join(tmp, 'page-encrypted.eml'), encryptedText, 'binary');
  r = openssl(['cms', '-decrypt', '-in', path.join(tmp, 'page-encrypted.eml'), '-recip', path.join(fixtures, 'alice.pem'), '-inkey', path.join(fixtures, 'alice-key.pem'), '-out', path.join(tmp, 'page-decrypted.txt')]);
  check(r.ok, 'openssl cms -decrypt opens the page\'s encrypted message' + (r.ok ? '' : ': ' + r.out.trim()));
  check(r.ok && /For Alice only/.test(fs.readFileSync(path.join(tmp, 'page-decrypted.txt'), 'binary')), 'and finds the text');
  const roundTrip = await smime.readMessage(smime.latin1Bytes(encryptedText), own);
  check(roundTrip.encrypted && roundTrip.encrypted.ok && roundTrip.content.text === 'For Alice only.\r\n', 'the module decrypts its own message');

  const bothText = await smime.composeMessage({ headers: { from: 'alice@example.test', to: 'alice@example.test', subject: 'Both' }, text: 'Signed and encrypted.\r\n', sign: signer, encrypt: [alice] });
  fs.writeFileSync(path.join(tmp, 'page-both.eml'), bothText, 'binary');
  r = openssl(['cms', '-decrypt', '-in', path.join(tmp, 'page-both.eml'), '-recip', path.join(fixtures, 'alice.pem'), '-inkey', path.join(fixtures, 'alice-key.pem'), '-out', path.join(tmp, 'page-both-inner.eml')]);
  let r2 = r.ok ? openssl(['cms', '-verify', '-in', path.join(tmp, 'page-both-inner.eml'), '-CAfile', path.join(fixtures, 'ca.pem'), '-out', path.join(tmp, 'page-both-content.txt')]) : r;
  check(r.ok && r2.ok && /Signed and encrypted/.test(fs.readFileSync(path.join(tmp, 'page-both-content.txt'), 'binary')), 'signed then encrypted on the page: OpenSSL decrypts, then verifies' + (r2.ok ? '' : ': ' + r2.out.trim()));
  const bothBack = await smime.readMessage(smime.latin1Bytes(bothText), own);
  check(bothBack.layers.join('>') === 'encrypted>signed' && bothBack.signed.valid && bothBack.encrypted.ok, 'and the module reads it back: ' + bothBack.layers.join(' > '));

  console.log('== MIME details');
  const qp = smime.qpEncode(smime.utf8('café = ok \r\nline two'));
  check(qp === 'caf=C3=A9 =3D ok=20\r\nline two', 'quoted-printable: ' + JSON.stringify(qp));
  check(smime.decodeHeaderValue('=?utf-8?B?Y2Fmw6k=?= =?utf-8?Q?_ok?=') === 'café ok', 'RFC 2047 words decode');
  const ct = smime.parseMime(smime.utf8('Content-Type: text/plain; charset="utf-8"; name*=utf-8\'\'r%C3%A9sum%C3%A9.txt\r\n\r\nx'));
  check(ct.contentType.params.name === 'résumé.txt', 'RFC 2231 names decode: ' + ct.contentType.params.name);

  fs.rmSync(tmp, { recursive: true, force: true });
  if (madeFixtures) { fs.rmSync(fixtures, { recursive: true, force: true }); }
  console.log(checks + ' checks, ' + failures + ' failures');
  process.exit(failures ? 1 : 0);
})().catch(function (e) { console.log('ERROR ' + (e && e.stack || e)); process.exit(2); });

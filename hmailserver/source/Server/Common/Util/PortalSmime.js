// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// S/MIME for the webmail page, in the browser, on the Web Crypto API and
// nothing else: DER, X.509, PKCS#8 and PKCS#12 (PBES2), CMS SignedData and
// EnvelopedData (RFC 5652, RFC 8551), and the MIME on either side of them.
//
// Served as /portal-smime.js and loaded by the page; also loadable in Node
// (module.exports), which is how build/check-portal-smime.js runs it against
// messages OpenSSL made and hands what it made to OpenSSL.
//
// Where the Web Crypto API stops, this file does the arithmetic itself:
// RSAES-PKCS1-v1_5, the key transport nearly every S/MIME client uses, is not
// in Web Crypto, so the recipient's content key is decrypted with the private
// key's CRT parameters and BigInt, and encrypted for a recipient with the
// public exponent the same way. RSA-OAEP, when a sender used it, goes through
// Web Crypto. Not in this release, and said so where it is met: 3DES and RC2
// (the legacy PKCS#12 and des-EDE3-CBC content), and EC key agreement for
// encryption - an EC certificate signs and verifies, RSA encrypts.
(function (root, factory) {
  if (typeof module === 'object' && module.exports) { module.exports = factory(); }
  else { root.hmSmime = factory(); }
}(typeof self !== 'undefined' ? self : this, function () {
  'use strict';

  const cryptoApi = (typeof crypto !== 'undefined' && crypto.subtle) ? crypto : (typeof globalThis !== 'undefined' ? globalThis.crypto : null);
  const subtle = cryptoApi && cryptoApi.subtle;
  const textEncoder = new TextEncoder();

  // ---- bytes ---------------------------------------------------------------

  function utf8(text) { return textEncoder.encode(text); }

  function concat(list) {
    let total = 0;
    for (const part of list) { total += part.length; }
    const out = new Uint8Array(total);
    let at = 0;
    for (const part of list) { out.set(part, at); at += part.length; }
    return out;
  }

  function equalBytes(a, b) {
    if (!a || !b || a.length !== b.length) { return false; }
    let diff = 0;
    for (let i = 0; i < a.length; i++) { diff |= a[i] ^ b[i]; }
    return diff === 0;
  }

  function hex(bytes) {
    let s = '';
    for (let i = 0; i < bytes.length; i++) { s += (bytes[i] < 16 ? '0' : '') + bytes[i].toString(16); }
    return s;
  }

  function fromHex(text) {
    const out = new Uint8Array(text.length >> 1);
    for (let i = 0; i < out.length; i++) { out[i] = parseInt(text.substr(i * 2, 2), 16); }
    return out;
  }

  function binaryString(bytes) {
    let s = '';
    for (let i = 0; i < bytes.length; i += 0x8000) { s += String.fromCharCode.apply(null, bytes.subarray(i, i + 0x8000)); }
    return s;
  }

  function latin1Bytes(text) {
    const out = new Uint8Array(text.length);
    for (let i = 0; i < text.length; i++) { out[i] = text.charCodeAt(i) & 0xff; }
    return out;
  }

  function b64encode(bytes) { return btoa(binaryString(bytes)); }

  function b64decode(text) {
    const clean = String(text).replace(/[^A-Za-z0-9+/=]/g, '');
    const s = atob(clean);
    const out = new Uint8Array(s.length);
    for (let i = 0; i < s.length; i++) { out[i] = s.charCodeAt(i); }
    return out;
  }

  function b64url(bytes) { return b64encode(bytes).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, ''); }
  function b64urlDecode(text) { return b64decode(String(text).replace(/-/g, '+').replace(/_/g, '/')); }

  function lines76(text) { return text.replace(/(.{76})/g, '$1\r\n').replace(/\r\n$/, ''); }

  function randomBytes(length) { const out = new Uint8Array(length); cryptoApi.getRandomValues(out); return out; }

  // PEM: every block in the text, with its label.
  function pemBlocks(text) {
    const blocks = [];
    const re = /-----BEGIN ([^-]+)-----([\s\S]*?)-----END \1-----/g;
    let m;
    while ((m = re.exec(text)) !== null) { blocks.push({ label: m[1].trim(), bytes: b64decode(m[2]) }); }
    return blocks;
  }

  function looksLikePem(bytes) {
    if (bytes.length < 11) { return false; }
    return binaryString(bytes.subarray(0, 11)) === '-----BEGIN ';
  }

  // ---- big integers --------------------------------------------------------

  function bytesToBig(bytes) { return bytes.length ? BigInt('0x' + hex(bytes)) : 0n; }

  function bigToBytes(value, length) {
    let h = value.toString(16);
    if (h.length % 2) { h = '0' + h; }
    const raw = fromHex(h);
    if (length === undefined) { return raw; }
    if (raw.length > length) { throw new Error('integer too large'); }
    const out = new Uint8Array(length);
    out.set(raw, length - raw.length);
    return out;
  }

  function modPow(base, exponent, modulus) {
    let result = 1n;
    base %= modulus;
    while (exponent > 0n) {
      if (exponent & 1n) { result = (result * base) % modulus; }
      exponent >>= 1n;
      base = (base * base) % modulus;
    }
    return result;
  }

  // ---- DER / BER -----------------------------------------------------------

  const TAG = { BOOL: 0x01, INT: 0x02, BITSTR: 0x03, OCTET: 0x04, NULL: 0x05, OID: 0x06, UTF8: 0x0c, PRINTABLE: 0x13, T61: 0x14, IA5: 0x16, UTC: 0x17, GEN: 0x18, BMP: 0x1e, SEQ: 0x30, SET: 0x31 };

  // One node: tag byte, class, whether constructed, the content and the raw
  // encoding. BER indefinite lengths (0x80, ended by two zero bytes) are
  // read too - some clients write their CMS that way.
  function derRead(bytes, offset) {
    offset = offset || 0;
    if (offset >= bytes.length) { throw new Error('DER: past the end'); }
    const tagByte = bytes[offset];
    if ((tagByte & 0x1f) === 0x1f) { throw new Error('DER: long tag numbers are not used here'); }
    let at = offset + 1;
    if (at >= bytes.length) { throw new Error('DER: no length'); }
    let length = bytes[at++];
    let indefinite = false;
    if (length === 0x80) {
      indefinite = true;
    } else if (length & 0x80) {
      const count = length & 0x7f;
      if (count > 4 || at + count > bytes.length) { throw new Error('DER: bad length'); }
      length = 0;
      for (let i = 0; i < count; i++) { length = length * 256 + bytes[at++]; }
    }
    const node = { tag: tagByte, cls: tagByte >> 6, constructed: (tagByte & 0x20) !== 0, number: tagByte & 0x1f, start: offset, contentStart: at, indefinite: indefinite };
    if (indefinite) {
      if (!node.constructed) { throw new Error('DER: indefinite length on a primitive'); }
      let cursor = at;
      for (;;) {
        if (cursor + 1 >= bytes.length) { throw new Error('DER: unterminated indefinite length'); }
        if (bytes[cursor] === 0 && bytes[cursor + 1] === 0) { break; }
        cursor = derRead(bytes, cursor).end;
      }
      node.contentEnd = cursor;
      node.end = cursor + 2;
    } else {
      if (at + length > bytes.length) { throw new Error('DER: length past the end'); }
      node.contentEnd = at + length;
      node.end = at + length;
    }
    node.content = bytes.subarray(node.contentStart, node.contentEnd);
    node.raw = bytes.subarray(node.start, node.end);
    return node;
  }

  function derChildren(node) {
    const out = [];
    let at = 0;
    const content = node.content;
    while (at < content.length) {
      const child = derRead(content, at);
      out.push(child);
      at = child.end;
    }
    return out;
  }

  // The octets of a string type: a BER constructed one is its pieces joined.
  function octetsOf(node) {
    if (!node.constructed) { return node.content; }
    return concat(derChildren(node).map(octetsOf));
  }

  function oidDecode(bytes) {
    const parts = [];
    let value = 0;
    for (let i = 0; i < bytes.length; i++) {
      value = value * 128 + (bytes[i] & 0x7f);
      if (!(bytes[i] & 0x80)) {
        if (parts.length === 0) { parts.push(Math.floor(value / 40) > 2 ? 2 : Math.floor(value / 40)); parts.push(value - parts[0] * 40); }
        else { parts.push(value); }
        value = 0;
      }
    }
    return parts.join('.');
  }

  function oidEncode(text) {
    const parts = text.split('.').map(Number);
    const bytes = [];
    const push = function (v) {
      const stack = [v & 0x7f];
      v = Math.floor(v / 128);
      while (v > 0) { stack.push((v & 0x7f) | 0x80); v = Math.floor(v / 128); }
      while (stack.length) { bytes.push(stack.pop()); }
    };
    push(parts[0] * 40 + parts[1]);
    for (let i = 2; i < parts.length; i++) { push(parts[i]); }
    return new Uint8Array(bytes);
  }

  function derLength(length) {
    if (length < 0x80) { return new Uint8Array([length]); }
    const bytes = [];
    let v = length;
    while (v > 0) { bytes.unshift(v & 0xff); v = Math.floor(v / 256); }
    return new Uint8Array([0x80 | bytes.length].concat(bytes));
  }

  function tlv(tag, content) { return concat([new Uint8Array([tag]), derLength(content.length), content]); }
  function seq() { return tlv(TAG.SEQ, concat(Array.prototype.slice.call(arguments))); }
  function oid(text) { return tlv(TAG.OID, oidEncode(text)); }
  function octet(bytes) { return tlv(TAG.OCTET, bytes); }
  function derNull() { return new Uint8Array([TAG.NULL, 0]); }
  function explicit(number, inner) { return tlv(0xa0 | number, inner); }
  function implicitConstructed(number, content) { return tlv(0xa0 | number, content); }
  function implicitPrimitive(number, content) { return tlv(0x80 | number, content); }

  function derInt(value) {
    let bytes = value instanceof Uint8Array ? value : bigToBytes(BigInt(value));
    let start = 0;
    while (start < bytes.length - 1 && bytes[start] === 0 && !(bytes[start + 1] & 0x80)) { start++; }
    bytes = bytes.subarray(start);
    if (bytes.length === 0) { bytes = new Uint8Array([0]); }
    if (bytes[0] & 0x80) { bytes = concat([new Uint8Array([0]), bytes]); }
    return tlv(TAG.INT, bytes);
  }

  // A DER SET OF: its elements sorted by their encodings.
  function setOf(items) {
    const sorted = items.slice().sort(function (a, b) {
      const n = Math.min(a.length, b.length);
      for (let i = 0; i < n; i++) { if (a[i] !== b[i]) { return a[i] - b[i]; } }
      return a.length - b.length;
    });
    return tlv(TAG.SET, concat(sorted));
  }

  function utcTime(date) {
    const p = function (n) { return (n < 10 ? '0' : '') + n; };
    const text = String(date.getUTCFullYear()).slice(2) + p(date.getUTCMonth() + 1) + p(date.getUTCDate()) + p(date.getUTCHours()) + p(date.getUTCMinutes()) + p(date.getUTCSeconds()) + 'Z';
    return tlv(TAG.UTC, utf8(text));
  }

  function parseTime(node) {
    const text = binaryString(node.content);
    let year, rest;
    if (node.tag === TAG.UTC) { year = parseInt(text.slice(0, 2), 10); year += year < 50 ? 2000 : 1900; rest = text.slice(2); }
    else { year = parseInt(text.slice(0, 4), 10); rest = text.slice(4); }
    const n = function (i) { return parseInt(rest.substr(i, 2), 10) || 0; };
    return new Date(Date.UTC(year, n(0) - 1, n(2), n(4), n(6), n(8)));
  }

  function decodeString(node) {
    if (node.tag === TAG.BMP) {
      let s = '';
      for (let i = 0; i + 1 < node.content.length; i += 2) { s += String.fromCharCode((node.content[i] << 8) | node.content[i + 1]); }
      return s;
    }
    if (node.tag === TAG.UTF8) { return new TextDecoder('utf-8').decode(node.content); }
    return binaryString(node.content);
  }

  function algorithmOid(node) { return oidDecode(derChildren(node)[0].content); }

  // ---- OIDs ----------------------------------------------------------------

  const OID = {
    data: '1.2.840.113549.1.7.1', signedData: '1.2.840.113549.1.7.2', envelopedData: '1.2.840.113549.1.7.3', encryptedData: '1.2.840.113549.1.7.6',
    sha1: '1.3.14.3.2.26', sha256: '2.16.840.1.101.3.4.2.1', sha384: '2.16.840.1.101.3.4.2.2', sha512: '2.16.840.1.101.3.4.2.3',
    rsaEncryption: '1.2.840.113549.1.1.1', rsaOaep: '1.2.840.113549.1.1.7', rsaPss: '1.2.840.113549.1.1.10', mgf1: '1.2.840.113549.1.1.8',
    sha1WithRsa: '1.2.840.113549.1.1.5', sha256WithRsa: '1.2.840.113549.1.1.11', sha384WithRsa: '1.2.840.113549.1.1.12', sha512WithRsa: '1.2.840.113549.1.1.13',
    ecPublicKey: '1.2.840.10045.2.1', ecdsaSha1: '1.2.840.10045.4.1', ecdsaSha256: '1.2.840.10045.4.3.2', ecdsaSha384: '1.2.840.10045.4.3.3', ecdsaSha512: '1.2.840.10045.4.3.4',
    p256: '1.2.840.10045.3.1.7', p384: '1.3.132.0.34', p521: '1.3.132.0.35',
    contentType: '1.2.840.113549.1.9.3', messageDigest: '1.2.840.113549.1.9.4', signingTime: '1.2.840.113549.1.9.5', emailAddress: '1.2.840.113549.1.9.1',
    aes128cbc: '2.16.840.1.101.3.4.1.2', aes192cbc: '2.16.840.1.101.3.4.1.22', aes256cbc: '2.16.840.1.101.3.4.1.42',
    aes128gcm: '2.16.840.1.101.3.4.1.6', aes192gcm: '2.16.840.1.101.3.4.1.26', aes256gcm: '2.16.840.1.101.3.4.1.46', des3cbc: '1.2.840.113549.3.7',
    pbes2: '1.2.840.113549.1.5.13', pbkdf2: '1.2.840.113549.1.5.12',
    hmacSha1: '1.2.840.113549.2.7', hmacSha256: '1.2.840.113549.2.9', hmacSha384: '1.2.840.113549.2.10', hmacSha512: '1.2.840.113549.2.11',
    keyBag: '1.2.840.113549.1.12.10.1.1', shroudedKeyBag: '1.2.840.113549.1.12.10.1.2', certBag: '1.2.840.113549.1.12.10.1.3', x509CertBag: '1.2.840.113549.1.9.22.1',
    friendlyName: '1.2.840.113549.1.9.20', localKeyId: '1.2.840.113549.1.9.21',
    subjectAltName: '2.5.29.17', basicConstraints: '2.5.29.19', keyUsage: '2.5.29.15', subjectKeyId: '2.5.29.14', extKeyUsage: '2.5.29.37',
    cn: '2.5.4.3', o: '2.5.4.10', ou: '2.5.4.11', c: '2.5.4.6', l: '2.5.4.7', st: '2.5.4.8'
  };

  const HASH_BY_OID = {};
  HASH_BY_OID[OID.sha1] = 'SHA-1'; HASH_BY_OID[OID.sha256] = 'SHA-256'; HASH_BY_OID[OID.sha384] = 'SHA-384'; HASH_BY_OID[OID.sha512] = 'SHA-512';
  HASH_BY_OID[OID.hmacSha1] = 'SHA-1'; HASH_BY_OID[OID.hmacSha256] = 'SHA-256'; HASH_BY_OID[OID.hmacSha384] = 'SHA-384'; HASH_BY_OID[OID.hmacSha512] = 'SHA-512';
  const HASH_OID = { 'SHA-1': OID.sha1, 'SHA-256': OID.sha256, 'SHA-384': OID.sha384, 'SHA-512': OID.sha512 };
  const CURVE_BY_OID = {}; CURVE_BY_OID[OID.p256] = 'P-256'; CURVE_BY_OID[OID.p384] = 'P-384'; CURVE_BY_OID[OID.p521] = 'P-521';
  const CURVE_BYTES = { 'P-256': 32, 'P-384': 48, 'P-521': 66 };
  const DN_NAMES = {}; DN_NAMES[OID.cn] = 'CN'; DN_NAMES[OID.o] = 'O'; DN_NAMES[OID.ou] = 'OU'; DN_NAMES[OID.c] = 'C'; DN_NAMES[OID.l] = 'L'; DN_NAMES[OID.st] = 'ST'; DN_NAMES[OID.emailAddress] = 'emailAddress';

  function algorithmId(oidText, withNull) { return withNull ? seq(oid(oidText), derNull()) : seq(oid(oidText)); }

  // ---- X.509 ---------------------------------------------------------------

  function parseName(node) {
    const map = {};
    const parts = [];
    for (const rdn of derChildren(node)) {
      for (const atv of derChildren(rdn)) {
        const kids = derChildren(atv);
        const type = oidDecode(kids[0].content);
        const value = decodeString(kids[1]);
        const name = DN_NAMES[type] || type;
        map[name] = value;
        parts.push(name + '=' + value);
      }
    }
    return { der: node.raw, text: parts.join(', '), map: map };
  }

  function parseCertificate(der) {
    const cert = derRead(der);
    const top = derChildren(cert);
    const tbs = top[0];
    const fields = derChildren(tbs);
    let i = 0;
    if (fields[0].tag === 0xa0) { i = 1; }
    const serial = fields[i++];
    i++; // signature algorithm
    const issuer = parseName(fields[i++]);
    const validity = derChildren(fields[i++]);
    const subject = parseName(fields[i++]);
    const spki = fields[i++];
    const spkiParts = derChildren(spki);
    const keyAlgorithm = derChildren(spkiParts[0]);
    const keyOid = oidDecode(keyAlgorithm[0].content);
    const out = {
      raw: cert.raw, tbs: tbs.raw, serial: serial.content, serialHex: hex(serial.content).replace(/^(00)+(?=..)/, ''),
      issuer: issuer, subject: subject, notBefore: parseTime(validity[0]), notAfter: parseTime(validity[1]),
      spki: spki.raw, keyType: keyOid === OID.rsaEncryption ? 'rsa' : (keyOid === OID.ecPublicKey ? 'ec' : 'other'), curve: null,
      emails: [], isCA: false, keyUsage: null, subjectKeyId: null, extendedKeyUsage: []
    };
    if (out.keyType === 'ec' && keyAlgorithm[1] && keyAlgorithm[1].tag === TAG.OID) { out.curve = CURVE_BY_OID[oidDecode(keyAlgorithm[1].content)] || null; }
    if (out.keyType === 'rsa') {
      const bits = spkiParts[1].content.subarray(1);
      const rsa = derChildren(derRead(bits));
      out.rsa = { n: bytesToBig(rsa[0].content), e: bytesToBig(rsa[1].content), bytes: bigToBytes(bytesToBig(rsa[0].content)).length };
    } else if (out.keyType === 'ec') {
      out.ecPoint = spkiParts[1].content.subarray(1);
    }
    if (subject.map.emailAddress) { out.emails.push(subject.map.emailAddress.toLowerCase()); }
    for (; i < fields.length; i++) {
      if (fields[i].tag !== 0xa3) { continue; }
      for (const ext of derChildren(derChildren(fields[i])[0])) {
        const kids = derChildren(ext);
        const type = oidDecode(kids[0].content);
        const value = kids[kids.length - 1].content;
        if (type === OID.subjectAltName) {
          for (const name of derChildren(derRead(value))) {
            if (name.tag === 0x81) { const email = binaryString(name.content).toLowerCase(); if (out.emails.indexOf(email) < 0) { out.emails.push(email); } }
          }
        } else if (type === OID.basicConstraints) {
          const bc = derChildren(derRead(value));
          out.isCA = bc.length > 0 && bc[0].tag === TAG.BOOL && bc[0].content[0] !== 0;
        } else if (type === OID.keyUsage) {
          const bits = derRead(value).content;
          const first = bits.length > 1 ? bits[1] : 0;
          out.keyUsage = { digitalSignature: (first & 0x80) !== 0, keyEncipherment: (first & 0x20) !== 0 };
        } else if (type === OID.subjectKeyId) {
          out.subjectKeyId = hex(derRead(value).content);
        } else if (type === OID.extKeyUsage) {
          out.extendedKeyUsage = derChildren(derRead(value)).map(function (n) { return oidDecode(n.content); });
        }
      }
    }
    out.name = subject.map.CN || out.emails[0] || subject.text;
    return out;
  }

  async function fingerprint(cert) { return hex(new Uint8Array(await subtle.digest('SHA-256', cert.raw))); }

  function hashForSignature(sigOid, digestOid) {
    switch (sigOid) {
      case OID.sha1WithRsa: case OID.ecdsaSha1: return 'SHA-1';
      case OID.sha256WithRsa: case OID.ecdsaSha256: return 'SHA-256';
      case OID.sha384WithRsa: case OID.ecdsaSha384: return 'SHA-384';
      case OID.sha512WithRsa: case OID.ecdsaSha512: return 'SHA-512';
      default: return HASH_BY_OID[digestOid] || 'SHA-256';
    }
  }

  async function verifyKeyOf(cert, sigOid, sigParams, digestOid) {
    const hash = hashForSignature(sigOid, digestOid);
    if (cert.keyType === 'rsa') {
      if (sigOid === OID.rsaPss) {
        let pssHash = 'SHA-1';
        let saltLength = 20;
        if (sigParams && sigParams.tag === TAG.SEQ) {
          for (const p of derChildren(sigParams)) {
            if (p.tag === 0xa0) { pssHash = HASH_BY_OID[algorithmOid(derChildren(p)[0])] || pssHash; }
            if (p.tag === 0xa2) { saltLength = Number(bytesToBig(derChildren(p)[0].content)); }
          }
        }
        return { key: await subtle.importKey('spki', cert.spki, { name: 'RSA-PSS', hash: pssHash }, true, ['verify']), algorithm: { name: 'RSA-PSS', saltLength: saltLength } };
      }
      return { key: await subtle.importKey('spki', cert.spki, { name: 'RSASSA-PKCS1-v1_5', hash: hash }, true, ['verify']), algorithm: { name: 'RSASSA-PKCS1-v1_5' } };
    }
    if (cert.keyType === 'ec') {
      if (!cert.curve) { throw new Error('the certificate uses an EC curve this page does not know'); }
      return { key: await subtle.importKey('spki', cert.spki, { name: 'ECDSA', namedCurve: cert.curve }, true, ['verify']), algorithm: { name: 'ECDSA', hash: hash }, curve: cert.curve };
    }
    throw new Error('the certificate\'s key type is not supported');
  }

  // ECDSA signatures: DER SEQUENCE of two INTEGERs on the wire, r||s for Web Crypto.
  function ecdsaDerToRaw(der, curve) {
    const size = CURVE_BYTES[curve];
    const parts = derChildren(derRead(der));
    const trim = function (bytes) { let s = 0; while (s < bytes.length - 1 && bytes[s] === 0) { s++; } return bytes.subarray(s); };
    return concat([bigToBytes(bytesToBig(trim(parts[0].content)), size), bigToBytes(bytesToBig(trim(parts[1].content)), size)]);
  }

  function ecdsaRawToDer(raw) {
    const half = raw.length / 2;
    return seq(derInt(raw.subarray(0, half)), derInt(raw.subarray(half)));
  }

  // ---- private keys --------------------------------------------------------

  // A PKCS#8 PrivateKeyInfo: imported for signing, its numbers kept for the
  // RSA arithmetic Web Crypto does not offer.
  async function importPrivateKey(pkcs8) {
    const info = derChildren(derRead(pkcs8));
    const algorithm = derChildren(info[1]);
    const keyOid = oidDecode(algorithm[0].content);
    if (keyOid === OID.rsaEncryption) {
      const signKey = await subtle.importKey('pkcs8', pkcs8, { name: 'RSASSA-PKCS1-v1_5', hash: 'SHA-256' }, true, ['sign']);
      const jwk = await subtle.exportKey('jwk', signKey);
      const big = function (field) { return bytesToBig(b64urlDecode(jwk[field])); };
      return { type: 'rsa', pkcs8: pkcs8, signKey: signKey, rsa: { n: big('n'), e: big('e'), d: big('d'), p: big('p'), q: big('q'), dp: big('dp'), dq: big('dq'), qi: big('qi') } };
    }
    if (keyOid === OID.ecPublicKey) {
      const curve = algorithm[1] && algorithm[1].tag === TAG.OID ? CURVE_BY_OID[oidDecode(algorithm[1].content)] : null;
      if (!curve) { throw new Error('the key uses an EC curve this page does not know'); }
      const signKey = await subtle.importKey('pkcs8', pkcs8, { name: 'ECDSA', namedCurve: curve }, true, ['sign']);
      const jwk = await subtle.exportKey('jwk', signKey);
      return { type: 'ec', pkcs8: pkcs8, signKey: signKey, curve: curve, ecPoint: concat([new Uint8Array([4]), b64urlDecode(jwk.x), b64urlDecode(jwk.y)]) };
    }
    throw new Error('the private key is neither RSA nor EC');
  }

  function keyMatchesCertificate(key, cert) {
    if (key.type === 'rsa' && cert.keyType === 'rsa') { return key.rsa.n === cert.rsa.n && key.rsa.e === cert.rsa.e; }
    if (key.type === 'ec' && cert.keyType === 'ec') { return equalBytes(key.ecPoint, cert.ecPoint); }
    return false;
  }

  // PBES2 (RFC 8018): PBKDF2 then AES-CBC. The two legacy PBE families are
  // named in the error they raise.
  async function pbes2Decrypt(algorithmNode, ciphertext, password) {
    const alg = derChildren(algorithmNode);
    const algOid = oidDecode(alg[0].content);
    if (algOid !== OID.pbes2) {
      if (algOid.indexOf('1.2.840.113549.1.12.1.') === 0) { throw new Error('legacy PKCS#12 encryption (3DES or RC2); export the file again with AES - OpenSSL 3 does so by default, and the Windows export wizard offers AES256-SHA256 - or as PEM'); }
      if (algOid.indexOf('1.2.840.113549.1.5.') === 0) { throw new Error('legacy PBES1 encryption; export the key again with AES or as PEM'); }
      throw new Error('unknown key encryption ' + algOid);
    }
    const params = derChildren(alg[1]);
    const kdf = derChildren(params[0]);
    if (oidDecode(kdf[0].content) !== OID.pbkdf2) { throw new Error('unknown key derivation'); }
    const kdfParams = derChildren(kdf[1]);
    const salt = kdfParams[0].content;
    const iterations = Number(bytesToBig(kdfParams[1].content));
    let prf = 'SHA-1';
    for (let i = 2; i < kdfParams.length; i++) { if (kdfParams[i].tag === TAG.SEQ) { prf = HASH_BY_OID[algorithmOid(kdfParams[i])] || prf; } }
    const scheme = derChildren(params[1]);
    const schemeOid = oidDecode(scheme[0].content);
    const keyBits = schemeOid === OID.aes128cbc ? 128 : (schemeOid === OID.aes192cbc ? 192 : (schemeOid === OID.aes256cbc ? 256 : 0));
    if (!keyBits) {
      if (schemeOid === OID.des3cbc) { throw new Error('the key is encrypted with 3DES; export it again with AES or as PEM'); }
      throw new Error('unknown encryption scheme ' + schemeOid);
    }
    const iv = scheme[1].content;
    const baseKey = await subtle.importKey('raw', utf8(password), 'PBKDF2', false, ['deriveBits']);
    const bits = await subtle.deriveBits({ name: 'PBKDF2', salt: salt, iterations: iterations, hash: prf }, baseKey, keyBits);
    const aesKey = await subtle.importKey('raw', bits, 'AES-CBC', false, ['decrypt']);
    try {
      return new Uint8Array(await subtle.decrypt({ name: 'AES-CBC', iv: iv }, aesKey, ciphertext));
    } catch (e) {
      throw new Error('wrong password');
    }
  }

  async function decryptPkcs8(encrypted, password) {
    const info = derChildren(derRead(encrypted));
    const plain = await pbes2Decrypt(info[0], octetsOf(info[1]), password);
    try { derRead(plain); } catch (e) { throw new Error('wrong password'); }
    return plain;
  }

  // A PKCS#12 file: every key and certificate in it, the keys paired with
  // their certificates by public key, the rest as the chain.
  async function importPkcs12(bytes, password) {
    const pfx = derChildren(derRead(bytes));
    const authSafe = derChildren(pfx[1]);
    if (oidDecode(authSafe[0].content) !== OID.data) { throw new Error('not a PKCS#12 file'); }
    const safes = derChildren(derRead(octetsOf(derChildren(authSafe[1])[0])));
    const keys = [];
    const certs = [];
    const readSafeContents = async function (contentBytes) {
      for (const bag of derChildren(derRead(contentBytes))) {
        const kids = derChildren(bag);
        const bagOid = oidDecode(kids[0].content);
        const value = derChildren(kids[1])[0];
        if (bagOid === OID.keyBag) { keys.push(value.raw); }
        else if (bagOid === OID.shroudedKeyBag) { keys.push(await decryptPkcs8(value.raw, password)); }
        else if (bagOid === OID.certBag) {
          const certBag = derChildren(value);
          if (oidDecode(certBag[0].content) === OID.x509CertBag) { certs.push(octetsOf(derChildren(certBag[1])[0])); }
        }
      }
    };
    for (const info of safes) {
      const kids = derChildren(info);
      const type = oidDecode(kids[0].content);
      const inner = derChildren(kids[1])[0];
      if (type === OID.data) { await readSafeContents(octetsOf(inner)); }
      else if (type === OID.encryptedData) {
        const enc = derChildren(inner);
        const eci = derChildren(enc[1]);
        await readSafeContents(await pbes2Decrypt(eci[1], octetsOf(eci[2]), password));
      }
    }
    const parsedCerts = certs.map(parseCertificate);
    const out = { keys: [], certificates: parsedCerts };
    for (const pkcs8 of keys) {
      const key = await importPrivateKey(pkcs8);
      const cert = parsedCerts.filter(function (c) { return keyMatchesCertificate(key, c); })[0] || null;
      out.keys.push({ key: key, certificate: cert });
    }
    return out;
  }

  // Whatever a user gives: PEM (keys clear or encrypted, certificates), a
  // PKCS#12, a DER certificate or a DER PKCS#8. Answers the keys with their
  // certificates and every other certificate.
  async function importAny(bytes, password) {
    const out = { keys: [], certificates: [] };
    if (looksLikePem(bytes)) {
      const keyBlocks = [];
      for (const block of pemBlocks(new TextDecoder('utf-8').decode(bytes))) {
        if (block.label === 'CERTIFICATE') { out.certificates.push(parseCertificate(block.bytes)); }
        else if (block.label === 'PRIVATE KEY') { keyBlocks.push(block.bytes); }
        else if (block.label === 'ENCRYPTED PRIVATE KEY') { keyBlocks.push(await decryptPkcs8(block.bytes, password || '')); }
        else if (block.label === 'RSA PRIVATE KEY' || block.label === 'EC PRIVATE KEY') { throw new Error('a traditional ' + block.label + ' block; convert it to PKCS#8 (openssl pkcs8 -topk8) or export a PKCS#12'); }
      }
      for (const pkcs8 of keyBlocks) {
        const key = await importPrivateKey(pkcs8);
        out.keys.push({ key: key, certificate: out.certificates.filter(function (c) { return keyMatchesCertificate(key, c); })[0] || null });
      }
      return out;
    }
    let top;
    try { top = derRead(bytes); } catch (e) { throw new Error('not PEM, PKCS#12 or DER'); }
    const kids = derChildren(top);
    // A certificate starts with a SEQUENCE (the TBS); a PKCS#12 and a PKCS#8 with an INTEGER version.
    if (kids.length && kids[0].tag === TAG.SEQ) { out.certificates.push(parseCertificate(bytes)); return out; }
    if (kids.length >= 2 && kids[1].tag === TAG.SEQ && derChildren(kids[1])[0].tag === TAG.OID && oidDecode(derChildren(kids[1])[0].content) === OID.data) { return importPkcs12(bytes, password || ''); }
    const key = await importPrivateKey(bytes);
    out.keys.push({ key: key, certificate: null });
    return out;
  }

  // ---- wrapping a private key under the account password -------------------
  //
  // PBKDF2-HMAC-SHA256 from the password and a random salt, then AES-256-GCM
  // over the PKCS#8. The server stores what this makes and cannot open it.

  const WRAP_ITERATIONS = 600000;

  async function deriveWrapKey(password, salt, iterations) {
    const baseKey = await subtle.importKey('raw', utf8(password), 'PBKDF2', false, ['deriveKey']);
    return subtle.deriveKey({ name: 'PBKDF2', salt: salt, iterations: iterations, hash: 'SHA-256' }, baseKey, { name: 'AES-GCM', length: 256 }, false, ['encrypt', 'decrypt']);
  }

  async function wrapPrivateKey(pkcs8, password) {
    const salt = randomBytes(16);
    const iv = randomBytes(12);
    const key = await deriveWrapKey(password, salt, WRAP_ITERATIONS);
    const data = new Uint8Array(await subtle.encrypt({ name: 'AES-GCM', iv: iv }, key, pkcs8));
    return { v: 1, kdf: 'PBKDF2-SHA256', iterations: WRAP_ITERATIONS, salt: b64encode(salt), iv: b64encode(iv), data: b64encode(data) };
  }

  async function unwrapPrivateKey(wrapped, password) {
    const key = await deriveWrapKey(password, b64decode(wrapped.salt), wrapped.iterations || WRAP_ITERATIONS);
    try {
      return new Uint8Array(await subtle.decrypt({ name: 'AES-GCM', iv: b64decode(wrapped.iv) }, key, b64decode(wrapped.data)));
    } catch (e) {
      throw new Error('wrong password');
    }
  }

  // ---- MIME ----------------------------------------------------------------

  function findHeaderEnd(bytes, from) {
    for (let i = from; i < bytes.length; i++) {
      if (bytes[i] === 10) {
        if (i + 1 < bytes.length && bytes[i + 1] === 10) { return { headerEnd: i + 1, bodyStart: i + 2 }; }
        if (i + 2 < bytes.length && bytes[i + 1] === 13 && bytes[i + 2] === 10) { return { headerEnd: i + 1, bodyStart: i + 3 }; }
      }
    }
    return { headerEnd: bytes.length, bodyStart: bytes.length };
  }

  function parseHeaders(text) {
    const headers = [];
    const lines = text.split(/\r?\n/);
    for (const line of lines) {
      if (!line) { continue; }
      if ((line[0] === ' ' || line[0] === '\t') && headers.length) { headers[headers.length - 1].value += ' ' + line.trim(); continue; }
      const colon = line.indexOf(':');
      if (colon < 0) { continue; }
      headers.push({ name: line.slice(0, colon).trim(), value: line.slice(colon + 1).trim() });
    }
    return headers;
  }

  function parseContentType(value) {
    const out = { type: 'text/plain', params: {} };
    if (!value) { return out; }
    const parts = value.split(';');
    out.type = parts[0].trim().toLowerCase();
    const extended = {};
    for (let i = 1; i < parts.length; i++) {
      const eq = parts[i].indexOf('=');
      if (eq < 0) { continue; }
      let name = parts[i].slice(0, eq).trim().toLowerCase();
      let val = parts[i].slice(eq + 1).trim();
      if (val[0] === '"') { val = val.slice(1, val.lastIndexOf('"') >= 1 ? val.lastIndexOf('"') : undefined); }
      const star = name.match(/^([^*]+)\*(\d*)\*?$/);
      if (star) {
        const base = star[1];
        const index = star[2] === '' ? 0 : parseInt(star[2], 10);
        if (!extended[base]) { extended[base] = []; }
        extended[base][index] = val;
        continue;
      }
      out.params[name] = val;
    }
    for (const name in extended) {
      let joined = extended[name].join('');
      const m = joined.match(/^([^']*)'[^']*'(.*)$/);
      if (m) { try { joined = decodeURIComponent(m[2]); } catch (e) { joined = m[2]; } }
      out.params[name] = joined;
    }
    return out;
  }

  // RFC 2047 encoded words in a header value.
  function decodeHeaderValue(value) {
    if (!value) { return ''; }
    return value.replace(/=\?([^?]+)\?([bBqQ])\?([^?]*)\?=(\s+(?==\?))?/g, function (all, charset, kind, text) {
      let bytes;
      if (kind === 'b' || kind === 'B') { bytes = b64decode(text); }
      else { bytes = qpDecode(text.replace(/_/g, ' '), true); }
      return decodeText(bytes, charset);
    });
  }

  function qpDecode(text, header) {
    const out = [];
    for (let i = 0; i < text.length; i++) {
      const c = text[i];
      if (c === '=') {
        if (text[i + 1] === '\r' && text[i + 2] === '\n') { i += 2; continue; }
        if (text[i + 1] === '\n') { i += 1; continue; }
        const h = text.substr(i + 1, 2);
        if (/^[0-9A-Fa-f]{2}$/.test(h)) { out.push(parseInt(h, 16)); i += 2; continue; }
      }
      out.push(c.charCodeAt(0) & 0xff);
    }
    return new Uint8Array(out);
  }

  function decodeText(bytes, charset) {
    const label = (charset || 'utf-8').toLowerCase().replace(/^"|"$/g, '');
    try { return new TextDecoder(label).decode(bytes); }
    catch (e) { return new TextDecoder('utf-8').decode(bytes); }
  }

  // One entity: its headers, its parsed Content-Type, the raw body and, for a
  // multipart, its parts - each with the exact byte range it occupies in the
  // bytes given, which is what a detached signature covers.
  function parseMime(bytes, start, end) {
    start = start || 0;
    end = end === undefined ? bytes.length : end;
    const slice = bytes.subarray(start, end);
    const split = findHeaderEnd(slice, 0);
    const headerText = binaryString(slice.subarray(0, split.headerEnd));
    const headers = parseHeaders(headerText);
    const header = function (name) {
      name = name.toLowerCase();
      for (const h of headers) { if (h.name.toLowerCase() === name) { return h.value; } }
      return '';
    };
    const entity = { headers: headers, header: header, contentType: parseContentType(header('Content-Type')), encoding: header('Content-Transfer-Encoding').toLowerCase(), body: slice.subarray(split.bodyStart), bodyStart: start + split.bodyStart, start: start, end: end, parts: null };
    const disposition = parseContentType(header('Content-Disposition'));
    entity.disposition = disposition.type;
    entity.filename = decodeHeaderValue(disposition.params.filename || entity.contentType.params.name || '');
    if (entity.contentType.type.indexOf('multipart/') === 0 && entity.contentType.params.boundary) {
      entity.parts = [];
      const boundary = '--' + entity.contentType.params.boundary;
      const text = binaryString(entity.body);
      let position = 0;
      let partStart = -1;
      for (;;) {
        const at = text.indexOf(boundary, position);
        if (at < 0) { break; }
        // A delimiter is at the start of a line.
        if (at > 0 && text[at - 1] !== '\n') { position = at + boundary.length; continue; }
        if (partStart >= 0) {
          let partEnd = at;
          if (partEnd > 0 && text[partEnd - 1] === '\n') { partEnd--; }
          if (partEnd > 0 && text[partEnd - 1] === '\r') { partEnd--; }
          entity.parts.push(parseMime(bytes, entity.bodyStart + partStart, entity.bodyStart + partEnd));
        }
        let lineEnd = text.indexOf('\n', at + boundary.length);
        const closing = text.substr(at + boundary.length, 2) === '--';
        if (lineEnd < 0 || closing) { break; }
        partStart = lineEnd + 1;
        position = partStart;
      }
    }
    return entity;
  }

  function decodeBody(entity) {
    if (entity.encoding === 'base64') { return b64decode(binaryString(entity.body)); }
    if (entity.encoding === 'quoted-printable') { return qpDecode(binaryString(entity.body), false); }
    return entity.body;
  }

  // The text, the HTML and the attachments of an entity, the way the page
  // shows a message: the first text/plain and text/html that are not
  // attachments, everything else listed.
  function extractContent(entity) {
    const out = { text: '', html: '', attachments: [] };
    const walk = function (e) {
      const type = e.contentType.type;
      if (e.parts) {
        if (type === 'multipart/alternative') { for (const p of e.parts) { walk(p); } return; }
        for (const p of e.parts) { walk(p); }
        return;
      }
      const isAttachment = e.disposition === 'attachment' || (e.filename && type.indexOf('text/') !== 0);
      if (!isAttachment && type === 'text/plain' && !out.text) { out.text = decodeText(decodeBody(e), e.contentType.params.charset); return; }
      if (!isAttachment && type === 'text/html' && !out.html) { out.html = decodeText(decodeBody(e), e.contentType.params.charset); return; }
      out.attachments.push({ name: e.filename || 'part', type: type, bytes: decodeBody(e), contentId: (e.header('Content-ID') || '').replace(/^<|>$/g, '') });
    };
    walk(entity);
    return out;
  }

  // Quoted-printable, RFC 2045: everything outside printable ASCII, '=' and
  // a space or tab that ends a line is =XX; lines are at most 76 characters.
  function qpEncode(bytes) {
    let out = '';
    let line = '';
    const flush = function (soft) { out += line + (soft ? '=\r\n' : '\r\n'); line = ''; };
    for (let i = 0; i < bytes.length; i++) {
      const b = bytes[i];
      if (b === 13 && bytes[i + 1] === 10) { i++; if (line.length && (line[line.length - 1] === ' ' || line[line.length - 1] === '\t')) { const last = line[line.length - 1]; line = line.slice(0, -1) + '=' + (last === ' ' ? '20' : '09'); } flush(false); continue; }
      if (b === 10) { if (line.length && (line[line.length - 1] === ' ' || line[line.length - 1] === '\t')) { const last = line[line.length - 1]; line = line.slice(0, -1) + '=' + (last === ' ' ? '20' : '09'); } flush(false); continue; }
      let piece;
      if ((b >= 33 && b <= 126 && b !== 61) || b === 32 || b === 9) { piece = String.fromCharCode(b); }
      else { piece = '=' + (b < 16 ? '0' : '') + b.toString(16).toUpperCase(); }
      if (line.length + piece.length > 75) { flush(true); }
      line += piece;
    }
    if (line.length) { out += line; }
    return out;
  }

  function encodeHeaderWord(text) {
    if (/^[\x20-\x7e]*$/.test(text)) { return text; }
    return '=?utf-8?B?' + b64encode(utf8(text)) + '?=';
  }

  function encodeFilename(name) {
    if (/^[\x20-\x7e]*$/.test(name) && name.indexOf('"') < 0) { return 'filename="' + name + '"'; }
    return "filename*=utf-8''" + encodeURIComponent(name);
  }

  function boundaryText() { return '=_hm_' + hex(randomBytes(12)); }

  // The MIME entity of a message written on the page: text, optional HTML,
  // attachments (base64 as the page reads files). No RFC 5322 headers; those
  // are put on by messageHeaders, outside whatever wraps this.
  function buildMime(spec) {
    const textPart = 'Content-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: quoted-printable\r\n\r\n' + qpEncode(utf8(spec.text || ''));
    let body = textPart;
    if (spec.html) {
      const htmlPart = 'Content-Type: text/html; charset=utf-8\r\nContent-Transfer-Encoding: quoted-printable\r\n\r\n' + qpEncode(utf8(spec.html));
      const b = boundaryText();
      body = 'Content-Type: multipart/alternative; boundary="' + b + '"\r\n\r\n--' + b + '\r\n' + textPart + '\r\n--' + b + '\r\n' + htmlPart + '\r\n--' + b + '--\r\n';
    }
    const attachments = spec.attachments || [];
    if (attachments.length) {
      const b = boundaryText();
      let mixed = 'Content-Type: multipart/mixed; boundary="' + b + '"\r\n\r\n--' + b + '\r\n' + body;
      for (const a of attachments) {
        const data = typeof a.data === 'string' ? a.data.replace(/\s+/g, '') : b64encode(a.bytes);
        mixed += '\r\n--' + b + '\r\nContent-Type: ' + (a.type || 'application/octet-stream') + '; name="' + (a.name || 'file').replace(/[^\x20-\x7e]|"/g, '_') + '"\r\nContent-Transfer-Encoding: base64\r\nContent-Disposition: attachment; ' + encodeFilename(a.name || 'file') + '\r\n\r\n' + lines76(data);
      }
      body = mixed + '\r\n--' + b + '--\r\n';
    }
    return utf8(body);
  }

  function messageHeaders(fields) {
    let out = '';
    const put = function (name, value) { if (value) { out += name + ': ' + value + '\r\n'; } };
    put('From', fields.from);
    put('To', fields.to);
    put('Cc', fields.cc);
    put('Subject', encodeHeaderWord(fields.subject || ''));
    put('Date', fields.date);
    put('Message-ID', fields.messageId);
    put('In-Reply-To', fields.inReplyTo);
    put('References', fields.references);
    if (fields.receipt) { put('Disposition-Notification-To', fields.receipt); }
    out += 'MIME-Version: 1.0\r\n';
    return out;
  }

  // ---- CMS: SignedData -----------------------------------------------------

  function attribute(type, value) { return seq(oid(type), tlv(TAG.SET, value)); }

  async function makeSignerInfo(content, own, hash) {
    const digest = new Uint8Array(await subtle.digest(hash, content));
    const attrs = setOf([attribute(OID.contentType, oid(OID.data)), attribute(OID.signingTime, utcTime(new Date())), attribute(OID.messageDigest, octet(digest))]);
    let signature, sigAlg;
    if (own.key.type === 'rsa') {
      const key = hash === 'SHA-256' ? own.key.signKey : await subtle.importKey('pkcs8', own.key.pkcs8, { name: 'RSASSA-PKCS1-v1_5', hash: hash }, true, ['sign']);
      signature = new Uint8Array(await subtle.sign({ name: 'RSASSA-PKCS1-v1_5' }, key, attrs));
      sigAlg = algorithmId(OID.rsaEncryption, true);
    } else {
      const raw = new Uint8Array(await subtle.sign({ name: 'ECDSA', hash: hash }, own.key.signKey, attrs));
      signature = ecdsaRawToDer(raw);
      sigAlg = algorithmId(hash === 'SHA-384' ? OID.ecdsaSha384 : (hash === 'SHA-512' ? OID.ecdsaSha512 : OID.ecdsaSha256), false);
    }
    const sid = seq(own.certificate.issuer.der, derInt(own.certificate.serial));
    const signedAttrs = implicitConstructed(0, derRead(attrs).content);
    return seq(derInt(1), sid, algorithmId(HASH_OID[hash], true), signedAttrs, sigAlg, octet(signature));
  }

  // SignedData over the content: detached (the S/MIME multipart/signed
  // form) or with the content inside (the application/pkcs7-mime form).
  async function sign(content, own, detached) {
    const hash = 'SHA-256';
    const signerInfo = await makeSignerInfo(content, own, hash);
    const certs = [own.certificate].concat(own.chain || []).map(function (c) { return c.raw; });
    const encap = detached ? seq(oid(OID.data)) : seq(oid(OID.data), explicit(0, octet(content)));
    const signedData = seq(derInt(1), setOf([algorithmId(HASH_OID[hash], true)]), encap, implicitConstructed(0, concat(certs)), tlv(TAG.SET, signerInfo));
    return seq(oid(OID.signedData), explicit(0, signedData));
  }

  function findCertificate(certs, sidNode) {
    if (sidNode.tag === 0x80) {
      const skid = hex(sidNode.content);
      return certs.filter(function (c) { return c.subjectKeyId === skid; })[0] || null;
    }
    const kids = derChildren(sidNode);
    const issuer = kids[0].raw;
    const serial = kids[1].content;
    return certs.filter(function (c) { return equalBytes(c.issuer.der, issuer) && bytesToBig(c.serial) === bytesToBig(serial); })[0] || null;
  }

  // Verifies a SignedData: the first signer whose certificate is present.
  // content is the detached content, or null when it is inside.
  async function verify(cmsBytes, content) {
    const out = { valid: false, signer: null, certificates: [], signingTime: null, content: content, error: '' };
    try {
      const info = derChildren(derRead(cmsBytes));
      if (oidDecode(info[0].content) !== OID.signedData) { out.error = 'not a signed message'; return out; }
      const signedData = derChildren(derChildren(info[1])[0]);
      let i = 1;
      i++; // digestAlgorithms
      const encap = derChildren(signedData[i++]);
      if (encap.length > 1) { out.content = octetsOf(derChildren(encap[1])[0]); }
      if (!out.content) { out.error = 'the signed content is missing'; return out; }
      if (signedData[i] && signedData[i].tag === 0xa0) {
        for (const c of derChildren(signedData[i])) { if (c.tag === TAG.SEQ) { try { out.certificates.push(parseCertificate(c.raw)); } catch (e) { /* not a certificate: skip */ } } }
        i++;
      }
      if (signedData[i] && signedData[i].tag === 0xa1) { i++; }
      const signerInfos = derChildren(signedData[i]);
      if (!signerInfos.length) { out.error = 'no signer'; return out; }
      let lastError = 'the signer\'s certificate is not in the message';
      for (const si of signerInfos) {
        const fields = derChildren(si);
        const cert = findCertificate(out.certificates, fields[1]);
        if (!cert) { continue; }
        const digestOid = algorithmOid(fields[2]);
        const hash = HASH_BY_OID[digestOid];
        if (!hash) { lastError = 'unknown digest ' + digestOid; continue; }
        let j = 3;
        let signedAttrs = null;
        if (fields[j].tag === 0xa0) { signedAttrs = fields[j++]; }
        const sigAlgNode = fields[j++];
        const sigAlgParts = derChildren(sigAlgNode);
        const sigOid = oidDecode(sigAlgParts[0].content);
        const signature = octetsOf(fields[j++]);
        let signedBytes;
        if (signedAttrs) {
          let digestAttr = null;
          for (const attr of derChildren(signedAttrs)) {
            const kids = derChildren(attr);
            const type = oidDecode(kids[0].content);
            const value = derChildren(kids[1])[0];
            if (type === OID.messageDigest) { digestAttr = value.content; }
            if (type === OID.signingTime) { out.signingTime = parseTime(value); }
          }
          let actual = new Uint8Array(await subtle.digest(hash, out.content));
          if (digestAttr && !equalBytes(digestAttr, actual)) {
            // The signer hashed the canonical form (every line ending CRLF,
            // RFC 8551 3.1.1); a message that reached us with bare LFs or
            // doubled CRs is hashed that way before it is called changed.
            const canonical = latin1Bytes(binaryString(out.content).replace(/\r*\n/g, '\r\n'));
            actual = new Uint8Array(await subtle.digest(hash, canonical));
          }
          if (!digestAttr || !equalBytes(digestAttr, actual)) { lastError = 'the content was changed after it was signed'; continue; }
          signedBytes = tlv(TAG.SET, signedAttrs.content);
        } else {
          signedBytes = out.content;
        }
        const verifier = await verifyKeyOf(cert, sigOid, sigAlgParts[1] || null, digestOid);
        const sig = verifier.algorithm.name === 'ECDSA' ? ecdsaDerToRaw(signature, verifier.curve) : signature;
        const ok = await subtle.verify(verifier.algorithm, verifier.key, sig, signedBytes);
        if (!ok) { lastError = 'the signature does not match'; continue; }
        out.valid = true;
        out.signer = cert;
        out.error = '';
        return out;
      }
      out.error = lastError;
    } catch (e) {
      out.error = 'the signature could not be read: ' + (e && e.message ? e.message : e);
    }
    return out;
  }

  // ---- CMS: EnvelopedData --------------------------------------------------

  function rsaPkcs1Encrypt(message, n, e, keyBytes) {
    if (message.length > keyBytes - 11) { throw new Error('the key is too small'); }
    const ps = new Uint8Array(keyBytes - message.length - 3);
    for (let i = 0; i < ps.length; i++) { do { ps[i] = randomBytes(1)[0]; } while (ps[i] === 0); }
    const em = concat([new Uint8Array([0, 2]), ps, new Uint8Array([0]), message]);
    return bigToBytes(modPow(bytesToBig(em), e, n), keyBytes);
  }

  function rsaPkcs1Decrypt(ciphertext, rsa) {
    const c = bytesToBig(ciphertext);
    // CRT: m1 = c^dp mod p, m2 = c^dq mod q, h = qi (m1 - m2) mod p, m = m2 + h q.
    const m1 = modPow(c, rsa.dp, rsa.p);
    const m2 = modPow(c, rsa.dq, rsa.q);
    let h = (rsa.qi * (m1 - m2)) % rsa.p;
    if (h < 0n) { h += rsa.p; }
    const m = m2 + h * rsa.q;
    const em = bigToBytes(m, bigToBytes(rsa.n).length);
    if (em[0] !== 0 || em[1] !== 2) { throw new Error('the content key could not be decrypted'); }
    let at = 2;
    while (at < em.length && em[at] !== 0) { at++; }
    if (at < 10 || at >= em.length) { throw new Error('the content key could not be decrypted'); }
    return em.subarray(at + 1);
  }

  // EnvelopedData for the recipients' certificates: AES-256-CBC content, the
  // key carried to each recipient with RSAES-PKCS1-v1_5.
  async function encrypt(content, recipients) {
    const contentKey = randomBytes(32);
    const iv = randomBytes(16);
    const aesKey = await subtle.importKey('raw', contentKey, 'AES-CBC', false, ['encrypt']);
    const ciphertext = new Uint8Array(await subtle.encrypt({ name: 'AES-CBC', iv: iv }, aesKey, content));
    const infos = [];
    for (const cert of recipients) {
      if (cert.keyType !== 'rsa') { throw new Error((cert.emails[0] || cert.name) + ': only an RSA certificate can receive an encrypted message from this page'); }
      const encryptedKey = rsaPkcs1Encrypt(contentKey, cert.rsa.n, cert.rsa.e, cert.rsa.bytes);
      infos.push(seq(derInt(0), seq(cert.issuer.der, derInt(cert.serial)), algorithmId(OID.rsaEncryption, true), octet(encryptedKey)));
    }
    const eci = seq(oid(OID.data), seq(oid(OID.aes256cbc), octet(iv)), implicitPrimitive(0, ciphertext));
    const enveloped = seq(derInt(0), setOf(infos), eci);
    return seq(oid(OID.envelopedData), explicit(0, enveloped));
  }

  // Opens an EnvelopedData with one of the account's keys. own: [{key, certificate}].
  async function decrypt(cmsBytes, own) {
    const info = derChildren(derRead(cmsBytes));
    if (oidDecode(info[0].content) !== OID.envelopedData) { throw new Error('not an encrypted message'); }
    const enveloped = derChildren(derChildren(info[1])[0]);
    let i = 1;
    if (enveloped[i].tag === 0xa0) { i++; } // originatorInfo
    const recipientInfos = derChildren(enveloped[i++]);
    const eci = derChildren(enveloped[i++]);
    let contentKey = null;
    let used = null;
    let reason = 'this message was not encrypted for any of your certificates';
    for (const ri of recipientInfos) {
      if (ri.tag !== TAG.SEQ) { continue; } // only KeyTransRecipientInfo
      const fields = derChildren(ri);
      const rid = fields[1];
      const match = own.filter(function (o) { return o.certificate && findCertificate([o.certificate], rid); })[0];
      if (!match) { continue; }
      const keyAlg = derChildren(fields[2]);
      const keyOid = oidDecode(keyAlg[0].content);
      const encryptedKey = octetsOf(fields[3]);
      try {
        if (keyOid === OID.rsaEncryption) {
          contentKey = rsaPkcs1Decrypt(encryptedKey, match.key.rsa);
        } else if (keyOid === OID.rsaOaep) {
          let hash = 'SHA-1';
          if (keyAlg[1] && keyAlg[1].tag === TAG.SEQ) { for (const p of derChildren(keyAlg[1])) { if (p.tag === 0xa0) { hash = HASH_BY_OID[algorithmOid(derChildren(p)[0])] || hash; } } }
          const oaepKey = await subtle.importKey('pkcs8', match.key.pkcs8, { name: 'RSA-OAEP', hash: hash }, false, ['decrypt']);
          contentKey = new Uint8Array(await subtle.decrypt({ name: 'RSA-OAEP' }, oaepKey, encryptedKey));
        } else {
          reason = 'the key transport ' + keyOid + ' is not supported';
          continue;
        }
        used = match.certificate;
        break;
      } catch (e) {
        reason = e.message || String(e);
      }
    }
    if (!contentKey) { throw new Error(reason); }
    const algorithm = derChildren(eci[1]);
    const algOid = oidDecode(algorithm[0].content);
    const ciphertext = octetsOf(eci[2]);
    let plain;
    if (algOid === OID.aes128cbc || algOid === OID.aes192cbc || algOid === OID.aes256cbc) {
      const key = await subtle.importKey('raw', contentKey, 'AES-CBC', false, ['decrypt']);
      plain = new Uint8Array(await subtle.decrypt({ name: 'AES-CBC', iv: algorithm[1].content }, key, ciphertext));
    } else if (algOid === OID.aes128gcm || algOid === OID.aes192gcm || algOid === OID.aes256gcm) {
      const params = derChildren(algorithm[1]);
      const tagLength = params.length > 1 ? Number(bytesToBig(params[1].content)) * 8 : 96;
      const key = await subtle.importKey('raw', contentKey, 'AES-GCM', false, ['decrypt']);
      plain = new Uint8Array(await subtle.decrypt({ name: 'AES-GCM', iv: params[0].content, tagLength: tagLength }, key, ciphertext));
    } else if (algOid === OID.des3cbc) {
      throw new Error('the message is encrypted with 3DES, which this page does not decrypt; ask the sender for AES');
    } else {
      throw new Error('the content encryption ' + algOid + ' is not supported');
    }
    return { content: plain, recipient: used };
  }

  // ---- S/MIME: the message level -------------------------------------------

  // Reads a message as stored: unwraps every signed and encrypted layer it
  // can, and says what it found. own: [{key, certificate}] to decrypt with.
  async function readMessage(bytes, own) {
    const out = { signed: null, encrypted: null, entity: null, layers: [] };
    let entity = parseMime(bytes);
    for (let depth = 0; depth < 4; depth++) {
      const type = entity.contentType.type;
      if (type === 'multipart/signed' && entity.parts && entity.parts.length >= 2) {
        const signed = entity.parts[0];
        const signatureEntity = entity.parts[1];
        const content = bytes.subarray(signed.start, signed.end);
        const result = await verify(decodeBody(signatureEntity), content);
        result.content = null;
        out.signed = result;
        out.layers.push('signed');
        entity = signed;
        continue;
      }
      if (type === 'application/pkcs7-mime' || type === 'application/x-pkcs7-mime') {
        const cms = decodeBody(entity);
        let smimeType = (entity.contentType.params['smime-type'] || '').toLowerCase();
        if (!smimeType) { try { smimeType = oidDecode(derChildren(derRead(cms))[0].content) === OID.envelopedData ? 'enveloped-data' : 'signed-data'; } catch (e) { smimeType = 'signed-data'; } }
        if (smimeType === 'enveloped-data') {
          out.layers.push('encrypted');
          try {
            const opened = await decrypt(cms, own || []);
            out.encrypted = { ok: true, recipient: opened.recipient, error: '' };
            entity = parseMime(opened.content);
            bytes = opened.content;
            continue;
          } catch (e) {
            out.encrypted = { ok: false, recipient: null, error: e.message || String(e) };
            break;
          }
        }
        const result = await verify(cms, null);
        out.signed = result;
        out.layers.push('signed');
        if (!result.content) { break; }
        bytes = result.content;
        entity = parseMime(bytes);
        continue;
      }
      break;
    }
    out.entity = entity;
    if (!(out.encrypted && !out.encrypted.ok)) { out.content = extractContent(entity); }
    return out;
  }

  // Writes a message: the entity from the page's fields, signed with own
  // (or not), encrypted for the recipients' certificates (or not), under
  // the RFC 5322 headers given. Answers the whole message as text.
  async function composeMessage(spec) {
    let entity = buildMime(spec);
    if (spec.sign) {
      const p7s = await sign(entity, spec.sign, true);
      const b = boundaryText();
      const wrapper = 'Content-Type: multipart/signed; protocol="application/pkcs7-signature"; micalg=sha-256; boundary="' + b + '"\r\n\r\nThis is a cryptographically signed message in MIME format.\r\n\r\n--' + b + '\r\n';
      const tail = '\r\n--' + b + '\r\nContent-Type: application/pkcs7-signature; name="smime.p7s"\r\nContent-Transfer-Encoding: base64\r\nContent-Disposition: attachment; filename="smime.p7s"\r\n\r\n' + lines76(b64encode(p7s)) + '\r\n--' + b + '--\r\n';
      entity = concat([utf8(wrapper), entity, utf8(tail)]);
    }
    if (spec.encrypt && spec.encrypt.length) {
      const p7m = await encrypt(entity, spec.encrypt);
      entity = utf8('Content-Type: application/pkcs7-mime; smime-type=enveloped-data; name="smime.p7m"\r\nContent-Transfer-Encoding: base64\r\nContent-Disposition: attachment; filename="smime.p7m"\r\n\r\n' + lines76(b64encode(p7m)) + '\r\n');
    }
    return messageHeaders(spec.headers || {}) + binaryString(entity);
  }

  return {
    // bytes
    utf8: utf8, latin1Bytes: latin1Bytes, b64encode: b64encode, b64decode: b64decode, hex: hex, concat: concat, binaryString: binaryString, pemBlocks: pemBlocks,
    // DER
    derRead: derRead, derChildren: derChildren, oidDecode: oidDecode, OID: OID,
    // certificates and keys
    parseCertificate: parseCertificate, fingerprint: fingerprint, importPrivateKey: importPrivateKey, keyMatchesCertificate: keyMatchesCertificate,
    decryptPkcs8: decryptPkcs8, importPkcs12: importPkcs12, importAny: importAny, wrapPrivateKey: wrapPrivateKey, unwrapPrivateKey: unwrapPrivateKey,
    // MIME
    parseMime: parseMime, decodeBody: decodeBody, extractContent: extractContent, buildMime: buildMime, messageHeaders: messageHeaders, decodeHeaderValue: decodeHeaderValue, qpEncode: qpEncode,
    // CMS
    sign: sign, verify: verify, encrypt: encrypt, decrypt: decrypt,
    // messages
    readMessage: readMessage, composeMessage: composeMessage
  };
}));

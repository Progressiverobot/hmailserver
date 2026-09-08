// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace RegressionTests.Shared
{
   /// <summary>
   ///    The wire forms DNSSEC is made of, for a test to build a signed zone the server's
   ///    validating resolver will accept: names in canonical form, type bitmaps, NSEC and
   ///    NSEC3 rdata, the NSEC3 hash and its base32hex spelling, TLSA and TXT rdata.
   ///    RFC 4034 for the shapes, RFC 5155 for NSEC3.
   /// </summary>
   public static class DnsWire
   {
      public const int TypeA = 1;
      public const int TypeNs = 2;
      public const int TypeSoa = 6;
      public const int TypeMx = 15;
      public const int TypeTxt = 16;
      public const int TypeDs = 43;
      public const int TypeRrsig = 46;
      public const int TypeNsec = 47;
      public const int TypeDnskey = 48;
      public const int TypeNsec3 = 50;
      public const int TypeTlsa = 52;

      /// <summary>The name in wire form, lowercase, ending in the root label; "" is the root itself.</summary>
      public static byte[] Name(string name)
      {
         var wire = new List<byte>();
         foreach (var bytes in name.TrimEnd('.').ToLowerInvariant()
                                   .Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries)
                                   .Select(label => Encoding.ASCII.GetBytes(label)))
         {
            wire.Add((byte) bytes.Length);
            wire.AddRange(bytes);
         }
         wire.Add(0);
         return wire.ToArray();
      }

      public static int LabelCount(string name)
      {
         return name.TrimEnd('.').Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries).Length;
      }

      /// <summary>RFC 4034 4.1.2: window blocks of the types present.</summary>
      public static byte[] TypeBitmap(IEnumerable<int> types)
      {
         var bitmap = new List<byte>();
         foreach (var window in types.Distinct().OrderBy(t => t).GroupBy(t => t >> 8))
         {
            var bits = new byte[32];
            var length = 0;
            foreach (var low in window.Select(type => type & 0xFF))
            {
               bits[low >> 3] |= (byte) (0x80 >> (low & 7));
               length = Math.Max(length, (low >> 3) + 1);
            }
            bitmap.Add((byte) window.Key);
            bitmap.Add((byte) length);
            bitmap.AddRange(bits.Take(length));
         }
         return bitmap.ToArray();
      }

      public static byte[] Nsec(string nextOwner, IEnumerable<int> types)
      {
         return Name(nextOwner).Concat(TypeBitmap(types)).ToArray();
      }

      public static byte[] Nsec3(bool optOut, ushort iterations, byte[] salt, byte[] nextHashedOwner, IEnumerable<int> types)
      {
         var rdata = new List<byte> { 1, (byte) (optOut ? 1 : 0), (byte) (iterations >> 8), (byte) (iterations & 0xFF), (byte) salt.Length };
         rdata.AddRange(salt);
         rdata.Add((byte) nextHashedOwner.Length);
         rdata.AddRange(nextHashedOwner);
         rdata.AddRange(TypeBitmap(types));
         return rdata.ToArray();
      }

      /// <summary>RFC 5155 5: SHA-1 of the canonical name and the salt, iterated.</summary>
      public static byte[] Nsec3Hash(string name, byte[] salt, int iterations)
      {
         using (var sha1 = SHA1.Create())
         {
            var hash = sha1.ComputeHash(Name(name).Concat(salt).ToArray());
            for (var i = 0; i < iterations; i++)
               hash = sha1.ComputeHash(hash.Concat(salt).ToArray());
            return hash;
         }
      }

      /// <summary>RFC 4648 base32hex without padding, uppercase - the spelling of an NSEC3 owner label.</summary>
      public static string Base32Hex(byte[] data)
      {
         const string alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUV";
         var result = new StringBuilder();
         var buffer = 0;
         var bits = 0;
         foreach (var b in data)
         {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
               result.Append(alphabet[(buffer >> (bits - 5)) & 31]);
               bits -= 5;
            }
         }
         if (bits > 0)
            result.Append(alphabet[(buffer << (5 - bits)) & 31]);
         return result.ToString();
      }

      public static byte[] Txt(string value)
      {
         var rdata = new List<byte>();
         var bytes = Encoding.ASCII.GetBytes(value);
         for (var offset = 0; offset < bytes.Length; offset += 255)
         {
            var length = Math.Min(255, bytes.Length - offset);
            rdata.Add((byte) length);
            rdata.AddRange(bytes.Skip(offset).Take(length));
         }
         if (rdata.Count == 0)
            rdata.Add(0);
         return rdata.ToArray();
      }

      public static byte[] Tlsa(byte usage, byte selector, byte matching, byte[] data)
      {
         return new[] { usage, selector, matching }.Concat(data).ToArray();
      }

      public static byte[] Mx(ushort preference, string exchange)
      {
         return new[] { (byte) (preference >> 8), (byte) (preference & 0xFF) }.Concat(Name(exchange)).ToArray();
      }

      public static byte[] Ns(string host)
      {
         return Name(host);
      }
   }

   /// <summary>
   ///    One DNSSEC zone with one ECDSA P-256 key (algorithm 13) that is both its KSK and
   ///    its ZSK: the DNSKEY record the zone publishes, the DS record its parent publishes
   ///    for it, and RRSIGs over any RRset. The resolver under test accepts algorithm 13
   ///    and SHA-256 DS digests, and .NET's ECDsa signs in exactly the r||s form the
   ///    record carries, so nothing has to be re-encoded.
   /// </summary>
   public sealed class DnssecZone : IDisposable
   {
      private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

      public DnssecZone(string name)
      {
         Name = name.TrimEnd('.').ToLowerInvariant();

         var q = _key.ExportParameters(false).Q;
         DnskeyRdata = new byte[] { 0x01, 0x01, 3, 13 }.Concat(Pad32(q.X)).Concat(Pad32(q.Y)).ToArray();
         KeyTag = ComputeKeyTag(DnskeyRdata);

         using (var sha256 = SHA256.Create())
         {
            var digest = sha256.ComputeHash(DnsWire.Name(Name).Concat(DnskeyRdata).ToArray());
            DsRdata = new[] { (byte) (KeyTag >> 8), (byte) (KeyTag & 0xFF), (byte) 13, (byte) 2 }.Concat(digest).ToArray();
            DsDigestHex = BitConverter.ToString(digest).Replace("-", "");
         }
      }

      /// <summary>"" for the root, otherwise the name without a trailing dot.</summary>
      public string Name { get; private set; }

      public byte[] DnskeyRdata { get; private set; }
      public ushort KeyTag { get; private set; }
      public byte[] DsRdata { get; private set; }
      public string DsDigestHex { get; private set; }

      /// <summary>The trust-anchor line the server's DnssecTrustAnchors setting takes: "keytag algorithm digesttype digest".</summary>
      public string TrustAnchor
      {
         get { return KeyTag + " 13 2 " + DsDigestHex; }
      }

      /// <summary>
      ///    The RRSIG rdata over one RRset, as this zone's key signs it: RFC 4034 3.1.8.1,
      ///    the RRSIG rdata without the signature, then every RR in canonical form and
      ///    canonical order. Valid from an hour ago until tomorrow unless told otherwise.
      /// </summary>
      public byte[] Sign(string owner, int type, uint ttl, IEnumerable<byte[]> rdatas, DateTime? inception = null, DateTime? expiration = null, byte[] signWithThisKeyInstead = null)
      {
         var from = ToUnix(inception ?? DateTime.UtcNow.AddHours(-1));
         var until = ToUnix(expiration ?? DateTime.UtcNow.AddDays(1));

         var prefix = new List<byte>
         {
            (byte) (type >> 8), (byte) (type & 0xFF),
            13,
            (byte) DnsWire.LabelCount(owner)
         };
         prefix.AddRange(Be32(ttl));
         prefix.AddRange(Be32(until));
         prefix.AddRange(Be32(from));
         prefix.Add((byte) (KeyTag >> 8));
         prefix.Add((byte) (KeyTag & 0xFF));
         prefix.AddRange(DnsWire.Name(Name));

         var signed = new List<byte>(prefix);
         var ownerWire = DnsWire.Name(owner);
         foreach (var rdata in rdatas.OrderBy(r => r, new ByteArrayOrder()))
         {
            signed.AddRange(ownerWire);
            signed.Add((byte) (type >> 8)); signed.Add((byte) (type & 0xFF));
            signed.Add(0); signed.Add(1);
            signed.AddRange(Be32(ttl));
            signed.Add((byte) (rdata.Length >> 8)); signed.Add((byte) (rdata.Length & 0xFF));
            signed.AddRange(rdata);
         }

         var signature = signWithThisKeyInstead ?? _key.SignData(signed.ToArray(), HashAlgorithmName.SHA256);
         return prefix.Concat(signature).ToArray();
      }

      public void Dispose()
      {
         _key.Dispose();
      }

      /// <summary>RFC 4034 appendix B.</summary>
      public static ushort ComputeKeyTag(byte[] rdata)
      {
         long accumulator = 0;
         for (var i = 0; i < rdata.Length; i++)
            accumulator += (i & 1) == 1 ? rdata[i] : (long) rdata[i] << 8;
         accumulator += (accumulator >> 16) & 0xFFFF;
         return (ushort) (accumulator & 0xFFFF);
      }

      private static byte[] Pad32(byte[] value)
      {
         if (value.Length == 32)
            return value;
         var padded = new byte[32];
         Array.Copy(value, 0, padded, 32 - value.Length, value.Length);
         return padded;
      }

      private static uint ToUnix(DateTime utc)
      {
         return (uint) (utc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
      }

      private static byte[] Be32(uint value)
      {
         return new[] { (byte) (value >> 24), (byte) (value >> 16), (byte) (value >> 8), (byte) value };
      }

      /// <summary>Canonical RR order (RFC 4034 6.3): the rdata compared as unsigned octets, a prefix first.</summary>
      private sealed class ByteArrayOrder : IComparer<byte[]>
      {
         public int Compare(byte[] x, byte[] y)
         {
            var length = Math.Min(x.Length, y.Length);
            for (var i = 0; i < length; i++)
               if (x[i] != y[i])
                  return x[i] < y[i] ? -1 : 1;
            return x.Length.CompareTo(y.Length);
         }
      }
   }
}

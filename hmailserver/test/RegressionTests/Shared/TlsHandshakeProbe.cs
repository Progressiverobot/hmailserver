// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;

namespace RegressionTests.Shared
{
   /// <summary>
   ///    Asks a TLS listener which key-exchange group it prefers, by hand.
   ///
   ///    This exists because .NET cannot answer the question. SslStream will complete a
   ///    handshake and report the protocol version and the cipher suite, but it does not
   ///    expose the negotiated key-exchange group - so a test written with SslStream can
   ///    prove a listener speaks TLS and cannot prove it offers the hybrid post-quantum
   ///    KEM that [Settings] TlsKeyExchangeGroups configures. That is the property that
   ///    matters here: the optional listeners each used to build their own SSL_CTX, and
   ///    the observable consequence was classical-only key exchange with nothing
   ///    anywhere saying so.
   ///
   ///    The trick is a TLS 1.3 ClientHello carrying supported_groups but an EMPTY
   ///    client_shares vector. RFC 8446 leaves the server no choice: it cannot complete
   ///    the handshake without a share, so it answers with a HelloRetryRequest naming
   ///    the one group it wants - which is exactly the answer under test, and it arrives
   ///    before any certificate, cipher or application data is involved. Offer two
   ///    groups and the reply says which of the two the listener puts first.
   ///
   ///    Written for the ManageSieve STARTTLS tests and extracted here once the REST API
   ///    and Web Services HTTPS listeners moved onto the same shared TLS configuration
   ///    and needed the same proof. Works on any stream that is at the start of a TLS
   ///    handshake, whether that is a fresh implicit-TLS socket or a connection that has
   ///    just been upgraded by STARTTLS.
   /// </summary>
   public static class TlsHandshakeProbe
   {
      // IANA TLS supported-group codepoints.
      public const int GroupX25519Mlkem768 = 0x11EC;
      public const int GroupSecP256r1Mlkem768 = 0x11EB;
      public const int GroupX25519 = 0x001D;
      public const int GroupSecP256r1 = 0x0017;
      public const int GroupSecP384r1 = 0x0018;

      // The fixed ServerHello.random that marks a HelloRetryRequest (RFC 8446 4.1.3).
      private static readonly byte[] HelloRetryRequestRandom =
      {
         0xCF, 0x21, 0xAD, 0x74, 0xE5, 0x9A, 0x61, 0x11, 0xBE, 0x1D, 0x8C, 0x02, 0x1E, 0x65, 0xB8, 0x91,
         0xC2, 0xA2, 0x11, 0x16, 0x7A, 0xBB, 0x8C, 0x5E, 0x07, 0x9E, 0x09, 0xE2, 0xC8, 0xA8, 0x33, 0x9C
      };

      private static void AddUInt16(List<byte> target, int value)
      {
         target.Add((byte) (value >> 8));
         target.Add((byte) value);
      }

      private static void AddExtension(List<byte> target, int extensionType, byte[] data)
      {
         AddUInt16(target, extensionType);
         AddUInt16(target, data.Length);
         target.AddRange(data);
      }

      private static byte[] BuildClientHello(int[] supportedGroups)
      {
         var extensions = new List<byte>();

         // supported_versions: TLS 1.3 only, so the server cannot answer with a
         // TLS 1.2 ServerHello (which carries no key_share extension to read).
         AddExtension(extensions, 0x002B, new byte[] {0x02, 0x03, 0x04});

         var groupData = new List<byte>();
         AddUInt16(groupData, supportedGroups.Length * 2);
         foreach (int group in supportedGroups)
            AddUInt16(groupData, group);
         AddExtension(extensions, 0x000A, groupData.ToArray());

         // signature_algorithms. Never used - a HelloRetryRequest is decided before
         // the certificate is chosen - but a ClientHello without it is rejected.
         AddExtension(extensions, 0x000D, new byte[]
         {
            0x00, 0x0A,
            0x04, 0x03, // ecdsa_secp256r1_sha256
            0x08, 0x04, // rsa_pss_rsae_sha256
            0x04, 0x01, // rsa_pkcs1_sha256
            0x05, 0x03, // ecdsa_secp384r1_sha384
            0x08, 0x05  // rsa_pss_rsae_sha384
         });

         // key_share with an empty client_shares vector.
         AddExtension(extensions, 0x0033, new byte[] {0x00, 0x00});

         var body = new List<byte>();
         body.Add(0x03);
         body.Add(0x03); // legacy_version

         var filler = new byte[64];
         new Random(20260812).NextBytes(filler);

         for (int i = 0; i < 32; i++)
            body.Add(filler[i]); // random

         body.Add(0x20);
         for (int i = 32; i < 64; i++)
            body.Add(filler[i]); // legacy_session_id (compatibility mode)

         AddUInt16(body, 6);
         body.AddRange(new byte[] {0x13, 0x01, 0x13, 0x02, 0x13, 0x03}); // TLS 1.3 cipher suites

         body.Add(0x01);
         body.Add(0x00); // legacy_compression_methods: null only

         AddUInt16(body, extensions.Count);
         body.AddRange(extensions);

         var handshake = new List<byte>();
         handshake.Add(0x01); // client_hello
         handshake.Add((byte) (body.Count >> 16));
         handshake.Add((byte) (body.Count >> 8));
         handshake.Add((byte) body.Count);
         handshake.AddRange(body);

         var record = new List<byte>();
         record.Add(0x16); // handshake
         record.Add(0x03);
         record.Add(0x01); // legacy record version
         AddUInt16(record, handshake.Count);
         record.AddRange(handshake);

         return record.ToArray();
      }

      private static byte[] ReadExactly(Stream stream, int count)
      {
         var buffer = new byte[count];
         int read = 0;

         while (read < count)
         {
            int thisRead = stream.Read(buffer, read, count - read);
            if (thisRead <= 0)
               Assert.Fail("The server closed the connection after " + read + " of " + count +
                           " expected bytes. It did not answer the ClientHello.");

            read += thisRead;
         }

         return buffer;
      }

      /// <summary>
      ///    Sends the probe ClientHello on a stream positioned at the start of a
      ///    handshake, and returns the group codepoint the server named in its
      ///    HelloRetryRequest. The stream is spent afterwards - the handshake is
      ///    deliberately never completed - so open a fresh connection for anything else.
      /// </summary>
      public static int GetPreferredGroup(Stream stream, int[] offeredGroups)
      {
         byte[] clientHello = BuildClientHello(offeredGroups);
         stream.Write(clientHello, 0, clientHello.Length);
         stream.Flush();

         byte[] header = ReadExactly(stream, 5);
         byte[] payload = ReadExactly(stream, (header[3] << 8) | header[4]);

         if (header[0] == 0x15)
            Assert.Fail("The listener answered with a TLS alert (level " + payload[0] + ", description " +
                        payload[1] + ") instead of a HelloRetryRequest. The server and client share no key " +
                        "exchange group, which means the group list it installed is not usable.");

         Assert.AreEqual(0x16, (int) header[0], "Expected a TLS handshake record.");
         Assert.AreEqual(0x02, (int) payload[0], "Expected a ServerHello handshake message.");

         int pos = 4; // handshake type + 3 length bytes
         pos += 2;    // legacy_version

         for (int i = 0; i < HelloRetryRequestRandom.Length; i++)
            Assert.AreEqual((int) HelloRetryRequestRandom[i], (int) payload[pos + i],
               "The ServerHello random does not match the HelloRetryRequest value, so the server accepted a " +
               "key share we never sent.");
         pos += 32;

         pos += 1 + payload[pos]; // legacy_session_id_echo
         pos += 2;                // cipher_suite
         pos += 1;                // legacy_compression_method

         int extensionsEnd = pos + 2 + ((payload[pos] << 8) | payload[pos + 1]);
         pos += 2;

         while (pos + 4 <= extensionsEnd)
         {
            int extensionType = (payload[pos] << 8) | payload[pos + 1];
            int extensionLength = (payload[pos + 2] << 8) | payload[pos + 3];
            pos += 4;

            if (extensionType == 0x0033)
            {
               Assert.AreEqual(2, extensionLength,
                  "A HelloRetryRequest key_share must hold exactly the selected group.");
               return (payload[pos] << 8) | payload[pos + 1];
            }

            pos += extensionLength;
         }

         Assert.Fail("The HelloRetryRequest contained no key_share extension.");
         return 0;
      }

      /// <summary>
      ///    Asserts that the listener on this stream prefers the hybrid post-quantum
      ///    group over the classical one, which is the observable consequence of taking
      ///    its context from SslContextInitializer rather than building its own.
      /// </summary>
      public static void AssertPrefersPostQuantumGroup(Stream stream, string listenerName)
      {
         int selected = GetPreferredGroup(stream, new[] {GroupX25519Mlkem768, GroupX25519});

         Assert.AreEqual(GroupX25519Mlkem768, selected,
            "The " + listenerName + " listener preferred group 0x" + selected.ToString("X4") +
            " over the hybrid post-quantum group X25519MLKEM768, so it is not using the shared TLS " +
            "configuration from [Settings] TlsKeyExchangeGroups.");
      }
   }
}

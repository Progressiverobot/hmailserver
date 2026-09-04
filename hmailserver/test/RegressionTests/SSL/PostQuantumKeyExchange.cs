// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Linq;
using NUnit.Framework;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;

namespace RegressionTests.SSL
{
   /// <summary>
   /// Every listener used to negotiate classical-only key exchange even though the
   /// build links a PQC-capable OpenSSL: SslContextInitializer replaced OpenSSL's
   /// default group list (which contains X25519MLKEM768) with a hard-coded
   /// "secp384r1:x25519:secp256r1", so the hybrid post-quantum KEMs were compiled
   /// in, linked, and never offered. The list is now configurable
   /// (hMailServer.ini [Settings] TlsKeyExchangeGroups) and defaults to the hybrid
   /// groups first.
   ///
   /// SslStream does not expose which group was negotiated, and Windows SChannel
   /// may not implement the hybrids at all, so a normal TLS client cannot observe
   /// this. These tests therefore speak TLS 1.3 by hand: they send a ClientHello
   /// whose key_share extension carries an *empty* client_shares vector, which is
   /// legal (RFC 8446 4.1.2) and leaves the server with no usable share. The
   /// server must then answer with a HelloRetryRequest that names the group it
   /// prefers - which is precisely the server-side preference we are trying to
   /// pin. Only the first server record is read; the handshake is abandoned.
   ///
   /// With SSL_OP_CIPHER_SERVER_PREFERENCE off - the default, and what these tests
   /// pin - OpenSSL picks the first group in the *client's* order that the server
   /// also has, so the server's list decides which groups are available rather
   /// than which one wins. That is exactly why the change carries no interop risk,
   /// and it is why each test offers the group it expects ahead of a classical one.
   ///
   /// The riskiest part of the change is not the new default but the failure path:
   /// if OpenSSL rejects a configured list, the context must still end up with a
   /// working group list, or TLS breaks on every listener at once. That is what
   /// InvalidGroupList_FallsBackToClassicalGroupsAndKeepsTlsWorking covers.
   /// </summary>
   [TestFixture]
   public class PostQuantumKeyExchange : TestFixtureBase
   {
      [SetUp]
      public new void SetUp()
      {
         // Group selection follows client preference only while this is off, and it
         // is persisted server-side, so pin it rather than inherit it.
         _application.Settings.TlsOptionPreferServerCiphersEnabled = false;
      }

      // IANA TLS supported-group codepoints.
      private const int GroupX25519Mlkem768 = 0x11EC;
      private const int GroupSecP256r1Mlkem768 = 0x11EB;
      private const int GroupX25519 = 0x001D;
      private const int GroupSecp256r1 = 0x0017;
      private const int GroupSecp384r1 = 0x0018;

      private const string SettingName = "TlsKeyExchangeGroups";

      // The fixed ServerHello.random that marks a HelloRetryRequest (RFC 8446 4.1.3).
      private static readonly byte[] HelloRetryRequestRandom =
      {
         0xCF, 0x21, 0xAD, 0x74, 0xE5, 0x9A, 0x61, 0x11, 0xBE, 0x1D, 0x8C, 0x02, 0x1E, 0x65, 0xB8, 0x91,
         0xC2, 0xA2, 0x11, 0x16, 0x7A, 0xBB, 0x8C, 0x5E, 0x07, 0x9E, 0x09, 0xE2, 0xC8, 0xA8, 0x33, 0x9C
      };

      /// <summary>
      /// Writes (or, with a null value, removes) a setting in every hMailServer.ini
      /// we can find, the same way the other ini-driven regression tests do.
      /// </summary>
      private void WriteSetting(string key, string value)
      {
         string programDirectory = _application.Settings.Directories.ProgramDirectory;
         string[] candidates =
         {
            Paths.Combine(programDirectory, "hMailServer.ini"),
            Paths.Combine(programDirectory, "Bin", "hMailServer.ini")
         };

         bool wroteAny = false;
         foreach (string iniPath in candidates.Where(File.Exists))
         {
            Assert.IsTrue(
               IniFile.WritePrivateProfileString("Settings", key, value, iniPath),
               "Failed to write " + key + " to " + iniPath + ".");
            wroteAny = true;
         }

         Assert.IsTrue(wroteAny, "Could not locate an existing hMailServer.ini to update.");
      }

      /// <summary>
      /// Puts the given group list in the ini (null removes the setting, so that the
      /// built-in default applies) and restarts the server so the listeners rebuild
      /// their SSL contexts. Reinitialize, not just Stop/Start: the list is read from
      /// the ini during application initialization.
      /// </summary>
      private void ApplyGroupList(string groups)
      {
         WriteSetting(SettingName, groups);

         SslSetup.SetupSSLPorts(_application);

         _application.Reinitialize();

         Thread.Sleep(1000);
      }

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

      /// <summary>
      /// A TLS 1.3 ClientHello offering the given groups in supported_groups and an
      /// empty client_shares vector in key_share, which forces the server to reveal
      /// its own group preference in a HelloRetryRequest.
      /// </summary>
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

         // signature_algorithms. Never actually used here - a HelloRetryRequest is
         // decided before the certificate is chosen - but a ClientHello without it
         // is rejected outright.
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
      /// Connects to an implicit-TLS listener, sends the hand-built ClientHello and
      /// returns the group codepoint the server named in its HelloRetryRequest.
      /// </summary>
      private static int GetPreferredGroup(int port, int[] offeredGroups)
      {
         using (var client = new TcpClient())
         {
            client.Connect("127.0.0.1", port);
            client.ReceiveTimeout = 20000;
            client.SendTimeout = 20000;

            NetworkStream stream = client.GetStream();

            byte[] clientHello = BuildClientHello(offeredGroups);
            stream.Write(clientHello, 0, clientHello.Length);
            stream.Flush();

            byte[] header = ReadExactly(stream, 5);
            byte[] payload = ReadExactly(stream, (header[3] << 8) | header[4]);

            if (header[0] == 0x15)
               Assert.Fail("Port " + port + " answered with a TLS alert (level " + payload[0] + ", description " +
                           payload[1] + ") instead of a HelloRetryRequest. The server and client share no key " +
                           "exchange group, which means the configured group list is not usable.");

            Assert.AreEqual(0x16, (int) header[0], "Port " + port + ": expected a handshake record.");
            Assert.AreEqual(0x02, (int) payload[0], "Port " + port + ": expected a ServerHello handshake message.");

            int pos = 4; // handshake type + 3 length bytes
            pos += 2;    // legacy_version

            for (int i = 0; i < HelloRetryRequestRandom.Length; i++)
               Assert.AreEqual((int) HelloRetryRequestRandom[i], (int) payload[pos + i],
                  "Port " + port + ": the ServerHello random does not match the HelloRetryRequest value, so the " +
                  "server accepted a key share we never sent.");
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
                     "Port " + port + ": a HelloRetryRequest key_share must hold exactly the selected group.");
                  return (payload[pos] << 8) | payload[pos + 1];
               }

               pos += extensionLength;
            }

            Assert.Fail("Port " + port + ": the HelloRetryRequest contained no key_share extension.");
            return 0;
         }
      }

      private static string DescribeGroup(int group)
      {
         switch (group)
         {
            case GroupX25519Mlkem768: return "X25519MLKEM768";
            case GroupSecP256r1Mlkem768: return "SecP256r1MLKEM768";
            case GroupX25519: return "X25519";
            case GroupSecp256r1: return "secp256r1";
            case GroupSecp384r1: return "secp384r1";
            default: return "group 0x" + group.ToString("X4");
         }
      }

      /// <summary>
      /// Sends and reads a message over the implicit-TLS SMTP, POP3 and IMAP
      /// listeners. Used to prove that whatever group list is in force, ordinary
      /// TLS clients still work - SChannel does not have to know the hybrid groups,
      /// because the group is negotiated and it simply picks a classical one.
      /// </summary>
      private void AssertTlsWorksOnAllListeners(string localPart)
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, localPart + "@example.test", "test");

         var smtp = new SmtpClientSimulator(true, 25001);
         smtp.Send(account.Address, account.Address, "PQ", "Body over TLS");

         Pop3ClientSimulator.AssertMessageCount(account.Address, "test", 1);

         var pop3 = new Pop3ClientSimulator(true, 11001);
         string text = pop3.GetFirstMessageText(account.Address, "test");
         Assert.IsTrue(text.Contains("Body over TLS"), text);

         var imap = new ImapClientSimulator(true, 14301);
         imap.ConnectAndLogon(account.Address, "test");
         Assert.IsTrue(imap.SelectFolder("Inbox"), "Could not select Inbox over TLS.");
         Assert.IsTrue(imap.Logout(), "Could not log out of the TLS IMAP session.");
         imap.Disconnect();
      }

      [Test]
      [Description("With the default configuration every TLS listener prefers the hybrid post-quantum group X25519MLKEM768 over the classical curves")]
      public void DefaultConfiguration_PrefersHybridPostQuantumGroupOnEveryListener()
      {
         // No setting in the ini, so the built-in default applies.
         ApplyGroupList(null);

         // The client offers exactly one hybrid group and one classical group. A
         // server whose list is classical-only can only answer X25519 here, which
         // is what the hard-coded "secp384r1:x25519:secp256r1" did - so this
         // assertion sees X25519 before the fix and X25519MLKEM768 after it.
         int[] offered = {GroupX25519Mlkem768, GroupX25519};

         foreach (int port in new[] {25001, 11001, 14301})
         {
            int selected = GetPreferredGroup(port, offered);

            Assert.AreEqual(GroupX25519Mlkem768, selected,
               "Port " + port + " preferred " + DescribeGroup(selected) +
               " over the hybrid post-quantum group X25519MLKEM768.");
         }
      }

      [Test]
      [Description("TlsKeyExchangeGroups changes which group the listeners prefer")]
      public void ConfiguredGroupList_IsHonoured()
      {
         try
         {
            ApplyGroupList("SecP256r1MLKEM768:X25519");

            // Before the fix the ini setting did not exist and the group list was
            // hard-coded, so the server answered X25519 regardless of what is
            // configured here.
            int selected = GetPreferredGroup(25001, new[] {GroupSecP256r1Mlkem768, GroupX25519});

            Assert.AreEqual(GroupSecP256r1Mlkem768, selected,
               "The configured group list was ignored; the server preferred " + DescribeGroup(selected) + ".");
         }
         finally
         {
            ApplyGroupList(null);
         }
      }

      [Test]
      [Description("A TlsKeyExchangeGroups value OpenSSL rejects falls back to the classical groups instead of leaving the listeners without a usable group")]
      public void InvalidGroupList_FallsBackToClassicalGroupsAndKeepsTlsWorking()
      {
         try
         {
            // An admin typo, or a group name a future OpenSSL drops. Getting this
            // path wrong is the one way this change could take TLS down entirely,
            // so assert both halves: a group is still negotiated, and it comes from
            // the classical fallback list.
            ApplyGroupList("NoSuchGroup:AlsoNotAGroup");

            // The hybrid group is offered first, so a server that still had it would
            // answer X25519MLKEM768. secp384r1 can only be the answer if the
            // classical fallback list is what got installed.
            int selected = GetPreferredGroup(25001, new[] {GroupX25519Mlkem768, GroupSecp384r1});

            Assert.AreEqual(GroupSecp384r1, selected,
               "After rejecting the configured list the server should fall back to the classical list, which offers " +
               "secp384r1 and no hybrid group, but it preferred " + DescribeGroup(selected) + ".");

            AssertTlsWorksOnAllListeners("pqfallback");

            // The fallback must be *reported*, not silent: an administrator whose
            // typo quietly downgraded key exchange on every listener would have no
            // way to find out. AssertReportedError also deletes the ERROR log, which
            // matters beyond this test - the next fixture's PerformBasicSetup calls
            // AssertNoReportedError, so a deliberately provoked error left behind
            // fails an unrelated test in whichever fixture happens to run next.
            CustomAsserts.AssertReportedError("HM5720");
         }
         finally
         {
            ApplyGroupList(null);

            // Safety net. If an assertion above threw before the AssertReportedError
            // call, the provoked errors are still in the log and would take an
            // innocent fixture down with them, hiding the real failure.
            LogHandler.DeleteErrorLog();
         }
      }

      [Test]
      [Description("An empty TlsKeyExchangeGroups value falls back to the classical groups rather than leaving the context without a group list")]
      public void EmptyGroupList_FallsBackToClassicalGroups()
      {
         try
         {
            // OpenSSL accepts an empty group list and then has no group at all, so
            // an empty setting must never reach it - the classical list must be
            // installed instead. If it ever did reach OpenSSL there would be no
            // shared group and this call would see a handshake_failure alert.
            ApplyGroupList("");

            int selected = GetPreferredGroup(25001, new[] {GroupX25519Mlkem768, GroupSecp384r1});

            Assert.AreEqual(GroupSecp384r1, selected,
               "An empty group list should fall back to the classical list, but the server preferred " +
               DescribeGroup(selected) + ".");
         }
         finally
         {
            ApplyGroupList(null);
         }
      }

      [Test]
      [Description("Ordinary TLS clients still connect to every listener with the hybrid post-quantum groups enabled by default")]
      public void HybridGroupsEnabled_TlsStillWorksOnEveryListener()
      {
         ApplyGroupList(null);

         AssertTlsWorksOnAllListeners("pqdefault");
      }
   }
}

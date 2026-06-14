// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

using System.IO;
using System.Runtime.InteropServices;
using hMailServer;
using NUnit.Framework;
using RegressionTests.Shared;

namespace RegressionTests.SMTP
{
   /// <summary>
   /// B4 SRS (Sender Rewriting Scheme) for forwarded mail. When SRS is enabled
   /// (hMailServer.ini [Settings] SRSEnabled/SRSSecret), the envelope MAIL FROM of
   /// a forwarded message from an external sender is rewritten to a signed,
   /// reversible SRS0 address at the local forwarding domain so SPF stays aligned;
   /// a bounce sent back to that address is decoded and relayed to the original
   /// sender. An SRS address whose signature does not validate is rejected.
   /// </summary>
   [TestFixture]
   public class Srs : TestFixtureBase
   {
      [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
      private static extern bool WritePrivateProfileString(string section, string key, string value, string filePath);

      private void WriteSetting(string key, string value)
      {
         // The server reads hMailServer.ini from its bin directory; write to every
         // existing candidate so the file the service actually reads is updated
         // regardless of the install/dev layout.
         string programDirectory = _application.Settings.Directories.ProgramDirectory;
         string[] candidates =
         {
            Path.Combine(programDirectory, "hMailServer.ini"),
            Path.Combine(programDirectory, "Bin", "hMailServer.ini"),
         };

         bool wroteAny = false;
         foreach (string iniPath in candidates)
         {
            if (!File.Exists(iniPath))
               continue;

            Assert.IsTrue(
               WritePrivateProfileString("Settings", key, value, iniPath),
               "Failed to write " + key + " to " + iniPath + ".");
            wroteAny = true;
         }

         Assert.IsTrue(wroteAny, "Could not locate an existing hMailServer.ini to update.");
      }

      private static string ExtractReturnPath(string messageText)
      {
         foreach (string rawLine in messageText.Replace("\r\n", "\n").Split('\n'))
         {
            if (rawLine.StartsWith("Return-Path:"))
            {
               string value = rawLine.Substring("Return-Path:".Length).Trim();
               value = value.TrimStart('<').TrimEnd('>');
               return value;
            }
         }

         return null;
      }

      [Test]
      [Description("With SRS enabled, forwarding a message from an external sender rewrites the " +
                   "envelope MAIL FROM to a signed SRS0 address at the local forwarding domain; the " +
                   "signed address reverses back to the original sender (RCPT accepted), while a " +
                   "tampered SRS0 address fails signature validation and is rejected.")]
      public void TestSrsRewriteAndReverse()
      {
         try
         {
            WriteSetting("SRSEnabled", "1");
            WriteSetting("SRSSecret", "regression-srs-secret");
            _application.Reinitialize();

            var forwarder = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "srsfwd@example.test", "test");
            var destination = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "srsdest@example.test", "test");

            forwarder.ForwardAddress = destination.Address;
            forwarder.ForwardEnabled = true;
            forwarder.Save();

            // An external sender (not a local domain) forwarded to a local mailbox.
            const string externalSender = "alice@external-sender.example";
            SmtpClientSimulator.StaticSend(externalSender, forwarder.Address, "SRS test", "Body");
            _application.SubmitEMail();

            var text = Pop3ClientSimulator.AssertGetFirstMessageText(destination.Address, "test");
            string returnPath = ExtractReturnPath(text);

            Assert.IsNotNull(returnPath, "No Return-Path on the forwarded message: " + text);
            Assert.IsTrue(returnPath.StartsWith("SRS0="),
               "Envelope MAIL FROM was not SRS-rewritten. Return-Path: " + returnPath);
            Assert.IsTrue(returnPath.ToLower().EndsWith("@example.test"),
               "SRS address is not at the forwarding domain. Return-Path: " + returnPath);
            Assert.IsFalse(returnPath.ToLower().Contains(externalSender),
               "Original external sender leaked into the envelope. Return-Path: " + returnPath);

            // The signed SRS0 address reverses to the original sender: RCPT is accepted.
            var socket = new TcpConnection();
            Assert.IsTrue(socket.Connect(25));
            Assert.IsTrue(socket.Receive().StartsWith("220"));
            socket.Send("HELO example.com\r\n");
            Assert.IsTrue(socket.Receive().StartsWith("250"));
            socket.Send("MAIL FROM:<>\r\n");
            Assert.IsTrue(socket.Receive().StartsWith("250"));
            var validRcpt = socket.SendAndReceive("RCPT TO:<" + returnPath + ">\r\n");
            Assert.IsTrue(validRcpt.StartsWith("250"),
               "A valid SRS0 bounce address should reverse and be accepted. Got: " + validRcpt);

            // Tampering the signature makes the address unreversible: RCPT is rejected.
            string tampered = TamperSrsHash(returnPath);
            var tamperedRcpt = socket.SendAndReceive("RCPT TO:<" + tampered + ">\r\n");
            Assert.IsFalse(tamperedRcpt.StartsWith("250"),
               "A tampered SRS0 address must be rejected. Got: " + tamperedRcpt);

            socket.Send("QUIT\r\n");
            socket.Disconnect();
         }
         finally
         {
            WriteSetting("SRSEnabled", "0");
            WriteSetting("SRSSecret", "");
            _application.Reinitialize();
         }
      }

      private static string TamperSrsHash(string srsAddress)
      {
         // The hash is the first field after the "SRS0=" prefix; flip its first char.
         char c = srsAddress[5];
         char replacement = c == '0' ? '1' : '0';
         return srsAddress.Substring(0, 5) + replacement + srsAddress.Substring(6);
      }
   }
}

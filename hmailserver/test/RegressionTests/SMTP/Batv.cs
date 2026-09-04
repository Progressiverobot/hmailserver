// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Collections.Generic;
using System.IO;
using System.Linq;
using hMailServer;
using NUnit.Framework;
using RegressionTests.Shared;

namespace RegressionTests.SMTP
{
   /// <summary>
   /// B4 BATV (Bounce Address Tag Validation), "prvs" scheme. When BATV is enabled
   /// (hMailServer.ini [Settings] BATVEnabled/BATVSecret), the envelope MAIL FROM of
   /// locally-originated outbound mail is signed into a prvs-tagged address at the
   /// sender's own domain so that a bounce returned to it can be validated; a bounce
   /// (null sender) addressed to a prvs return-path whose signature does not validate
   /// is rejected as forged backscatter.
   /// </summary>
   [TestFixture]
   public class Batv : TestFixtureBase
   {

      private void WriteSetting(string key, string value)
      {
         // The server reads hMailServer.ini from its bin directory; write to every
         // existing candidate so the file the service actually reads is updated
         // regardless of the install/dev layout.
         string programDirectory = _application.Settings.Directories.ProgramDirectory;
         string[] candidates =
         {
            Paths.Combine(programDirectory, "hMailServer.ini"),
            Paths.Combine(programDirectory, "Bin", "hMailServer.ini"),
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

      private static string ExtractEnvelopeAddress(string mailFromCommand)
      {
         int start = mailFromCommand.IndexOf('<') + 1;
         int end = mailFromCommand.LastIndexOf('>');
         return mailFromCommand.Substring(start, end - start);
      }

      private static string TamperPrvsHash(string prvsAddress)
      {
         // The 6-hex signature begins at index 9 ("prvs=" + K + DDD); flip its first
         // character so the address is still well-formed but fails validation.
         char c = prvsAddress[9];
         char replacement = c == '0' ? '1' : '0';
         return prvsAddress.Substring(0, 9) + replacement + prvsAddress.Substring(10);
      }

      [Test]
      [Description("With BATV enabled, outbound mail from a local sender has its envelope MAIL FROM " +
                   "signed into a prvs-tagged address at the sender's own domain. The signed address " +
                   "validates and is accepted as a bounce return-path, while a tampered prvs address " +
                   "is rejected as forged backscatter.")]
      public void TestBatvSignAndValidate()
      {
         Route route = null;
         try
         {
            WriteSetting("BATVEnabled", "1");
            WriteSetting("BATVSecret", "regression-batv-secret");
            _application.Reinitialize();

            SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "batvsender@example.test", "test");

            var deliveryResults = new Dictionary<string, int>();
            deliveryResults["catch@example.com"] = 250;

            int port = TestSetup.GetNextFreePort();
            using (var server = new SmtpServerSimulator(1, port))
            {
               server.SecondsToWaitBeforeTerminate = 60;
               server.AddRecipientResult(deliveryResults);
               server.StartListen();

               // Route the (external) destination domain back to the local simulator
               // so the outbound MAIL FROM the server transmits can be inspected.
               route = _settings.Routes.Add();
               route.DomainName = "example.com";
               route.TargetSMTPHost = "localhost";
               route.TargetSMTPPort = port;
               route.NumberOfTries = 1;
               route.MinutesBetweenTry = 5;
               route.Save();

               // A local sender relays out to the routed destination.
               SmtpClientSimulator.StaticSend("batvsender@example.test", "catch@example.com", "BATV test", "Body");
               _application.SubmitEMail();

               server.WaitForCompletion();

               string mailFrom = server.MailFromCommand;
               string envelope = ExtractEnvelopeAddress(mailFrom);

               Assert.IsTrue(envelope.StartsWith("prvs="),
                  "Outbound MAIL FROM was not BATV-signed. MAIL FROM: " + mailFrom);
               Assert.IsTrue(envelope.ToLower().EndsWith("@example.test"),
                  "BATV-signed address is not at the sender's local domain. MAIL FROM: " + mailFrom);
               Assert.IsTrue(envelope.ToLower().Contains("batvsender"),
                  "Original local-part missing from the BATV address. MAIL FROM: " + mailFrom);

               // The signed prvs address validates and reverses to the local sender:
               // a bounce (null sender) addressed to it is accepted.
               var socket = new TcpConnection();
               Assert.IsTrue(socket.Connect(25));
               Assert.IsTrue(socket.Receive().StartsWith("220"));
               socket.Send("HELO example.com\r\n");
               Assert.IsTrue(socket.Receive().StartsWith("250"));
               socket.Send("MAIL FROM:<>\r\n");
               Assert.IsTrue(socket.Receive().StartsWith("250"));
               string validRcpt = socket.SendAndReceive("RCPT TO:<" + envelope + ">\r\n");
               Assert.IsTrue(validRcpt.StartsWith("250"),
                  "A valid BATV bounce address should validate and be accepted. Got: " + validRcpt);

               // A tampered signature makes the address forged backscatter: rejected.
               string tampered = TamperPrvsHash(envelope);
               string tamperedRcpt = socket.SendAndReceive("RCPT TO:<" + tampered + ">\r\n");
               Assert.IsFalse(tamperedRcpt.StartsWith("250"),
                  "A tampered BATV address must be rejected as backscatter. Got: " + tamperedRcpt);

               socket.Send("QUIT\r\n");
               socket.Disconnect();
            }
         }
         finally
         {
            if (route != null)
               _settings.Routes.DeleteByDBID(route.ID);

            WriteSetting("BATVEnabled", "0");
            WriteSetting("BATVSecret", "");
            _application.Reinitialize();
         }
      }
   }
}

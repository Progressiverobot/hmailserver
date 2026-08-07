using System;
using System.Text;
using hMailServer;
using NUnit.Framework;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;

namespace RegressionTests.AntiVirus
{
   [TestFixture]
   public class ClamAV : TestFixtureBase
   {
      [SetUp]
      public new void SetUp()
      {
         CustomAsserts.AssertClamDRunning();

         _antiVirus = _application.Settings.AntiVirus;

         _antiVirus.Action = eAntivirusAction.hDeleteEmail;
      }

      private hMailServer.AntiVirus _antiVirus;

      [Test]
      public void TestIncorrectPort()
      {
         _antiVirus.ClamAVEnabled = true;
         _antiVirus.ClamAVPort = 110;

         var account1 = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");
         SmtpClientSimulator.StaticSend(account1.Address, account1.Address, "Mail 1", "DummyBody");
         Pop3ClientSimulator.AssertMessageCount(account1.Address, "test", 1);

         // +OK POP3, since we are connecting to POP3 port
         var defaultLog = LogHandler.ReadCurrentDefaultLog();
         Assert.IsTrue(defaultLog.Contains("No virus detected: +OK POP3"));
      }

      [Test]
      public void TestNoVirus()
      {
         _antiVirus.ClamAVEnabled = true;

         var account1 = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");
         SmtpClientSimulator.StaticSend(account1.Address, account1.Address, "Mail 1", "Mail 1");
         Pop3ClientSimulator.AssertMessageCount(account1.Address, "test", 1);
      }

      [Test]
      public void TestNotEnabled()
      {
         LogHandler.DeleteCurrentDefaultLog();
         var account1 = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");
         SmtpClientSimulator.StaticSend(account1.Address, account1.Address, "Mail 1", "Mail 1");
         Pop3ClientSimulator.AssertMessageCount(account1.Address, "test", 1);
         var defaultLog = LogHandler.ReadCurrentDefaultLog();
         Assert.IsFalse(defaultLog.Contains("Connecting to ClamAV"));
      }

      [Test]
      public void TestUnusedPort()
      {
         _antiVirus.ClamAVEnabled = true;
         _antiVirus.ClamAVPort = 54391;

         var account1 = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");
         SmtpClientSimulator.StaticSend(account1.Address, account1.Address, "Mail 1", "DummyBody");
         Pop3ClientSimulator.AssertMessageCount(account1.Address, "test", 1);
         CustomAsserts.AssertReportedError("Unable to connect to ClamAV server at localhost:54391.");
      }

      [Test]
      public void TestWithVirus()
      {
         _antiVirus.ClamAVEnabled = true;
         LogHandler.DeleteCurrentDefaultLog();

         var account1 = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");

         SmtpClientSimulator.StaticSendRaw(account1.Address, account1.Address,
            BuildMessageWithEicarAttachment(account1.Address));

         CustomAsserts.AssertRecipientsInDeliveryQueue(0);
         Pop3ClientSimulator.AssertMessageCount(account1.Address, "test", 0);

         var defaultLog = LogHandler.ReadCurrentDefaultLog();
         Assert.IsTrue(defaultLog.Contains("Connecting to ClamAV"));

         // The signature name has changed over ClamAV releases - a bare EICAR file
         // is Eicar-Test-Signature, the same file with a trailing CRLF is
         // Eicar-Signature - so match the family rather than one exact name.
         StringAssert.IsMatch(@"Message will be deleted \(contained virus Eicar[-\w]*\)\.", defaultLog);
      }

      /// <summary>
      /// Builds a MIME message carrying EICAR as a base64 attachment.
      ///
      /// It has to be an attachment. ClamAV's EICAR signatures match a whole file,
      /// not a substring: the 68-byte string on its own is Eicar-Test-Signature and
      /// with a trailing CRLF it is Eicar-Signature, but the same string with any
      /// other content around it is not detected at all. This test used to put
      /// EICAR straight in the body of a non-MIME message, which older ClamAV
      /// extracted as a scannable part and current ClamAV does not - so the message
      /// scanned clean and was delivered. As an attachment ClamAV decodes the
      /// base64 back to exactly the EICAR file and matches, which is also how a
      /// virus would actually arrive.
      ///
      /// The string is assembled from fragments and encoded at run time so that
      /// neither it nor its base64 form is ever a literal in this source file -
      /// on-access virus scanners quarantine files that contain either.
      /// </summary>
      private static string BuildMessageWithEicarAttachment(string address)
      {
         var firstPart = @"X5O!P%@AP[4\PZX54(P^)7CC)7}";
         var secondPart = @"$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*";
         var encoded = Convert.ToBase64String(Encoding.ASCII.GetBytes(firstPart + secondPart));

         return
            "From: " + address + "\r\n" +
            "To: " + address + "\r\n" +
            "Subject: Mail 1\r\n" +
            "MIME-Version: 1.0\r\n" +
            "Content-Type: multipart/mixed; boundary=\"eicar-boundary\"\r\n" +
            "\r\n" +
            "--eicar-boundary\r\n" +
            "Content-Type: text/plain\r\n" +
            "\r\n" +
            "Test message.\r\n" +
            "\r\n" +
            "--eicar-boundary\r\n" +
            "Content-Type: application/octet-stream; name=\"eicar.com\"\r\n" +
            "Content-Transfer-Encoding: base64\r\n" +
            "Content-Disposition: attachment; filename=\"eicar.com\"\r\n" +
            "\r\n" +
            encoded + "\r\n" +
            "\r\n" +
            "--eicar-boundary--\r\n";
      }
   }
}
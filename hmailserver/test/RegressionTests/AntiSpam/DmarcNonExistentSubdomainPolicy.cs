// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com

using System.Text.RegularExpressions;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Shared;

namespace RegressionTests.AntiSpam
{
   /// <summary>
   ///    The RFC 9989 np= tag: the policy for subdomains that do not exist.
   ///
   ///    p= covers the domain and sp= covers its subdomains, and between them they
   ///    leave the cheapest forgery there is uncovered. A phisher does not need a
   ///    subdomain to be delegated to them - they only need the From header to say
   ///    "accounts.thebank.test", and no domain owner can publish a record for a name
   ///    they have never heard of. np= is the answer: whatever else the domain says,
   ///    a subdomain that is not in the DNS at all gets this policy.
   ///
   ///    The distinction the whole tag rests on is NXDOMAIN versus NODATA, and it is
   ///    easy to get wrong in the direction that rejects real mail. A subdomain with
   ///    no A record still exists if it holds an MX, a TXT, or merely something
   ///    beneath it - and treating those as non-existent would apply np=reject, which
   ///    is usually the strictest policy the domain publishes, to mail from real
   ///    parts of the sender's own estate. So both cases are tested here, and the
   ///    NODATA one is the more important of the two.
   /// </summary>
   [TestFixture]
   public class DmarcNonExistentSubdomainPolicy : TestFixtureBase
   {
      private const string PolicyDomain = "np-domain.test";

      private static string DmarcVerdict(string messageText)
      {
         var match = Regex.Match(messageText, @"dmarc=(\w+)(\s*\([^)]*\))?", RegexOptions.IgnoreCase);

         if (!match.Success)
            return "";

         return ("dmarc=" + match.Groups[1].Value + match.Groups[2].Value)
            .Replace(" ", "").ToLowerInvariant();
      }

      private static string SendAndReadVerdict(string address, string fromDomain)
      {
         string message =
            "From: <user@" + fromDomain + ">\r\n" +
            "To: <" + address + ">\r\n" +
            "Subject: np probe\r\n" +
            "Date: Thu, 20 Aug 2026 10:00:00 +0000\r\n" +
            "\r\n" +
            "Probe body\r\n";

         // An envelope sender under another organizational domain, so nothing can
         // align and every message here reaches the policy decision.
         SmtpClientSimulator.StaticSendRaw("bounce@unaligned-envelope.test", address, message);

         ImapClientSimulator.AssertMessageCount(address, "test", "Inbox", 1);

         var imap = new ImapClientSimulator();
         imap.ConnectAndLogon(address, "test");
         imap.SelectFolder("Inbox");

         return imap.Fetch("1 RFC822");
      }

      [Test]
      [Description("np=reject applies to a subdomain that answers NXDOMAIN, while a subdomain that exists but has no A record - NODATA - is judged by sp= instead. The second half is what keeps np= from rejecting mail from real subdomains.")]
      public void NpAppliesOnlyToSubdomainsThatAreTrulyAbsent()
      {
         var antiSpam = _application.Settings.AntiSpam;
         antiSpam.SpamMarkThreshold = 100;
         antiSpam.SpamDeleteThreshold = 1000;
         antiSpam.DMARCEnabled = true;

         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "np-ghost@example.test", "test");
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "np-real@example.test", "test");

         ServerIniFile.SetSetting("AuthenticationResultsEnabled", "1");
         ServerIniFile.SetSetting("DNSServer", "127.0.0.1");
         ServerIniFile.SetSetting("DNSQueryTimeout", "5");

         // sp=none and np=reject deliberately disagree, so the verdict names which
         // tag was applied and no other reading is possible.
         using (new FakeDnsServer()
            .WithTxt("_dmarc." + PolicyDomain, "v=DMARC1; p=none; sp=none; np=reject; psd=n")

            // The subdomain that does not exist. Without this the fake server would
            // answer NODATA, the server would correctly conclude the name exists,
            // and np= would never be reached.
            .WithNxDomain("ghost." + PolicyDomain)

            // ...and one that exists while having no address of its own: it holds an
            // MX, which is the ordinary shape for a name that only receives mail.
            .WithMx("real." + PolicyDomain, 10, "mx." + PolicyDomain)
            .WithA("mx." + PolicyDomain, "127.0.0.1"))
         {
            try
            {
               RestartServerAndReacquireCom();

               string ghost = SendAndReadVerdict("np-ghost@example.test", "ghost." + PolicyDomain);

               ClassicAssert.AreEqual("dmarc=fail(p=reject)", DmarcVerdict(ghost),
                  "A subdomain that answers NXDOMAIN must be judged by np=reject, not by the " +
                  "sp=none published beside it. This is the forgery np= exists for: the phisher " +
                  "picks a name the domain owner has never heard of.\r\n" + ghost);

               string real = SendAndReadVerdict("np-real@example.test", "real." + PolicyDomain);

               ClassicAssert.AreEqual("dmarc=fail(p=none)", DmarcVerdict(real),
                  "A subdomain with an MX and no A record EXISTS - the query answers NOERROR with " +
                  "no address, not NXDOMAIN - so sp=none applies. Reading NODATA as absence would " +
                  "apply np=reject to real parts of a sender's own estate, which is the failure " +
                  "that rejects legitimate mail.\r\n" + real);
            }
            finally
            {
               ServerIniFile.SetSetting("AuthenticationResultsEnabled", null);
               ServerIniFile.SetSetting("DNSServer", null);
               ServerIniFile.SetSetting("DNSQueryTimeout", null);
               RestartServerAndReacquireCom();
            }
         }
      }
   }
}

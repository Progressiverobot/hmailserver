// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// http://www.hmailserver.com

using System.Reflection;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;
using hMailServer;

namespace RegressionTests.Sieve
{
   /// <summary>
   ///    The date and currentdate tests and :index/:last (RFC 5260), asserted
   ///    through real deliveries.
   ///
   ///    The date tests are the first in this engine where the VALUE needs
   ///    arithmetic rather than extraction, and the arithmetic is where they go
   ///    wrong: a zone conversion that mangles the instant still passes every test
   ///    written in the message's own zone. So the decisive test here files on an
   ///    HOUR THE RAW HEADER NEVER STATES - "15:30 +0200" matched as hour "13"
   ///    under :zone "+0000" - which only a correct conversion can produce.
   ///
   ///    The date header used everywhere is fixed - Tue, 05 Aug 2003 - so weekday
   ///    and calendar assertions are stable for ever.
   /// </summary>
   [TestFixture]
   public class SieveDateDelivery : TestFixtureBase
   {
      private const string Password = "secret";
      private const string TargetFolder = "Matched";
      private const string FixedDateHeader = "Tue, 05 Aug 2003 15:30:00 +0200";

      private static void SetScript(Account account, string script)
      {
         account.GetType().InvokeMember(
            "SieveScript", BindingFlags.SetProperty, null, account, new object[] { script });
      }

      /// <summary>
      ///    Delivers a message carrying the fixed Date header (plus optional extra
      ///    headers) under the given script, and answers where it landed.
      /// </summary>
      // Some tests deliver twice (a positive and its control), and an account
      // address can only be added once per fixture set-up.
      private static int accountSequence_;

      private bool FilesIntoMatchedFolder(string script, string extraHeaders = "")
      {
         accountSequence_++;
         Account recipient = SingletonProvider<TestSetup>.Instance.AddAccount(
            _domain, "sieve-date-" + accountSequence_ + "@example.test", Password);

         recipient.IMAPFolders.Add(TargetFolder);

         SetScript(recipient, script);

         string raw =
            "From: sieve-date-sender@example.test\r\n" +
            "To: sieve-date@example.test\r\n" +
            "Subject: Date test\r\n" +
            "Date: " + FixedDateHeader + "\r\n" +
            extraHeaders +
            "\r\n" +
            "Body text.\r\n";

         var smtp = new SmtpClientSimulator();
         smtp.SendRaw("sieve-date-sender@example.test", recipient.Address, raw);

         CustomAsserts.AssertRecipientsInDeliveryQueue(0);

         IMAPFolder inbox = recipient.IMAPFolders.get_ItemByName("INBOX");
         IMAPFolder matched = recipient.IMAPFolders.get_ItemByName(TargetFolder);

         int inInbox = inbox.Messages.Count;
         int inMatched = matched.Messages.Count;

         Assert.AreEqual(1, inInbox + inMatched,
            "Exactly one copy of the message should exist. INBOX has " + inInbox + " and " +
            TargetFolder + " has " + inMatched + ".");

         return inMatched == 1;
      }

      private static string FileIntoIf(string test)
      {
         return "require [\"date\", \"index\", \"fileinto\"];\r\n" +
                "if " + test + " {\r\n" +
                "  fileinto \"" + TargetFolder + "\";\r\n" +
                "}\r\n";
      }

      [Test]
      [Description("date :originalzone reads the header's own wall clock: hour 15, zone +0200.")]
      public void OriginalzoneReadsTheHeadersOwnClock()
      {
         Assert.IsTrue(
            FilesIntoMatchedFolder(FileIntoIf("allof(date :originalzone \"date\" \"hour\" \"15\", date :originalzone \"date\" \"zone\" \"+0200\")")),
            "':originalzone' did not reproduce the wall clock and zone the header itself states.");
      }

      /// <summary>
      ///    The decisive test: 15:30 in +0200 is 13:30 in +0000, and "13" appears
      ///    nowhere in the transmitted bytes. Only a correct conversion between the
      ///    header's zone and the requested one can file this message.
      /// </summary>
      [Test]
      [Description("date :zone \"+0000\" re-expresses the instant: hour 13, which the raw header never states.")]
      public void ZoneConversionProducesAnHourTheHeaderNeverStates()
      {
         Assert.IsTrue(
            FilesIntoMatchedFolder(FileIntoIf("date :zone \"+0000\" \"date\" \"hour\" \"13\"")),
            "':zone \"+0000\"' did not convert 15:30 +0200 to hour 13 - the zone arithmetic is wrong, " +
            "and every date filter written across zones goes to the wrong branch.");
      }

      /// <summary>
      ///    The negative control: the same conversion must NOT also report the
      ///    header's own hour. An implementation that ignores :zone and reads the
      ///    wall clock passes the positive tests written in the original zone and
      ///    fails only here.
      /// </summary>
      [Test]
      [Description("Under :zone \"+0000\" the header's own hour (15) does not match - the control the rest depend on.")]
      public void ZoneConversionDoesNotAlsoMatchTheUnconvertedHour()
      {
         Assert.IsFalse(
            FilesIntoMatchedFolder(FileIntoIf("date :zone \"+0000\" \"date\" \"hour\" \"15\"")),
            "':zone \"+0000\"' matched the UNCONVERTED hour, so the zone tag is being ignored.");
      }

      [Test]
      [Description("5 August 2003 was a Tuesday: weekday is 2 (0 = Sunday).")]
      public void WeekdayOfTheFixedDateIsTuesday()
      {
         Assert.IsTrue(
            FilesIntoMatchedFolder(FileIntoIf("date :originalzone \"date\" \"weekday\" \"2\"")),
            "The weekday part did not report Tuesday as 2; RFC 5260 counts 0 = Sunday.");
      }

      [Test]
      [Description("The date part composes yyyy-mm-dd, usable with relational comparisons.")]
      public void TheDatePartSupportsRelationalComparison()
      {
         // "Is this message from before 2010?" - the shape date filters actually
         // take in the wild, via i;ascii-numeric on yyyymmdd-free parts. The
         // "date" part is compared as a string here: :is proves composition.
         Assert.IsTrue(
            FilesIntoMatchedFolder(FileIntoIf("date :originalzone \"date\" \"date\" \"2003-08-05\"")),
            "The date part did not compose yyyy-mm-dd from the header.");
      }

      [Test]
      [Description("currentdate \"year\" matches the year this test is running in.")]
      public void CurrentdateMatchesTheCurrentYear()
      {
         string year = System.DateTime.Now.Year.ToString("D4");

         Assert.IsTrue(
            FilesIntoMatchedFolder(FileIntoIf("currentdate \"year\" \"" + year + "\"")),
            "'currentdate \"year\"' did not match the year the server is running in (" + year + ").");
      }

      [Test]
      [Description("currentdate does not match a year that is long gone.")]
      public void CurrentdateDoesNotMatchTheWrongYear()
      {
         Assert.IsFalse(
            FilesIntoMatchedFolder(FileIntoIf("currentdate \"year\" \"1999\"")),
            "'currentdate \"year\"' matched 1999, so it is matching everything rather than the clock.");
      }

      /// <summary>
      ///    :index on the header test: two X-Priority-Route headers, and the script
      ///    matches only the second. Without :index both instances are searched and
      ///    the test cannot distinguish them; the control asserts index 1 does NOT
      ///    see the second value.
      /// </summary>
      [Test]
      [Description(":index 2 selects the second instance of a repeated header; :index 1 does not see it.")]
      public void IndexSelectsAmongRepeatedHeaders()
      {
         const string extraHeaders =
            "X-Priority-Route: alpha\r\n" +
            "X-Priority-Route: beta\r\n";

         Assert.IsTrue(
            FilesIntoMatchedFolder(FileIntoIf("header :index 2 :is \"X-Priority-Route\" \"beta\""), extraHeaders),
            "':index 2' did not select the second instance of the repeated header.");

         Assert.IsFalse(
            FilesIntoMatchedFolder(FileIntoIf("header :index 1 :is \"X-Priority-Route\" \"beta\""), extraHeaders),
            "':index 1' matched the SECOND instance, so the index is not selecting at all.");
      }

      [Test]
      [Description(":index 2 :last counts from the end, selecting the first of the two.")]
      public void IndexLastCountsFromTheEnd()
      {
         const string extraHeaders =
            "X-Priority-Route: alpha\r\n" +
            "X-Priority-Route: beta\r\n";

         Assert.IsTrue(
            FilesIntoMatchedFolder(FileIntoIf("header :index 2 :last :is \"X-Priority-Route\" \"alpha\""), extraHeaders),
            "':index 2 :last' did not count from the end.");
      }
   }
}

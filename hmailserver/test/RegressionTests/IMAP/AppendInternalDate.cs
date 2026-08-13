// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

using NUnit.Framework;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;

namespace RegressionTests.IMAP
{
   /// <summary>
   ///    The internal date supplied with APPEND, which is any quoted word in the
   ///    command and is not validated anywhere before it reaches
   ///    Time::GetInternalDateFromIMAPInternalDate.
   ///
   ///    That function checked the size of the DATE fields where it meant to check the
   ///    size of the TIME fields - the same expression twice, a copy of the line above
   ///    it - and then read vecTimeParts[1] and [2] regardless. A time with fewer than
   ///    three colon-separated fields therefore indexed one or two elements past the
   ///    end of a std::vector&lt;CStdStringW&gt;, interpreted the heap that followed the
   ///    allocation as a string object, and passed it to _ttoi, which dereferences its
   ///    buffer pointer.
   ///
   ///    Two ways to see it, and both are asserted below, because which one fires
   ///    depends on what happens to be in the adjacent heap:
   ///
   ///      - it faults, and the crash oracle in TestFixtureBase records the access
   ///        violation; or
   ///      - it does not fault, and the minute or second of the stored internal date
   ///        is whatever those bytes decoded to.
   ///
   ///    Neither shape is visible from a passing APPEND alone, which is why the
   ///    existing Append fixture never caught it: the command still returns OK.
   /// </summary>
   [TestFixture]
   public class AppendInternalDate : TestFixtureBase
   {
      [Test]
      [Description("APPEND with an internal date that has no seconds must not read past the end of its parsed time fields.")]
      public void AppendWithInternalDateMissingSeconds()
      {
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "date@example.test", "test");

         var simulator = new ImapClientSimulator();
         simulator.Connect();
         simulator.LogonWithLiteral("date@example.test", "test");

         var result = simulator.SendSingleCommandWithLiteral(
            "A01 APPEND INBOX \"01-Jan-2020 12:30\" {4}", "ABCD");

         Assert.IsTrue(result.Contains("A01 OK"), result);
         Assert.AreEqual(1, simulator.GetMessageCount("INBOX"));
         simulator.Disconnect();

         // The seconds field is the one that was read out of bounds. It must be the
         // zero we substituted, and the hour and minute must have survived intact.
         var reader = new ImapClientSimulator();
         reader.ConnectAndLogon("date@example.test", "test");
         reader.SelectFolder("INBOX");
         var internalDate = reader.Fetch("1 INTERNALDATE");
         reader.Disconnect();

         // " 1-Jan-2020", not "01-Jan-2020". RFC 3501 date-day-fixed is
         // "(SP DIGIT) / 2DIGIT", so a single-digit day is space-padded on the way OUT
         // even though the client may send it either way on the way in. The server is
         // right here; this assertion was written expecting a zero and is kept in the
         // RFC's shape so that a future reader does not "correct" it back.
         Assert.IsTrue(internalDate.Contains(" 1-Jan-2020 12:30:00"),
            "Expected the internal date to be  1-Jan-2020 12:30:00. Got: " + internalDate);
      }

      [Test]
      [Description("APPEND with an internal date carrying no time at all must not read past the end of its parsed time fields.")]
      public void AppendWithInternalDateMissingTime()
      {
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "date@example.test", "test");

         var simulator = new ImapClientSimulator();
         simulator.Connect();
         simulator.LogonWithLiteral("date@example.test", "test");

         // A date and a zone, no time. Both the minute and the second were read past
         // the end of the vector here, not just the second.
         var result = simulator.SendSingleCommandWithLiteral(
            "A01 APPEND INBOX \"01-Jan-2020 +0000\" {4}", "ABCD");

         Assert.IsTrue(result.Contains("A01 OK"), result);
         Assert.AreEqual(1, simulator.GetMessageCount("INBOX"));
         simulator.Disconnect();

         var reader = new ImapClientSimulator();
         reader.ConnectAndLogon("date@example.test", "test");
         reader.SelectFolder("INBOX");
         var internalDate = reader.Fetch("1 INTERNALDATE");
         reader.Disconnect();

         // Space-padded day again - see the note in AppendWithInternalDateMissingSeconds.
         Assert.IsTrue(internalDate.Contains(" 1-Jan-2020 00:00:00"),
            "Expected the internal date to be  1-Jan-2020 00:00:00. Got: " + internalDate);
      }

      [Test]
      [Description("A well-formed APPEND internal date still round-trips, so the tolerance above cannot pass by the parse having stopped working.")]
      public void AppendWithWellFormedInternalDate()
      {
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "date@example.test", "test");

         var simulator = new ImapClientSimulator();
         simulator.Connect();
         simulator.LogonWithLiteral("date@example.test", "test");

         var result = simulator.SendSingleCommandWithLiteral(
            "A01 APPEND INBOX \"30-Apr-2004 17:38:48 +0200\" {4}", "ABCD");

         Assert.IsTrue(result.Contains("A01 OK"), result);
         Assert.AreEqual(1, simulator.GetMessageCount("INBOX"));
         simulator.Disconnect();

         var reader = new ImapClientSimulator();
         reader.ConnectAndLogon("date@example.test", "test");
         reader.SelectFolder("INBOX");
         var internalDate = reader.Fetch("1 INTERNALDATE");
         reader.Disconnect();

         // The zone is discarded by the conversion - it always was - so only the
         // date and clock time are pinned here.
         Assert.IsTrue(internalDate.Contains("30-Apr-2004 17:38:48"),
            "Expected the internal date to be 30-Apr-2004 17:38:48. Got: " + internalDate);
      }

      [Test]
      [Description("A run of malformed internal dates on one session must leave the connection and the service usable.")]
      public void AppendWithRepeatedMalformedInternalDates()
      {
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "date@example.test", "test");

         // Several shapes in one session. Repetition matters for an out-of-bounds
         // read: a single one may land on heap that happens to decode harmlessly,
         // and the whole point is that the outcome was not deterministic.
         //
         // The last three are the other half of the defect. A garbage minute or
         // second, a month name that does not parse, and an impossible clock time
         // were all formatted and handed to Message::SetCreateTime, which writes to a
         // datetime column - so the INSERT failed, and because the APPEND path does
         // not check the result of the save the client was still answered OK. The
         // message count assertion after the loop is what catches that: the file is
         // on disk, the row is not, and no client can see it.
         var malformed = new[]
         {
            "01-Jan-2020 12",
            "01-Jan-2020 12:",
            "01-Jan-2020 :",
            "01-Jan-2020 12:30",
            "01-Jan-2020 abc",
            "01-Jan-2020 +0000",
            "1-Jan-2020 0",
            "01-Jan-2020 99:99",
            "01-Marcha-2020 10:00:00",
            "01-Jan-2020 24:00:00"
         };

         var simulator = new ImapClientSimulator();
         simulator.Connect();
         simulator.LogonWithLiteral("date@example.test", "test");

         for (var i = 0; i < malformed.Length; i++)
         {
            var tag = "A" + (i + 10);
            var result = simulator.SendSingleCommandWithLiteral(
               tag + " APPEND INBOX \"" + malformed[i] + "\" {4}", "ABCD");

            Assert.IsTrue(result.Contains(tag + " OK"), malformed[i] + " -> " + result);
         }

         Assert.AreEqual(malformed.Length, simulator.GetMessageCount("INBOX"));
         simulator.Disconnect();

         // And the session after all that is an ordinary one. If the server had gone
         // down, this is where it shows.
         var reader = new ImapClientSimulator();
         Assert.IsTrue(reader.ConnectAndLogon("date@example.test", "test"));
         Assert.AreEqual(malformed.Length, reader.GetMessageCount("INBOX"));
         reader.Disconnect();
      }
   }
}

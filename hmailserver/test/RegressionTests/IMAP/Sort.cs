// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using NUnit.Framework;
using RegressionTests.Shared;

namespace RegressionTests.IMAP
{
   [TestFixture]
   public class Sort : TestFixtureBase
   {
      [SetUp]
      public new void SetUp()
      {
         _application.Settings.IMAPSortEnabled = true;

         base.SetUp();
      }

      [Test]
      [Description("Issue 340, Incorrect date sorting order")]
      public void TestDateSortOrder()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "imapsort@example.test", "test");
         var simulator = new ImapClientSimulator();

         var sWelcomeMessage = simulator.Connect();
         simulator.LogonWithLiteral("imapsort@example.test", "test");
         Assert.IsTrue(simulator.SelectFolder("Inbox"));

         var response =
            simulator.SendSingleCommandWithLiteral("A04 APPEND INBOX \"22-Feb-2008 22:00:00 +0200\" {37}",
               "Date: Wed, 15 Dec 2010 13:00:00 +0000");
         Assert.IsTrue(response.Contains("* 1 EXISTS"), response);

         response = simulator.SendSingleCommandWithLiteral("A04 APPEND INBOX \"22-Feb-2008 21:00:00 +0200\" {37}",
            "Date: Wed, 15 Dec 2010 14:00:00 +0000");
         Assert.IsTrue(response.Contains("* 2 EXISTS"), response);

         response = simulator.SendSingleCommandWithLiteral("A04 APPEND INBOX \"22-Feb-2008 20:00:00 +0200\" {37}",
            "Date: Wed, 15 Dec 2010 12:00:00 +0000");
         Assert.IsTrue(response.Contains("* 3 EXISTS"), response);

         response = simulator.SendSingleCommandWithLiteral("A04 APPEND INBOX \"23-Feb-2008 01:30:23 +0200\" {37}",
            "Date: Wed, 15 Dec 2010 11:00:00 +0000");
         Assert.IsTrue(response.Contains("* 4 EXISTS"), response);

         var sortDateResponse = simulator.SendSingleCommand("A10 SORT (DATE) US-ASCII ALL");

         Assert.IsTrue(sortDateResponse.Contains(" 4 3 1 2"));
         simulator.Disconnect();
      }

      [Test]
      [Description("Issue 340, Incorrect date sorting order")]
      public void TestDateSortOrderNonexistantDate()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "imapsort@example.test", "test");
         var simulator = new ImapClientSimulator();

         var sWelcomeMessage = simulator.Connect();
         simulator.LogonWithLiteral("imapsort@example.test", "test");
         Assert.IsTrue(simulator.SelectFolder("Inbox"));

         var response = simulator.SendSingleCommandWithLiteral(
            "A04 APPEND INBOX \"22-Feb-2008 22:00:00 +0200\" {4}", "ABCD");
         Assert.IsTrue(response.Contains("* 1 EXISTS"), response);

         response = simulator.SendSingleCommandWithLiteral("A04 APPEND INBOX \"22-Feb-2008 21:00:00 +0200\" {4}",
            "ABCD");
         Assert.IsTrue(response.Contains("* 2 EXISTS"), response);

         response = simulator.SendSingleCommandWithLiteral("A04 APPEND INBOX \"22-Feb-2008 20:00:00 +0200\" {4}",
            "ABCD");
         Assert.IsTrue(response.Contains("* 3 EXISTS"), response);

         response = simulator.SendSingleCommandWithLiteral("A04 APPEND INBOX \"23-Feb-2008 01:30:23 +0200\" {4}",
            "ABCD");
         Assert.IsTrue(response.Contains("* 4 EXISTS"), response);

         /*
          * RFC 5256 "2.2. Sent Date" chapter. If the sent date cannot be determined (a Date: header is missing or cannot be parsed), 
          * the INTERNALDATE for that message is used as the sent date.
          */

         var sortDateResponse = simulator.SendSingleCommand("A10 SORT (DATE) US-ASCII ALL");
         var sortArivalDateResponse = simulator.SendSingleCommand("A10 SORT (ARRIVAL) US-ASCII ALL");

         Assert.IsTrue(sortArivalDateResponse.Contains(" 3 2 1 4"));
         Assert.AreEqual(sortDateResponse, sortArivalDateResponse);
         simulator.Disconnect();
      }

      /// <summary>
      ///    A Date header that is PRESENT but unparseable must sort by INTERNALDATE,
      ///    not by the 1899 OLE epoch.
      ///
      ///    TestDateSortOrderNonexistantDate covers a MISSING date, and that is
      ///    exactly how the defect survived: GetDateTimeFromMimeHeader answers an
      ///    invalid DateTime for garbage, whose raw value stringifies to a perfectly
      ///    non-empty 1899 timestamp, so the emptiness fallback never fired and the
      ///    most malformed message in the mailbox sorted to the front. The fix tests
      ///    validity. Found by the adversarial pass over THREAD, which had inherited
      ///    the same shape from this code.
      ///
      ///    Discrimination is by construction: the malformed message carries the
      ///    LATEST internaldate, so the fixed code sorts it last and the broken code
      ///    sorts it first - there is no order both produce.
      /// </summary>
      [Test]
      public void TestDateSortOrderUnparseableDate()
      {
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "imapsort@example.test", "test");
         var simulator = new ImapClientSimulator();

         simulator.Connect();
         simulator.LogonWithLiteral("imapsort@example.test", "test");
         Assert.IsTrue(simulator.SelectFolder("Inbox"));

         // Message 1: valid Date in 2020.
         string msg1 = "Date: Sat, 15 Aug 2020 10:00:00 +0000\r\nSubject: a\r\n\r\nBody";
         var response = simulator.SendSingleCommandWithLiteral(
            "A01 APPEND INBOX \"01-Jan-2019 10:00:00 +0000\" {" + msg1.Length + "}", msg1);
         Assert.IsTrue(response.Contains("* 1 EXISTS"), response);

         // Message 2: GARBAGE Date, internaldate mid-2025 - the latest of the three.
         string msg2 = "Date: not-a-date-at-all\r\nSubject: b\r\n\r\nBody";
         response = simulator.SendSingleCommandWithLiteral(
            "A02 APPEND INBOX \"01-Jun-2025 10:00:00 +0000\" {" + msg2.Length + "}", msg2);
         Assert.IsTrue(response.Contains("* 2 EXISTS"), response);

         // Message 3: valid Date in 2010.
         string msg3 = "Date: Sun, 15 Aug 2010 10:00:00 +0000\r\nSubject: c\r\n\r\nBody";
         response = simulator.SendSingleCommandWithLiteral(
            "A03 APPEND INBOX \"01-Jan-2019 11:00:00 +0000\" {" + msg3.Length + "}", msg3);
         Assert.IsTrue(response.Contains("* 3 EXISTS"), response);

         string sortResponse = simulator.SendSingleCommand("A04 SORT (DATE) US-ASCII ALL");

         // Fixed: 2010, 2020, then the malformed one by its 2025 internaldate.
         // Broken: the malformed one first, at the 1899 epoch.
         Assert.IsTrue(sortResponse.Contains("* SORT 3 1 2"),
            "A message with an unparseable Date did not sort by its INTERNALDATE. Response: " + sortResponse);

         simulator.Disconnect();
      }

      [Test]
      [Description("Issue 168 - IMAP: Search for message with specific UID fails. ")]
      public void TestSearchSpecficUID()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "search@example.test", "test");

         // Send a message to this account.
         var smtpClientSimulator = new SmtpClientSimulator();
         for (var i = 0; i < 5; i++)
            smtpClientSimulator.Send("search@example.test", "search@example.test", "Test1",
               "This is a test of IMAP Search");

         ImapClientSimulator.AssertMessageCount("search@example.test", "test", "INBOX", 5);

         var messages = account.IMAPFolders.get_ItemByName("Inbox").Messages;

         var second = messages[1].UID;
         var third = messages[2].UID;
         var fourth = messages[3].UID;


         var simulator = new ImapClientSimulator();
         simulator.Connect();
         simulator.Logon("search@example.test", "test");
         Assert.IsTrue(simulator.SelectFolder("INBOX"));

         var result =
            simulator.SendSingleCommand(string.Format("a01 SORT (REVERSE DATE) UTF-8 ALL UID {0},{1}", second, third));
         AssertSortResultContains(result, 2, 3);

         result = simulator.SendSingleCommand(string.Format("a01 SORT (DATE) UTF-8 ALL UID {0},{1}", third, second));
         AssertSortResultContains(result, 2, 3);

         result = simulator.SendSingleCommand(string.Format("a01 SORT (DATE) UTF-8 ALL UID {0}:{1}", second, fourth));
         AssertSortResultContains(result, 2, 3, 4);

         result = simulator.SendSingleCommand(string.Format("a01 SORT (DATE) UTF-8 ALL UID {0}:*", second));
         AssertSortResultContains(result, 2, 3, 4, 5);
      }

      private void AssertSortResultContains(string sortResponse, params int[] expected)
      {
         var response = ParseSortResult(sortResponse);

         Assert.AreEqual(expected.Length, response.Count, sortResponse);

         foreach (var expectedItem in expected)
            Assert.IsTrue(response.Contains(expectedItem), sortResponse);
      }

      private List<int> ParseSortResult(string resultText)
      {
         // Parses a string such as * SORT 2 3 4 5
         var messageListPart = resultText.Substring("* SORT ".Length);
         var end = messageListPart.IndexOf("\r\n", StringComparison.CurrentCultureIgnoreCase);
         messageListPart = messageListPart.Substring(0, end);

         var messages = messageListPart.Split(' ');

         var result = new List<int>();

         foreach (var message in messages)
            result.Add(int.Parse(message));

         return result;
      }


      [Test]
      public void TestSortDeletedOrAnswered()
      {
         var domain = _application.Domains[0];
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "search@example.test", "test");

         // Send a message to this account.
         var smtpClientSimulator = new SmtpClientSimulator();
         smtpClientSimulator.Send("search@example.test", "search@example.test", "aa", "This is a test of IMAP Search");
         ImapClientSimulator.AssertMessageCount("search@example.test", "test", "INBOX", 1);
         smtpClientSimulator.Send("search@example.test", "search@example.test", "bb", "This is a test of IMAP Search");
         ImapClientSimulator.AssertMessageCount("search@example.test", "test", "INBOX", 2);

         var simulator = new ImapClientSimulator();
         var sWelcomeMessage = simulator.Connect();
         simulator.Logon("search@example.test", "test");
         Assert.IsTrue(simulator.SelectFolder("INBOX"));

         Assert.AreEqual("", simulator.Sort("(DATE) UTF-8 ALL OR ANSWERED DELETED"));
      }

      [Test]
      public void TestSortReverseArrival()
      {
         var domain = _application.Domains[0];
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "search@example.test", "test");

         // Send a message to this account.
         var smtpClientSimulator = new SmtpClientSimulator();
         smtpClientSimulator.Send("search@example.test", "search@example.test", "Test1",
            "This is a test of IMAP Search");
         ImapClientSimulator.AssertMessageCount("search@example.test", "test", "INBOX", 1);

         // The two messages needs to be sent a second apart, so we actually need to pause a bit here.

         Thread.Sleep(1000);
         smtpClientSimulator.Send("search@example.test", "search@example.test", "Test2",
            "This is a test of IMAP Search");
         ImapClientSimulator.AssertMessageCount("search@example.test", "test", "INBOX", 2);

         var simulator = new ImapClientSimulator();

         var sWelcomeMessage = simulator.Connect();
         simulator.Logon("search@example.test", "test");
         Assert.IsTrue(simulator.SelectFolder("INBOX"));

         Assert.AreEqual("1 2", simulator.Sort("(ARRIVAL) UTF-8 ALL"));
         Assert.AreEqual("2 1", simulator.Sort("(REVERSE ARRIVAL) UTF-8 ALL"));
      }

      [Test]
      public void TestSortReverseSize()
      {
         var domain = _application.Domains[0];
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "search@example.test", "test");

         var longBodyString = new StringBuilder();
         longBodyString.Append('A', 10000);

         // Send a message to this account.
         var smtpClientSimulator = new SmtpClientSimulator();
         smtpClientSimulator.Send("search@example.test", "search@example.test", "Test1", longBodyString.ToString());
         ImapClientSimulator.AssertMessageCount("search@example.test", "test", "INBOX", 1);

         smtpClientSimulator.Send("search@example.test", "search@example.test", "Test2", "Test body");
         ImapClientSimulator.AssertMessageCount("search@example.test", "test", "INBOX", 2);

         var simulator = new ImapClientSimulator();

         var sWelcomeMessage = simulator.Connect();
         simulator.Logon("search@example.test", "test");
         Assert.IsTrue(simulator.SelectFolder("INBOX"));

         Assert.AreEqual("2 1", simulator.Sort("(SIZE) UTF-8 ALL"));
         Assert.AreEqual("1 2", simulator.Sort("(REVERSE SIZE) UTF-8 ALL"));
      }

      [Test]
      public void TestSortSubject()
      {
         var domain = _application.Domains[0];
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "search@example.test", "test");

         // Send a message to this account.
         var smtpClientSimulator = new SmtpClientSimulator();
         smtpClientSimulator.Send("search@example.test", "search@example.test", "Test1",
            "This is a test of IMAP Search");
         ImapClientSimulator.AssertMessageCount("search@example.test", "test", "INBOX", 1);
         smtpClientSimulator.Send("search@example.test", "search@example.test", "Test2",
            "This is a test of IMAP Search");
         ImapClientSimulator.AssertMessageCount("search@example.test", "test", "INBOX", 2);

         var simulator = new ImapClientSimulator();

         var sWelcomeMessage = simulator.Connect();
         simulator.Logon("search@example.test", "test");
         Assert.IsTrue(simulator.SelectFolder("INBOX"));

         Assert.AreEqual("1 2", simulator.Sort("(SUBJECT) UTF-8 ALL"));
      }

      [Test]
      public void TestSortSubjectReverse()
      {
         var domain = _application.Domains[0];
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "search@example.test", "test");

         // Send a message to this account.
         var smtpClientSimulator = new SmtpClientSimulator();
         smtpClientSimulator.Send("search@example.test", "search@example.test", "Test1",
            "This is a test of IMAP Search");
         ImapClientSimulator.AssertMessageCount("search@example.test", "test", "INBOX", 1);
         smtpClientSimulator.Send("search@example.test", "search@example.test", "Test2",
            "This is a test of IMAP Search");
         ImapClientSimulator.AssertMessageCount("search@example.test", "test", "INBOX", 2);

         var simulator = new ImapClientSimulator();

         var sWelcomeMessage = simulator.Connect();
         simulator.Logon("search@example.test", "test");
         Assert.IsTrue(simulator.SelectFolder("INBOX"));

         Assert.AreEqual("2 1", simulator.Sort("(REVERSE SUBJECT) UTF-8 ALL"));
      }

      [Test]
      public void TestSortSubjectSearch()
      {
         var domain = _application.Domains[0];
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "search@example.test", "test");

         // Send a message to this account.
         var smtpClientSimulator = new SmtpClientSimulator();
         smtpClientSimulator.Send("search@example.test", "search@example.test", "aa", "This is a test of IMAP Search");
         ImapClientSimulator.AssertMessageCount("search@example.test", "test", "INBOX", 1);
         smtpClientSimulator.Send("search@example.test", "search@example.test", "bb", "This is a test of IMAP Search");
         ImapClientSimulator.AssertMessageCount("search@example.test", "test", "INBOX", 2);

         var simulator = new ImapClientSimulator();
         var sWelcomeMessage = simulator.Connect();
         simulator.Logon("search@example.test", "test");
         Assert.IsTrue(simulator.SelectFolder("INBOX"));

         Assert.AreEqual("1 2", simulator.Sort("(DATE) UTF-8 ALL UNANSWERED OR HEADER SUBJECT aa HEADER SUBJECT bb"));
         Assert.AreEqual("1 2",
            simulator.Sort("(DATE) UTF-8 ALL UNANSWERED OR (HEADER SUBJECT aa) (HEADER SUBJECT bb)"));
         Assert.AreEqual("1 2",
            simulator.Sort("(DATE) UTF-8 ALL UNANSWERED (OR HEADER SUBJECT aa HEADER SUBJECT bb)"));

         Assert.AreEqual("1", simulator.Sort("(DATE) UTF-8 ALL UNANSWERED OR HEADER SUBJECT aa HEADER SUBJECT cc"));
         Assert.AreEqual("1",
            simulator.Sort("(DATE) UTF-8 ALL UNANSWERED OR (HEADER SUBJECT aa) (HEADER SUBJECT cc)"));
         Assert.AreEqual("1", simulator.Sort("(DATE) UTF-8 ALL UNANSWERED (OR HEADER SUBJECT aa HEADER SUBJECT cc)"));

         Assert.AreEqual("2", simulator.Sort("(DATE) UTF-8 ALL UNANSWERED OR HEADER SUBJECT bb HEADER SUBJECT cc"));
         Assert.AreEqual("2",
            simulator.Sort("(DATE) UTF-8 ALL UNANSWERED OR (HEADER SUBJECT bb) (HEADER SUBJECT cc)"));
         Assert.AreEqual("2", simulator.Sort("(DATE) UTF-8 ALL UNANSWERED (OR HEADER SUBJECT bb HEADER SUBJECT cc)"));
      }


      [Test]
      public void TestSubjectSearch()
      {
         var domain = _application.Domains[0];
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "search@example.test", "test");

         // Send a message to this account.
         var smtpClientSimulator = new SmtpClientSimulator();
         smtpClientSimulator.Send("search@example.test", "search@example.test", "Test1",
            "This is a test of IMAP Search");
         ImapClientSimulator.AssertMessageCount("search@example.test", "test", "INBOX", 1);
         smtpClientSimulator.Send("search@example.test", "search@example.test", "Test2",
            "This is a test of IMAP Search");
         ImapClientSimulator.AssertMessageCount("search@example.test", "test", "INBOX", 2);

         var simulator = new ImapClientSimulator();

         var sWelcomeMessage = simulator.Connect();
         simulator.Logon("search@example.test", "test");
         Assert.IsTrue(simulator.SelectFolder("INBOX"));

         Assert.AreEqual("1", simulator.Sort("(REVERSE SUBJECT) UTF-8 ALL HEADER SUBJECT \"Test1\""));
         Assert.AreEqual("2", simulator.Sort("(REVERSE SUBJECT) UTF-8 ALL HEADER SUBJECT \"Test2\""));
         Assert.AreEqual("1", simulator.Sort("(REVERSE SUBJECT) UTF-8 ALL (HEADER SUBJECT \"Test1\")"));
         Assert.AreEqual("2", simulator.Sort("(REVERSE SUBJECT) UTF-8 ALL (HEADER SUBJECT \"Test2\")"));
      }


      [Test]
      public void TestSubjectSearchMultipleMatches()
      {
         var domain = _application.Domains[0];
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "search@example.test", "test");

         // Send a message to this account.
         var smtpClientSimulator = new SmtpClientSimulator();
         smtpClientSimulator.Send("search@example.test", "search@example.test", "Test1",
            "This is a test of IMAP Search");
         ImapClientSimulator.AssertMessageCount("search@example.test", "test", "INBOX", 1);
         smtpClientSimulator.Send("search@example.test", "search@example.test", "TestA",
            "This is a test of IMAP Search");
         ImapClientSimulator.AssertMessageCount("search@example.test", "test", "INBOX", 2);
         smtpClientSimulator.Send("search@example.test", "search@example.test", "Test1",
            "This is a test of IMAP Search");
         ImapClientSimulator.AssertMessageCount("search@example.test", "test", "INBOX", 3);

         var simulator = new ImapClientSimulator();

         var sWelcomeMessage = simulator.Connect();
         simulator.Logon("search@example.test", "test");
         Assert.IsTrue(simulator.SelectFolder("INBOX"));

         Assert.AreEqual("1 3", simulator.Sort("(SUBJECT) UTF-8 ALL HEADER SUBJECT \"Test1\""));
         Assert.AreEqual("2", simulator.Sort("(SUBJECT) UTF-8 ALL HEADER SUBJECT \"TestA\""));
         Assert.AreEqual("3 1", simulator.Sort("(REVERSE SUBJECT) UTF-8 ALL HEADER SUBJECT \"Test1\""));
         Assert.AreEqual("2", simulator.Sort("(REVERSE SUBJECT) UTF-8 ALL HEADER SUBJECT \"TestA\""));
         Assert.AreEqual("3 1", simulator.Sort("(REVERSE SUBJECT) UTF-8 ALL (HEADER SUBJECT) \"Test1\""));
         Assert.AreEqual("2", simulator.Sort("(REVERSE SUBJECT) UTF-8 ALL (HEADER SUBJECT) \"TestA\""));
      }

      [Test]
      public void TestSubjectSearchValueWithParanthesis()
      {
         var domain = _application.Domains[0];
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "search@example.test", "test");

         // Send a message to this account.
         var smtpClientSimulator = new SmtpClientSimulator();
         smtpClientSimulator.Send("search@example.test", "search@example.test", "Te(st1",
            "This is a test of IMAP Search");
         ImapClientSimulator.AssertMessageCount("search@example.test", "test", "INBOX", 1);
         smtpClientSimulator.Send("search@example.test", "search@example.test", "Te)st2",
            "This is a test of IMAP Search");
         ImapClientSimulator.AssertMessageCount("search@example.test", "test", "INBOX", 2);

         var simulator = new ImapClientSimulator();

         var sWelcomeMessage = simulator.Connect();
         simulator.Logon("search@example.test", "test");
         Assert.IsTrue(simulator.SelectFolder("INBOX"));

         Assert.AreEqual("1", simulator.Sort("(SUBJECT) UTF-8 ALL HEADER SUBJECT \"Te(st1\""));
         Assert.AreEqual("2", simulator.Sort("(SUBJECT) UTF-8 ALL HEADER SUBJECT \"Te)st2\""));
      }

      [Test]
      [Description("A SORT whose parameter list never closes is a syntax error, not an access violation. " +
                   "The word parser produces no words at all for a command that fails validation, and the " +
                   "sort key list and charset were then read out of an empty vector.")]
      public void TestSortWithUnclosedParameterListIsRefused()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "sortcrash@example.test", "test");

         SmtpClientSimulator.StaticSend(account.Address, account.Address, "MySubject", "MyBody");
         ImapClientSimulator.AssertMessageCount(account.Address, "test", "INBOX", 1);

         var simulator = new ImapClientSimulator();
         Assert.IsTrue(simulator.ConnectAndLogon(account.Address, "test"));
         Assert.IsTrue(simulator.SelectFolder("INBOX"));

         // Against the unfixed server this line produces no response at all: the parser
         // yields no words for a command that fails validation, and the sort key list was
         // read from element 0 of that empty vector. The /EHa catch(...) in TCPConnection
         // swallows the access violation, logs error 5136 and drops the session; the crash
         // oracle records the fault, which is what fails this test.
         var result = simulator.SendSingleCommand("A01 SORT (");
         Assert.IsTrue(result.Contains("A01 BAD"),
            "An unclosed SORT parameter list must be answered BAD. " + result);

         // The session survived, and a real SORT still works on it. Both spellings of the
         // charset are covered on purpose: the SORT path now has to consume that token
         // itself, because it used to survive into the criteria parser and be discarded
         // there as an unrecognised word - which is no longer something that happens
         // quietly.
         result = simulator.SendSingleCommand("A02 SORT (DATE) US-ASCII ALL");
         Assert.IsTrue(result.Contains("A02 OK"),
            "A valid SORT must still work after a refused one. " + result);

         result = simulator.SendSingleCommand("A03 SORT (DATE) \"US-ASCII\" ALL");
         Assert.IsTrue(result.Contains("A03 OK"),
            "A quoted charset must be accepted by SORT. " + result);

         simulator.Disconnect();
      }
   }
}
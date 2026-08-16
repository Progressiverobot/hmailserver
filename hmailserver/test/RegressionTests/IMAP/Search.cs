// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Microsoft.VisualBasic;
using NUnit.Framework;
using RegressionTests.Shared;

namespace RegressionTests.IMAP
{
   [TestFixture]
   public class Search : TestFixtureBase
   {
      [Test]
      public void TestNestedOr()
      {
         var application = SingletonProvider<TestSetup>.Instance.GetApp();


         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "search@example.test", "test");

         // Send a message to this account.
         var smtpClientSimulator = new SmtpClientSimulator();
         smtpClientSimulator.Send("search@example.test", "search@example.test", "Search test",
            "This is a test of IMAP Search");

         ImapClientSimulator.AssertMessageCount(account.Address, "test", "Inbox", 1);

         var simulator = new ImapClientSimulator();
         var sWelcomeMessage = simulator.Connect();
         simulator.Logon("search@example.test", "test");
         simulator.SelectFolder("INBOX");


         var result =
            simulator.SendSingleCommand(
               "A4 SEARCH OR OR OR OR OR OR SINCE 28-May-2008 SINCE 28-May-2008 SINCE 28-May-2008 SINCE 28-May-2008 SINCE 28-May-2008 SINCE 28-May-2008 SINCE 28-May-2008 ALL");
         Assert.IsTrue(result.StartsWith("* SEARCH"), result);

         result =
            simulator.SendSingleCommand(
               "A4 SEARCH OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR OR SINCE 28-May-2008 SINCE 28-May-2008 SINCE 28-May-2008 SINCE 28-May-2008 SINCE 28-May-2008 SINCE 28-May-2008 SINCE 28-May-2008 SINCE 28-May-2008 SINCE 28-May-2008 SINCE 28-May-2008 SINCE 28-May-2008 SINCE 28-May-2008 SINCE 28-May-2008 SINCE 28-May-2008 SINCE 28-May-2008 SINCE 28-May-2008 SINCE 28-May-2008 SINCE 28-May-2008 SINCE 28-May-2008 SINCE 28-May-2008 SINCE 28-May-2008 SINCE 28-May-2008 SINCE 28-May-2008 SINCE 28-May-2008 SINCE 28-May-2008 SINCE 28-May-2008 SINCE 28-May-2008 SINCE 28-May-2008 SINCE 28-May-2008 SINCE 28-May-2008 SINCE 28-May-2008 SINCE 28-May-2008 SINCE 28-May-2008 SINCE 28-May-2008 SINCE 28-May-2008 SINCE 28-May-2008 SINCE 28-May-2008 SINCE 28-May-2008 SINCE 28-May-2008 SINCE 28-May-2008 SINCE 28-May-2008 ALL");
         Assert.IsTrue(result.StartsWith("A4 NO"), result);
      }

      [Test]
      public void TestNestedOrSearch()
      {
         var application = SingletonProvider<TestSetup>.Instance.GetApp();


         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "search@example.test", "test");

         // Send a message to this account.
         var smtpClientSimulator = new SmtpClientSimulator();
         smtpClientSimulator.Send("search@example.test", "search@example.test", "Search test",
            "This is a test of IMAP Search");

         ImapClientSimulator.AssertMessageCount(account.Address, "test", "Inbox", 1);

         var simulator = new ImapClientSimulator();
         var sWelcomeMessage = simulator.Connect();
         simulator.Logon("search@example.test", "test");
         simulator.SelectFolder("INBOX");

         var result =
            simulator.SendSingleCommand("A4 SEARCH ALL OR OR SINCE 28-May-2008 SINCE 28-May-2008 SINCE 28-May-2008");
         Assert.IsTrue(result.StartsWith("* SEARCH 1"), result);

         result = simulator.SendSingleCommand("A4 SEARCH ALL OR SMALLER 1 LARGER 10000");
         Assert.IsTrue(result.StartsWith("* SEARCH\r\n"), result);

         result = simulator.SendSingleCommand("A4 SEARCH ALL OR OR SMALLER 1 LARGER 10000 SMALLER 10000");
         Assert.IsTrue(result.StartsWith("* SEARCH 1\r\n"), result);
      }

      [Test]
      public void TestSearch()
      {
         var application = SingletonProvider<TestSetup>.Instance.GetApp();

         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "se'arch@example.test", "test");

         // Send a message to this account.
         var smtpClientSimulator = new SmtpClientSimulator();
         smtpClientSimulator.Send(account.Address, account.Address, "Search test", "This is a test of IMAP Search");

         ImapClientSimulator.AssertMessageCount(account.Address, "test", "INBOX", 1);

         var simulator = new ImapClientSimulator();

         var sWelcomeMessage = simulator.Connect();
         simulator.Logon(account.Address, "test");
         Assert.IsTrue(simulator.SelectFolder("INBOX"));

         simulator.SetFlagOnFirstMessage(true, "\\ANSWERED");
         if (simulator.Search("ANSWERED") != "1")
            throw new Exception("ERROR - Search or flag failed");

         simulator.SetFlagOnFirstMessage(false, "\\ANSWERED");
         if (simulator.Search("ANSWERED") != "")
            throw new Exception("ERROR - Search or flag failed");

         simulator.SetFlagOnFirstMessage(true, "\\DELETED");
         if (simulator.Search("DELETED") != "1")
            throw new Exception("ERROR - Search or flag failed");

         simulator.SetFlagOnFirstMessage(false, "\\DELETED");
         if (simulator.Search("DELETED") != "")
            throw new Exception("ERROR - Search or flag failed");

         simulator.SetFlagOnFirstMessage(true, "\\DRAFT");
         if (simulator.Search("DRAFT") != "1")
            throw new Exception("ERROR - Search or flag failed");

         simulator.SetFlagOnFirstMessage(false, "\\DRAFT");
         if (simulator.Search("DRAFT") != "")
            throw new Exception("ERROR - Search or flag failed");

         simulator.SetFlagOnFirstMessage(true, "\\FLAGGED");
         if (simulator.Search("FLAGGED  ") != "1")
            throw new Exception("ERROR - Search or flag failed");

         simulator.SetFlagOnFirstMessage(false, "\\FLAGGED");
         if (simulator.Search("FLAGGED") != "")
            throw new Exception("ERROR - Search or flag failed");

         simulator.SetFlagOnFirstMessage(true, "\\SEEN");
         if (simulator.Search("SEEN") != "1")
            throw new Exception("ERROR - Search or flag failed");

         simulator.SetFlagOnFirstMessage(false, "\\SEEN");
         if (simulator.Search("SEEN") != "")
            throw new Exception("ERROR - Search or flag failed");

         simulator.SetFlagOnFirstMessage(true, "\\ANSWERED");
         if (simulator.Search("UNANSWERED") != "")
            throw new Exception("ERROR - Search or flag failed");

         simulator.SetFlagOnFirstMessage(false, "\\ANSWERED");
         if (simulator.Search("UNANSWERED") != "1")
            throw new Exception("ERROR - Search or flag failed");

         simulator.SetFlagOnFirstMessage(true, "\\DELETED");
         if (simulator.Search("UNDELETED") != "")
            throw new Exception("ERROR - Search or flag failed");

         simulator.SetFlagOnFirstMessage(false, "\\DELETED");
         if (simulator.Search("UNDELETED") != "1")
            throw new Exception("ERROR - Search or flag failed");

         simulator.SetFlagOnFirstMessage(true, "\\DRAFT");
         if (simulator.Search("UNDRAFT") != "")
            throw new Exception("ERROR - Search or flag failed");

         simulator.SetFlagOnFirstMessage(false, "\\DRAFT");
         if (simulator.Search("UNDRAFT") != "1")
            throw new Exception("ERROR - Search or flag failed");

         simulator.SetFlagOnFirstMessage(true, "\\FLAGGED");
         if (simulator.Search("UNFLAGGED") != "")
            throw new Exception("ERROR - Search or flag failed");

         simulator.SetFlagOnFirstMessage(false, "\\FLAGGED");
         if (simulator.Search("UNFLAGGED") != "1")
            throw new Exception("ERROR - Search or flag failed");

         // SEARCH using LARGER & SMALLER
         if (simulator.Search("SMALLER 10") != "")
            throw new Exception("ERROR - Search or flag failed");

         if (simulator.Search("SMALLER 10000") != "1")
            throw new Exception("ERROR - Search or flag failed");

         if (simulator.Search("LARGER 10") != "1")
            throw new Exception("ERROR - Search or flag failed");

         if (simulator.Search("LARGER 10000") != "")
            throw new Exception("ERROR - Search or flag failed");
      }

      [Test]
      public void TestSearchInvalidCharset()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "search@example.test", "test");

         SmtpClientSimulator.StaticSend(account.Address, account.Address, "MySubject", "MyBody");

         Pop3ClientSimulator.AssertMessageCount(account.Address, "test", 1);

         var simulator = new ImapClientSimulator();
         Assert.IsTrue(simulator.ConnectAndLogon(account.Address, "test"));
         Assert.IsTrue(simulator.SelectFolder("INBOX"));

         var result = simulator.SendSingleCommand("A01 SEARCH CHARSET NONEXISTANT ALL SUBJECT MySubject");

         // RFC 3501 section 7.1: BADCHARSET may name the charsets that would have worked, and
         // a response code is always followed by text. The reply used to be the bare
         // "A01 NO [BADCHARSET]", which is not a well-formed resp-text and gives the client
         // nothing to retry with.
         Assert.IsTrue(result.StartsWith("A01 NO [BADCHARSET"), result);
         Assert.IsTrue(result.Contains("UTF-8"),
            "BADCHARSET should name the charsets the server does accept. " + result);
         Assert.IsTrue(result.EndsWith("supported.\r\n"),
            "The BADCHARSET response code must be followed by human-readable text. " + result);
      }

      [Test]
      public void TestSearchLargeBody()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "search@example.test", "test");
         var body = new StringBuilder();
         body.AppendLine("From: search@example.test");
         body.AppendLine("Subject: Test");
         body.AppendLine();
         for (var i = 0; i < 20000; i++) // One megabye body.
            body.AppendLine("12345678901234567890123456789012345678901234567890");
         body.AppendLine("TestString");
         body.AppendLine();

         SmtpClientSimulator.StaticSendRaw(account.Address, account.Address, body.ToString());

         Pop3ClientSimulator.AssertMessageCount(account.Address, "test", 1);

         var simulator = new ImapClientSimulator();
         Assert.IsTrue(simulator.ConnectAndLogon(account.Address, "test"));
         Assert.IsTrue(simulator.SelectFolder("INBOX"));
         var result = simulator.Search("CHARSET UTF-8 ALL TEXT InvalidText");
         Assert.AreEqual("", result);

         result = simulator.Search("CHARSET UTF-8 ALL TEXT TestStringA");
         Assert.AreEqual("", result);

         result = simulator.Search("CHARSET UTF-8 ALL TEXT TestString");
         Assert.AreEqual("1", result);

         result = simulator.Search("CHARSET UTF-8 ALL TEXT TestStr");
         Assert.AreEqual("1", result);

         result = simulator.Search("UNDELETED BODY \"TestString\"");
         Assert.AreEqual("1", result);

         simulator.Close();
      }

      [Test]
      public void TestSearchON()
      {
         var application = SingletonProvider<TestSetup>.Instance.GetApp();


         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "search@example.test", "test");

         // Send a message to this account.
         var smtpClientSimulator = new SmtpClientSimulator();
         smtpClientSimulator.Send("search@example.test", "search@example.test", "Search test",
            "This is a test of IMAP Search");
         ImapClientSimulator.AssertMessageCount("search@example.test", "test", "INBOX", 1);

         var simulator = new ImapClientSimulator();

         var sWelcomeMessage = simulator.Connect();
         simulator.Logon("search@example.test", "test");
         Assert.IsTrue(simulator.SelectFolder("INBOX"));

         var formattedTomorrow =
            (DateTime.Now + new TimeSpan(1, 0, 0, 0)).ToString("dd-MMM-yyyy", CultureInfo.InvariantCulture).ToUpper();
         var formattedToday = DateTime.Now.ToString("dd-MMM-yyyy", CultureInfo.InvariantCulture).ToUpper();

         if (simulator.Search("ON " + formattedTomorrow) != "") throw new Exception("ERROR - Search or flag failed");

         if (simulator.Search("ON " + formattedToday) != "1") throw new Exception("ERROR - Search or flag failed");
      }


      [Test]
      public void TestSearchOR()
      {
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "search@example.test", "test");

         // Send a message to this account.
         var smtpClientSimulator = new SmtpClientSimulator();
         smtpClientSimulator.Send("search@example.test", "search@example.test", "Search test",
            "This is a test of IMAP Search");
         ImapClientSimulator.AssertMessageCount("search@example.test", "test", "INBOX", 1);

         var simulator = new ImapClientSimulator();

         var sWelcomeMessage = simulator.Connect();
         simulator.Logon("search@example.test", "test");
         Assert.IsTrue(simulator.SelectFolder("INBOX"));

         Assert.AreEqual("1", simulator.Search("OR SINCE 28-May-2001 ON 28-May-2001 ALL"));

         // Searching for mail sent a year from now or a specific date 2012 should not return any matches.
         var nextYear = DateTime.UtcNow.Year + 1;
         Assert.That(simulator.Search($"OR SINCE 28-May-{nextYear} ON 28-May-2012 ALL"), Is.Null.Or.Empty);

         var formattedToday = DateTime.Now.ToString("dd-MMM-yyyy", CultureInfo.InvariantCulture).ToUpper();
         Assert.AreEqual("1", simulator.Search("OR SINCE 28-May-2017 ON " + formattedToday + " ALL"));

         var formatted2001 = new DateTime(2001, 01, 01).ToString("dd-MMM-yyyy").ToUpper();
         Assert.AreEqual("1", simulator.Search("OR SINCE 28-May-2008 ON " + formatted2001 + " ALL"));
      }

      [Test]
      public void TestSearchORWithLiterals()
      {
         var application = SingletonProvider<TestSetup>.Instance.GetApp();


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

         var result = simulator.Send("A01 SEARCH ALL OR (HEADER SUBJECT {5}");
         result = simulator.Send("Test1) (HEADER SUBJECT {5}");
         result = simulator.Send("Test2)");
         Assert.IsTrue(result.StartsWith("* SEARCH 1 2"));
      }

      [Test]
      public void TestSearchORWithLiterals2()
      {
         var application = SingletonProvider<TestSetup>.Instance.GetApp();


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

         var result = simulator.Send("A01 SEARCH ALL OR (HEADER SUBJECT {5}");
         result = simulator.Send("Test1) (HEADER SUBJECT {5}");
         result = simulator.Send("Test5)");
         Assert.IsTrue(result.StartsWith("* SEARCH 1"));
      }

      [Test]
      public void TestSearchORWithLiterals3()
      {
         var application = SingletonProvider<TestSetup>.Instance.GetApp();


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

         var result = simulator.Send("A01 SEARCH ALL OR (HEADER SUBJECT {5}");
         result = simulator.Send("Test5) (HEADER SUBJECT {5}");
         result = simulator.Send("Test2)");
         Assert.IsTrue(result.StartsWith("* SEARCH 2"));
      }

      [Test]
      public void TestSearchORWithParenthesis()
      {
         var application = SingletonProvider<TestSetup>.Instance.GetApp();

         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "search@example.test", "test");

         // Send a message to this account.
         var smtpClientSimulator = new SmtpClientSimulator();
         smtpClientSimulator.Send("search@example.test", "search@example.test", "Search test",
            "This is a test of IMAP Search");
         ImapClientSimulator.AssertMessageCount("search@example.test", "test", "INBOX", 1);
         smtpClientSimulator.Send("search@example.test", "search@example.test", "Search test",
            "This is a test of IMAP Search");
         ImapClientSimulator.AssertMessageCount("search@example.test", "test", "INBOX", 2);

         var simulator = new ImapClientSimulator();

         var sWelcomeMessage = simulator.Connect();
         simulator.Logon("search@example.test", "test");
         Assert.IsTrue(simulator.SelectFolder("INBOX"));

         if (simulator.Search("OR (SINCE 28-May-2001) (ON 28-May-2001) ALL") != "1 2")
            throw new Exception("ERROR - Search or flag failed");
      }

      [Test]
      public void TestSearchORWithParenthesisSubject()
      {
         var application = SingletonProvider<TestSetup>.Instance.GetApp();


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

         if (simulator.Search("OR (SUBJECT \"Test1\") (ON 28-May-2001) ALL") != "1")
            throw new Exception("ERROR - Search or flag failed");

         if (simulator.Search("OR (SUBJECT \"Test2\") (ON 28-May-2001) ALL") != "2")
            throw new Exception("ERROR - Search or flag failed");
      }

      [Test]
      public void TestSearchORWithParenthesisSubjectNested()
      {
         var application = SingletonProvider<TestSetup>.Instance.GetApp();


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

         if (simulator.Search("ALL (OR (HEADER SUBJECT \"Test1\") (HEADER SUBJECT \"Test2\"))") != "1 2")
            throw new Exception("ERROR - Search or flag failed");
      }

      [Test]
      [Description("Issue 167 - IMAP: Search for message in range fails.")]
      public void TestSearchRange()
      {
         var application = SingletonProvider<TestSetup>.Instance.GetApp();


         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "search@example.test", "test");

         // Send a message to this account.
         var smtpClientSimulator = new SmtpClientSimulator();
         for (var i = 0; i < 5; i++)
            smtpClientSimulator.Send("search@example.test", "search@example.test", "Test1",
               "This is a test of IMAP Search");

         ImapClientSimulator.AssertMessageCount("search@example.test", "test", "INBOX", 5);

         var simulator = new ImapClientSimulator();
         var sWelcomeMessage = simulator.Connect();
         simulator.Logon("search@example.test", "test");
         Assert.IsTrue(simulator.SelectFolder("INBOX"));

         var result = simulator.SendSingleCommand("a01 search 2:4");
         Assert.IsTrue(result.StartsWith("* SEARCH 2 3 4"));

         result = simulator.SendSingleCommand("a01 search 3,2");
         Assert.IsTrue(result.StartsWith("* SEARCH 2 3"));

         result = simulator.SendSingleCommand("a01 search 3:*");
         Assert.IsTrue(result.StartsWith("* SEARCH 3 4 5"));

         result = simulator.SendSingleCommand("a01 search 3,1,3");
         Assert.IsTrue(result.StartsWith("* SEARCH 1 3"));

         result = simulator.SendSingleCommand("a01 search 1:*");
         Assert.IsTrue(result.StartsWith("* SEARCH 1 2 3 4 5"));
      }

      [Test]
      [Description("Test that searching for message UID's works.")]
      public void TestSearchUID()
      {
         var application = SingletonProvider<TestSetup>.Instance.GetApp();


         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "search@example.test", "test");

         // Send a message to this account.
         var smtpClientSimulator = new SmtpClientSimulator();
         for (var i = 0; i < 3; i++)
            smtpClientSimulator.Send("search@example.test", "search@example.test", "Test1",
               "This is a test of IMAP Search");

         ImapClientSimulator.AssertMessageCount("search@example.test", "test", "INBOX", 3);

         // There should be 3 UID's, 1,2,3 or similar. No skips in the middle fo them.
         var simulator = new ImapClientSimulator();
         var sWelcomeMessage = simulator.Connect();
         simulator.Logon("search@example.test", "test");
         Assert.IsTrue(simulator.SelectFolder("INBOX"));

         var result = simulator.SendSingleCommand("* UID SEARCH UID 1:*");

         // Potentially, the response is multiline. (UID RESPONSE and an OK line). We only want the first line...
         result = result.Substring(0, result.IndexOf("\r\n"));

         var tokens = Strings.Split(result, " ", -1, CompareMethod.Text);

         var uids = new List<int>();
         foreach (var token in tokens)
         {
            int temp;
            if (int.TryParse(token, out temp)) uids.Add(temp);
         }

         Assert.AreEqual(3, uids.Count, result);

         Assert.AreEqual(1, uids[0]);
         Assert.AreEqual(2, uids[1]);
         Assert.AreEqual(3, uids[2]);
      }

      [Test]
      public void TestSearchUSASCII()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "search@example.test", "test");

         SmtpClientSimulator.StaticSend(account.Address, account.Address, "MySubject", "MyBody");

         Pop3ClientSimulator.AssertMessageCount(account.Address, "test", 1);

         var simulator = new ImapClientSimulator();
         Assert.IsTrue(simulator.ConnectAndLogon(account.Address, "test"));
         Assert.IsTrue(simulator.SelectFolder("INBOX"));

         var result = simulator.Search("CHARSET US-ASCII ALL SUBJECT MySubject");
         Assert.AreEqual("1", result);

         result = simulator.Search("CHARSET US-ASCII ALL SUBJECT MySubjact");
         Assert.AreEqual("", result);
      }

      [Test]
      public void TestSearchUTF8()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "search@example.test", "test");

         var body = TestSetup.GetResource("Messages.MessageContainingGreekAndJapanese.txt");

         SmtpClientSimulator.StaticSendRaw(account.Address, account.Address, body);

         Pop3ClientSimulator.AssertMessageCount(account.Address, "test", 1);

         var simulator = new ImapClientSimulator();
         Assert.IsTrue(simulator.ConnectAndLogon(account.Address, "test"));
         Assert.IsTrue(simulator.SelectFolder("INBOX"));

         var result = simulator.Search("CHARSET UTF-8 ALL TEXT GRΣΣK");
         Assert.AreEqual("1", result);

         result = simulator.Search("CHARSET UTF-8 ALL TEXT ÅÄÖ");
         Assert.AreEqual("1", result);

         result = simulator.Search("CHARSET UTF-8 ALL TEXT 標準語標準語");
         Assert.AreEqual("1", result);

         result = simulator.Search("CHARSET UTF-8 ALL TEXT ßEßEß");
         Assert.AreEqual("1", result);

         result = simulator.Search("CHARSET UTF-8 ALL TEXT ÅÅÅ");
         Assert.AreEqual("", result);

         result = simulator.Search("CHARSET UTF-8 ALL TEXT GREEK");
         Assert.AreEqual("", result);

         result = simulator.Search("CHARSET UTF-8 ALL TEXT ßEEEß");
         Assert.AreEqual("", result);

         result = simulator.Search("CHARSET UTF-8 ALL TEXT 標準語標語語");
         Assert.AreEqual("", result);
      }

      [Test]
      [Description("Tests the ALL TEXT search command. TEXT should match both header and body")]
      public void TestSearchUTF8TEXT()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "search@example.test", "test");
         var body = TestSetup.GetResource("Messages.MessageContainingGreekSubject.txt");
         SmtpClientSimulator.StaticSendRaw(account.Address, account.Address, body);

         Pop3ClientSimulator.AssertMessageCount(account.Address, "test", 1);

         var simulator = new ImapClientSimulator();
         Assert.IsTrue(simulator.ConnectAndLogon(account.Address, "test"));
         Assert.IsTrue(simulator.SelectFolder("INBOX"));

         var result = simulator.Search("CHARSET UTF-8 ALL TEXT GRΣΣK");
         Assert.AreEqual("1", result);

         result = simulator.Search("CHARSET UTF-8 ALL TEXT 標準語");
         Assert.AreEqual("1", result);

         result = simulator.Search("CHARSET UTF-8 ALL TEXT GRΣΣK標準語");
         Assert.AreEqual("1", result);

         result = simulator.Search("CHARSET UTF-8 ALL TEXT GRΣΣKWHAT標準語");
         Assert.AreEqual("", result);
      }

      [Test]
      public void TestSearchWithLiterals()
      {
         var application = SingletonProvider<TestSetup>.Instance.GetApp();

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

         var result = simulator.SendSingleCommandWithLiteral("A01 SEARCH HEADER SUBJECT {5}", "Test1");
         Assert.IsTrue(result.StartsWith("* SEARCH 1\r\n"));

         result = simulator.SendSingleCommandWithLiteral("A01 SEARCH HEADER SUBJECT {5}", "Test2");
         Assert.IsTrue(result.StartsWith("* SEARCH 2\r\n"));
      }

      [Test]
      public void TestSearchWithNOTDeleted()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "search@example.test", "test");

         // Send a message to this account.
         var smtpClientSimulator = new SmtpClientSimulator();
         smtpClientSimulator.Send("search@example.test", "search@example.test", "TestSubject", "TestBody");
         smtpClientSimulator.Send("search@example.test", "search@example.test", "TestSubject", "TestBody");
         ImapClientSimulator.AssertMessageCount("search@example.test", "test", "INBOX", 2);

         var simulator = new ImapClientSimulator();

         simulator.Connect();
         simulator.Logon("search@example.test", "test");
         simulator.SelectFolder("Inbox");
         simulator.SetDeletedFlag(2);

         var searchResult =
            simulator.Search("(OR FROM \"TestSubject\" (OR SUBJECT \"TestSubject\" BODY \"TestSubject\")) NOT DELETED");

         Assert.AreEqual("1", searchResult);
      }

      [Test]
      [Description("RFC 3501: a search key the server does not implement must be refused with BAD. It used to " +
                   "be dropped, and a search whose keys were all dropped matched every message in the mailbox.")]
      public void TestUnrecognizedSearchKeyIsRefusedRatherThanIgnored()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "search@example.test", "test");

         SmtpClientSimulator.StaticSend(account.Address, account.Address, "MySubject", "MyBody");
         ImapClientSimulator.AssertMessageCount(account.Address, "test", "INBOX", 1);

         var simulator = new ImapClientSimulator();
         Assert.IsTrue(simulator.ConnectAndLogon(account.Address, "test"));
         Assert.IsTrue(simulator.SelectFolder("INBOX"));

         // Against the unfixed server this answered "* SEARCH 1" and then OK: the key and its
         // argument were both discarded as unrecognised words, which left an empty criteria
         // list, and an empty criteria list matches everything. The danger is not the wrong
         // line, it is what a client does with it - Thunderbird's Search Messages window
         // offers Delete over the result, and an archiver moves "whatever the search
         // returned", so the whole mailbox gets acted on.
         var result = simulator.SendSingleCommand("A01 SEARCH X-NO-SUCH-KEY 12");
         Assert.IsFalse(result.Contains("* SEARCH 1"),
            "An unrecognised search key must not silently match every message. " + result);
         Assert.IsTrue(result.Contains("A01 BAD"),
            "An unrecognised search key must be answered BAD. " + result);

         // A SEARCH with no criteria at all reached the same empty-criteria path.
         result = simulator.SendSingleCommand("A02 SEARCH");
         Assert.IsFalse(result.Contains("* SEARCH 1"),
            "A SEARCH with no search key must not match every message. " + result);
         Assert.IsTrue(result.Contains("A02 BAD"),
            "A SEARCH with no search key must be answered BAD. " + result);

         // Refusing those two must not have cost us the ordinary case, and the session has
         // to survive both refusals.
         result = simulator.SendSingleCommand("A03 SEARCH ALL");
         Assert.IsTrue(result.Contains("* SEARCH 1"),
            "A valid search must still work after a refused one. " + result);

         simulator.Disconnect();
      }

      [Test]
      [Description("RFC 3501: KEYWORD matches messages carrying a keyword flag and UNKEYWORD those without it. " +
                   "No keyword can be stored here, so KEYWORD matches nothing - it used to match everything.")]
      public void TestKeywordSearchKeys()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "search@example.test", "test");

         SmtpClientSimulator.StaticSend(account.Address, account.Address, "MySubject", "MyBody");
         ImapClientSimulator.AssertMessageCount(account.Address, "test", "INBOX", 1);

         var simulator = new ImapClientSimulator();
         Assert.IsTrue(simulator.ConnectAndLogon(account.Address, "test"));
         Assert.IsTrue(simulator.SelectFolder("INBOX"));

         // Unfixed: "* SEARCH 1". KEYWORD and its argument were dropped, the criteria list came
         // out empty, and a client asking "which of my messages carry this label" was told
         // "all of them". Nothing can carry a keyword - messageflags is a fixed 8-bit bitmask
         // and PERMANENTFLAGS advertises no \* - so the correct answer is none of them.
         var result = simulator.SendSingleCommand("A01 SEARCH KEYWORD $Forwarded");
         Assert.IsTrue(result.Contains("* SEARCH\r\n"),
            "KEYWORD cannot match anything, because no keyword can be stored. " + result);
         Assert.IsTrue(result.Contains("A01 OK"),
            "KEYWORD is a base RFC 3501 key and must not be refused. " + result);

         result = simulator.SendSingleCommand("A02 SEARCH UNKEYWORD $Forwarded");
         Assert.IsTrue(result.Contains("* SEARCH 1"),
            "UNKEYWORD matches every message, since no message carries the keyword. " + result);

         simulator.Disconnect();
      }

      [Test]
      [Description("IMAP keywords are case-insensitive, so a lower-case \"not\" must invert the key that " +
                   "follows it. It was compared case-sensitively, which gave the opposite result set.")]
      public void TestLowerCaseNotInvertsTheFollowingKey()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "search@example.test", "test");

         SmtpClientSimulator.StaticSend(account.Address, account.Address, "MySubject", "MyBody");
         ImapClientSimulator.AssertMessageCount(account.Address, "test", "INBOX", 1);

         var simulator = new ImapClientSimulator();
         Assert.IsTrue(simulator.ConnectAndLogon(account.Address, "test"));
         Assert.IsTrue(simulator.SelectFolder("INBOX"));

         Assert.AreEqual("1", simulator.Search("NOT DELETED"));

         // Unfixed this returned "" - the lower-case "not" was discarded as an unrecognised
         // word and the DELETED that followed was then applied positively, so the client
         // asking for undeleted mail was handed the deleted mail instead.
         Assert.AreEqual("1", simulator.Search("not deleted"));

         simulator.Disconnect();
      }

      [Test]
      [Description("RFC 5032 (WITHIN): OLDER and YOUNGER match on the internal date relative to now, " +
                   "the interval must be a positive number of seconds, and WITHIN is advertised.")]
      public void TestWithinOlderAndYounger()
      {
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "within@example.test", "test");

         var simulator = new ImapClientSimulator();
         simulator.Connect();
         simulator.LogonWithLiteral("within@example.test", "test");

         Assert.IsTrue(simulator.GetCapabilities().Contains(" WITHIN"),
            "CAPABILITY must advertise WITHIN, or no client will ever send OLDER or YOUNGER.");

         Assert.IsTrue(simulator.SelectFolder("INBOX"));

         // Message 1 is given an internal date in 2008 by APPEND; message 2 gets "now".
         var appended =
            simulator.SendSingleCommandWithLiteral("A01 APPEND INBOX \"22-Feb-2008 22:00:00 +0200\" {37}",
               "Date: Wed, 15 Dec 2010 13:00:00 +0000");
         Assert.IsTrue(appended.Contains("* 1 EXISTS"), appended);

         appended = simulator.SendSingleCommandWithLiteral("A02 APPEND INBOX {4}", "ABCD");
         Assert.IsTrue(appended.Contains("* 2 EXISTS"), appended);

         // Unfixed, both of these returned "1 2": OLDER/YOUNGER and their intervals were
         // discarded, leaving an empty criteria list that matched the whole mailbox.
         Assert.AreEqual("1", simulator.Search("OLDER 3600"));
         Assert.AreEqual("2", simulator.Search("YOUNGER 3600"));

         // RFC 5032 says the interval is an nz-number. A zero or non-numeric interval is a
         // syntax error rather than "0 seconds ago", which would match on one key and not
         // on the other and look like the server had made a decision about the mail.
         var result = simulator.SendSingleCommand("A03 SEARCH OLDER notanumber");
         Assert.IsTrue(result.Contains("A03 BAD"), result);

         result = simulator.SendSingleCommand("A04 SEARCH YOUNGER 0");
         Assert.IsTrue(result.Contains("A04 BAD"), result);

         simulator.Disconnect();
      }

      [Test]
      [Description("RFC 5182 section 2.1: the result saved by SEARCH RETURN (SAVE) can be used as a search " +
                   "key. \"$\" used to be an unrecognised word, which matched every message instead.")]
      public void TestSavedSearchResultUsedAsSearchKey()
      {
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "searchresk@example.test", "test");

         var simulator = new ImapClientSimulator();
         simulator.Connect();
         simulator.LogonWithLiteral("searchresk@example.test", "test");

         simulator.SendSingleCommandWithLiteral("A01 APPEND INBOX {4}", "ABCD");
         simulator.SendSingleCommandWithLiteral("A02 APPEND INBOX {4}", "EFGH");
         simulator.SendSingleCommandWithLiteral("A03 APPEND INBOX {4}", "IJKL");
         Assert.IsTrue(simulator.SelectFolder("INBOX"));

         simulator.SendSingleCommand("A04 STORE 2 +FLAGS (\\Seen)");

         var save = simulator.SendSingleCommand("A05 UID SEARCH RETURN (SAVE) SEEN");
         Assert.IsTrue(save.Contains("A05 OK"), save);

         // Unfixed: "1 2 3". The saved set is the second message and nothing else.
         Assert.AreEqual("2", simulator.Search("$"));

         // Combined with another key, as RFC 5182's own example does.
         Assert.AreEqual("", simulator.Search("$ UNSEEN"));
         Assert.AreEqual("2", simulator.Search("$ SEEN"));

         simulator.Disconnect();
      }
   }
}
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;
using hMailServer;

namespace RegressionTests.Sieve
{
   /// <summary>
   ///    End-to-end verification that the Sieve "vacation" action (RFC 5230) and its
   ///    ":seconds" parameter (RFC 6131) actually send an auto-reply, and that the
   ///    properties which stop it becoming a mail loop or a backscatter source hold
   ///    in the delivery path rather than only in the evaluator.
   ///
   ///    Before this fixture, "vacation" was parsed, validated, evaluated and
   ///    reported in the action summary - and then nothing happened. That shape is
   ///    the one worth guarding against, because from the outside it is
   ///    indistinguishable from a working feature right up to the moment somebody
   ///    goes on holiday: the script uploads cleanly, the syntax checker is happy,
   ///    the summary says "vacation", and no mail is ever sent. Every test here
   ///    therefore asserts on a real message arriving in a real mailbox over SMTP and
   ///    POP3, not on the summary string. SieveVacation covers the summary; the two
   ///    fixtures are deliberately not testing the same thing.
   ///
   ///    Why each test fails against the unfixed code is stated on the test.
   ///
   ///    The Sieve script is configured late-bound via the COM Account.SieveScript
   ///    property, so the test does not depend on the registered type library being
   ///    regenerated.
   /// </summary>
   [TestFixture]
   public class SieveVacationDelivery : TestFixtureBase
   {
      private const string Password = "test";

      // The number of live records SieveVacationTracker allows one account's store
      // to hold (SieveVacationTracker.cpp, MaxLiveRecords). Spelled out rather than
      // asked of the server because the server does not expose it, and because a
      // mismatch has to show up as a failure: if the two ever disagree,
      // TestFullTrackingStoreRefusesToReplyRatherThanForgetting either fills the
      // store short of the cap (and sees a reply where it expects none) or overfills
      // it (and the store is refused as foreign for a different reason). Either way
      // the failure names this constant.
      private const int MaxTrackedResponses = 2000;

      private const string StoreHeaderLine = "# hMailServer Sieve vacation response tracking, format 1";

      private static void SetScript(object account, string script)
      {
         account.GetType().InvokeMember(
            "SieveScript", BindingFlags.SetProperty, null, account, new object[] { script });
      }

      /// <summary>
      ///    Installs a script on an account and clears any vacation suppression
      ///    windows left behind first.
      ///
      ///    Both halves matter. Sieve scripts and the vacation response records are
      ///    files under the data directory, keyed by account address, and they survive
      ///    the account being deleted and recreated - which is what PerformBasicSetup
      ///    does between test runs. Without the clearing step the second run of this
      ///    fixture would find a live seven-day suppression window from the first and
      ///    see no auto-reply at all. Setting the script to the empty string is the
      ///    production path for "this user is no longer filtering", and clearing the
      ///    records is part of what that means.
      /// </summary>
      private static void SetScriptFromClean(object account, string script)
      {
         SetScript(account, "");
         SetScript(account, script);
      }

      // Where SieveStorage keeps an account's vacation response records. Spelled out
      // here rather than asked of the server because the point of the tests that use
      // it is precisely that the suppression state lives in a file: a test that asked
      // the implementation where its state was would pass against an implementation
      // that kept it in memory.
      private string VacationStorePath(string address)
      {
         int at = address.LastIndexOf('@');
         string localPart = address.Substring(0, at).ToLowerInvariant();
         string domain = address.Substring(at + 1).ToLowerInvariant();

         return Path.Combine(_application.Settings.Directories.DataDirectory,
            "Sieve", domain, localPart, "vacation.responses");
      }

      private static void AssertNoReply(Account sender)
      {
         // AssertMessageCount drains the delivery queue before it believes a count of
         // zero, so this cannot pass merely because the reply has not been delivered
         // yet.
         Pop3ClientSimulator.AssertMessageCount(sender.Address, Password, 0);
      }

      private static string ReadReply(Account sender)
      {
         // Reads and deletes, so the mailbox is empty again for the next assertion.
         return Pop3ClientSimulator.AssertGetFirstMessageText(sender.Address, Password);
      }

      private static void AssertDelivered(Account account, int expectedCount)
      {
         IMAPFolder inbox = account.IMAPFolders.get_ItemByName("INBOX");
         CustomAsserts.AssertFolderMessageCount(inbox, expectedCount);
      }

      // Builds a syntactically valid response store holding the given number of
      // records, all with the same expiry. The keys are distinct 64-character hex
      // digests, which is the only key shape the tracker writes or accepts.
      private static string BuildStore(int recordCount, long expiryUnixSeconds)
      {
         var builder = new StringBuilder();
         builder.Append(StoreHeaderLine).Append("\r\n");

         for (int i = 0; i < recordCount; i++)
            builder.Append(expiryUnixSeconds).Append(' ').Append(i.ToString("x64")).Append("\r\n");

         return builder.ToString();
      }

      private static HashSet<string> ReadStoreKeys(string path)
      {
         var keys = new HashSet<string>();

         foreach (string line in File.ReadAllLines(path))
         {
            if (line.Length == 0 || line[0] == '#')
               continue;

            int space = line.IndexOf(' ');
            if (space > 0)
               keys.Add(line.Substring(space + 1).Trim());
         }

         return keys;
      }

      // Invoked late-bound for the same reason as the SieveScript property: so the
      // test does not depend on the registered type library being regenerated.
      private string CheckSyntax(string script)
      {
         object utilities = _application.Utilities;
         object result = utilities.GetType().InvokeMember(
            "CheckSieveSyntax", BindingFlags.InvokeMethod, null, utilities, new object[] { script });

         return (string)result;
      }

      [Test]
      [Description("A vacation script actually sends an auto-reply, with the RFC 5230 / RFC 3834 headers, and sends exactly one inside the suppression window.")]
      public void TestVacationSendsOneReplyInsideTheWindow()
      {
         Account away = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "sieve-vac-away@example.test", Password);
         Account friend = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "sieve-vac-friend@example.test", Password);

         SetScriptFromClean(away,
            "require \"vacation\";\r\n" +
            "vacation :days 7 \"I am on holiday until Monday.\";");

         SmtpClientSimulator.StaticSend(friend.Address, away.Address, "Lunch on Thursday?", "Are you free?");

         // The message itself is still delivered: an auto-reply is a side effect, not
         // a delivery decision.
         AssertDelivered(away, 1);

         // Against the unfixed code this is where the fixture stops: the evaluator
         // decided an auto-reply was due and reported it in the action summary, and
         // LocalDelivery::EvaluateSieveScript_ then read the summary for
         // keep/fileinto/redirect only. No reply was ever composed, so the friend's
         // mailbox stayed empty and AssertGetFirstMessageText times out.
         string reply = ReadReply(friend);

         // RFC 3834 5 / RFC 5230 4.6: without this header the far end's own
         // auto-responder has no way to know not to answer, which is one half of every
         // two-responder loop.
         Assert.IsTrue(reply.Contains("Auto-Submitted: auto-replied"),
            "The auto-reply must identify itself as an automatic response. Reply was:\r\n" + reply);

         // The reply goes back to the envelope sender.
         Assert.IsTrue(reply.Contains("To: " + friend.Address), reply);
         Assert.IsTrue(reply.Contains("From: " + away.Address), reply);

         // RFC 5230 4.2: with no ":subject" the subject is "Auto: " and the original
         // subject. Not "Re: ", which is what the older account-level auto-reply uses
         // and which claims to be part of a conversation.
         Assert.IsTrue(reply.Contains("Subject: Auto: Lunch on Thursday?"), reply);

         Assert.IsTrue(reply.Contains("I am on holiday until Monday."), reply);

         // RFC 3834 4: a null envelope return path, so that a bounce of the reply
         // cannot start a second exchange. TraceHeaderWriter turns the empty envelope
         // sender into this header on delivery.
         Assert.IsTrue(reply.Contains("Return-Path: <>"), reply);

         // A second message from the same sender inside the seven-day window gets no
         // second reply. This is the whole point of the tracking store: two people who
         // are both away and who both write once would otherwise exchange mail until
         // an administrator noticed.
         SmtpClientSimulator.StaticSend(friend.Address, away.Address, "Or Friday?", "Second try.");
         AssertDelivered(away, 2);
         AssertNoReply(friend);
      }

      [Test]
      [Description("The suppression window lives in a file, not in memory, so it survives a restart.")]
      public void TestSuppressionWindowIsPersistedOnDisk()
      {
         Account away = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "sieve-vac-persist@example.test", Password);
         Account friend = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "sieve-vac-pfriend@example.test", Password);

         SetScriptFromClean(away,
            "require \"vacation\";\r\n" +
            "vacation :days 30 \"Away for a month.\";");

         string storePath = VacationStorePath(away.Address);
         Assert.IsFalse(File.Exists(storePath), "The response store should not exist before any reply is sent: " + storePath);

         SmtpClientSimulator.StaticSend(friend.Address, away.Address, "First", "body");
         AssertDelivered(away, 1);
         ReadReply(friend);

         // The record has to be on disk. An in-memory map - which is what the
         // account-level auto-reply uses, and which is why a nightly service restart
         // makes it re-answer everybody every day - would leave nothing here.
         Assert.IsTrue(File.Exists(storePath), "The response store was not written: " + storePath);

         string store = File.ReadAllText(storePath);
         Assert.IsTrue(store.Contains("format 1"), "Unexpected response store contents:\r\n" + store);

         // Exactly one record, and the store is rewritten in full rather than appended
         // to, so nothing else has crept in.
         Assert.AreEqual(1, ReadStoreKeys(storePath).Count, "Unexpected response store contents:\r\n" + store);

         SmtpClientSimulator.StaticSend(friend.Address, away.Address, "Second", "body");
         AssertDelivered(away, 2);
         AssertNoReply(friend);

         // Now the negative control that makes the assertion above mean something.
         // Deleting the file must re-enable the reply. If the suppression were held in
         // memory and merely mirrored to disk, this third message would still be
         // suppressed and the test would fail - which is exactly the distinction
         // between "persisted" and "written down as well".
         File.Delete(storePath);

         SmtpClientSimulator.StaticSend(friend.Address, away.Address, "Third", "body");
         AssertDelivered(away, 3);

         string reply = ReadReply(friend);
         Assert.IsTrue(reply.Contains("Away for a month."), reply);
      }

      [Test]
      [Description("A tracking store that is full of live records refuses to reply rather than evicting a record it would then have forgotten.")]
      public void TestFullTrackingStoreRefusesToReplyRatherThanForgetting()
      {
         Account away = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "sieve-vac-full@example.test", Password);
         Account friend = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "sieve-vac-ffull@example.test", Password);

         SetScriptFromClean(away,
            "require \"vacation\";\r\n" +
            "vacation :days 30 \"Store is full.\";");

         string storePath = VacationStorePath(away.Address);

         // A store already holding its maximum number of records, none of them
         // expired and none of them this sender's.
         long future = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 30 * 86400L;
         string prepared = BuildStore(MaxTrackedResponses, future);
         File.WriteAllText(storePath, prepared);

         HashSet<string> keysBefore = ReadStoreKeys(storePath);
         Assert.AreEqual(MaxTrackedResponses, keysBefore.Count, "The prepared store did not contain the expected number of records.");

         SmtpClientSimulator.StaticSend(friend.Address, away.Address, "Hello", "body");
         AssertDelivered(away, 1);

         // THIS is the finding this test exists for. The earlier implementation capped
         // the store by dropping the record whose window expired soonest and then sent
         // the reply anyway. That is a responder which forgets it has already replied
         // to somebody: the sender whose record was dropped gets a second reply for
         // their next message, and a third for the one after, which is a mail loop and
         // a backscatter source. Against that code this assertion fails, because the
         // reply is sent.
         //
         // Refusing costs one auto-reply. Forgetting costs an unbounded number of
         // them, so the only acceptable answer when the store cannot record a new
         // response is "do not send".
         AssertNoReply(friend);

         // ...and nothing was dropped to make room. The record count alone cannot show
         // this - evict-one-then-add-one leaves the count unchanged - so every key that
         // was there before must still be there.
         HashSet<string> keysAfter = ReadStoreKeys(storePath);
         foreach (string key in keysBefore)
         {
            Assert.IsTrue(keysAfter.Contains(key),
               "A record was evicted from the response store to make room for a new one. " +
               "A vacation responder that forgets a reply it has sent will send it again: " + key);
         }

         // The negative control: with the store out of the way the same sender, script
         // and message do get a reply, so the assertion above is about the full store
         // and not about a feature that never worked.
         File.Delete(storePath);

         SmtpClientSimulator.StaticSend(friend.Address, away.Address, "Hello again", "body");
         AssertDelivered(away, 2);
         Assert.IsTrue(ReadReply(friend).Contains("Store is full."));

         // And the cap is on LIVE records only: a store of the same size whose records
         // have all expired is pruned back to nothing and the reply goes out. Without
         // that, one busy fortnight would silently disable a user's auto-reply forever.
         long past = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60;
         File.WriteAllText(storePath, BuildStore(MaxTrackedResponses, past));

         SmtpClientSimulator.StaticSend(friend.Address, away.Address, "Third time", "body");
         AssertDelivered(away, 3);
         Assert.IsTrue(ReadReply(friend).Contains("Store is full."),
            "A store whose records have all expired must be pruned, not treated as full.");

         Assert.AreEqual(1, ReadStoreKeys(storePath).Count,
            "Expired records should have been pruned away when the store was rewritten.");
      }

      [Test]
      [Description("A tracking store that cannot be read or understood suppresses the reply instead of assuming none was sent.")]
      public void TestUnreadableTrackingStoreRefusesToReply()
      {
         Account away = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "sieve-vac-bad@example.test", Password);
         Account friend = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "sieve-vac-bfriend@example.test", Password);

         SetScriptFromClean(away,
            "require \"vacation\";\r\n" +
            "vacation :days 30 \"Unreadable store.\";");

         string storePath = VacationStorePath(away.Address);
         long future = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 30 * 86400L;

         // Case 1: a record line that cannot be parsed. The line we cannot read may be
         // the very record that says this sender has already been answered, so "no
         // matching record found" is not a fact about this store - it is a guess. The
         // guess that sends mail is the wrong one.
         File.WriteAllText(storePath,
            StoreHeaderLine + "\r\n" +
            future + " " + 1.ToString("x64") + "\r\n" +
            "this line is not a record\r\n");

         SmtpClientSimulator.StaticSend(friend.Address, away.Address, "One", "body");
         AssertDelivered(away, 1);
         AssertNoReply(friend);

         // Case 2: an empty file. Every store this code writes begins with its header
         // line, so a zero-byte one means either a read that failed or a write that was
         // interrupted - not "nothing has been answered".
         File.WriteAllText(storePath, "");

         SmtpClientSimulator.StaticSend(friend.Address, away.Address, "Two", "body");
         AssertDelivered(away, 2);
         AssertNoReply(friend);

         // Case 3: a file that is not ours at all. Without the header check this would
         // parse as a store with no records, which is indistinguishable from a store
         // that says nobody has been answered.
         File.WriteAllText(storePath, "some other program's data\r\n");

         SmtpClientSimulator.StaticSend(friend.Address, away.Address, "Three", "body");
         AssertDelivered(away, 3);
         AssertNoReply(friend);

         // The negative control for all three.
         File.Delete(storePath);

         SmtpClientSimulator.StaticSend(friend.Address, away.Address, "Four", "body");
         AssertDelivered(away, 4);
         Assert.IsTrue(ReadReply(friend).Contains("Unreadable store."));
      }

      [Test]
      [Description("RFC 6131 ':seconds' sets the suppression window, and ':seconds 0' means reply to every message.")]
      public void TestVacationSecondsControlsTheWindow()
      {
         Account away = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "sieve-vac-secs@example.test", Password);
         Account friend = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "sieve-vac-sfriend@example.test", Password);

         // ":seconds 0" is deliberately used for the deterministic half of this test:
         // with the ":days" default of 7 the second message would get no reply, so two
         // replies can only mean that ":seconds" was read and honoured. No sleeping is
         // involved, so it cannot be flaky.
         SetScriptFromClean(away,
            "require [\"vacation\", \"vacation-seconds\"];\r\n" +
            "vacation :seconds 0 \"Replying to everything.\";");

         SmtpClientSimulator.StaticSend(friend.Address, away.Address, "One", "body");
         AssertDelivered(away, 1);
         Assert.IsTrue(ReadReply(friend).Contains("Replying to everything."));

         SmtpClientSimulator.StaticSend(friend.Address, away.Address, "Two", "body");
         AssertDelivered(away, 2);
         Assert.IsTrue(ReadReply(friend).Contains("Replying to everything."),
            "':seconds 0' must reply to every qualifying message; the ':days' default of 7 would have suppressed this one.");

         // ":seconds 0" records nothing, because there is no window to record.
         Assert.IsFalse(File.Exists(VacationStorePath(away.Address)),
            "':seconds 0' has no suppression window, so it should not write a tracking store.");

         // A window measured in seconds still suppresses. An hour is used rather than a
         // couple of seconds because the assertions in between take real time - reading
         // the first reply over POP3 and draining the queue - and a short window would
         // leave the test asserting "no reply" about a window that had already
         // legitimately expired. That would fail intermittently on a slow machine and
         // tell nobody anything.
         SetScriptFromClean(away,
            "require [\"vacation\", \"vacation-seconds\"];\r\n" +
            "vacation :seconds 3600 \"Hourly window.\";");

         SmtpClientSimulator.StaticSend(friend.Address, away.Address, "Three", "body");
         AssertDelivered(away, 3);
         Assert.IsTrue(ReadReply(friend).Contains("Hourly window."));

         SmtpClientSimulator.StaticSend(friend.Address, away.Address, "Four", "body");
         AssertDelivered(away, 4);
         AssertNoReply(friend);

         // ...and a window that has passed really is gone rather than suppressing
         // forever, which is what the pruning in the tracking store is for. This half
         // only ever expects a reply, so extra elapsed time can only help it - there is
         // no window it has to fit inside.
         SetScriptFromClean(away,
            "require [\"vacation\", \"vacation-seconds\"];\r\n" +
            "vacation :seconds 1 \"One second window.\";");

         SmtpClientSimulator.StaticSend(friend.Address, away.Address, "Five", "body");
         AssertDelivered(away, 5);
         Assert.IsTrue(ReadReply(friend).Contains("One second window."));

         Thread.Sleep(2000);

         SmtpClientSimulator.StaticSend(friend.Address, away.Address, "Six", "body");
         AssertDelivered(away, 6);
         Assert.IsTrue(ReadReply(friend).Contains("One second window."),
            "A ':seconds 1' window must have expired two seconds later; an expired record has to be dropped, not honoured.");
      }

      [Test]
      [Description("The reply goes to the envelope sender, never to the From header.")]
      public void TestReplyGoesToEnvelopeSenderNotFromHeader()
      {
         Account away = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "sieve-vac-env@example.test", Password);
         Account envelopeSender = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "sieve-vac-envfrom@example.test", Password);
         Account headerFrom = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "sieve-vac-hdrfrom@example.test", Password);

         SetScriptFromClean(away,
            "require \"vacation\";\r\n" +
            "vacation :days 7 \"Out of office.\";");

         // A message whose From header names somebody other than the envelope sender.
         // Ordinary mail looks like this whenever it has been forwarded or sent through
         // a list, and forged mail looks like this on purpose: answering the From
         // address delivers a holiday notice to a third party who never wrote to this
         // user, which is textbook backscatter.
         string raw =
            "From: \"Someone Else\" <" + headerFrom.Address + ">\r\n" +
            "To: " + away.Address + "\r\n" +
            "Subject: Question\r\n" +
            "\r\n" +
            "Body.\r\n";

         SmtpClientSimulator.StaticSendRaw(envelopeSender.Address, away.Address, raw);
         AssertDelivered(away, 1);

         string reply = ReadReply(envelopeSender);
         Assert.IsTrue(reply.Contains("To: " + envelopeSender.Address), reply);
         Assert.IsFalse(reply.Contains(headerFrom.Address),
            "The auto-reply must not be addressed to the From header. Reply was:\r\n" + reply);

         AssertNoReply(headerFrom);
      }

      [Test]
      [Description("':handle' keeps two different vacation messages in separate suppression windows.")]
      public void TestHandleSeparatesSuppressionWindows()
      {
         Account away = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "sieve-vac-handle@example.test", Password);
         Account friend = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "sieve-vac-hfriend@example.test", Password);

         // RFC 5230 4.4: the suppression window is keyed on the handle as well as the
         // sender, so one script can hold two different responses. Keying on the sender
         // alone - which is what the account-level auto-reply does - would make the
         // second message below get nothing.
         SetScriptFromClean(away,
            "require \"vacation\";\r\n" +
            "if header :contains \"Subject\" \"urgent\" {\r\n" +
            "  vacation :handle \"urgent-notice\" :days 7 \"Call my mobile.\";\r\n" +
            "}\r\n" +
            "else {\r\n" +
            "  vacation :handle \"normal-notice\" :days 7 \"I will read this on Monday.\";\r\n" +
            "}");

         SmtpClientSimulator.StaticSend(friend.Address, away.Address, "urgent: server down", "body");
         AssertDelivered(away, 1);
         Assert.IsTrue(ReadReply(friend).Contains("Call my mobile."));

         SmtpClientSimulator.StaticSend(friend.Address, away.Address, "lunch", "body");
         AssertDelivered(away, 2);
         Assert.IsTrue(ReadReply(friend).Contains("I will read this on Monday."),
            "A different ':handle' is a different suppression window, so this second message is due a reply.");

         // The same handle as the first message, from the same sender, inside the
         // window: suppressed.
         SmtpClientSimulator.StaticSend(friend.Address, away.Address, "urgent: still down", "body");
         AssertDelivered(away, 3);
         AssertNoReply(friend);
      }

      [Test]
      [Description("A ':mime' reason supplies the reply's Content-* headers and body, and cannot forge anything else.")]
      public void TestMimeReasonAppliesContentHeadersOnly()
      {
         Account away = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "sieve-vac-mime@example.test", Password);
         Account friend = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "sieve-vac-mfriend@example.test", Password);

         // The reason is a MIME entity (RFC 5230 4.3). It also tries to set two headers
         // it has no business setting: clearing Auto-Submitted would re-enable exactly
         // the loops this feature is built to avoid, and it is the sort of thing a
         // script author would try in order to stop a correspondent's filter from
         // binning the notice.
         SetScriptFromClean(away,
            "require \"vacation\";\r\n" +
            "vacation :mime text:\r\n" +
            "Content-Type: text/html; charset=\"utf-8\"\r\n" +
            "Auto-Submitted: no\r\n" +
            "X-Injected-By-Script: yes\r\n" +
            "\r\n" +
            "<html><body><p>I am away until Monday.</p></body></html>\r\n" +
            ".\r\n" +
            ";");

         SmtpClientSimulator.StaticSend(friend.Address, away.Address, "Hello", "body");
         AssertDelivered(away, 1);

         string reply = ReadReply(friend);

         // The entity's own Content-Type is used, so the reply really is HTML.
         Assert.IsTrue(reply.Contains("text/html"), reply);
         Assert.IsTrue(reply.Contains("<p>I am away until Monday.</p>"), reply);

         // ...and the two headers the reason had no business setting were dropped.
         Assert.IsTrue(reply.Contains("Auto-Submitted: auto-replied"), reply);
         Assert.IsFalse(reply.Contains("Auto-Submitted: no"),
            "A ':mime' reason must not be able to clear the Auto-Submitted header. Reply was:\r\n" + reply);
         Assert.IsFalse(reply.Contains("X-Injected-By-Script"),
            "A ':mime' reason may only contribute Content-* and MIME-Version headers. Reply was:\r\n" + reply);
      }

      [Test]
      [Description("':from' is honoured only for an address that actually reaches the account.")]
      public void TestFromIsHonouredOnlyForTheUsersOwnAddresses()
      {
         Account away = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "sieve-vac-from@example.test", Password);
         Account spoofTarget = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "sieve-vac-victim@example.test", Password);
         Account friend = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "sieve-vac-ffriend2@example.test", Password);

         const string aliasAddress = "sieve-vac-alias@example.test";
         SingletonProvider<TestSetup>.Instance.AddAlias(_domain, aliasAddress, away.Address);

         // An alias that reaches this account is a legitimate ":from" - it is one of the
         // user's own addresses, which is the restriction RFC 5230 4.4 allows an
         // implementation to impose.
         SetScriptFromClean(away,
            "require \"vacation\";\r\n" +
            "vacation :from \"" + aliasAddress + "\" :days 7 \"Away, via my alias.\";");

         SmtpClientSimulator.StaticSend(friend.Address, away.Address, "Hello alias", "body");
         AssertDelivered(away, 1);

         string reply = ReadReply(friend);
         Assert.IsTrue(reply.Contains("From: " + aliasAddress),
            "':from' naming one of the account's own addresses should be used. Reply was:\r\n" + reply);

         // Somebody else's mailbox is not. Any user who can upload a script could
         // otherwise send mail that appears to come from any address on the server,
         // using the server's own reputation to do it.
         SetScriptFromClean(away,
            "require \"vacation\";\r\n" +
            "vacation :from \"" + spoofTarget.Address + "\" :days 7 \"Away, spoofed.\";");

         SmtpClientSimulator.StaticSend(friend.Address, away.Address, "Hello spoof", "body");
         AssertDelivered(away, 2);

         reply = ReadReply(friend);
         Assert.IsTrue(reply.Contains("From: " + away.Address),
            "':from' naming somebody else's address must fall back to the account's own. Reply was:\r\n" + reply);
         Assert.IsFalse(reply.Contains("From: " + spoofTarget.Address), reply);
      }

      [Test]
      [Description("No auto-reply to a robot or list-management sender address, even when no List-* header is present.")]
      public void TestNoReplyToRobotSenderAddresses()
      {
         Account away = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "sieve-vac-robot@example.test", Password);
         Account friend = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "sieve-vac-rfriend@example.test", Password);

         // The robot senders are real mailboxes, so that "no reply was sent" is an
         // assertion about a mailbox that would have received one rather than about an
         // address that could not have been delivered to anyway.
         Account listOwner = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "owner-users@example.test", Password);
         Account listRequest = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "users-request@example.test", Password);
         Account mailerDaemon = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "mailer-daemon@example.test", Password);
         Account noReply = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "no-reply@example.test", Password);

         SetScriptFromClean(away,
            "require \"vacation\";\r\n" +
            "vacation :days 7 \"Out of office.\";");

         // A pre-RFC-2369 mailing list: no List-* headers at all, just the "owner-"
         // convention on the envelope sender. This is the shape the header check cannot
         // see, and replying to it sends the holiday notice to the list owner - or, for
         // the "-request" form, to a robot that answers back.
         string raw =
            "From: \"Users List\" <users@example.test>\r\n" +
            "To: " + away.Address + "\r\n" +
            "Subject: Re: something\r\n" +
            "\r\n" +
            "Body.\r\n";

         SmtpClientSimulator.StaticSendRaw(listOwner.Address, away.Address, raw);
         AssertDelivered(away, 1);
         AssertNoReply(listOwner);

         SmtpClientSimulator.StaticSendRaw(listRequest.Address, away.Address, raw);
         AssertDelivered(away, 2);
         AssertNoReply(listRequest);

         // Upper case, because RFC 3834 names this one in upper case and a
         // case-sensitive comparison would let it through.
         SmtpClientSimulator.StaticSendRaw("MAILER-DAEMON@example.test", away.Address, raw);
         AssertDelivered(away, 3);
         AssertNoReply(mailerDaemon);

         SmtpClientSimulator.StaticSendRaw(noReply.Address, away.Address, raw);
         AssertDelivered(away, 4);
         AssertNoReply(noReply);

         // The control: an ordinary person at the same domain still gets a reply, so
         // the four assertions above are not just a broken feature.
         SmtpClientSimulator.StaticSend(friend.Address, away.Address, "A real question", "body");
         AssertDelivered(away, 5);
         Assert.IsTrue(ReadReply(friend).Contains("Out of office."));
      }

      [Test]
      [Description("No auto-reply once a message has been through the configured number of generated hops.")]
      public void TestNoReplyOnceTheRuleLoopLimitIsReached()
      {
         Account away = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "sieve-vac-loop@example.test", Password);
         Account friend = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "sieve-vac-lfriend@example.test", Password);

         SetScriptFromClean(away,
            "require \"vacation\";\r\n" +
            "vacation :days 7 \"Out of office.\";");

         // hMailServer's own generated-mail loop counter. A message that arrives
         // already carrying the limit has been through that many generated hops -
         // forwards, rule replies, bounces - and whatever the individual guards think,
         // it is in a cycle. Vacation is one of the participants that has to stop
         // adding to it, exactly as RuleApplier::IsGeneratedResponseAllowed does for
         // rule-generated mail.
         int loopLimit = _settings.RuleLoopLimit;

         string raw =
            "From: " + friend.Address + "\r\n" +
            "To: " + away.Address + "\r\n" +
            "Subject: Round and round\r\n" +
            "X-hMailServer-LoopCount: " + loopLimit + "\r\n" +
            "\r\n" +
            "Body.\r\n";

         SmtpClientSimulator.StaticSendRaw(friend.Address, away.Address, raw);
         AssertDelivered(away, 1);
         AssertNoReply(friend);

         // One hop below the limit is still answered, so the check is on the number and
         // not on the presence of the header.
         string rawUnderLimit =
            "From: " + friend.Address + "\r\n" +
            "To: " + away.Address + "\r\n" +
            "Subject: Nearly there\r\n" +
            "X-hMailServer-LoopCount: " + (loopLimit - 1) + "\r\n" +
            "\r\n" +
            "Body.\r\n";

         SmtpClientSimulator.StaticSendRaw(friend.Address, away.Address, rawUnderLimit);
         AssertDelivered(away, 2);
         Assert.IsTrue(ReadReply(friend).Contains("Out of office."));
      }

      [Test]
      [Description("Clearing an account's Sieve script also clears its vacation suppression windows.")]
      public void TestClearingTheScriptClearsTheSuppressionWindows()
      {
         Account away = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "sieve-vac-clear@example.test", Password);
         Account friend = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "sieve-vac-cfriend@example.test", Password);

         const string script =
            "require \"vacation\";\r\n" +
            "vacation :days 30 \"Away for a month.\";";

         SetScriptFromClean(away, script);

         SmtpClientSimulator.StaticSend(friend.Address, away.Address, "First", "body");
         AssertDelivered(away, 1);
         ReadReply(friend);

         Assert.IsTrue(File.Exists(VacationStorePath(away.Address)));

         // A user who comes back, deletes the script, then goes away again a week later
         // expects the people who wrote to them in between to hear back. The
         // account-level auto-reply does the same thing when it is switched off
         // (SMTPVacationMessageCreator::VacationMessageTurnedOff); a thirty-day window
         // that outlives the script that created it is a surprise, not a safety
         // property.
         SetScript(away, "");
         Assert.IsFalse(File.Exists(VacationStorePath(away.Address)),
            "Clearing the script should have removed the vacation response store.");

         SetScript(away, script);

         SmtpClientSimulator.StaticSend(friend.Address, away.Address, "Second", "body");
         AssertDelivered(away, 2);
         Assert.IsTrue(ReadReply(friend).Contains("Away for a month."));
      }

      [Test]
      [Description("The RFC 6131 and ':from' / ':addresses' argument rules are enforced when the script is uploaded.")]
      public void TestVacationArgumentValidation()
      {
         // RFC 6131: ":seconds" needs its own capability name, because a server that
         // silently rounded it up to a day would be carrying out a different
         // instruction from the one written.
         Assert.IsNotEmpty(CheckSyntax("require \"vacation\";\r\nvacation :seconds 60 \"away\";"));
         Assert.IsEmpty(CheckSyntax("require [\"vacation\", \"vacation-seconds\"];\r\nvacation :seconds 60 \"away\";"));

         // ...and "vacation-seconds" implies "vacation", so a plain ":days" script that
         // requires only the former is legal.
         Assert.IsEmpty(CheckSyntax("require \"vacation-seconds\";\r\nvacation :days 3 \"away\";"));

         // The two windows are mutually exclusive. Preferring one silently would make
         // the suppression window depend on an implementation detail.
         Assert.IsNotEmpty(CheckSyntax(
            "require [\"vacation\", \"vacation-seconds\"];\r\nvacation :days 1 :seconds 60 \"away\";"));

         // Zero seconds is legal - it means "reply to every message" - so it must not
         // be swept up with the ":days 0" rejection.
         Assert.IsEmpty(CheckSyntax("require [\"vacation\", \"vacation-seconds\"];\r\nvacation :seconds 0 \"away\";"));
         Assert.IsNotEmpty(CheckSyntax("require \"vacation\";\r\nvacation :days 0 \"away\";"));

         // ":from" is now accepted, because the reply can honour it - but only for an
         // address that reaches the account, which is checked at delivery time where
         // the account is known. What can be checked here is that it is an address at
         // all.
         Assert.IsEmpty(CheckSyntax("require \"vacation\";\r\nvacation :from \"other@example.test\" \"away\";"));
         Assert.IsNotEmpty(CheckSyntax("require \"vacation\";\r\nvacation :from \"not an address\" \"away\";"));

         // ":addresses" feeds the RFC 5230 4.5 recipient check, which is a loop guard.
         // An entry that is not an address silently contributes nothing to it, and the
         // author would never find that out, so the script is refused.
         Assert.IsEmpty(CheckSyntax(
            "require \"vacation\";\r\nvacation :addresses [\"a@example.test\", \"b@example.test\"] \"away\";"));
         Assert.IsNotEmpty(CheckSyntax(
            "require \"vacation\";\r\nvacation :addresses [\"a@example.test\", \"rubbish\"] \"away\";"));

         // The reply's suppression window is a number of seconds, and a negative one is
         // not a window at all - it would reach the tracker looking exactly like
         // ":seconds 0", which answers every message.
         Assert.IsNotEmpty(CheckSyntax(
            "require [\"vacation\", \"vacation-seconds\"];\r\nvacation :seconds -5 \"away\";"));
      }
   }
}

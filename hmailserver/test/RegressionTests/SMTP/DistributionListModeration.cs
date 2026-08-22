// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Collections.Generic;
using hMailServer;
using NUnit.Framework;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;

namespace RegressionTests.SMTP
{
   /// <summary>
   /// Distribution-list moderation and per-list bounce handling.
   ///
   /// Moderation: a list with a moderator address no longer refuses a sender its
   /// mode would have 550'd - the posting is accepted and forwarded to the
   /// moderator, stamped with an X-hMailServer-Moderation header naming the list,
   /// and reaches no member. The moderator approves it by resending it to the list
   /// from an AUTHENTICATED session: an authenticated sender whose address is the
   /// moderator's may always post. The authentication requirement is the security
   /// boundary, and the forged-approval test here is the one that matters most:
   /// MAIL FROM is free text, so an unauthenticated session claiming the
   /// moderator's address must end up in the moderation queue like any other
   /// stranger, never in the members' mailboxes.
   ///
   /// Bounce handling: a list with a bounce address stamps it as the envelope
   /// sender of every copy it sends, so the member's copy carries Return-Path of
   /// the list owner and a dead subscriber's bounces go to the one person who can
   /// remove them - instead of to whoever happened to post last. An empty bounce
   /// address is the off switch and must preserve the old envelope exactly.
   ///
   /// NOTE: ModeratorAddress and BounceAddress are new COM properties. This
   /// fixture is written against those names and will not compile until
   /// Interop.hMailServer.dll is regenerated from the updated IDL.
   /// </summary>
   [TestFixture]
   public class DistributionListModeration : TestFixtureBase
   {
      private const string Password = "test";

      private const string ListAddress = "list@example.test";
      private const string MemberAddress = "member1@example.test";
      private const string ModeratorAddress = "moderator@example.test";
      private const string PosterAddress = "poster@example.test";

      /// <summary>
      /// A membership-mode list whose only member is member1, so any other sender
      /// is one the mode refuses. The moderator is deliberately NOT a member:
      /// their address alone must never satisfy the membership check, or the
      /// forged-approval test below would be testing nothing.
      /// </summary>
      private DistributionList CreateMemberOnlyList(string moderatorAddress)
      {
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, MemberAddress, Password);
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, ModeratorAddress, Password);
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, PosterAddress, Password);

         var recipients = new List<string> {MemberAddress};

         var list = SingletonProvider<TestSetup>.Instance.AddDistributionList(_domain, ListAddress, recipients);
         list.Mode = eDistributionListMode.eLMMembership;
         list.ModeratorAddress = moderatorAddress;
         list.Save();

         return list;
      }

      [Test]
      [Description("A sender the list mode refuses is forwarded to the moderator, labelled, and reaches no member")]
      public void OutsideSenderToModeratedListReachesModeratorAndNotMembers()
      {
         CreateMemberOnlyList(ModeratorAddress);

         // poster is not a member, so membership mode would have refused this with
         // a 550. With a moderator configured the send must succeed instead.
         SmtpClientSimulator.StaticSend(PosterAddress, ListAddress, "Please approve", "Body of posting");

         var moderatorCopy = Pop3ClientSimulator.AssertGetFirstMessageText(ModeratorAddress, Password);

         // The context the moderator needs: which list wants the approval.
         Assert.IsTrue(moderatorCopy.Contains("X-hMailServer-Moderation: " + ListAddress), moderatorCopy);

         // The forward is not a distribution, so it must not be dressed as one.
         Assert.IsFalse(moderatorCopy.Contains("List-Id:"), moderatorCopy);

         // The posting itself travelled intact.
         Assert.IsTrue(moderatorCopy.Contains("Subject: Please approve"), moderatorCopy);
         Assert.IsTrue(moderatorCopy.Contains("Body of posting"), moderatorCopy);

         // And the members saw nothing. This waits for the delivery queue to
         // drain first, so a copy in flight would still be caught.
         ImapClientSimulator.AssertMessageCount(MemberAddress, Password, "Inbox", 0);

         // Moderation is not an error condition.
         CustomAsserts.AssertNoReportedError();
      }

      [Test]
      [Description("The moderator's authenticated resend of a posting reaches the members")]
      public void ModeratorAuthenticatedApprovalReachesMembers()
      {
         CreateMemberOnlyList(ModeratorAddress);

         // The approval: the moderator resends the posting to the list from an
         // authenticated session. The moderator is not a member, so this only
         // works if moderator status itself authorizes the post.
         string errorMessage;
         var smtpClient = new SmtpClientSimulator();
         smtpClient.Send(false, ModeratorAddress, Password, ModeratorAddress, ListAddress,
            "Please approve", "Body of posting", out errorMessage);

         var memberCopy = Pop3ClientSimulator.AssertGetFirstMessageText(MemberAddress, Password);

         // The approved posting is a distribution and is dressed as one.
         Assert.IsTrue(memberCopy.Contains("List-Id: <list.example.test>"), memberCopy);
         Assert.IsTrue(memberCopy.Contains("Subject: Please approve"), memberCopy);
         Assert.IsTrue(memberCopy.Contains("Body of posting"), memberCopy);

         // The moderator's own post must not boomerang into moderation.
         ImapClientSimulator.AssertMessageCount(ModeratorAddress, Password, "Inbox", 0);

         CustomAsserts.AssertNoReportedError();
      }

      [Test]
      [Description("SECURITY: an unauthenticated session claiming the moderator's address cannot approve")]
      public void ForgedApprovalFromUnauthenticatedSessionDoesNotReachMembers()
      {
         CreateMemberOnlyList(ModeratorAddress);

         // The forgery: MAIL FROM claims the moderator's address, but the session
         // never authenticated. MAIL FROM is free text, so if this distributed,
         // anyone who could spell the moderator's address could approve their own
         // posting. It must be treated as what it provably is - a stranger's
         // posting - and go to the real moderator for approval.
         SmtpClientSimulator.StaticSend(ModeratorAddress, ListAddress, "Forged approval", "Malicious body");

         // The forgery landed in the approval queue, in front of the real
         // moderator, labelled as awaiting approval...
         var moderatorCopy = Pop3ClientSimulator.AssertGetFirstMessageText(ModeratorAddress, Password);
         Assert.IsTrue(moderatorCopy.Contains("X-hMailServer-Moderation: " + ListAddress), moderatorCopy);

         // ...and no member ever saw it. This is the assertion that matters.
         ImapClientSimulator.AssertMessageCount(MemberAddress, Password, "Inbox", 0);
      }

      [Test]
      [Description("The list's bounce address becomes the envelope sender of the distributed copies")]
      public void BounceAddressBecomesEnvelopeSenderOfDistributedCopies()
      {
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, MemberAddress, Password);
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, PosterAddress, Password);
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "owner@example.test", Password);

         var recipients = new List<string> {MemberAddress};
         var list = SingletonProvider<TestSetup>.Instance.AddDistributionList(_domain, ListAddress, recipients);
         list.Mode = eDistributionListMode.eLMPublic;
         list.BounceAddress = "owner@example.test";
         list.Save();

         SmtpClientSimulator.StaticSend(PosterAddress, ListAddress, "Mail 1", "Mail 1");

         var memberCopy = Pop3ClientSimulator.AssertGetFirstMessageText(MemberAddress, Password);

         // Return-Path is written at local delivery from the message's envelope
         // sender, so this is asserting on the envelope itself: a bounce of this
         // copy goes to the list owner, not to the poster.
         Assert.IsTrue(memberCopy.Contains("Return-Path: <owner@example.test>"), memberCopy);
         Assert.IsFalse(memberCopy.Contains("Return-Path: <" + PosterAddress + ">"), memberCopy);

         CustomAsserts.AssertNoReportedError();
      }

      [Test]
      [Description("An empty bounce address preserves the original envelope sender - the off switch")]
      public void EmptyBounceAddressPreservesOriginalEnvelopeSender()
      {
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, MemberAddress, Password);
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, PosterAddress, Password);

         var recipients = new List<string> {MemberAddress};
         var list = SingletonProvider<TestSetup>.Instance.AddDistributionList(_domain, ListAddress, recipients);
         list.Mode = eDistributionListMode.eLMPublic;
         list.Save();

         SmtpClientSimulator.StaticSend(PosterAddress, ListAddress, "Mail 1", "Mail 1");

         var memberCopy = Pop3ClientSimulator.AssertGetFirstMessageText(MemberAddress, Password);

         // Exactly the old behaviour: the poster is the envelope sender, and no
         // moderation machinery has left a trace on an unmoderated list.
         Assert.IsTrue(memberCopy.Contains("Return-Path: <" + PosterAddress + ">"), memberCopy);
         Assert.IsFalse(memberCopy.Contains("X-hMailServer-Moderation:"), memberCopy);

         CustomAsserts.AssertNoReportedError();
      }

      [Test]
      [Description("Without a moderator, a refused sender is still refused at RCPT TO - moderation off is today's behaviour")]
      public void NoModeratorPreservesRefusalBehaviour()
      {
         CreateMemberOnlyList("");

         // Same posting as the first test, but the list has no moderator: the
         // membership mode's 550 comes back at RCPT TO like it always has.
         var refused = false;
         try
         {
            SmtpClientSimulator.StaticSend(PosterAddress, ListAddress, "Mail 1", "Mail 1");
         }
         catch (DeliveryFailedException ex)
         {
            refused = true;
            Assert.IsTrue(ex.Message.Contains("Not authorized sender."), ex.Message);
         }

         Assert.IsTrue(refused, "A non-member's posting to an unmoderated membership list was not refused.");

         // Refused means nobody got it - not the members, and not the moderator
         // account either, which in this test is just an ordinary mailbox.
         ImapClientSimulator.AssertMessageCount(MemberAddress, Password, "Inbox", 0);
         ImapClientSimulator.AssertMessageCount(ModeratorAddress, Password, "Inbox", 0);

         // And a member may still post: moderation off changes nothing for the
         // senders the mode accepts.
         SmtpClientSimulator.StaticSend(MemberAddress, ListAddress, "Mail 2", "Mail 2");
         ImapClientSimulator.AssertMessageCount(MemberAddress, Password, "Inbox", 1);
      }
   }
}

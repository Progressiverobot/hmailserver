// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Shared;

namespace RegressionTests.Domains
{
   /// <summary>
   ///    A domain rename has to be followed by every reference to an address at the
   ///    old name, including the references held by OTHER domains.
   ///    NameChanger::RenameDomain only ever walked the objects the renamed domain
   ///    itself owns, which is why the existing single-domain coverage in
   ///    Infrastructure/Persistence/Basics.cs (TestRenameDomainWithAliases,
   ///    TestRenameDomainWithList, TestRenameDomainWithAccountForward) passed while
   ///    this did not work at all.
   ///
   ///    The consequence is not cosmetic. Once the old name stops being hosted,
   ///    RecipientParser finds no account for it and falls through to its
   ///    route/external branch, so a message that used to be delivered into a local
   ///    mailbox is instead relayed to whatever MX answers for the old name on the
   ///    internet - and on the alias path bTreatSecurityAsLocal has already been
   ///    set, so the relay restriction does not stop it. Nothing in the log said so.
   ///
   ///    Against the unfixed server every Assert.AreEqual below reports the OLD
   ///    domain name, and the two delivery assertions time out because the message
   ///    was handed to the outbound queue instead of a mailbox.
   /// </summary>
   [TestFixture]
   public class DomainRenameCascade : TestFixtureBase
   {
      [Test]
      public void AliasTargetInAnotherDomainFollowsTheRename()
      {
         var secondary = SingletonProvider<TestSetup>.Instance.AddDomain("renamesource.test");
         SingletonProvider<TestSetup>.Instance.AddAccount(secondary, "target@renamesource.test", "secret1");

         SingletonProvider<TestSetup>.Instance.AddAlias(_domain, "sales@example.test", "target@renamesource.test");

         secondary.Name = "renamedestination.test";
         secondary.Save();

         var alias = _domain.Aliases.get_ItemByName("sales@example.test");
         Assert.AreEqual("target@renamedestination.test", alias.Value);
         Assert.IsTrue(alias.Active, "The alias should still be active - a rename is not a reason to switch it off.");

         // The assertion that matters: the mail arrives in the mailbox rather than
         // going out to the internet addressed to a domain we no longer host.
         var body = Guid.NewGuid().ToString();
         SmtpClientSimulator.StaticSend("someone@example.test", "sales@example.test", "Subject", body);

         Pop3ClientSimulator.AssertMessageCount("target@renamedestination.test", "secret1", 1);

         var text = Pop3ClientSimulator.AssertGetFirstMessageText("target@renamedestination.test", "secret1");
         StringAssert.Contains(body, text);
      }

      [Test]
      public void DistributionListMemberInAnotherDomainFollowsTheRename()
      {
         var secondary = SingletonProvider<TestSetup>.Instance.AddDomain("renamesource.test");
         SingletonProvider<TestSetup>.Instance.AddAccount(secondary, "member@renamesource.test", "secret1");

         var members = new List<string> { "member@renamesource.test" };
         SingletonProvider<TestSetup>.Instance.AddDistributionList(_domain, "list@example.test", members);

         secondary.Name = "renamedestination.test";
         secondary.Save();

         var list = _domain.DistributionLists.get_ItemByAddress("list@example.test");
         Assert.AreEqual(1, list.Recipients.Count);
         Assert.AreEqual("member@renamedestination.test", list.Recipients[0].RecipientAddress);

         var body = Guid.NewGuid().ToString();
         SmtpClientSimulator.StaticSend("someone@example.test", "list@example.test", "Subject", body);

         Pop3ClientSimulator.AssertMessageCount("member@renamedestination.test", "secret1", 1);

         var text = Pop3ClientSimulator.AssertGetFirstMessageText("member@renamedestination.test", "secret1");
         StringAssert.Contains(body, text);
      }

      [Test]
      public void ForwardAddressInAnotherDomainFollowsTheRename()
      {
         var secondary = SingletonProvider<TestSetup>.Instance.AddDomain("renamesource.test");
         SingletonProvider<TestSetup>.Instance.AddAccount(secondary, "target@renamesource.test", "secret1");

         var forwarder = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "forwarder@example.test", "secret1");
         forwarder.ForwardAddress = "target@renamesource.test";
         forwarder.ForwardEnabled = true;
         forwarder.Save();

         secondary.Name = "renamedestination.test";
         secondary.Save();

         var reloaded = _domain.Accounts.get_ItemByAddress("forwarder@example.test");
         Assert.AreEqual("target@renamedestination.test", reloaded.ForwardAddress);
         Assert.IsTrue(reloaded.ForwardEnabled, "Forwarding should stay switched on across a rename.");
      }

      [Test]
      public void ADisabledForwardAddressStillFollowsTheRename()
      {
         var secondary = SingletonProvider<TestSetup>.Instance.AddDomain("renamesource.test");
         SingletonProvider<TestSetup>.Instance.AddAccount(secondary, "target@renamesource.test", "secret1");

         // Forwarding switched off but the address left in place: a very common
         // state, and the address has to be rewritten anyway or switching
         // forwarding back on later reaches a domain that no longer exists.
         var forwarder = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "forwarder@example.test", "secret1");
         forwarder.ForwardAddress = "target@renamesource.test";
         forwarder.ForwardEnabled = false;
         forwarder.Save();

         secondary.Name = "renamedestination.test";
         secondary.Save();

         var reloaded = _domain.Accounts.get_ItemByAddress("forwarder@example.test");
         Assert.AreEqual("target@renamedestination.test", reloaded.ForwardAddress);
         Assert.IsFalse(reloaded.ForwardEnabled, "The rename must not switch forwarding on.");
      }

      /// <summary>
      ///    Negative control. The sweep matches the renamed domain name exactly, so a
      ///    reference to a genuinely external domain, to a third local domain, or to
      ///    a domain whose name merely ends with the renamed one must be left alone.
      ///    Without this a "rename example.test" would capture
      ///    anything@sub.example.test as well.
      /// </summary>
      [Test]
      public void ReferencesToOtherDomainsAreNotRewritten()
      {
         var secondary = SingletonProvider<TestSetup>.Instance.AddDomain("renamesource.test");
         var third = SingletonProvider<TestSetup>.Instance.AddDomain("third.test");
         var subdomain = SingletonProvider<TestSetup>.Instance.AddDomain("sub.renamesource.test");

         SingletonProvider<TestSetup>.Instance.AddAccount(third, "target@third.test", "secret1");
         SingletonProvider<TestSetup>.Instance.AddAccount(subdomain, "target@sub.renamesource.test", "secret1");

         SingletonProvider<TestSetup>.Instance.AddAlias(_domain, "toexternal@example.test", "target@external.example");
         SingletonProvider<TestSetup>.Instance.AddAlias(_domain, "tothird@example.test", "target@third.test");
         SingletonProvider<TestSetup>.Instance.AddAlias(_domain, "tosub@example.test", "target@sub.renamesource.test");

         secondary.Name = "renamedestination.test";
         secondary.Save();

         Assert.AreEqual("target@external.example", _domain.Aliases.get_ItemByName("toexternal@example.test").Value);
         Assert.AreEqual("target@third.test", _domain.Aliases.get_ItemByName("tothird@example.test").Value);
         Assert.AreEqual("target@sub.renamesource.test", _domain.Aliases.get_ItemByName("tosub@example.test").Value);
      }

      /// <summary>
      ///    A rename is matched without regard to case, because nothing normalises
      ///    the case of a stored alias target, list membership or forward address -
      ///    Basics.cs::TestRenameDomainWithList already relies on that for the
      ///    same-domain path, with a member stored as recipient2@Example.test.
      /// </summary>
      [Test]
      public void TheMatchIgnoresCase()
      {
         var secondary = SingletonProvider<TestSetup>.Instance.AddDomain("renamesource.test");
         SingletonProvider<TestSetup>.Instance.AddAccount(secondary, "target@renamesource.test", "secret1");

         SingletonProvider<TestSetup>.Instance.AddAlias(_domain, "sales@example.test", "Target@RenameSource.TEST");

         secondary.Name = "renamedestination.test";
         secondary.Save();

         Assert.AreEqual("Target@renamedestination.test", _domain.Aliases.get_ItemByName("sales@example.test").Value);
      }
   }
}

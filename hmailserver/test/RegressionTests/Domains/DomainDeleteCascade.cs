// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

using System;
using System.Collections.Generic;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;

namespace RegressionTests.Domains
{
   /// <summary>
   ///    Deleting a domain removes its own accounts, aliases, lists and domain
   ///    aliases. What it did not do was deal with the references OTHER local
   ///    domains hold to addresses inside it, and the result was worse than an
   ///    orphan row: an address at a deleted domain is no longer local, so
   ///    RecipientParser falls through to its external branch and the message is
   ///    relayed out to whatever MX now answers for that name on the internet. An
   ///    alias, a distribution list membership or an account forward that used to
   ///    deliver into a mailbox on this server silently became a forward to a third
   ///    party - permanently, and with nothing in the log to say it had happened.
   ///
   ///    Each reference is now dealt with in the way that fails closed and stays
   ///    visible: an alias is switched off (delivery is refused with "Alias is not
   ///    active."), forwarding is switched off but the address is kept so the
   ///    administrator can see where it used to go, and a list membership - which
   ///    has no active flag to switch - is removed and logged.
   ///
   ///    Against the unfixed server the alias stays active and the message is
   ///    ACCEPTED for outbound relay instead of being refused, the list keeps both
   ///    members, and forwarding stays on.
   /// </summary>
   [TestFixture]
   public class DomainDeleteCascade : TestFixtureBase
   {
      [Test]
      public void DeletingADomainDisablesAliasesPointingIntoIt()
      {
         var secondary = SingletonProvider<TestSetup>.Instance.AddDomain("deleted.test");
         SingletonProvider<TestSetup>.Instance.AddAccount(secondary, "target@deleted.test", "secret1");

         SingletonProvider<TestSetup>.Instance.AddAlias(_domain, "sales@example.test", "target@deleted.test");

         secondary.Delete();

         var alias = _domain.Aliases.get_ItemByName("sales@example.test");
         Assert.IsFalse(alias.Active, "The alias points at a domain which is no longer hosted, so it must not be followed.");
         Assert.AreEqual("target@deleted.test", alias.Value,
            "The target is deliberately left in place: the administrator has to be able to see what the alias used to resolve to.");

         // The mail is refused at RCPT TO rather than accepted and relayed to
         // whoever owns deleted.test now.
         CustomAsserts.Throws<DeliveryFailedException>(() =>
            SmtpClientSimulator.StaticSend("someone@example.test", "sales@example.test", "Subject", Guid.NewGuid().ToString()));
      }

      [Test]
      public void DeletingADomainRemovesDistributionListMembersInIt()
      {
         var secondary = SingletonProvider<TestSetup>.Instance.AddDomain("deleted.test");
         SingletonProvider<TestSetup>.Instance.AddAccount(secondary, "member@deleted.test", "secret1");
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "keeper@example.test", "secret1");

         var members = new List<string> { "member@deleted.test", "keeper@example.test" };
         SingletonProvider<TestSetup>.Instance.AddDistributionList(_domain, "list@example.test", members);

         secondary.Delete();

         var list = _domain.DistributionLists.get_ItemByAddress("list@example.test");
         Assert.AreEqual(1, list.Recipients.Count,
            "The member at the deleted domain should have been removed; every other member must survive.");
         Assert.AreEqual("keeper@example.test", list.Recipients[0].RecipientAddress);

         // The list still works for the members it has left.
         var body = Guid.NewGuid().ToString();
         SmtpClientSimulator.StaticSend("someone@example.test", "list@example.test", "Subject", body);

         Pop3ClientSimulator.AssertMessageCount("keeper@example.test", "secret1", 1);
      }

      [Test]
      public void DeletingADomainSwitchesOffForwardingIntoIt()
      {
         var secondary = SingletonProvider<TestSetup>.Instance.AddDomain("deleted.test");
         SingletonProvider<TestSetup>.Instance.AddAccount(secondary, "target@deleted.test", "secret1");

         var forwarder = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "forwarder@example.test", "secret1");
         forwarder.ForwardAddress = "target@deleted.test";
         forwarder.ForwardEnabled = true;
         forwarder.ForwardKeepOriginal = false;
         forwarder.Save();

         secondary.Delete();

         var reloaded = _domain.Accounts.get_ItemByAddress("forwarder@example.test");
         Assert.IsFalse(reloaded.ForwardEnabled,
            "Forwarding to a domain which is no longer hosted would send the mail off-site.");
         Assert.AreEqual("target@deleted.test", reloaded.ForwardAddress,
            "The address is kept so the administrator can see what forwarding was set to.");

         // With forwarding off and keep-original off, the mail must now stay in the
         // mailbox rather than disappear off-site.
         var body = Guid.NewGuid().ToString();
         SmtpClientSimulator.StaticSend("someone@example.test", "forwarder@example.test", "Subject", body);

         Pop3ClientSimulator.AssertMessageCount("forwarder@example.test", "secret1", 1);
      }

      /// <summary>
      ///    A domain answers under its domain aliases as well as its own name, and
      ///    those names stop resolving with it. An alias whose target names the
      ///    domain alias is exactly as exposed as one that names the domain.
      /// </summary>
      [Test]
      public void DeletingADomainAlsoCoversItsDomainAliases()
      {
         var secondary = SingletonProvider<TestSetup>.Instance.AddDomain("deleted.test");
         SingletonProvider<TestSetup>.Instance.AddAccount(secondary, "target@deleted.test", "secret1");

         var domainAlias = secondary.DomainAliases.Add();
         domainAlias.AliasName = "deletedalias.test";
         domainAlias.Save();

         SingletonProvider<TestSetup>.Instance.AddAlias(_domain, "sales@example.test", "target@deletedalias.test");

         secondary.Delete();

         Assert.IsFalse(_domain.Aliases.get_ItemByName("sales@example.test").Active);
      }

      /// <summary>
      ///    Negative control. Deleting one domain must not disturb references into
      ///    any other domain, external or local, and must not capture a domain whose
      ///    name merely ends with the deleted one.
      /// </summary>
      [Test]
      public void DeletingADomainLeavesReferencesToOtherDomainsAlone()
      {
         var secondary = SingletonProvider<TestSetup>.Instance.AddDomain("deleted.test");
         var third = SingletonProvider<TestSetup>.Instance.AddDomain("third.test");
         var subdomain = SingletonProvider<TestSetup>.Instance.AddDomain("sub.deleted.test");

         SingletonProvider<TestSetup>.Instance.AddAccount(third, "target@third.test", "secret1");
         SingletonProvider<TestSetup>.Instance.AddAccount(subdomain, "target@sub.deleted.test", "secret1");

         SingletonProvider<TestSetup>.Instance.AddAlias(_domain, "toexternal@example.test", "target@external.example");
         SingletonProvider<TestSetup>.Instance.AddAlias(_domain, "tothird@example.test", "target@third.test");
         SingletonProvider<TestSetup>.Instance.AddAlias(_domain, "tosub@example.test", "target@sub.deleted.test");

         var forwarder = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "forwarder@example.test", "secret1");
         forwarder.ForwardAddress = "target@third.test";
         forwarder.ForwardEnabled = true;
         forwarder.Save();

         secondary.Delete();

         Assert.IsTrue(_domain.Aliases.get_ItemByName("toexternal@example.test").Active);
         Assert.IsTrue(_domain.Aliases.get_ItemByName("tothird@example.test").Active);
         Assert.IsTrue(_domain.Aliases.get_ItemByName("tosub@example.test").Active);

         Assert.IsTrue(_domain.Accounts.get_ItemByAddress("forwarder@example.test").ForwardEnabled);
      }

      /// <summary>
      ///    The stored case of an alias target, a list membership or a forward
      ///    address is whatever was typed - nothing normalises it - so the match has
      ///    to ignore case or the exposure survives for any reference that happens to
      ///    be capitalised differently from the domain.
      /// </summary>
      [Test]
      public void TheMatchIgnoresCase()
      {
         var secondary = SingletonProvider<TestSetup>.Instance.AddDomain("deleted.test");
         SingletonProvider<TestSetup>.Instance.AddAccount(secondary, "target@deleted.test", "secret1");

         SingletonProvider<TestSetup>.Instance.AddAlias(_domain, "sales@example.test", "Target@Deleted.TEST");

         secondary.Delete();

         Assert.IsFalse(_domain.Aliases.get_ItemByName("sales@example.test").Active);
      }
   }
}

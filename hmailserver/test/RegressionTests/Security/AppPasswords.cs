// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Shared;
using hMailServer;

namespace RegressionTests.Security
{
   /// <summary>
   ///    Application-specific passwords: a per-account credential, revocable on its
   ///    own, that authenticates over IMAP, POP3 and SMTP alongside the account's own
   ///    password.
   ///
   ///    They exist because per-account two-factor authentication is otherwise
   ///    impossible. A mail client has nowhere to type a code, so an account that
   ///    required one could not be opened by any client at all - which is why every
   ///    large provider issues a credential per client instead, each revocable when a
   ///    laptop goes missing without changing the password the person actually knows.
   ///
   ///    Two properties are load-bearing and each has a test here: the clear text
   ///    exists exactly once, at generation, and is never readable afterwards; and the
   ///    account's own password keeps working, because an app password is an addition
   ///    rather than a replacement.
   /// </summary>
   [TestFixture]
   public class AppPasswords : TestFixtureBase
   {
      private Account _account;

      [SetUp]
      public new void SetUp()
      {
         _account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "appuser@example.test", "test");

         RequireRegisteredInterfaces(_account);
      }

      /// <summary>
      ///    Reports an unregistered COM interface as what it is - a machine that has not
      ///    run the server's registration since the interface was added - rather than as
      ///    eight failing security tests.
      ///
      ///    hMailServer is an out-of-process COM server running as LocalSystem, so the
      ///    marshaller resolves IInterfaceAppPasswords through HKLM - a per-user
      ///    registration does not reach it. post-build.bat registers after every build
      ///    and the installer registers on install, so the registered state is the
      ///    normal one; a developer who built in a non-elevated shell has the other one,
      ///    and REGDB_E_IIDNOTREG from every test in this fixture says nothing at all
      ///    about app passwords.
      ///
      ///    Deliberately a runtime check rather than [Explicit]: an [Explicit] fixture
      ///    stays skipped on the machines where it WOULD pass, and a security feature
      ///    whose tests never run anywhere is indistinguishable from one that does not
      ///    work. This way the fixture runs by default and stands down only on the exact
      ///    error that means it cannot.
      /// </summary>
      private static void RequireRegisteredInterfaces(Account account)
      {
         try
         {
            var probe = account.AppPasswords;
            ClassicAssert.IsNotNull(probe);
         }
         catch (System.Runtime.InteropServices.COMException ex) when ((uint) ex.ErrorCode == 0x80040155)
         {
            Assert.Ignore(
               "The app-password COM interfaces are not registered on this machine, so nothing here can run. " +
               "Run 'hMailServer.exe /RegisterTypeLib' from an ELEVATED prompt in " +
               "hmailserver/source/Server/hMailServer/x64/Release, then run this fixture again. " +
               "/RegisterTypeLib and not /Register: the latter also re-creates the Windows service, " +
               "which drops the sc sdset grant that lets this suite start and stop it unelevated. " +
               "REGDB_E_IIDNOTREG: " + ex.Message);
         }
      }

      private static string Issue(Account account, string name)
      {
         var passwords = account.AppPasswords;
         var password = passwords.Add();
         password.Name = name;
         string clearText = password.Generate();
         password.Save();

         ClassicAssert.IsFalse(string.IsNullOrEmpty(clearText),
            "Generate must return the clear text - it is the only time it exists.");

         return clearText;
      }

      [Test]
      [Description("An app password logs the account in over IMAP, POP3 and SMTP, and the account's own password still does too")]
      public void AnAppPasswordAuthenticatesEveryProtocol()
      {
         string appPassword = Issue(_account, "Thunderbird on the laptop");

         var imap = new ImapClientSimulator();
         ClassicAssert.IsTrue(imap.ConnectAndLogon(_account.Address, appPassword),
            "IMAP must accept the app password.");
         imap.Disconnect();

         var pop3 = new Pop3ClientSimulator();
         ClassicAssert.IsTrue(pop3.ConnectAndLogon(_account.Address, appPassword),
            "POP3 must accept the app password.");
         pop3.Disconnect();

         var smtp = new SmtpClientSimulator();
         string smtpError;
         smtp.Send(false, _account.Address, appPassword, _account.Address, _account.Address,
            "app password", "Sent with an app password.", out smtpError);

         Pop3ClientSimulator.AssertMessageCount(_account.Address, "test", 1);

         // The negative control that makes the rest of this fixture mean something: an
         // app password is an addition, not a replacement. A change that made the
         // account password stop working would otherwise pass every test above.
         var stillWorks = new ImapClientSimulator();
         ClassicAssert.IsTrue(stillWorks.ConnectAndLogon(_account.Address, "test"),
            "The account's own password must keep working.");
         stillWorks.Disconnect();
      }

      [Test]
      [Description("A wrong password is still refused once app passwords exist - the check must not become a way in")]
      public void AWrongPasswordIsStillRefused()
      {
         Issue(_account, "Some client");

         var imap = new ImapClientSimulator();
         ClassicAssert.IsFalse(imap.ConnectAndLogon(_account.Address, "not-the-password"),
            "A wrong password must still be refused.");
         imap.Disconnect();

         var pop3 = new Pop3ClientSimulator();
         ClassicAssert.IsFalse(pop3.ConnectAndLogon(_account.Address, "not-the-password"),
            "A wrong password must still be refused on POP3 too.");
         pop3.Disconnect();
      }

      [Test]
      [Description("The clear text is not readable after generation - the store holds a hash, and nothing hands it back")]
      public void TheClearTextIsNotReadableAfterwards()
      {
         string appPassword = Issue(_account, "Only once");

         var passwords = _account.AppPasswords;
         passwords.Refresh();

         ClassicAssert.AreEqual(1, passwords.Count);

         var stored = passwords[0];

         // The interface has no property that returns it. What it does expose - the
         // name, the timestamps, the active flag - must not contain it either, which is
         // the shape a well-meaning "convenience" addition would break.
         ClassicAssert.AreNotEqual(appPassword, stored.Name);
         ClassicAssert.IsFalse(stored.Name.Contains(appPassword));
         ClassicAssert.IsFalse(stored.CreatedTime.Contains(appPassword));
      }

      [Test]
      [Description("Revoking with Active=false refuses the credential without deleting it, so it can be put back")]
      public void RevokingRefusesItAndCanBeUndone()
      {
         string appPassword = Issue(_account, "Old phone");

         var passwords = _account.AppPasswords;
         passwords.Refresh();
         var stored = passwords[0];
         stored.Active = false;
         stored.Save();

         var refused = new ImapClientSimulator();
         ClassicAssert.IsFalse(refused.ConnectAndLogon(_account.Address, appPassword),
            "A revoked app password must be refused.");
         refused.Disconnect();

         stored.Active = true;
         stored.Save();

         var restored = new ImapClientSimulator();
         ClassicAssert.IsTrue(restored.ConnectAndLogon(_account.Address, appPassword),
            "Re-activating it must let it back in - that is the point of a flag rather than a delete.");
         restored.Disconnect();
      }

      [Test]
      [Description("Deleting one leaves the others working - revocation has to be per credential or it is not revocation")]
      public void DeletingOneLeavesTheOthers()
      {
         string laptop = Issue(_account, "Laptop");
         string phone = Issue(_account, "Phone");

         var passwords = _account.AppPasswords;
         passwords.Refresh();
         ClassicAssert.AreEqual(2, passwords.Count);

         passwords.DeleteByDBID(passwords[0].ID);

         var refused = new ImapClientSimulator();
         ClassicAssert.IsFalse(refused.ConnectAndLogon(_account.Address, laptop),
            "The deleted credential must be refused.");
         refused.Disconnect();

         var accepted = new ImapClientSimulator();
         ClassicAssert.IsTrue(accepted.ConnectAndLogon(_account.Address, phone),
            "The other credential must be untouched.");
         accepted.Disconnect();
      }

      [Test]
      [Description("An app password opens its own account and no other")]
      public void ItDoesNotOpenAnotherAccount()
      {
         var other = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "otheruser@example.test", "test");

         string appPassword = Issue(_account, "Scoped");

         var imap = new ImapClientSimulator();
         ClassicAssert.IsFalse(imap.ConnectAndLogon(other.Address, appPassword),
            "An app password must not authenticate a different account.");
         imap.Disconnect();
      }

      [Test]
      [Description("LastUsedTime is empty until the credential authenticates, which is what identifies one nobody needs")]
      public void LastUsedIsEmptyUntilItAuthenticates()
      {
         string appPassword = Issue(_account, "Never used");

         var before = _account.AppPasswords;
         before.Refresh();
         ClassicAssert.AreEqual("", before[0].LastUsedTime,
            "A credential that has never authenticated must say so - it is the one an administrator can revoke with no consequences.");

         var imap = new ImapClientSimulator();
         ClassicAssert.IsTrue(imap.ConnectAndLogon(_account.Address, appPassword));
         imap.Disconnect();

         var after = _account.AppPasswords;
         after.Refresh();
         ClassicAssert.AreNotEqual("", after[0].LastUsedTime,
            "Once it has authenticated, the time must be recorded.");
      }

      [Test]
      [Description("Deleting the account deletes its app passwords - a credential outliving its mailbox is one nobody will revoke")]
      public void DeletingTheAccountTakesThemWithIt()
      {
         var doomed = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "doomed@example.test", "test");
         string appPassword = Issue(doomed, "Doomed");

         doomed.Delete();

         var recreated = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "doomed@example.test", "test");

         var passwords = recreated.AppPasswords;
         passwords.Refresh();

         ClassicAssert.AreEqual(0, passwords.Count,
            "Account ids are reissued, so a row left behind would attach the previous holder's credential to the new owner's mailbox.");

         var imap = new ImapClientSimulator();
         ClassicAssert.IsFalse(imap.ConnectAndLogon(recreated.Address, appPassword),
            "And it must not authenticate.");
         imap.Disconnect();
      }

      [Test]
      [Description("A generated secret carries real entropy - 20 symbols from a 30-character alphabet, not the 48 bits the general password generator returns")]
      public void AGeneratedSecretIsLongEnoughToBeWorthHaving()
      {
         string first = Issue(_account, "Entropy one");
         string second = Issue(_account, "Entropy two");

         string stripped = first.Replace("-", "");

         ClassicAssert.AreEqual(20, stripped.Length,
            "20 symbols from a 30-character alphabet is about 98 bits. The server's general " +
            "PasswordGenerator returns twelve hex characters - 48 bits - which is not enough for " +
            "a credential that is typed into a client once and then lives for years. Got: " + first);

         ClassicAssert.AreNotEqual(first, second,
            "Two generated secrets must differ, which is the cheapest possible check that the " +
            "generator is not returning something fixed.");

         foreach (char c in stripped)
         {
            ClassicAssert.IsTrue("23456789ABCDEFGHJKMNPQRSTVWXYZ".IndexOf(c) >= 0,
               "The alphabet omits I, L, O, U, 0 and 1 so the secret survives being read aloud " +
               "and retyped. Unexpected character '" + c + "' in: " + first);
         }

         // And it authenticates, which is the part that would break if the separators
         // were stored but not returned, or the other way round.
         var imap = new ImapClientSimulator();
         ClassicAssert.IsTrue(imap.ConnectAndLogon(_account.Address, first),
            "The generated secret must authenticate exactly as returned, separators included.");
         imap.Disconnect();
      }

      [Test]
      [Description("A chosen password shorter than 12 characters is refused rather than quietly stored")]
      public void AShortChosenPasswordIsRefused()
      {
         var passwords = _account.AppPasswords;
         var password = passwords.Add();
         password.Name = "Too short";

         Assert.Throws<System.Runtime.InteropServices.COMException>(() => password.SetPassword("short"),
            "A credential that opens a mailbox must not be settable to something guessable.");
      }

      [Test]
      [Description("An account with no app passwords is unaffected: the ordinary password path is untouched")]
      public void AnAccountWithNoneIsUnaffected()
      {
         var plain = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "plain@example.test", "test");

         var accepted = new ImapClientSimulator();
         ClassicAssert.IsTrue(accepted.ConnectAndLogon(plain.Address, "test"));
         accepted.Disconnect();

         var refused = new ImapClientSimulator();
         ClassicAssert.IsFalse(refused.ConnectAndLogon(plain.Address, "wrong"));
         refused.Disconnect();
      }
   }
}

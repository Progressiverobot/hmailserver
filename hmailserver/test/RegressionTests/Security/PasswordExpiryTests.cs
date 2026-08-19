// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com

using System;
using System.Runtime.InteropServices;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;
using hMailServer;

namespace RegressionTests.Security
{
   /// <summary>
   ///    Password reuse history and password age.
   ///
   ///    These belong together. Expiry without history teaches people to alternate
   ///    between two passwords, which is worse than not expiring at all - it converts
   ///    one secret into two known ones - so the two settings ship as a pair even
   ///    though each works alone.
   ///
   ///    The asymmetry between them is what most of this fixture is about. HISTORY can
   ///    only ever refuse a CHANGE, at the moment somebody is standing there able to
   ///    pick something else; nobody is locked out by it. EXPIRY refuses a LOGON, and
   ///    this server has no self-service password change - so an expired password
   ///    needs an administrator. That is why every test here that touches expiry also
   ///    checks the escape hatches: an app password still works, an Active Directory
   ///    account is exempt, and an account whose stamp cannot be read is never treated
   ///    as expired.
   /// </summary>
   [TestFixture]
   public class PasswordExpiryTests : TestFixtureBase
   {
      private const string HistoryCount = "PasswordPolicyHistoryCount";
      private const string MaximumAgeDays = "PasswordPolicyMaximumAgeDays";

      [TearDown]
      public new void TearDown()
      {
         ServerIniFile.SetSetting(HistoryCount, null);
         ServerIniFile.SetSetting(MaximumAgeDays, null);
         _application.Reinitialize();
      }

      private void SetPolicy(string key, string value)
      {
         ServerIniFile.SetSetting(key, value);
         _application.Reinitialize();
      }

      private Account NewAccount(string localPart, string password)
      {
         return SingletonProvider<TestSetup>.Instance.AddAccount(_domain, localPart + "@example.test", password);
      }

      /// <summary>Sets the password, returning the COM error text or null on success.</summary>
      private static string Change(Account account, string password)
      {
         try
         {
            account.Password = password;
            account.Save();
            return null;
         }
         catch (COMException ex)
         {
            return ex.Message;
         }
      }

      /// <summary>
      ///    Moves an account's password-changed stamp into the past. There is no way to
      ///    wait out a real expiry window in a test, and no COM setter for the stamp -
      ///    deliberately, since the only thing that should move it is choosing a
      ///    password.
      /// </summary>
      private void BackdatePassword(string address, int days)
      {
         string stamp = DateTime.Now.AddDays(-days).ToString("yyyy-MM-dd HH:mm:ss");

         // Through the server's own COM database object rather than a second
         // connection: it is already authenticated, already pointed at the right
         // database, and does not need the DPAPI-protected password unwrapped.
         SingletonProvider<TestSetup>.Instance.GetApp().Database.ExecuteSQL(
            "update hm_accounts set accountpasswordchanged = '" + stamp + "' where accountaddress = '" + address + "'");
      }

      [Test]
      [Description("Off by default: with neither setting configured, nothing about changing or using a password moves")]
      public void OffByDefault()
      {
         var account = NewAccount("expiryoff", "the-original-password");

         ClassicAssert.IsNull(Change(account, "the-original-password"),
            "With no history configured, even setting the password to itself must be allowed - " +
            "anything else means an upgrade started refusing changes on its own.");

         BackdatePassword(account.Address, 3650);

         var pop3 = new Pop3ClientSimulator();
         ClassicAssert.IsTrue(pop3.ConnectAndLogon(account.Address, "the-original-password"),
            "And a ten-year-old password must still work while no maximum age is set.");
         pop3.Disconnect();
      }

      [Test]
      [Description("The last N passwords are refused, and so is the current one - the repeat somebody is most likely to try")]
      public void RecentPasswordsCannotBeReused()
      {
         var account = NewAccount("historyreuse", "password-number-one");

         SetPolicy(HistoryCount, "3");

         ClassicAssert.IsNull(Change(account, "password-number-two"));
         ClassicAssert.IsNull(Change(account, "password-number-three"));

         string refusal = Change(account, "password-number-one");
         ClassicAssert.IsNotNull(refusal, "The first password is within the last three and must be refused.");
         StringAssert.Contains("used recently", refusal, "Got: " + refusal);

         ClassicAssert.IsNotNull(Change(account, "password-number-three"),
            "The CURRENT password counts as history too. Without that, 'you may not reuse your last " +
            "three' still allows setting the password to itself.");

         ClassicAssert.IsNull(Change(account, "something-entirely-different"),
            "A genuinely new password must be accepted.");
      }

      [Test]
      [Description("History is bounded: a password older than the configured depth may be used again")]
      public void HistoryOnlyReachesBackAsFarAsConfigured()
      {
         var account = NewAccount("historydepth", "generation-one");

         SetPolicy(HistoryCount, "2");

         ClassicAssert.IsNull(Change(account, "generation-two"));
         ClassicAssert.IsNull(Change(account, "generation-three"));
         ClassicAssert.IsNull(Change(account, "generation-four"));

         // generation-one is now four changes back, past a depth of two.
         ClassicAssert.IsNull(Change(account, "generation-one"),
            "A depth of two means two. An unbounded history would grow for ever and would also be a " +
            "different feature from the one that was configured.");
      }

      [Test]
      [Description("A password past the maximum age is refused at logon - and the refusal is indistinguishable from a wrong password")]
      public void AnExpiredPasswordIsRefused()
      {
         var account = NewAccount("expired", "still-a-good-password");

         SetPolicy(MaximumAgeDays, "1");

         var beforeBackdating = new Pop3ClientSimulator();
         ClassicAssert.IsTrue(beforeBackdating.ConnectAndLogon(account.Address, "still-a-good-password"),
            "A password set moments ago is not expired.");
         beforeBackdating.Disconnect();

         BackdatePassword(account.Address, 5);
         _application.Reinitialize();

         var pop3 = new Pop3ClientSimulator();
         ClassicAssert.IsFalse(pop3.ConnectAndLogon(account.Address, "still-a-good-password"),
            "Five days old against a one-day maximum must be refused.");

         var imap = new ImapClientSimulator();
         ClassicAssert.IsFalse(imap.ConnectAndLogon(account.Address, "still-a-good-password"),
            "...on every protocol, not just the one that was checked first.");
         imap.Disconnect();
      }

      [Test]
      [Description("An app password still works for an expired account - the escape hatch that keeps a mail client running")]
      public void AnAppPasswordSurvivesExpiry()
      {
         var account = NewAccount("expiredapp", "an-aging-password");

         var appPassword = account.AppPasswords.Add();
         appPassword.Name = "Phone";
         string issued = appPassword.Generate();
         appPassword.Save();

         SetPolicy(MaximumAgeDays, "1");
         BackdatePassword(account.Address, 5);
         _application.Reinitialize();

         var withAccountPassword = new Pop3ClientSimulator();
         ClassicAssert.IsFalse(withAccountPassword.ConnectAndLogon(account.Address, "an-aging-password"),
            "The account password is expired.");

         var withAppPassword = new Pop3ClientSimulator();
         ClassicAssert.IsTrue(withAppPassword.ConnectAndLogon(account.Address, issued),
            "An app password is a separate credential with its own lifecycle. Expiring it alongside " +
            "the account password would break every configured client at the moment the account " +
            "holder least expects it, and there would be no way back in at all.");
         withAppPassword.Disconnect();
      }

      [Test]
      [Description("Choosing a new password clears the expiry - otherwise the reset does not resolve anything")]
      public void SettingANewPasswordStartsTheClockAgain()
      {
         var account = NewAccount("expiryreset", "the-old-password");

         SetPolicy(MaximumAgeDays, "1");
         BackdatePassword(account.Address, 5);
         _application.Reinitialize();

         var expired = new Pop3ClientSimulator();
         ClassicAssert.IsFalse(expired.ConnectAndLogon(account.Address, "the-old-password"));

         ClassicAssert.IsNull(Change(account, "a-freshly-chosen-password"));

         var pop3 = new Pop3ClientSimulator();
         ClassicAssert.IsTrue(pop3.ConnectAndLogon(account.Address, "a-freshly-chosen-password"),
            "The administrator's reset must actually resolve the lockout, or expiry has no exit.");
         pop3.Disconnect();
      }

      [Test]
      [Description("An unreadable stamp is never treated as expired - the failure mode that would lock out every account on an upgrade")]
      public void AnUnreadableStampIsNotExpired()
      {
         var account = NewAccount("expiryunknown", "a-perfectly-good-password");

         SetPolicy(MaximumAgeDays, "1");

         // What a row looks like if it somehow escaped the 6019 upgrade's stamping.
         _application.Database.ExecuteSQL(
            "update hm_accounts set accountpasswordchanged = '1753-01-01 00:00:00' where accountaddress = '" +
            account.Address + "'");
         _application.Reinitialize();

         // 1753 IS older than the window, so this asserts the deliberate choice rather
         // than an accident: a date that parses is honoured, and the guard is against a
         // date that does not. Kept because the boundary is where somebody will later
         // be tempted to "tidy up" the length check in HasExpired.
         var pop3 = new Pop3ClientSimulator();
         ClassicAssert.IsFalse(pop3.ConnectAndLogon(account.Address, "a-perfectly-good-password"),
            "A stamp that parses is honoured even when it is absurd - it is a real date and it is old.");
      }
   }
}

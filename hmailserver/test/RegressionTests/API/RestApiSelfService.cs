// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using hMailServer;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;

namespace RegressionTests.API
{
   /// <summary>
   ///    The account's own endpoints on the REST listener - GET /api/v1/me,
   ///    POST /api/v1/me/password, PUT /api/v1/me/vacation - and the sign-in
   ///    page that presents them. An account's credentials reach exactly these
   ///    three and nothing else; the administrator password and an API key
   ///    reach everything else and not these. Every assertion is made against
   ///    what the server then did: a password that logs on (or no longer does),
   ///    a vacation message COM reads back.
   /// </summary>
   [TestFixture]
   public class RestApiSelfService : TestFixtureBase
   {
      private const int RestPort = 9530;
      private const string AdminPassword = "testar";
      private const string UserPassword = "Original-Passw0rd!";

      private Account _account;

      // The fixture domain's name is not fixed, so the address follows it.
      private string Address
      {
         get { return "self@" + _domain.Name; }
      }

      [SetUp]
      public void StartRestApi()
      {
         _settings.SetAdministratorPassword(AdminPassword);

         _account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, Address, UserPassword);

         IniFileSetting.Write("RestApiBindAddress", "127.0.0.1");
         IniFileSetting.Write("RestApiPort", RestPort.ToString());
         IniFileSetting.Write("RestApiCertificateFile", "");
         IniFileSetting.Write("RestApiPrivateKeyFile", "");

         _application.Reinitialize();

         (int status, string body) probe = Http("GET", "/api/v1/status", AdminHeader());
         Assert.AreEqual(200, probe.status, "REST API did not answer /api/v1/status. Body: " + probe.body);
      }

      [TearDown]
      public void StopRestApi()
      {
         IniFileSetting.Write("RestApiPort", "0");
         _application.Reinitialize();
      }

      [Test]
      [Description("GET /api/v1/me answers an account's own credentials with its own state")]
      public void MeAnswersTheAccountsOwnState()
      {
         _account.MaxSize = 25;
         _account.Save();

         (int status, string body) me = Http("GET", "/api/v1/me", UserHeader(UserPassword));

         Assert.AreEqual(200, me.status, "Body: " + me.body);
         StringAssert.Contains("\"address\":\"" + Address + "\"", me.body);
         StringAssert.Contains("\"domain\":\"" + _domain.Name + "\"", me.body);
         StringAssert.Contains("\"limit_mb\":25", me.body);
         StringAssert.Contains("\"used_bytes\":", me.body);
         StringAssert.Contains("\"vacation\":{\"enabled\":false", me.body);
         StringAssert.Contains("\"second_factor\":false", me.body);
         StringAssert.Contains("\"directory_linked\":false", me.body);
      }

      [Test]
      [Description("The account's credentials reach the account's endpoints only, and the administrator's reach everything but them")]
      public void EachCredentialReachesItsOwnSurfaceOnly()
      {
         (int status, string body) wrongPassword = Http("GET", "/api/v1/me", UserHeader("not-the-password"));
         Assert.AreEqual(401, wrongPassword.status, "Body: " + wrongPassword.body);

         (int status, string body) unknownAccount = Http("GET", "/api/v1/me", BasicHeader("nobody@" + _domain.Name, UserPassword));
         Assert.AreEqual(401, unknownAccount.status, "Body: " + unknownAccount.body);

         (int status, string body) adminOnMe = Http("GET", "/api/v1/me", AdminHeader());
         Assert.AreEqual(403, adminOnMe.status, "The administrator is not an account. Body: " + adminOnMe.body);
         StringAssert.Contains("account's own credentials", adminOnMe.body);

         (int status, string body) accountOnAdmin = Http("GET", "/api/v1/status", UserHeader(UserPassword));
         Assert.AreEqual(403, accountOnAdmin.status, "An account must not reach the administration API. Body: " + accountOnAdmin.body);

         (int status, string body) accountOnDomains = Http("GET", "/api/v1/domains", UserHeader(UserPassword));
         Assert.AreEqual(403, accountOnDomains.status, "Body: " + accountOnDomains.body);
      }

      [Test]
      [Description("PUT /api/v1/me/vacation sets the automatic reply COM reads back, and clears it again")]
      public void VacationRoundTrip()
      {
         (int status, string body) on = Http("PUT", "/api/v1/me/vacation", UserHeader(UserPassword),
            "{\"enabled\":true,\"subject\":\"Away until Monday\",\"message\":\"I am out of the office.\",\"expires\":true,\"expires_date\":\"2099-12-31\"}");

         Assert.AreEqual(200, on.status, "Body: " + on.body);
         StringAssert.Contains("\"enabled\":true", on.body);

         Account reread = _domain.Accounts.ItemByAddress[Address];
         Assert.IsTrue(reread.VacationMessageIsOn, "COM must see the automatic reply switched on.");
         Assert.AreEqual("Away until Monday", reread.VacationSubject);
         Assert.AreEqual("I am out of the office.", reread.VacationMessage);
         Assert.IsTrue(reread.VacationMessageExpires);

         // The store keeps a date-time; the day is what was asked for.
         StringAssert.StartsWith("2099-12-31", reread.VacationMessageExpiresDate);

         (int status, string body) me = Http("GET", "/api/v1/me", UserHeader(UserPassword));
         StringAssert.Contains("\"subject\":\"Away until Monday\"", me.body);

         // And the API hands the day back in the form it accepts, so a page can
         // put it straight into a date field.
         StringAssert.Contains("\"expires_date\":\"2099-12-31\"", me.body);

         (int status, string body) off = Http("PUT", "/api/v1/me/vacation", UserHeader(UserPassword),
            "{\"enabled\":false,\"subject\":\"\",\"message\":\"\"}");

         Assert.AreEqual(200, off.status, "Body: " + off.body);

         reread = _domain.Accounts.ItemByAddress[Address];
         Assert.IsFalse(reread.VacationMessageIsOn, "COM must see the automatic reply switched off again.");
      }

      [Test]
      [Description("A vacation body without 'enabled', or with a malformed expiry date, is refused")]
      public void MalformedVacationIsRefused()
      {
         (int status, string body) noEnabled = Http("PUT", "/api/v1/me/vacation", UserHeader(UserPassword),
            "{\"subject\":\"x\"}");
         Assert.AreEqual(400, noEnabled.status, "Body: " + noEnabled.body);

         (int status, string body) badDate = Http("PUT", "/api/v1/me/vacation", UserHeader(UserPassword),
            "{\"enabled\":true,\"expires\":true,\"expires_date\":\"31/12/2099\"}");
         Assert.AreEqual(400, badDate.status, "Body: " + badDate.body);
         StringAssert.Contains("YYYY-MM-DD", badDate.body);
      }

      [Test]
      [Description("POST /api/v1/me/password changes the password the account logs on with")]
      public void PasswordChangeRoundTrip()
      {
         const string newPassword = "Replacement-Passw0rd!";

         (int status, string body) changed = Http("POST", "/api/v1/me/password", UserHeader(UserPassword),
            "{\"current\":\"" + UserPassword + "\",\"new\":\"" + newPassword + "\"}");

         Assert.AreEqual(200, changed.status, "Body: " + changed.body);

         (int status, string body) oldRefused = Http("GET", "/api/v1/me", UserHeader(UserPassword));
         Assert.AreEqual(401, oldRefused.status, "The old password must no longer log on. Body: " + oldRefused.body);

         (int status, string body) newAccepted = Http("GET", "/api/v1/me", UserHeader(newPassword));
         Assert.AreEqual(200, newAccepted.status, "The new password must log on. Body: " + newAccepted.body);

         // The stamp the account carries, which the page shows.
         StringAssert.Contains("\"password_changed\":\"20", newAccepted.body);

         // And what a mail client sees: a message delivered, then collected
         // with the new password.
         SmtpClientSimulator.StaticSend("test@test.com", Address, "After the change", "Collected with the new password.");
         Pop3ClientSimulator.AssertMessageCount(Address, newPassword, 1);
      }

      [Test]
      [Description("The current password has to be the account password itself, and must match")]
      public void WrongCurrentPasswordIsRefused()
      {
         (int status, string body) refused = Http("POST", "/api/v1/me/password", UserHeader(UserPassword),
            "{\"current\":\"not-it\",\"new\":\"Another-Passw0rd!\"}");

         Assert.AreEqual(403, refused.status, "Body: " + refused.body);

         (int status, string body) still = Http("GET", "/api/v1/me", UserHeader(UserPassword));
         Assert.AreEqual(200, still.status, "The password must be unchanged. Body: " + still.body);

         (int status, string body) missing = Http("POST", "/api/v1/me/password", UserHeader(UserPassword),
            "{\"new\":\"Another-Passw0rd!\"}");
         Assert.AreEqual(400, missing.status, "Body: " + missing.body);
      }

      [Test]
      [Description("The password policy that binds an administrator binds the account too")]
      public void PasswordPolicyIsApplied()
      {
         IniFileSetting.Write("PasswordPolicyMinimumLength", "16");
         _application.Reinitialize();

         try
         {
            (int status, string body) tooShort = Http("POST", "/api/v1/me/password", UserHeader(UserPassword),
               "{\"current\":\"" + UserPassword + "\",\"new\":\"Short-1!\"}");

            Assert.AreEqual(400, tooShort.status, "Body: " + tooShort.body);
            StringAssert.Contains("\"error\"", tooShort.body);

            (int status, string body) still = Http("GET", "/api/v1/me", UserHeader(UserPassword));
            Assert.AreEqual(200, still.status, "The password must be unchanged. Body: " + still.body);
         }
         finally
         {
            IniFileSetting.Write("PasswordPolicyMinimumLength", "0");
            _application.Reinitialize();
         }
      }

      [Test]
      [Description("The sign-in page and its script are served without authentication, with a policy that allows no inline script")]
      public void ThePortalIsServedWithItsPolicy()
      {
         Response page = Raw("GET", "/portal", null, null);
         Assert.AreEqual(200, page.Status, page.Body);
         StringAssert.StartsWith("text/html", page.Header("Content-Type"));
         StringAssert.Contains("script-src 'self'", page.Header("Content-Security-Policy"));
         StringAssert.Contains("frame-ancestors 'none'", page.Header("Content-Security-Policy"));
         Assert.AreEqual("no-store", page.Header("Cache-Control"));
         Assert.AreEqual("nosniff", page.Header("X-Content-Type-Options"));
         StringAssert.Contains("<script src=\"/portal.js\"></script>", page.Body);
         Assert.IsFalse(page.Body.Contains("onclick="), "No inline event handler: the policy would block it.");

         Response script = Raw("GET", "/portal.js", null, null);
         Assert.AreEqual(200, script.Status, script.Body);
         StringAssert.StartsWith("text/javascript", script.Header("Content-Type"));
         StringAssert.Contains("/api/v1/me", script.Body);
         StringAssert.Contains("/api/v1/me/folders", script.Body);
      }

      [Test]
      [Description("POST /api/v1/session turns the password into a cookie that authenticates the account's endpoints on its own")]
      public void ASessionCookieAuthenticatesWithoutThePassword()
      {
         Response started = Raw("POST", "/api/v1/session", UserHeader(UserPassword), null);
         Assert.AreEqual(201, started.Status, started.Body);
         StringAssert.Contains("\"address\":\"" + Address + "\"", started.Body);

         string setCookie = started.Header("Set-Cookie");
         StringAssert.StartsWith("hmailsession=", setCookie);
         StringAssert.Contains("HttpOnly", setCookie);
         StringAssert.Contains("SameSite=Strict", setCookie);
         StringAssert.Contains("Path=/", setCookie);
         Assert.IsFalse(setCookie.Contains("Secure"), "Over the plain loopback listener the cookie must not be marked Secure, or the browser would never send it.");

         string cookie = CookieOf(setCookie);
         Assert.AreEqual(64, cookie.Length, "A 32-byte token in hex.");

         Response me = Raw("GET", "/api/v1/me", null, null, "Cookie: hmailsession=" + cookie + "\r\n");
         Assert.AreEqual(200, me.Status, me.Body);
         StringAssert.Contains("\"address\":\"" + Address + "\"", me.Body);

         // The cookie is a credential for the account's own endpoints only.
         Response status = Raw("GET", "/api/v1/status", null, null, "Cookie: hmailsession=" + cookie + "\r\n");
         Assert.AreEqual(403, status.Status, status.Body);

         // A session cannot start another session: that takes the password.
         Response again = Raw("POST", "/api/v1/session", null, null, "Cookie: hmailsession=" + cookie + "\r\n");
         Assert.AreEqual(403, again.Status, again.Body);
      }

      [Test]
      [Description("A request that changes something on a session must carry the X-Requested-With header")]
      public void AChangeOnASessionNeedsTheHeader()
      {
         string cookie = SignIn();

         Response bare = Raw("PUT", "/api/v1/me/vacation", null,
            "{\"enabled\":true,\"subject\":\"s\",\"message\":\"m\"}",
            "Cookie: hmailsession=" + cookie + "\r\n");
         Assert.AreEqual(403, bare.Status, bare.Body);
         StringAssert.Contains("X-Requested-With", bare.Body);

         Account untouched = _domain.Accounts.ItemByAddress[Address];
         Assert.IsFalse(untouched.VacationMessageIsOn, "The refused request must have changed nothing.");

         Response withHeader = Raw("PUT", "/api/v1/me/vacation", null,
            "{\"enabled\":true,\"subject\":\"s\",\"message\":\"m\"}",
            "Cookie: hmailsession=" + cookie + "\r\nX-Requested-With: hMailServer\r\n");
         Assert.AreEqual(200, withHeader.Status, withHeader.Body);

         Account changed = _domain.Accounts.ItemByAddress[Address];
         Assert.IsTrue(changed.VacationMessageIsOn);

         // A password never needed the header: it is not something a browser
         // sends on its own.
         (int status, string body) reset = Http("PUT", "/api/v1/me/vacation", UserHeader(UserPassword),
            "{\"enabled\":false,\"subject\":\"\",\"message\":\"\"}");
         Assert.AreEqual(200, reset.status, reset.body);
      }

      [Test]
      [Description("DELETE /api/v1/session ends the session, and a garbage or ended cookie is refused")]
      public void SigningOutEndsTheSession()
      {
         string cookie = SignIn();

         Response ended = Raw("DELETE", "/api/v1/session", null, null,
            "Cookie: hmailsession=" + cookie + "\r\nX-Requested-With: hMailServer\r\n");
         Assert.AreEqual(200, ended.Status, ended.Body);
         StringAssert.Contains("Max-Age=0", ended.Header("Set-Cookie"));

         Response afterwards = Raw("GET", "/api/v1/me", null, null, "Cookie: hmailsession=" + cookie + "\r\n");
         Assert.AreEqual(401, afterwards.Status, afterwards.Body);

         Response garbage = Raw("GET", "/api/v1/me", null, null, "Cookie: hmailsession=deadbeef; other=1\r\n");
         Assert.AreEqual(401, garbage.Status, garbage.Body);

         Response noSession = Raw("DELETE", "/api/v1/session", UserHeader(UserPassword), null);
         Assert.AreEqual(400, noSession.Status, noSession.Body);
      }

      [Test]
      [Description("A password change ends the account's other sessions and keeps the one that made it")]
      public void APasswordChangeEndsOtherSessions()
      {
         const string newPassword = "Rotated-Passw0rd!";

         string mine = SignIn();
         string theirs = SignIn();

         Response changed = Raw("POST", "/api/v1/me/password", null,
            "{\"current\":\"" + UserPassword + "\",\"new\":\"" + newPassword + "\"}",
            "Cookie: hmailsession=" + mine + "\r\nX-Requested-With: hMailServer\r\n");
         Assert.AreEqual(200, changed.Status, changed.Body);

         Response theirsAfter = Raw("GET", "/api/v1/me", null, null, "Cookie: hmailsession=" + theirs + "\r\n");
         Assert.AreEqual(401, theirsAfter.Status, "The other session must have been ended. " + theirsAfter.Body);

         Response mineAfter = Raw("GET", "/api/v1/me", null, null, "Cookie: hmailsession=" + mine + "\r\n");
         Assert.AreEqual(200, mineAfter.Status, "The session that changed the password stays. " + mineAfter.Body);

         (int status, string body) newAccepted = Http("GET", "/api/v1/me", UserHeader(newPassword));
         Assert.AreEqual(200, newAccepted.status, newAccepted.body);
      }

      // ---------------------------------------------------------- quarantine ---

      private const string SurblTestPoint = "surbl-org-permanent-test-point.com.multi.surbl.org";
      private const string SpamBody =
         "This is a test message with a SURBL url: -> http://surbl-org-permanent-test-point.com/ <-";

      [OneTimeSetUp]
      public void ServeTheSurblTestPoint()
      {
         SuiteDns.Zone.WithA(SurblTestPoint, "127.0.0.2");
      }

      [OneTimeTearDown]
      public void RestoreTheSuiteDns()
      {
         SuiteDns.Reset();
      }

      // The recipe AntiSpam.Quarantine uses: the SURBL test point scores 10,
      // the delete threshold is 5, and with QuarantineEnabled the message is
      // held instead of refused.
      private void EnableQuarantine()
      {
         var antiSpam = _application.Settings.AntiSpam;
         antiSpam.SpamMarkThreshold = 1;
         antiSpam.SpamDeleteThreshold = 5;

         var surbl = antiSpam.SURBLServers[0];
         surbl.Active = true;
         surbl.Score = 10;
         surbl.Save();

         EmptyTheQuarantine();

         ServerIniFile.SetSetting("QuarantineEnabled", "1");
         _application.Reinitialize();
      }

      private void DisableQuarantine()
      {
         EmptyTheQuarantine();
         ServerIniFile.SetSetting("QuarantineEnabled", null);
         _application.Reinitialize();

         var surbl = _application.Settings.AntiSpam.SURBLServers[0];
         surbl.Active = false;
         surbl.Save();
      }

      private void EmptyTheQuarantine()
      {
         var quarantine = _application.Settings.AntiSpam.Quarantine;
         quarantine.Refresh();
         while (quarantine.Count > 0)
         {
            quarantine.DeleteByDBID(quarantine[0].ID);
            quarantine.Refresh();
         }
      }

      private static void SendSpam(string subject, params string[] recipients)
      {
         var socket = new TcpConnection();
         Assert.IsTrue(socket.Connect(25), "Could not connect to SMTP.");
         socket.ReadUntil("220");
         socket.Send("HELO test\r\n");
         socket.ReadUntil("250");
         socket.Send("MAIL FROM:<outsider@example.com>\r\n");
         socket.ReadUntil("\r\n");
         foreach (string recipient in recipients)
         {
            socket.Send("RCPT TO:<" + recipient + ">\r\n");
            socket.ReadUntil("\r\n");
         }
         socket.Send("DATA\r\n");
         socket.ReadUntil("\r\n");
         socket.Send("From: outsider@example.com\r\n" +
                     "To: " + string.Join(", ", recipients) + "\r\n" +
                     "Subject: " + subject + "\r\n" +
                     "\r\n" +
                     SpamBody + "\r\n" +
                     ".\r\n");
         string reply = socket.ReadUntil("\r\n");
         socket.Send("QUIT\r\n");
         socket.Disconnect();
         StringAssert.StartsWith("250", reply, "A quarantined message is accepted, not refused.");
      }

      private static long FirstHeldId(string listBody)
      {
         int at = listBody.IndexOf("\"id\":", StringComparison.Ordinal);
         Assert.IsTrue(at >= 0, "No held message in: " + listBody);
         int start = at + 5;
         int end = start;
         while (end < listBody.Length && char.IsDigit(listBody[end]))
            end++;
         return long.Parse(listBody.Substring(start, end - start));
      }

      [Test]
      [Description("A held message is listed for its recipient only, and releasing it delivers it to that recipient")]
      public void HeldMessagesAreListedAndReleasedForTheirRecipientOnly()
      {
         const string otherPassword = "Other-Passw0rd!";
         string other = "other@" + _domain.Name;
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, other, otherPassword);

         EnableQuarantine();
         try
         {
            SendSpam("Held for self", Address);

            (int status, string body) mine = Http("GET", "/api/v1/me/quarantine", UserHeader(UserPassword));
            Assert.AreEqual(200, mine.status, mine.body);
            StringAssert.Contains("\"enabled\":true", mine.body);
            StringAssert.Contains("\"subject\":\"Held for self\"", mine.body);
            StringAssert.DoesNotContain("recipients", mine.body);
            long id = FirstHeldId(mine.body);

            (int status, string body) theirs = Http("GET", "/api/v1/me/quarantine", BasicHeader(other, otherPassword));
            Assert.AreEqual(200, theirs.status, theirs.body);
            StringAssert.Contains("\"messages\":[]", theirs.body);

            (int status, string body) theirRelease = Http("POST", "/api/v1/me/quarantine/" + id + "/release", BasicHeader(other, otherPassword));
            Assert.AreEqual(404, theirRelease.status, "Somebody else's held message must look like no message at all. " + theirRelease.body);

            (int status, string body) release = Http("POST", "/api/v1/me/quarantine/" + id + "/release", UserHeader(UserPassword));
            Assert.AreEqual(200, release.status, release.body);

            Pop3ClientSimulator.AssertMessageCount(Address, UserPassword, 1);

            (int status, string body) after = Http("GET", "/api/v1/me/quarantine", UserHeader(UserPassword));
            StringAssert.Contains("\"messages\":[]", after.body);

            (int status, string body) gone = Http("POST", "/api/v1/me/quarantine/" + id + "/release", UserHeader(UserPassword));
            Assert.AreEqual(404, gone.status, gone.body);
         }
         finally
         {
            DisableQuarantine();
         }
      }

      [Test]
      [Description("A message held for two recipients is released or discarded for one of them without touching the other's copy")]
      public void OneRecipientsActionLeavesTheOthersCopy()
      {
         const string otherPassword = "Other-Passw0rd!";
         string other = "other@" + _domain.Name;
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, other, otherPassword);

         EnableQuarantine();
         try
         {
            SendSpam("Held for both", Address, other);

            (int status, string body) mine = Http("GET", "/api/v1/me/quarantine", UserHeader(UserPassword));
            long id = FirstHeldId(mine.body);

            (int status, string body) theirs = Http("GET", "/api/v1/me/quarantine", BasicHeader(other, otherPassword));
            StringAssert.Contains("\"subject\":\"Held for both\"", theirs.body);

            // I give mine up: nothing is delivered to me, and theirs is still held.
            (int status, string body) discard = Http("DELETE", "/api/v1/me/quarantine/" + id, UserHeader(UserPassword));
            Assert.AreEqual(200, discard.status, discard.body);

            (int status, string body) mineAfter = Http("GET", "/api/v1/me/quarantine", UserHeader(UserPassword));
            StringAssert.Contains("\"messages\":[]", mineAfter.body);

            (int status, string body) theirsAfter = Http("GET", "/api/v1/me/quarantine", BasicHeader(other, otherPassword));
            StringAssert.Contains("\"subject\":\"Held for both\"", theirsAfter.body);

            // They release theirs: delivered to them only, and the entry is gone.
            (int status, string body) release = Http("POST", "/api/v1/me/quarantine/" + id + "/release", BasicHeader(other, otherPassword));
            Assert.AreEqual(200, release.status, release.body);

            Pop3ClientSimulator.AssertMessageCount(other, otherPassword, 1);
            Pop3ClientSimulator.AssertMessageCount(Address, UserPassword, 0);

            var quarantine = _application.Settings.AntiSpam.Quarantine;
            quarantine.Refresh();
            Assert.AreEqual(0, quarantine.Count, "The entry goes with its last recipient.");
         }
         finally
         {
            DisableQuarantine();
         }
      }

      // ------------------------------------------------------- the mailbox ---

      private static void Deliver(string to, string subject, string body)
      {
         SmtpClientSimulator.StaticSend("sender@example.com", to, subject, body);
      }

      private static long NumberAt(string body, int start)
      {
         int end = start;
         while (end < body.Length && char.IsDigit(body[end]))
            end++;
         return long.Parse(body.Substring(start, end - start));
      }

      // The id of the object a marker sits in: the last "id" before it.
      private static long IdBefore(string body, string marker)
      {
         int at = body.IndexOf(marker, StringComparison.Ordinal);
         Assert.IsTrue(at >= 0, "Missing " + marker + " in: " + body);
         int idAt = body.LastIndexOf("\"id\":", at, StringComparison.Ordinal);
         Assert.IsTrue(idAt >= 0, "No id before " + marker + " in: " + body);
         return NumberAt(body, idAt + 5);
      }

      private static string Between(string body, string after, string until)
      {
         int start = body.IndexOf(after, StringComparison.Ordinal);
         Assert.IsTrue(start >= 0, "Missing " + after + " in: " + body);
         start += after.Length;
         int end = body.IndexOf(until, start, StringComparison.Ordinal);
         Assert.IsTrue(end >= start, "Unterminated " + after + " in: " + body);
         return body.Substring(start, end - start);
      }

      // One folder's own fields, cut out of the tree by its path: from the
      // "{" that opens it to its subfolders, so an assertion about its counts
      // cannot read a child's or a neighbour's.
      private static string FolderEntry(string tree, string path)
      {
         int at = tree.IndexOf("\"path\":\"" + path + "\"", StringComparison.Ordinal);
         Assert.IsTrue(at >= 0, "No folder " + path + " in: " + tree);
         int start = tree.LastIndexOf('{', at);
         int end = tree.IndexOf("\"subfolders\":[", at, StringComparison.Ordinal);
         return tree.Substring(start, end - start);
      }

      // One message's entry in a listing, by its subject: from the "{" that
      // opens it to the "}}" that closes its flags.
      private static string EntryFor(string listing, string subject)
      {
         int at = listing.IndexOf("\"subject\":\"" + subject + "\"", StringComparison.Ordinal);
         Assert.IsTrue(at >= 0, "No entry for " + subject + " in: " + listing);
         int start = listing.LastIndexOf('{', at);
         int end = listing.IndexOf("}}", at, StringComparison.Ordinal);
         return listing.Substring(start, end + 2 - start);
      }

      private string OtherAccount()
      {
         string address = "other@" + _domain.Name;
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, address, UserPassword);
         return address;
      }

      [Test]
      [Description("GET /api/v1/me/folders is the account's own folder tree with its counts, and nobody else's")]
      public void TheFolderTreeIsTheAccountsOwn()
      {
         Deliver(Address, "First things", "One.");
         Deliver(Address, "Second things", "Two.");
         Pop3ClientSimulator.AssertMessageCount(Address, UserPassword, 2);

         (int status, string body) before = Http("GET", "/api/v1/me/folders", UserHeader(UserPassword));
         Assert.AreEqual(200, before.status, "Body: " + before.body);
         string delimiter = Between(before.body, "\"delimiter\":\"", "\"");

         var imap = new ImapClientSimulator();
         Assert.IsTrue(imap.ConnectAndLogon(Address, UserPassword));
         Assert.IsTrue(imap.CreateFolder("Projects" + delimiter + "Alpha"));
         imap.Disconnect();

         (int status, string body) tree = Http("GET", "/api/v1/me/folders", UserHeader(UserPassword));
         Assert.AreEqual(200, tree.status, "Body: " + tree.body);

         string inbox = FolderEntry(tree.body, "INBOX");
         StringAssert.Contains("\"name\":\"INBOX\"", inbox);
         StringAssert.Contains("\"writable\":true", inbox);
         StringAssert.Contains("\"messages\":2,\"unseen\":2", inbox);

         string alpha = FolderEntry(tree.body, "Projects" + delimiter + "Alpha");
         StringAssert.Contains("\"name\":\"Alpha\"", alpha);
         long projectsId = IdBefore(tree.body, "\"path\":\"Projects\"");
         Assert.AreEqual(projectsId.ToString(), Between(alpha, "\"parent_id\":", ","), "Alpha's parent is Projects: " + tree.body);
         StringAssert.Contains("\"messages\":0,\"unseen\":0", alpha);

         (int status, string body) others = Http("GET", "/api/v1/me/folders", BasicHeader(OtherAccount(), UserPassword));
         Assert.AreEqual(200, others.status, "Body: " + others.body);
         Assert.IsFalse(others.body.Contains("Alpha"), "Another account's tree is its own: " + others.body);

         (int status, string body) admin = Http("GET", "/api/v1/me/folders", AdminHeader());
         Assert.AreEqual(403, admin.status, "Body: " + admin.body);
      }

      [Test]
      [Description("A folder's messages are listed newest first with their headers and live flags, paged, and to their owner only")]
      public void MessagesAreListedNewestFirstWithTheirFlags()
      {
         Deliver(Address, "First things", "One.");
         Pop3ClientSimulator.AssertMessageCount(Address, UserPassword, 1);
         Deliver(Address, "Second things", "Two.");
         Pop3ClientSimulator.AssertMessageCount(Address, UserPassword, 2);

         (int status, string body) tree = Http("GET", "/api/v1/me/folders", UserHeader(UserPassword));
         long inboxId = IdBefore(tree.body, "\"path\":\"INBOX\"");

         (int status, string body) page = Http("GET", "/api/v1/me/folders/" + inboxId + "/messages", UserHeader(UserPassword));
         Assert.AreEqual(200, page.status, "Body: " + page.body);
         StringAssert.Contains("\"folder_id\":" + inboxId + ",\"total\":2", page.body);
         Assert.Less(page.body.IndexOf("Second things", StringComparison.Ordinal), page.body.IndexOf("First things", StringComparison.Ordinal),
            "Newest first: " + page.body);

         string second = EntryFor(page.body, "Second things");
         StringAssert.Contains("\"from\":\"sender@example.com\"", second);
         StringAssert.Contains("\"seen\":false", second);
         long secondUid = long.Parse(Between(second, "\"uid\":", ","));

         (int status, string body) one = Http("GET", "/api/v1/me/folders/" + inboxId + "/messages?limit=1", UserHeader(UserPassword));
         Assert.AreEqual(200, one.status, "Body: " + one.body);
         StringAssert.Contains("\"total\":2", one.body);
         StringAssert.Contains("Second things", one.body);
         Assert.IsFalse(one.body.Contains("First things"), "limit=1 is the newest one only: " + one.body);

         (int status, string body) older = Http("GET", "/api/v1/me/folders/" + inboxId + "/messages?before_uid=" + secondUid, UserHeader(UserPassword));
         Assert.AreEqual(200, older.status, "Body: " + older.body);
         StringAssert.Contains("First things", older.body);
         Assert.IsFalse(older.body.Contains("Second things"), "before_uid pages past the newest: " + older.body);

         var imap = new ImapClientSimulator();
         Assert.IsTrue(imap.ConnectAndLogon(Address, UserPassword));
         Assert.IsTrue(imap.SelectFolder("INBOX"));
         Assert.IsTrue(imap.SetFlagOnMessage(1, true, "\\Seen"));
         imap.Disconnect();

         (int status, string body) after = Http("GET", "/api/v1/me/folders/" + inboxId + "/messages", UserHeader(UserPassword));
         Assert.AreEqual(200, after.status, "Body: " + after.body);
         StringAssert.Contains("\"seen\":true", EntryFor(after.body, "First things"));
         StringAssert.Contains("\"seen\":false", EntryFor(after.body, "Second things"));

         (int status, string body) counts = Http("GET", "/api/v1/me/folders", UserHeader(UserPassword));
         StringAssert.Contains("\"messages\":2,\"unseen\":1", FolderEntry(counts.body, "INBOX"));

         (int status, string body) unknown = Http("GET", "/api/v1/me/folders/987654321/messages", UserHeader(UserPassword));
         Assert.AreEqual(404, unknown.status, "Body: " + unknown.body);

         (int status, string body) others = Http("GET", "/api/v1/me/folders/" + inboxId + "/messages", BasicHeader(OtherAccount(), UserPassword));
         Assert.AreEqual(404, others.status, "Another account's folder is not found, not forbidden. Body: " + others.body);
      }

      [Test]
      [Description("GET /api/v1/me/messages/{id} reads one message with its text, for the account it belongs to and no other")]
      public void AMessageIsReadByItsOwnerOnly()
      {
         Deliver(Address, "Numbers", "The quarterly numbers are attached in spirit.");
         Pop3ClientSimulator.AssertMessageCount(Address, UserPassword, 1);

         (int status, string body) tree = Http("GET", "/api/v1/me/folders", UserHeader(UserPassword));
         long inboxId = IdBefore(tree.body, "\"path\":\"INBOX\"");
         (int status, string body) page = Http("GET", "/api/v1/me/folders/" + inboxId + "/messages", UserHeader(UserPassword));
         long messageId = IdBefore(page.body, "\"subject\":\"Numbers\"");

         (int status, string body) message = Http("GET", "/api/v1/me/messages/" + messageId, UserHeader(UserPassword));
         Assert.AreEqual(200, message.status, "Body: " + message.body);
         StringAssert.Contains("\"subject\":\"Numbers\"", message.body);
         StringAssert.Contains("\"folder_id\":" + inboxId, message.body);
         StringAssert.Contains("\"from\":\"sender@example.com\"", message.body);
         StringAssert.Contains("\"to\":\"" + Address + "\"", message.body);
         StringAssert.Contains("\"truncated\":false", message.body);
         StringAssert.Contains("The quarterly numbers are attached in spirit.", message.body);
         StringAssert.Contains("\"attachments\":[]", message.body);
         StringAssert.Contains("\"seen\":false", message.body);

         (int status, string body) others = Http("GET", "/api/v1/me/messages/" + messageId, BasicHeader(OtherAccount(), UserPassword));
         Assert.AreEqual(404, others.status, "Another account's message is not found, not forbidden. Body: " + others.body);

         (int status, string body) unknown = Http("GET", "/api/v1/me/messages/987654321", UserHeader(UserPassword));
         Assert.AreEqual(404, unknown.status, "Body: " + unknown.body);

         (int status, string body) admin = Http("GET", "/api/v1/me/messages/" + messageId, AdminHeader());
         Assert.AreEqual(403, admin.status, "Body: " + admin.body);
      }

      [Test]
      [Description("PUT /api/v1/me/messages/{id}/flags changes the named flags and no other, and IMAP sees them")]
      public void FlagsAreChangedAndSeenOverImap()
      {
         Deliver(Address, "Numbers", "One.");
         Pop3ClientSimulator.AssertMessageCount(Address, UserPassword, 1);

         (int status, string body) tree = Http("GET", "/api/v1/me/folders", UserHeader(UserPassword));
         long inboxId = IdBefore(tree.body, "\"path\":\"INBOX\"");
         (int status, string body) page = Http("GET", "/api/v1/me/folders/" + inboxId + "/messages", UserHeader(UserPassword));
         long messageId = IdBefore(page.body, "\"subject\":\"Numbers\"");
         string flagsPath = "/api/v1/me/messages/" + messageId + "/flags";

         (int status, string body) nothing = Http("PUT", flagsPath, UserHeader(UserPassword), "{\"colour\":\"red\"}");
         Assert.AreEqual(400, nothing.status, "Body: " + nothing.body);

         (int status, string body) set = Http("PUT", flagsPath, UserHeader(UserPassword), "{\"seen\":true,\"flagged\":true}");
         Assert.AreEqual(200, set.status, "Body: " + set.body);
         StringAssert.Contains("\"seen\":true,\"flagged\":true", set.body);

         var imap = new ImapClientSimulator();
         Assert.IsTrue(imap.ConnectAndLogon(Address, UserPassword));
         Assert.IsTrue(imap.SelectFolder("INBOX"));
         string flags = imap.GetFlags(1);
         StringAssert.Contains("\\Seen", flags);
         StringAssert.Contains("\\Flagged", flags);
         imap.Disconnect();

         (int status, string body) unseen = Http("PUT", flagsPath, UserHeader(UserPassword), "{\"seen\":false}");
         Assert.AreEqual(200, unseen.status, "Body: " + unseen.body);
         StringAssert.Contains("\"seen\":false,\"flagged\":true", unseen.body);

         imap = new ImapClientSimulator();
         Assert.IsTrue(imap.ConnectAndLogon(Address, UserPassword));
         Assert.IsTrue(imap.SelectFolder("INBOX"));
         flags = imap.GetFlags(1);
         Assert.IsFalse(flags.Contains("\\Seen"), "Only the named flag changed: " + flags);
         StringAssert.Contains("\\Flagged", flags);
         imap.Disconnect();

         (int status, string body) counts = Http("GET", "/api/v1/me/folders", UserHeader(UserPassword));
         StringAssert.Contains("\"messages\":1,\"unseen\":1", FolderEntry(counts.body, "INBOX"));

         (int status, string body) others = Http("PUT", flagsPath, BasicHeader(OtherAccount(), UserPassword), "{\"seen\":true}");
         Assert.AreEqual(404, others.status, "Another account's message is not found, not forbidden. Body: " + others.body);
      }

      [Test]
      [Description("POST /api/v1/me/messages/{id}/move puts the message in another of the account's folders, as a new message, and only there")]
      public void AMessageIsMovedToAnotherFolderOfTheAccount()
      {
         Deliver(Address, "Numbers", "One.");
         Pop3ClientSimulator.AssertMessageCount(Address, UserPassword, 1);

         var imap = new ImapClientSimulator();
         Assert.IsTrue(imap.ConnectAndLogon(Address, UserPassword));
         Assert.IsTrue(imap.CreateFolder("Projects"));
         imap.Disconnect();

         (int status, string body) tree = Http("GET", "/api/v1/me/folders", UserHeader(UserPassword));
         long inboxId = IdBefore(tree.body, "\"path\":\"INBOX\"");
         long projectsId = IdBefore(tree.body, "\"path\":\"Projects\"");
         (int status, string body) page = Http("GET", "/api/v1/me/folders/" + inboxId + "/messages", UserHeader(UserPassword));
         long messageId = IdBefore(page.body, "\"subject\":\"Numbers\"");
         string movePath = "/api/v1/me/messages/" + messageId + "/move";

         (int status, string body) missing = Http("POST", movePath, UserHeader(UserPassword), "{}");
         Assert.AreEqual(400, missing.status, "Body: " + missing.body);

         (int status, string body) unknown = Http("POST", movePath, UserHeader(UserPassword), "{\"folder_id\":987654321}");
         Assert.AreEqual(404, unknown.status, "Body: " + unknown.body);

         (int status, string body) same = Http("POST", movePath, UserHeader(UserPassword), "{\"folder_id\":" + inboxId + "}");
         Assert.AreEqual(400, same.status, "Body: " + same.body);

         string other = OtherAccount();
         Deliver(other, "Theirs", "Two.");
         Pop3ClientSimulator.AssertMessageCount(other, UserPassword, 1);
         (int status, string body) othersTree = Http("GET", "/api/v1/me/folders", BasicHeader(other, UserPassword));
         long othersInbox = IdBefore(othersTree.body, "\"path\":\"INBOX\"");

         (int status, string body) intoTheirs = Http("POST", movePath, UserHeader(UserPassword), "{\"folder_id\":" + othersInbox + "}");
         Assert.AreEqual(404, intoTheirs.status, "Another account's folder is not found, not forbidden. Body: " + intoTheirs.body);

         (int status, string body) moved = Http("POST", movePath, UserHeader(UserPassword), "{\"folder_id\":" + projectsId + "}");
         Assert.AreEqual(200, moved.status, "Body: " + moved.body);
         StringAssert.Contains("\"folder_id\":" + projectsId, moved.body);
         long newId = long.Parse(Between(moved.body, "\"id\":", ","));
         Assert.AreNotEqual(messageId, newId, "A moved message is a new row.");

         (int status, string body) inbox = Http("GET", "/api/v1/me/folders/" + inboxId + "/messages", UserHeader(UserPassword));
         StringAssert.Contains("\"total\":0", inbox.body);
         (int status, string body) projects = Http("GET", "/api/v1/me/folders/" + projectsId + "/messages", UserHeader(UserPassword));
         StringAssert.Contains("\"total\":1", projects.body);
         StringAssert.Contains("\"id\":" + newId + ",", projects.body);

         (int status, string body) old = Http("GET", "/api/v1/me/messages/" + messageId, UserHeader(UserPassword));
         Assert.AreEqual(404, old.status, "The old id is gone. Body: " + old.body);
         (int status, string body) fresh = Http("GET", "/api/v1/me/messages/" + newId, UserHeader(UserPassword));
         Assert.AreEqual(200, fresh.status, "Body: " + fresh.body);
         StringAssert.Contains("One.", fresh.body);

         imap = new ImapClientSimulator();
         Assert.IsTrue(imap.ConnectAndLogon(Address, UserPassword));
         Assert.AreEqual(0, imap.GetMessageCount("INBOX"));
         Assert.AreEqual(1, imap.GetMessageCount("Projects"));
         imap.Disconnect();

         Pop3ClientSimulator.AssertMessageCount(other, UserPassword, 1);
      }

      [Test]
      [Description("DELETE /api/v1/me/messages/{id} moves to the Trash folder when the account has one, and is final otherwise or on request")]
      public void DeleteGoesToTheTrashWhenThereIsOne()
      {
         Deliver(Address, "First", "One.");
         Pop3ClientSimulator.AssertMessageCount(Address, UserPassword, 1);

         (int status, string body) tree = Http("GET", "/api/v1/me/folders", UserHeader(UserPassword));
         long inboxId = IdBefore(tree.body, "\"path\":\"INBOX\"");
         string listPath = "/api/v1/me/folders/" + inboxId + "/messages";
         long first = IdBefore(Http("GET", listPath, UserHeader(UserPassword)).body, "\"subject\":\"First\"");

         (int status, string body) gone = Http("DELETE", "/api/v1/me/messages/" + first, UserHeader(UserPassword));
         Assert.AreEqual(200, gone.status, "Body: " + gone.body);
         StringAssert.Contains("\"deleted\":true", gone.body);
         Pop3ClientSimulator.AssertMessageCount(Address, UserPassword, 0);

         (int status, string body) again = Http("DELETE", "/api/v1/me/messages/" + first, UserHeader(UserPassword));
         Assert.AreEqual(404, again.status, "Body: " + again.body);

         var imap = new ImapClientSimulator();
         Assert.IsTrue(imap.ConnectAndLogon(Address, UserPassword));
         Assert.IsTrue(imap.CreateFolder("Trash"));
         imap.Disconnect();

         Deliver(Address, "Second", "Two.");
         Pop3ClientSimulator.AssertMessageCount(Address, UserPassword, 1);

         tree = Http("GET", "/api/v1/me/folders", UserHeader(UserPassword));
         long trashId = IdBefore(tree.body, "\"path\":\"Trash\"");
         StringAssert.Contains("\"special_use\":\"\\\\Trash\"", FolderEntry(tree.body, "Trash"));
         long second = IdBefore(Http("GET", listPath, UserHeader(UserPassword)).body, "\"subject\":\"Second\"");

         (int status, string body) trashed = Http("DELETE", "/api/v1/me/messages/" + second, UserHeader(UserPassword));
         Assert.AreEqual(200, trashed.status, "Body: " + trashed.body);
         StringAssert.Contains("\"deleted\":false,\"moved_to\":" + trashId, trashed.body);
         long inTrash = long.Parse(Between(trashed.body, "\"id\":", "}"));
         Pop3ClientSimulator.AssertMessageCount(Address, UserPassword, 0);

         imap = new ImapClientSimulator();
         Assert.IsTrue(imap.ConnectAndLogon(Address, UserPassword));
         Assert.AreEqual(1, imap.GetMessageCount("Trash"));
         imap.Disconnect();

         (int status, string body) emptied = Http("DELETE", "/api/v1/me/messages/" + inTrash, UserHeader(UserPassword));
         Assert.AreEqual(200, emptied.status, "Body: " + emptied.body);
         StringAssert.Contains("\"deleted\":true", emptied.body);

         Deliver(Address, "Third", "Three.");
         Pop3ClientSimulator.AssertMessageCount(Address, UserPassword, 1);
         long third = IdBefore(Http("GET", listPath, UserHeader(UserPassword)).body, "\"subject\":\"Third\"");

         (int status, string body) others = Http("DELETE", "/api/v1/me/messages/" + third, BasicHeader(OtherAccount(), UserPassword));
         Assert.AreEqual(404, others.status, "Another account's message is not found, not forbidden. Body: " + others.body);

         (int status, string body) admin = Http("DELETE", "/api/v1/me/messages/" + third, AdminHeader());
         Assert.AreEqual(403, admin.status, "Body: " + admin.body);

         (int status, string body) final = Http("DELETE", "/api/v1/me/messages/" + third + "?permanent=1", UserHeader(UserPassword));
         Assert.AreEqual(200, final.status, "Body: " + final.body);
         StringAssert.Contains("\"deleted\":true", final.body);

         imap = new ImapClientSimulator();
         Assert.IsTrue(imap.ConnectAndLogon(Address, UserPassword));
         Assert.AreEqual(0, imap.GetMessageCount("INBOX"));
         Assert.AreEqual(0, imap.GetMessageCount("Trash"));
         imap.Disconnect();
      }

      [Test]
      [Description("POST /api/v1/me/messages sends as the account to local recipients, and keeps a read copy in the Sent folder")]
      public void AMessageIsSentToLocalRecipientsAndKeptInSent()
      {
         string other = OtherAccount();
         string third = "third@" + _domain.Name;
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, third, UserPassword);

         _account.PersonFirstName = "Self";
         _account.PersonLastName = "Service";
         _account.Save();

         var imap = new ImapClientSimulator();
         Assert.IsTrue(imap.ConnectAndLogon(Address, UserPassword));
         Assert.IsTrue(imap.CreateFolder("Sent"));
         imap.Disconnect();

         (int status, string body) sent = Http("POST", "/api/v1/me/messages", UserHeader(UserPassword),
            "{\"to\":\"" + other + "\",\"cc\":\"" + third + "\",\"subject\":\"Hello there\",\"text\":\"Body text here.\"}");
         Assert.AreEqual(201, sent.status, "Body: " + sent.body);
         StringAssert.Contains("\"recipients\":2", sent.body);
         long sentId = long.Parse(Between(sent.body, "\"sent_id\":", "}"));
         Assert.Greater(sentId, 0, "A copy is kept in the Sent folder: " + sent.body);

         Pop3ClientSimulator.AssertMessageCount(other, UserPassword, 1);
         Pop3ClientSimulator.AssertMessageCount(third, UserPassword, 1);

         string received = Pop3ClientSimulator.AssertGetFirstMessageText(other, UserPassword);
         StringAssert.Contains("Body text here.", received);
         StringAssert.Contains("Subject: Hello there", received);
         StringAssert.Contains("From: \"Self Service\" <" + Address + ">", received);
         StringAssert.Contains("To: " + other, received);
         StringAssert.Contains("CC: " + third, received);
         StringAssert.Contains("Message-ID:", received);
         StringAssert.Contains("Date:", received);

         imap = new ImapClientSimulator();
         Assert.IsTrue(imap.ConnectAndLogon(Address, UserPassword));
         Assert.AreEqual(1, imap.GetMessageCount("Sent"));
         imap.Disconnect();

         (int status, string body) tree = Http("GET", "/api/v1/me/folders", UserHeader(UserPassword));
         StringAssert.Contains("\"messages\":1,\"unseen\":0", FolderEntry(tree.body, "Sent"));

         (int status, string body) copy = Http("GET", "/api/v1/me/messages/" + sentId, UserHeader(UserPassword));
         Assert.AreEqual(200, copy.status, "Body: " + copy.body);
         StringAssert.Contains("Body text here.", copy.body);
         StringAssert.Contains("\"seen\":true", copy.body);
      }

      [Test]
      [Description("A submission names the address it refuses, needs a recipient, and is the account's alone")]
      public void ASubmissionNamesWhatItRefuses()
      {
         (int status, string body) nobody = Http("POST", "/api/v1/me/messages", UserHeader(UserPassword),
            "{\"subject\":\"Hello\",\"text\":\"Nobody is here.\"}");
         Assert.AreEqual(400, nobody.status, "Body: " + nobody.body);

         string unknown = "nobody@" + _domain.Name;
         (int status, string body) refused = Http("POST", "/api/v1/me/messages", UserHeader(UserPassword),
            "{\"to\":\"" + unknown + "\",\"subject\":\"Hello\",\"text\":\"Nobody is here.\"}");
         Assert.AreEqual(400, refused.status, "Body: " + refused.body);
         StringAssert.Contains(unknown, refused.body);

         (int status, string body) malformed = Http("POST", "/api/v1/me/messages", UserHeader(UserPassword),
            "{\"to\":\"not an address\",\"text\":\"x\"}");
         Assert.AreEqual(400, malformed.status, "Body: " + malformed.body);

         (int status, string body) admin = Http("POST", "/api/v1/me/messages", AdminHeader(),
            "{\"to\":\"" + Address + "\",\"text\":\"x\"}");
         Assert.AreEqual(403, admin.status, "Body: " + admin.body);

         string cookie = SignIn();
         Response bare = Raw("POST", "/api/v1/me/messages", null,
            "{\"to\":\"" + Address + "\",\"text\":\"x\"}",
            "Cookie: hmailsession=" + cookie + "\r\n");
         Assert.AreEqual(403, bare.Status, "A send on a session needs the header. " + bare.Body);

         Pop3ClientSimulator.AssertMessageCount(Address, UserPassword, 0);
      }

      [Test]
      [Description("A submission to an external address goes out through the delivery queue like any other")]
      public void AMessageIsRelayedToAnExternalRecipient()
      {
         var deliveryResults = new System.Collections.Generic.Dictionary<string, int>();
         deliveryResults["test@dummy-example.com"] = 250;

         int smtpServerPort = TestSetup.GetNextFreePort();
         using (var server = new SmtpServerSimulator(1, smtpServerPort))
         {
            server.AddRecipientResult(deliveryResults);
            server.StartListen();

            TestSetup.AddRoutePointingAtLocalhost(1, smtpServerPort, false);

            (int status, string body) sent = Http("POST", "/api/v1/me/messages", UserHeader(UserPassword),
               "{\"to\":\"Dummy <test@dummy-example.com>\",\"subject\":\"Outward\",\"text\":\"Going out.\"}");
            Assert.AreEqual(201, sent.status, "Body: " + sent.body);
            StringAssert.Contains("\"recipients\":1", sent.body);

            server.WaitForCompletion();

            StringAssert.Contains("Going out.", server.MessageData);
            StringAssert.Contains("To: Dummy <test@dummy-example.com>", server.MessageData);
            StringAssert.Contains("Subject: Outward", server.MessageData);
            StringAssert.Contains("<" + Address + ">", server.MailFromCommand);
         }

         CustomAsserts.AssertRecipientsInDeliveryQueue(0);
      }

      private static byte[] Pattern(int length)
      {
         var bytes = new byte[length];
         for (int i = 0; i < length; i++)
            bytes[i] = (byte) (i % 251);
         return bytes;
      }

      private static string Base64Part(string contentType, string name, byte[] content)
      {
         return "--b1\r\n" +
                "Content-Type: " + contentType + "; name=\"" + name + "\"\r\n" +
                "Content-Transfer-Encoding: base64\r\n" +
                "Content-Disposition: attachment; filename=\"" + name + "\"\r\n" +
                "\r\n" +
                Convert.ToBase64String(content, Base64FormattingOptions.InsertLineBreaks) + "\r\n";
      }

      [Test]
      [Description("GET /api/v1/me/messages/{id}/attachments/{index} serves one attachment decoded, as a download, and only to its owner")]
      public void AnAttachmentIsDownloadedAsAFile()
      {
         byte[] numbers = Pattern(1000);
         byte[] page = Encoding.ASCII.GetBytes("<html><script>alert(1)</script></html>");
         string accented = "Résumé \"final\".pdf";
         string encodedName = "=?utf-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes(accented)) + "?=";

         string raw =
            "From: sender@example.com\r\n" +
            "To: " + Address + "\r\n" +
            "Subject: With files\r\n" +
            "MIME-Version: 1.0\r\n" +
            "Content-Type: multipart/mixed; boundary=\"b1\"\r\n" +
            "\r\n" +
            "--b1\r\n" +
            "Content-Type: text/plain\r\n" +
            "\r\n" +
            "See attached.\r\n" +
            Base64Part("application/octet-stream", "numbers.bin", numbers) +
            Base64Part("text/html", "page.html", page) +
            Base64Part("application/pdf", encodedName, Pattern(10)) +
            "--b1--\r\n";

         SmtpClientSimulator.StaticSendRaw("sender@example.com", Address, raw);
         Pop3ClientSimulator.AssertMessageCount(Address, UserPassword, 1);

         (int status, string body) tree = Http("GET", "/api/v1/me/folders", UserHeader(UserPassword));
         long inboxId = IdBefore(tree.body, "\"path\":\"INBOX\"");
         long messageId = IdBefore(Http("GET", "/api/v1/me/folders/" + inboxId + "/messages", UserHeader(UserPassword)).body, "\"subject\":\"With files\"");

         (int status, string body) message = Http("GET", "/api/v1/me/messages/" + messageId, UserHeader(UserPassword));
         Assert.AreEqual(200, message.status, "Body: " + message.body);
         StringAssert.Contains("{\"index\":0,\"name\":\"numbers.bin\",\"size\":1000}", message.body);
         StringAssert.Contains("\"name\":\"page.html\"", message.body);
         StringAssert.Contains("See attached.", message.body);

         string path = "/api/v1/me/messages/" + messageId + "/attachments/";

         Response numbersFile = Raw("GET", path + "0", UserHeader(UserPassword), null);
         Assert.AreEqual(200, numbersFile.Status, numbersFile.Body);
         Assert.AreEqual("application/octet-stream", numbersFile.Header("Content-Type"));
         StringAssert.Contains("attachment; filename=\"numbers.bin\"", numbersFile.Header("Content-Disposition"));
         Assert.AreEqual("nosniff", numbersFile.Header("X-Content-Type-Options"));
         Assert.AreEqual("no-store", numbersFile.Header("Cache-Control"));
         Assert.AreEqual(numbers, numbersFile.BodyBytes, "The bytes come back exactly as attached.");

         Response html = Raw("GET", path + "1", UserHeader(UserPassword), null);
         Assert.AreEqual(200, html.Status, html.Body);
         Assert.AreEqual("application/octet-stream", html.Header("Content-Type"), "A type a browser would render is not served under it.");
         StringAssert.Contains("sandbox", html.Header("Content-Security-Policy"));
         Assert.AreEqual(page, html.BodyBytes);

         Response pdf = Raw("GET", path + "2", UserHeader(UserPassword), null);
         Assert.AreEqual(200, pdf.Status, pdf.Body);
         Assert.AreEqual("application/pdf", pdf.Header("Content-Type"));
         StringAssert.Contains("filename=\"R_sum_ _final_.pdf\"", pdf.Header("Content-Disposition"));
         StringAssert.Contains("filename*=UTF-8''R%C3%A9sum%C3%A9%20%22final%22.pdf", pdf.Header("Content-Disposition"));

         (int status, string body) missing = Http("GET", path + "3", UserHeader(UserPassword));
         Assert.AreEqual(404, missing.status, "Body: " + missing.body);

         (int status, string body) others = Http("GET", path + "0", BasicHeader(OtherAccount(), UserPassword));
         Assert.AreEqual(404, others.status, "Body: " + others.body);

         (int status, string body) admin = Http("GET", path + "0", AdminHeader());
         Assert.AreEqual(403, admin.status, "Body: " + admin.body);
      }

      [Test]
      [Description("q on a folder's listing finds the text in the subject, the sender or the body, whatever its case")]
      public void AFolderIsSearchedBySubjectSenderAndBody()
      {
         SmtpClientSimulator.StaticSend("alice@example.com", Address, "Quarterly numbers", "The figures.");
         Pop3ClientSimulator.AssertMessageCount(Address, UserPassword, 1);
         SmtpClientSimulator.StaticSend("bob@example.com", Address, "Lunch", "The invoice is attached in spirit.");
         Pop3ClientSimulator.AssertMessageCount(Address, UserPassword, 2);
         SmtpClientSimulator.StaticSend("carol@example.com", Address, "Other", "Nothing to see.");
         Pop3ClientSimulator.AssertMessageCount(Address, UserPassword, 3);

         (int status, string body) tree = Http("GET", "/api/v1/me/folders", UserHeader(UserPassword));
         long inboxId = IdBefore(tree.body, "\"path\":\"INBOX\"");
         string listPath = "/api/v1/me/folders/" + inboxId + "/messages";

         (int status, string body) bySubject = Http("GET", listPath + "?q=numbers", UserHeader(UserPassword));
         Assert.AreEqual(200, bySubject.status, "Body: " + bySubject.body);
         StringAssert.Contains("\"query\":\"numbers\"", bySubject.body);
         StringAssert.Contains("Quarterly numbers", bySubject.body);
         Assert.IsFalse(bySubject.body.Contains("Lunch") || bySubject.body.Contains("Other"), "Only the match: " + bySubject.body);
         StringAssert.Contains("\"scanned\":3,\"complete\":true", bySubject.body);

         (int status, string body) byBody = Http("GET", listPath + "?q=INVOICE", UserHeader(UserPassword));
         Assert.AreEqual(200, byBody.status, "Body: " + byBody.body);
         StringAssert.Contains("Lunch", byBody.body);
         Assert.IsFalse(byBody.body.Contains("Quarterly"), "The body match only: " + byBody.body);

         (int status, string body) bySender = Http("GET", listPath + "?q=carol%40example", UserHeader(UserPassword));
         Assert.AreEqual(200, bySender.status, "Body: " + bySender.body);
         StringAssert.Contains("Other", bySender.body);
         Assert.IsFalse(bySender.body.Contains("Lunch"), "The sender match only: " + bySender.body);

         (int status, string body) nothing = Http("GET", listPath + "?q=zebra", UserHeader(UserPassword));
         Assert.AreEqual(200, nothing.status, "Body: " + nothing.body);
         StringAssert.Contains("\"messages\":[]", nothing.body);

         (int status, string body) plain = Http("GET", listPath, UserHeader(UserPassword));
         StringAssert.Contains("\"total\":3", plain.body);
         StringAssert.Contains("\"query\":\"\"", plain.body);
      }

      [Test]
      [Description("GET /api/v1/me/search looks through every folder of the account and names the folder of each hit")]
      public void ASearchAcrossFoldersNamesTheFolder()
      {
         SmtpClientSimulator.StaticSend("alice@example.com", Address, "Budget draft", "Numbers inside.");
         Pop3ClientSimulator.AssertMessageCount(Address, UserPassword, 1);
         SmtpClientSimulator.StaticSend("alice@example.com", Address, "Budget final", "Numbers agreed.");
         Pop3ClientSimulator.AssertMessageCount(Address, UserPassword, 2);

         var imap = new ImapClientSimulator();
         Assert.IsTrue(imap.ConnectAndLogon(Address, UserPassword));
         Assert.IsTrue(imap.CreateFolder("Projects"));
         imap.Disconnect();

         (int status, string body) tree = Http("GET", "/api/v1/me/folders", UserHeader(UserPassword));
         long inboxId = IdBefore(tree.body, "\"path\":\"INBOX\"");
         long projectsId = IdBefore(tree.body, "\"path\":\"Projects\"");
         long draft = IdBefore(Http("GET", "/api/v1/me/folders/" + inboxId + "/messages", UserHeader(UserPassword)).body, "\"subject\":\"Budget draft\"");

         (int status, string body) moved = Http("POST", "/api/v1/me/messages/" + draft + "/move", UserHeader(UserPassword), "{\"folder_id\":" + projectsId + "}");
         Assert.AreEqual(200, moved.status, "Body: " + moved.body);

         (int status, string body) found = Http("GET", "/api/v1/me/search?q=budget", UserHeader(UserPassword));
         Assert.AreEqual(200, found.status, "Body: " + found.body);
         StringAssert.Contains("\"folder\":\"Projects\"", found.body);
         StringAssert.Contains("\"folder\":\"INBOX\"", found.body);
         StringAssert.Contains("Budget draft", found.body);
         StringAssert.Contains("Budget final", found.body);
         StringAssert.Contains("\"complete\":true", found.body);

         (int status, string body) one = Http("GET", "/api/v1/me/search?q=agreed", UserHeader(UserPassword));
         StringAssert.Contains("Budget final", one.body);
         Assert.IsFalse(one.body.Contains("Budget draft"), "Only the match: " + one.body);

         (int status, string body) missing = Http("GET", "/api/v1/me/search", UserHeader(UserPassword));
         Assert.AreEqual(400, missing.status, "Body: " + missing.body);

         (int status, string body) admin = Http("GET", "/api/v1/me/search?q=budget", AdminHeader());
         Assert.AreEqual(403, admin.status, "Body: " + admin.body);
      }

      [Test]
      [Description("Text that is not ASCII comes out of the mailbox routes and goes into the automatic reply as UTF-8")]
      public void NonAsciiTextSurvivesBothWays()
      {
         string subject = "Résumé für Zoë";
         string encodedSubject = "=?utf-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes(subject)) + "?=";
         string raw =
            "From: =?utf-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes("Ærø Sørensen")) + "?= <sender@example.com>\r\n" +
            "To: " + Address + "\r\n" +
            "Subject: " + encodedSubject + "\r\n" +
            "MIME-Version: 1.0\r\n" +
            "Content-Type: text/plain; charset=utf-8\r\n" +
            "Content-Transfer-Encoding: 8bit\r\n" +
            "\r\n" +
            "Grüße aus Zürich.\r\n";

         SmtpClientSimulator.StaticSendRaw("sender@example.com", Address, raw);
         Pop3ClientSimulator.AssertMessageCount(Address, UserPassword, 1);

         (int status, string body) tree = Http("GET", "/api/v1/me/folders", UserHeader(UserPassword));
         long inboxId = IdBefore(tree.body, "\"path\":\"INBOX\"");
         (int status, string body) page = Http("GET", "/api/v1/me/folders/" + inboxId + "/messages", UserHeader(UserPassword));
         StringAssert.Contains("\"subject\":\"" + subject + "\"", page.body);
         StringAssert.Contains("Ærø Sørensen", page.body);

         long messageId = IdBefore(page.body, "\"subject\":\"" + subject + "\"");
         (int status, string body) message = Http("GET", "/api/v1/me/messages/" + messageId, UserHeader(UserPassword));
         StringAssert.Contains("Grüße aus Zürich.", message.body);

         (int status, string body) found = Http("GET", "/api/v1/me/folders/" + inboxId + "/messages?q=" + Uri.EscapeDataString("zürich"), UserHeader(UserPassword));
         StringAssert.Contains("\"subject\":\"" + subject + "\"", found.body);

         (int status, string body) vacation = Http("PUT", "/api/v1/me/vacation", UserHeader(UserPassword),
            "{\"enabled\":true,\"subject\":\"Väck mig\",\"message\":\"Jag är på semester.\"}");
         Assert.AreEqual(200, vacation.status, "Body: " + vacation.body);
         Account reread = _domain.Accounts.ItemByAddress[Address];
         Assert.AreEqual("Väck mig", reread.VacationSubject);
         Assert.AreEqual("Jag är på semester.", reread.VacationMessage);

         (int status, string body) me = Http("GET", "/api/v1/me", UserHeader(UserPassword));
         StringAssert.Contains("\"subject\":\"Väck mig\"", me.body);
      }

      // ------------------------------------------------------------ helpers ---

      private string SignIn()
      {
         Response started = Raw("POST", "/api/v1/session", UserHeader(UserPassword), null);
         Assert.AreEqual(201, started.Status, started.Body);
         return CookieOf(started.Header("Set-Cookie"));
      }

      private static string CookieOf(string setCookie)
      {
         int start = setCookie.IndexOf('=') + 1;
         int end = setCookie.IndexOf(';', start);
         return end > start ? setCookie.Substring(start, end - start) : setCookie.Substring(start);
      }

      private static string AdminHeader()
      {
         return BasicHeader("Administrator", AdminPassword);
      }

      private string UserHeader(string password)
      {
         return BasicHeader(Address, password);
      }

      private static string BasicHeader(string user, string password)
      {
         return "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + password));
      }

      private sealed class Response
      {
         public int Status;
         public string Body = "";
         public byte[] BodyBytes = new byte[0];
         public readonly System.Collections.Generic.Dictionary<string, string> Headers =
            new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

         public string Header(string name)
         {
            string value;
            return Headers.TryGetValue(name, out value) ? value : "";
         }
      }

      private static (int status, string body) Http(string method, string path, string authorization, string requestBody = null)
      {
         Response response = Raw(method, path, authorization, requestBody);
         return (response.Status, response.Body);
      }

      private static Response Raw(string method, string path, string authorization, string requestBody, string extraHeaders = null)
      {
         using (var client = new TcpClient())
         {
            Exception last = null;
            for (int attempt = 0; attempt < 25; attempt++)
            {
               try
               {
                  client.Connect("127.0.0.1", RestPort);
                  last = null;
                  break;
               }
               catch (SocketException ex)
               {
                  last = ex;
                  Thread.Sleep(200);
               }
            }

            if (last != null)
               throw last;

            using (NetworkStream stream = client.GetStream())
            using (var memory = new MemoryStream())
            {
               var headers = new StringBuilder();
               headers.Append(method + " " + path + " HTTP/1.0\r\n");
               headers.Append("Host: 127.0.0.1\r\n");
               if (authorization != null)
                  headers.Append("Authorization: " + authorization + "\r\n");
               if (extraHeaders != null)
                  headers.Append(extraHeaders);

               byte[] bodyBytes = requestBody == null ? new byte[0] : Encoding.UTF8.GetBytes(requestBody);
               if (requestBody != null)
               {
                  headers.Append("Content-Type: application/json\r\n");
                  headers.Append("Content-Length: " + bodyBytes.Length + "\r\n");
               }

               headers.Append("Connection: close\r\n\r\n");

               byte[] headerBytes = Encoding.ASCII.GetBytes(headers.ToString());
               stream.Write(headerBytes, 0, headerBytes.Length);
               if (bodyBytes.Length > 0)
                  stream.Write(bodyBytes, 0, bodyBytes.Length);

               byte[] buffer = new byte[8192];
               int read;
               while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                  memory.Write(buffer, 0, read);

               byte[] all = memory.ToArray();
               string raw = Encoding.UTF8.GetString(all);
               var response = new Response();

               int separator = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
               string head = separator >= 0 ? raw.Substring(0, separator) : raw;
               response.Body = separator >= 0 ? raw.Substring(separator + 4) : "";
               // The head is ASCII, so its length in characters is its length in
               // bytes; the body is kept as bytes too, for a download.
               if (separator >= 0)
               {
                  response.BodyBytes = new byte[all.Length - separator - 4];
                  Array.Copy(all, separator + 4, response.BodyBytes, 0, response.BodyBytes.Length);
               }

               string[] lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
               if (lines.Length > 0)
               {
                  string[] parts = lines[0].Split(' ');
                  if (parts.Length >= 2)
                     int.TryParse(parts[1], out response.Status);
               }

               for (int i = 1; i < lines.Length; i++)
               {
                  int colon = lines[i].IndexOf(':');
                  if (colon > 0)
                     response.Headers[lines[i].Substring(0, colon).Trim()] = lines[i].Substring(colon + 1).Trim();
               }

               return response;
            }
         }
      }
   }
}

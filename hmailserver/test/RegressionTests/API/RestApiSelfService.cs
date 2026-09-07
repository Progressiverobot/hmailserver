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

               string raw = Encoding.UTF8.GetString(memory.ToArray());
               var response = new Response();

               int separator = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
               string head = separator >= 0 ? raw.Substring(0, separator) : raw;
               response.Body = separator >= 0 ? raw.Substring(separator + 4) : "";

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

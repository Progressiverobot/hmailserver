// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Shared;

namespace RegressionTests.Infrastructure
{
   /// <summary>
   ///    Everything an account has, through a backup and a restore.
   ///
   ///    An account is not only its row, its folders and its mail. It is also an
   ///    address book (hm_contacts), the webmail's remembered choices
   ///    (hm_accountprefs), the messages it has put off (hm_scheduled), the large
   ///    files it has sent as links (hm_files), its S/MIME certificates and keys
   ///    (hm_smimekeys), the history that stops it reusing a password
   ///    (hm_passwordhistory), its calendar (hm_calendars, hm_calendarobjects) and
   ///    the memory of who has written to it before (hm_knownsenders) - and, on each
   ///    message, the keywords the webmail calls labels.
   ///
   ///    Until 15 September 2026 not one of those was in the archive, and because
   ///    every one of those tables cascades from hm_accounts and a restore deletes
   ///    every domain before it begins, RESTORING A BACKUP DELETED THEM ALL. That is
   ///    what these tests are: one per store, all in the same shape - make the data
   ///    through the surface that really writes it, take a backup, delete the whole
   ///    domain the way a disaster would, restore, and find the data exactly as it
   ///    was. Each of them fails on a build without AccountStores, which is the only
   ///    reason to trust them.
   ///
   ///    The data is made through the surfaces that really write it - the REST and
   ///    DAV listeners, and for the first-contact memory a delivery - rather than by
   ///    writing rows, so what is proved is what an account actually has rather than
   ///    what a test put in a table.
   /// </summary>
   [TestFixture]
   public class BackupAccountStores : TestFixtureBase
   {
      private const int WebServicesPort = 9111;
      private static int RestPort = 9542;

      private const string UserPassword = "Original-Passw0rd!";
      private const string AdminPassword = "testar";

      private const string FirstContactHeader = "X-hMailServer-First-Contact";

      private string _backupDirectory;
      private string _address;

      [SetUp]
      public void StartListeners()
      {
         _address = "stores@" + _domain.Name;
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, _address, UserPassword);
         _settings.SetAdministratorPassword(AdminPassword);

         _backupDirectory = Paths.Combine(Path.GetTempPath(), TestSetup.UniqueString());
         Directory.CreateDirectory(_backupDirectory);

         IniFileSetting.Write("CalDavRedirectUrl", "");
         IniFileSetting.Write("CardDavRedirectUrl", "");
         IniFileSetting.Write("WebServicesBindAddress", "127.0.0.1");
         IniFileSetting.Write("WebServicesHttpPort", WebServicesPort.ToString());
         IniFileSetting.Write("WebServicesHttpsPort", "0");

         RestPort = RestListener.Start(RestPort);
         IniFileSetting.Write("RestApiCertificateFile", "");
         IniFileSetting.Write("RestApiPrivateKeyFile", "");

         _application.Reinitialize();

         Response probe = Rest("GET", "/api/v1/me", User());
         Assert.AreEqual(200, probe.Status, "The REST listener did not answer. Body: " + probe.Body);
      }

      [TearDown]
      public void StopListeners()
      {
         TurnTheFirstContactNoteOff();

         IniFileSetting.Write("WebServicesHttpPort", "0");
         // Put back whatever a test turned on, whether it reached its own last line
         // or not: a setting left behind here runs every fixture after this one.
         IniFileSetting.Write("PasswordPolicyHistoryCount", "0");
         RestListener.Stop();
         _application.Reinitialize();

         try
         {
            if (Directory.Exists(_backupDirectory))
               Directory.Delete(_backupDirectory, true);
         }
         catch (Exception tidying) when (!ExceptionPolicy.IsFatal(tidying))
         {
            // The destination is a temp directory of this test's own. Failing the
            // test because it could not be removed would report the wrong thing.
         }
      }

      // ------------------------------------------------------------ hm_contacts

      [Test]
      [Description("hm_contacts: the account's address book survives a backup and a restore that deleted the domain, "
                   + "including the vCard a CardDAV client stored, byte for byte")]
      public void TheAddressBookComesBack()
      {
         Response added = Rest("POST", "/api/v1/me/contacts", User(),
            "{\"name\":\"Ada Lovelace\",\"address\":\"Ada@Example.com\"}");
         Assert.AreEqual(201, added.Status, "Body: " + added.Body);

         // A card as a client sends one: line breaks throughout, which is the value
         // that cannot cross an XML attribute as it stands and takes the base64 path.
         string card = Card("Grace Hopper", "Hopper;Grace", "grace@example.com");
         Response stored = Dav("PUT", AddressBook + "grace.vcf", card, "text/vcard; charset=utf-8");
         Assert.AreEqual(201, stored.Status, "Body: " + stored.Body);

         BackupDeleteAndRestore();

         Response listed = Rest("GET", "/api/v1/me/contacts", User());
         Assert.AreEqual(200, listed.Status, "Body: " + listed.Body);
         StringAssert.Contains("\"name\":\"Ada Lovelace\"", listed.Body);
         StringAssert.Contains("\"address\":\"ada@example.com\"", listed.Body);
         StringAssert.Contains("\"name\":\"Grace Hopper\"", listed.Body);
         StringAssert.Contains("\"count\":2", listed.Body);

         // The card itself, and under the resource name the client chose - which is a
         // column of its own and is what a client's next sync asks for.
         Response fetched = Dav("GET", AddressBook + "grace.vcf", null, null);
         Assert.AreEqual(200, fetched.Status, "Body: " + fetched.Body);
         Assert.AreEqual(card, fetched.Body, "The stored vCard did not come back byte for byte.");
      }

      // ------------------------------------------------------- hm_accountprefs

      [Test]
      [Description("hm_accountprefs: the webmail's remembered choices survive a backup and a restore, "
                   + "including a value the server attaches no meaning to")]
      public void ThePreferencesComeBack()
      {
         // The server treats a value as opaque, so the test uses one that is: JSON
         // with quotes and a newline in it, which is what a webmail keeps here.
         Response saved = Rest("PUT", "/api/v1/me/preferences", User(),
            "{\"theme\":\"dark\",\"density\":\"cosy\",\"signature\":\"Regards,\\r\\nAda\"}");
         Assert.AreEqual(200, saved.Status, "Body: " + saved.Body);

         BackupDeleteAndRestore();

         Response read = Rest("GET", "/api/v1/me/preferences", User());
         Assert.AreEqual(200, read.Status, "Body: " + read.Body);
         StringAssert.Contains("\"theme\":\"dark\"", read.Body);
         StringAssert.Contains("\"density\":\"cosy\"", read.Body);
         StringAssert.Contains("\"signature\":\"Regards,\\r\\nAda\"", read.Body);
      }

      // ---------------------------------------------------------- hm_scheduled

      [Test]
      [Description("hm_scheduled: a draft scheduled for later survives a backup and a restore, and comes back "
                   + "pointing at the restored draft rather than at whatever was given its old id")]
      public void TheScheduledMessageComesBack()
      {
         var imap = new ImapClientSimulator();
         Assert.IsTrue(imap.ConnectAndLogon(_address, UserPassword));
         Assert.IsTrue(imap.CreateFolder("Sent"));
         imap.Disconnect();

         Response draft = Rest("POST", "/api/v1/me/drafts", User(),
            "{\"to\":\"" + _address + "\",\"subject\":\"Not until Friday\",\"text\":\"Later.\"}");
         Assert.AreEqual(201, draft.Status, "Body: " + draft.Body);
         long draftId = long.Parse(Between(draft.Body, "\"id\":", ","));

         // Far enough away that the scheduler cannot run it during the test.
         string at = DateTime.Now.AddDays(30).ToString("yyyy-MM-dd HH:mm");
         Response scheduled = Rest("POST", "/api/v1/me/drafts/" + draftId + "/schedule", User(),
            "{\"send_at\":\"" + at + "\"}");
         Assert.AreEqual(201, scheduled.Status, "Body: " + scheduled.Body);

         BackupDeleteAndRestore();

         Response listed = Rest("GET", "/api/v1/me/scheduled", User());
         Assert.AreEqual(200, listed.Status, "Body: " + listed.Body);
         StringAssert.Contains("\"action\":\"send\"", listed.Body);
         StringAssert.Contains("\"subject\":\"Not until Friday\"", listed.Body);
         StringAssert.Contains("\"at\":\"" + at + "\"", listed.Body);

         // The point of carrying the reference as a folder path and a UID rather than
         // as the id: the restored row must name the restored draft. The subject in
         // the listing above is read from the message the row points at, so a row
         // pointing at nothing - or at something else - could not produce it. This
         // pins the id as well, since a row left holding the old number would name a
         // message that no longer exists.
         long messageId = long.Parse(Between(listed.Body, "\"message_id\":", ","));
         Response message = Rest("GET", "/api/v1/me/messages/" + messageId, User());
         Assert.AreEqual(200, message.Status, "The scheduled row names a message that is not there. Body: " + message.Body);
         StringAssert.Contains("Not until Friday", message.Body);
      }

      // -------------------------------------------------------------- hm_files

      [Test]
      [Description("hm_files: a file sent as a link survives a backup and a restore, and its link still serves the bytes")]
      public void TheFileSentAsALinkComesBack()
      {
         Response made = Rest("POST", "/api/v1/me/files", User(),
            "{\"name\":\"notes.txt\",\"type\":\"text/plain\",\"size\":5}");
         Assert.AreEqual(201, made.Status, "Body: " + made.Body);
         long fileId = long.Parse(Between(made.Body, "\"id\":", ","));
         string token = Between(made.Body, "\"token\":\"", "\"");

         Response bytes = Rest("PUT", "/api/v1/me/files/" + fileId + "/content?offset=0", User(), "hello",
            "application/octet-stream");
         Assert.AreEqual(200, bytes.Status, "Body: " + bytes.Body);
         StringAssert.Contains("\"complete\":true", bytes.Body);

         BackupDeleteAndRestore();

         Response listed = Rest("GET", "/api/v1/me/files", User());
         Assert.AreEqual(200, listed.Status, "Body: " + listed.Body);
         StringAssert.Contains("\"name\":\"notes.txt\"", listed.Body);
         StringAssert.Contains("\"token\":\"" + token + "\"", listed.Body);
         StringAssert.Contains("\"complete\":true", listed.Body);

         // The bytes were in the message store all along; the row that gives them a
         // name, an owner and an expiry was what the archive did not have. Both
         // halves have to be back for this to answer.
         Response fetched = Rest("GET", "/files/" + token, null);
         Assert.AreEqual(200, fetched.Status, "Body: " + fetched.Body);
         Assert.AreEqual("hello", fetched.Body);
         StringAssert.Contains("notes.txt", fetched.Header("Content-Disposition"));
      }

      // ---------------------------------------------------------- hm_smimekeys

      [Test]
      [Description("hm_smimekeys: the account's own certificate with its wrapped private key, and a correspondent's "
                   + "certificate, survive a backup and a restore")]
      public void TheSmimeKeyStoreComesBack()
      {
         string certificate = Convert.ToBase64String(new byte[] { 0x30, 0x03, 0x02, 0x01, 0x01 });
         string own = new string('a', 64);
         string bob = new string('b', 64);
         string key = "{\"kdf\":\"PBKDF2-SHA256\",\"iterations\":600000,\"salt\":\"AAAAAAAAAAAAAAAAAAAAAA==\","
                      + "\"iv\":\"AAAAAAAAAAAAAAAA\",\"data\":\"AQIDBA==\"}";

         Response mine = Rest("PUT", "/api/v1/me/smime/own", User(),
            "{\"address\":\"" + _address + "\",\"name\":\"Me, Myself\",\"fingerprint\":\"" + own + "\","
            + "\"certificate\":\"" + certificate + "\",\"chain\":[\"" + certificate + "\"],\"key\":" + key
            + ",\"not_after\":2000000000}");
         Assert.AreEqual(201, mine.Status, "Body: " + mine.Body);

         Response theirs = Rest("PUT", "/api/v1/me/smime/recipients", User(),
            "{\"address\":\"bob@example.test\",\"name\":\"Bob\",\"fingerprint\":\"" + bob + "\","
            + "\"certificate\":\"" + certificate + "\",\"not_after\":1900000000}");
         Assert.AreEqual(201, theirs.Status, "Body: " + theirs.Body);

         BackupDeleteAndRestore();

         Response listed = Rest("GET", "/api/v1/me/smime", User());
         Assert.AreEqual(200, listed.Status, "Body: " + listed.Body);
         StringAssert.Contains("\"fingerprint\":\"" + own + "\"", listed.Body);
         StringAssert.Contains("\"fingerprint\":\"" + bob + "\"", listed.Body);
         StringAssert.Contains("\"chain\":[\"" + certificate + "\"]", listed.Body);

         // The wrapped key above all. This server cannot open it, so if a restore
         // dropped it nothing here would fail - the account would simply never be
         // able to read its own encrypted mail again.
         StringAssert.Contains("\"key\":" + key, listed.Body);
         StringAssert.Contains("\"not_after\":2000000000", listed.Body);
      }

      // ---------------------------------------------- hm_calendars/-objects

      [Test]
      [Description("hm_calendars and hm_calendarobjects: the account's calendar and the event a CalDAV client "
                   + "stored survive a backup and a restore, byte for byte and under the same resource name")]
      public void TheCalendarComesBack()
      {
         string ics = Event("backup-0001", "20261101T120000Z", "20261101T130000Z", "A meeting");
         Response stored = Dav("PUT", Calendar + "meeting.ics", ics, "text/calendar; charset=utf-8");
         Assert.AreEqual(201, stored.Status, "Body: " + stored.Body);

         BackupDeleteAndRestore();

         Response fetched = Dav("GET", Calendar + "meeting.ics", null, null);
         Assert.AreEqual(200, fetched.Status, "Body: " + fetched.Body);
         Assert.AreEqual(ics, fetched.Body, "The stored calendar object did not come back byte for byte.");

         // And the collection it hangs under, which is a row of its own: the object
         // is a child of the calendar, so a calendar that came back under a new
         // identity with its objects still pointing at the old one would list empty.
         Response report = Dav("PROPFIND", Calendar,
            "<D:propfind xmlns:D=\"DAV:\"><D:prop><D:getetag/></D:prop></D:propfind>",
            "application/xml; charset=utf-8", "Depth: 1\r\n");
         Assert.AreEqual(207, report.Status, "Body: " + report.Body);
         StringAssert.Contains("meeting.ics", report.Body);
      }

      // --------------------------------------------------- hm_passwordhistory

      [Test]
      [Description("hm_passwordhistory: the passwords an account has been made to change away from survive a "
                   + "backup and a restore, so a restore does not silently let every account go back to one")]
      public void ThePasswordHistoryComesBack()
      {
         IniFileSetting.Write("PasswordPolicyHistoryCount", "3");
         _application.Reinitialize();

         string second = "Second-Passw0rd!";
         Response changed = Rest("POST", "/api/v1/me/password", User(),
            "{\"current\":\"" + UserPassword + "\",\"new\":\"" + second + "\"}");
         Assert.AreEqual(200, changed.Status, "Body: " + changed.Body);

         // The old password is now history, and going back to it is refused.
         Response back = Rest("POST", "/api/v1/me/password", BasicAuth(_address, second),
            "{\"current\":\"" + second + "\",\"new\":\"" + UserPassword + "\"}");
         Assert.AreEqual(409, back.Status, "The old password should be refused before the backup. Body: " + back.Body);

         BackupDeleteAndRestore();

         // And is still refused after it. On a build whose archive has no
         // hm_passwordhistory this answers 200: the history was deleted with the
         // domain and never came back, and nothing anywhere says so.
         Response again = Rest("POST", "/api/v1/me/password", BasicAuth(_address, second),
            "{\"current\":\"" + second + "\",\"new\":\"" + UserPassword + "\"}");
         Assert.AreEqual(409, again.Status,
            "The restored account accepted a password it had been made to change away from, so its password "
            + "history did not come back. Body: " + again.Body);

      }

      // ------------------------------------------------------ hm_knownsenders

      [Test]
      [Description("hm_knownsenders: the memory the first-contact note is decided against survives a backup and a "
                   + "restore, so a sender the account knew is not announced as a first contact again - while a "
                   + "sender first seen after the backup still is")]
      public void TheFirstContactMemoryComesBack()
      {
         // Nothing reads this table through an API, so it is proved by what it is
         // for: whether a delivery carries the note.
         const string known = "known@outside-stores.test";
         const string late = "late@outside-stores.test";

         string domainName = _domain.Name;

         // Turned off again in TearDown, on whatever domain the restore left.
         _domain.FirstContactTip = true;
         _domain.Save();

         // The account's first message ever. Remembered, not announced: a mailbox
         // that remembers nobody has nothing for a sender to be unusual against.
         // That rule is why the two deliveries after the restore are in the order
         // they are.
         SmtpClientSimulator.StaticSend(known, _address, "Before the backup", "Body");
         StringAssert.DoesNotContain(FirstContactHeader,
            Pop3ClientSimulator.AssertGetFirstMessageText(_address, UserPassword),
            "The first message an account ever receives carries no note.");

         RunBackup();

         // A sender first seen AFTER the backup was taken, so absent from the archive.
         // Announced, because the account already remembers the first sender - which
         // is also what shows that the memory held that sender when the backup ran.
         SmtpClientSimulator.StaticSend(late, _address, "After the backup", "Body");
         StringAssert.Contains(FirstContactHeader,
            Pop3ClientSimulator.AssertGetFirstMessageText(_address, UserPassword),
            "A sender the account had never had mail from must be announced.");

         DeleteAndRestore();

         Assert.IsTrue(_application.Domains.ItemByName[domainName].FirstContactTip,
            "The domain came back with the first-contact note off, so no delivery below could show anything.");

         // THE ORDER OF THE NEXT TWO DELIVERIES IS WHAT MAKES THIS A TEST.
         //
         // The negative control first: the sender recorded after the backup is not in
         // the archive, so the restored memory does not know them, and because that
         // memory is not empty they are announced. On a build whose archive does not
         // carry hm_knownsenders the restored account remembers nobody, this is the
         // first message of a mailbox with no memory, it carries no note - and the
         // test fails here.
         SmtpClientSimulator.StaticSend(late, _address, "Late sender, after the restore", "Body");
         StringAssert.Contains(FirstContactHeader,
            Pop3ClientSimulator.AssertGetFirstMessageText(_address, UserPassword),
            "A sender first seen after the backup is not in the archive and must be announced again. It was not, "
            + "which means the restored account remembered nobody at all: hm_knownsenders did not come back.");

         // And the sender the account knew when the backup was taken is not a first
         // contact. On a build without the table in the archive this one is announced
         // too, because by now that account remembers the late sender and nobody else.
         // Asked the other way round, both would pass on that build: an empty memory
         // announces nobody, and one delivery later it knows exactly one sender.
         SmtpClientSimulator.StaticSend(known, _address, "Known sender, after the restore", "Body");
         StringAssert.DoesNotContain(FirstContactHeader,
            Pop3ClientSimulator.AssertGetFirstMessageText(_address, UserPassword),
            "The restored account announced a sender it had had mail from before the backup, so the memory of "
            + "who has written to it did not come back.");
      }

      // --------------------------------------------------- a message's labels

      [Test]
      [Description("messagekeywords: a message comes back from a restore with its labels and its second star, "
                   + "which Message::XMLStore never wrote")]
      public void AMessageComesBackWithItsLabelsAndItsSecondStar()
      {
         SmtpClientSimulator.StaticSend(_address, _address, "Labelled", "This one is filed.");
         Pop3ClientSimulator.AssertMessageCount(_address, UserPassword, 1);

         var imap = new ImapClientSimulator();
         Assert.IsTrue(imap.ConnectAndLogon(_address, UserPassword));
         Assert.IsTrue(imap.SelectFolder("INBOX"));

         // A label of the reader's own, the second star and a follow-up: exactly
         // the three the webmail keeps here, none of them a system flag. The stars
         // above the first are the keywords $Star2 and $Star3 on top of \Flagged,
         // and a follow-up is $FollowUp beside them.
         Assert.IsTrue(imap.SetFlagOnMessage(1, true, "Invoices $Star2 $FollowUp"));
         Assert.IsTrue(imap.SetFlagOnMessage(1, true, "\\Seen"));

         string before = imap.GetFlags(1);
         StringAssert.Contains("Invoices", before);
         imap.Disconnect();

         BackupDeleteAndRestore();

         var after = new ImapClientSimulator();
         Assert.IsTrue(after.ConnectAndLogon(_address, UserPassword));
         Assert.IsTrue(after.SelectFolder("INBOX"));

         string flags = after.GetFlags(1);
         after.Disconnect();

         StringAssert.Contains("Invoices", flags,
            "The restored message lost its label, so messagekeywords is not in the archive. FETCH said: " + flags);
         StringAssert.Contains("$Star2", flags,
            "The restored message lost its second star. FETCH said: " + flags);
         StringAssert.Contains("$FollowUp", flags,
            "The restored message lost its follow-up flag. FETCH said: " + flags);
         StringAssert.Contains("\\Seen", flags, "The system flags must still be there too. FETCH said: " + flags);
      }

      // ------------------------------------------------- the backup's own word

      [Test]
      [Description("The verified restore reconciles the per-account rows it wrote against the archive it reads "
                   + "back, so a store that went missing would fail the backup rather than pass quietly")]
      public void TheVerifiedRestoreCountsThePerAccountRows()
      {
         Response added = Rest("POST", "/api/v1/me/contacts", User(),
            "{\"name\":\"Ada Lovelace\",\"address\":\"ada@example.com\"}");
         Assert.AreEqual(201, added.Status, "Body: " + added.Body);

         RunBackup();

         string log = TestSetup.ReadExistingTextFile(_application.Settings.Backup.LogFile);
         StringAssert.Contains("per-account store", log,
            "The verified restore said nothing about the per-account stores, so it is not checking them. Log: " + log);
      }

      // ------------------------------------------------------------- the cycle

      private void BackupDeleteAndRestore()
      {
         RunBackup();
         DeleteAndRestore();
      }

      private void DeleteAndRestore()
      {
         // The disaster, exactly as a restore assumes it: everything gone. Every one
         // of these tables cascades from hm_accounts, so this is also what deletes
         // them - which is why a restore from an archive that does not carry them
         // destroys them.
         while (_application.Domains.Count > 0)
            _application.Domains[0].Delete();

         string startTime = _application.Status.StartTime;

         FileInfo[] archives = new DirectoryInfo(_backupDirectory).GetFiles("*.7z");
         Assert.AreEqual(1, archives.Length, "Expected exactly one archive in " + _backupDirectory);

         var backup = _application.BackupManager.LoadBackup(archives[0].FullName);
         backup.RestoreDomains = true;
         backup.RestoreMessages = true;
         backup.RestoreSettings = false;
         backup.StartRestore();

         WaitForRestore(startTime);
      }

      /// <summary>
      ///    The first-contact note is a domain setting, and once a restore has run the
      ///    domain is no longer the object _domain holds - so it is turned off on
      ///    whatever domains there are rather than through _domain. The next SetUp
      ///    replaces the domain anyway; this is so that nothing a test here turned on
      ///    outlives it, whether or not it reached its last line.
      /// </summary>
      private void TurnTheFirstContactNoteOff()
      {
         try
         {
            var domains = _application.Domains;

            for (int index = 0; index < domains.Count; index++)
            {
               var domain = domains[index];

               if (domain.FirstContactTip)
               {
                  domain.FirstContactTip = false;
                  domain.Save();
               }
            }
         }
         catch (Exception tidying) when (!ExceptionPolicy.IsFatal(tidying))
         {
            // A restore that failed half way can leave domains nothing can save.
            // Failing TearDown over that would replace the failure that matters.
            Console.WriteLine("Could not turn the first-contact note off: " + tidying.Message);
         }
      }

      private void RunBackup()
      {
         var settings = _application.Settings.Backup;
         settings.BackupDomains = true;
         settings.BackupMessages = true;
         settings.BackupSettings = false;
         settings.CompressDestinationFiles = true;
         settings.Destination = _backupDirectory;

         CustomAsserts.AssertDeleteFile(settings.LogFile);

         _application.BackupManager.StartBackup();

         for (int attempt = 0; attempt < 120; attempt++)
         {
            try
            {
               string log = TestSetup.ReadExistingTextFile(settings.LogFile);

               if (log.Contains("BACKUP ERROR:"))
                  Assert.Fail("The backup failed. Log: " + log);

               if (log.Contains("Backup completed successfully"))
                  return;
            }
            catch (Exception sharing) when (!ExceptionPolicy.IsFatal(sharing))
            {
               // The server is writing the file; read it again in a moment.
            }

            Thread.Sleep(250);
         }

         Assert.Fail("The backup did not finish within 30 seconds.");
      }

      private void WaitForRestore(string lastStartTime)
      {
         for (int attempt = 0; attempt < 600; attempt++)
         {
            try
            {
               string startTime = _application.Status.StartTime;

               if (startTime.Length > 0 && startTime != lastStartTime)
               {
                  // The listeners come back with the reinitialisation; give them the
                  // moment they need before the next request, which would otherwise
                  // spend its retries on a port that is between binds.
                  Thread.Sleep(500);
                  return;
               }
            }
            catch (Exception talking) when (!ExceptionPolicy.IsFatal(talking))
            {
               // The COM server is reinitialising.
            }

            Thread.Sleep(100);
         }

         Assert.Fail("The restore did not finish within a minute.");
      }

      // ------------------------------------------------------------- the wires

      private string AddressBook
      {
         get { return "/dav/addressbooks/" + _address + "/contacts/"; }
      }

      private string Calendar
      {
         get { return "/dav/calendars/" + _address + "/calendar/"; }
      }

      private string User()
      {
         return BasicAuth(_address, UserPassword);
      }

      private static string BasicAuth(string user, string password)
      {
         return "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + password));
      }

      private Response Rest(string method, string path, string authorization, string body = null,
                            string contentType = "application/json")
      {
         return Raw(RestPort, method, path, authorization, body, contentType, null);
      }

      /// <summary>
      ///    A request to the DAV listener, as a TLS-terminating proxy would forward
      ///    it: CalDAV and CardDAV refuse Basic over plain HTTP, and the listener is
      ///    plain HTTP on this bench.
      /// </summary>
      private Response Dav(string method, string path, string body, string contentType,
                           string extraHeaders = null)
      {
         return Raw(WebServicesPort, method, path, BasicAuth(_address, UserPassword), body,
            contentType ?? "application/xml; charset=utf-8",
            "X-Forwarded-Proto: https\r\n" + (extraHeaders ?? ""));
      }

      private static string Card(string formattedName, string structuredName, string email)
      {
         return string.Join("\r\n", new[]
         {
            "BEGIN:VCARD",
            "VERSION:3.0",
            "PRODID:-//A phone//EN",
            "UID:backup-card-0001",
            "FN:" + formattedName,
            "N:" + structuredName,
            "EMAIL;TYPE=INTERNET:" + email,
            "END:VCARD",
            ""
         });
      }

      private static string Event(string uid, string start, string end, string summary)
      {
         return string.Join("\r\n", new[]
         {
            "BEGIN:VCALENDAR",
            "VERSION:2.0",
            "PRODID:-//A phone//EN",
            "BEGIN:VEVENT",
            "UID:" + uid,
            "DTSTAMP:20260101T000000Z",
            "DTSTART:" + start,
            "DTEND:" + end,
            "SUMMARY:" + summary,
            "SEQUENCE:0",
            "END:VEVENT",
            "END:VCALENDAR",
            ""
         });
      }

      private static string Between(string text, string after, string before)
      {
         int start = text.IndexOf(after, StringComparison.Ordinal);
         Assert.Greater(start, -1, "Expected " + after + " in: " + text);
         start += after.Length;

         int end = text.IndexOf(before, start, StringComparison.Ordinal);
         Assert.Greater(end, -1, "Expected " + before + " after " + after + " in: " + text);

         return text.Substring(start, end - start);
      }

      private class Response
      {
         public int Status;
         public string Body = "";
         public string HeaderText = "";
         public readonly System.Collections.Generic.Dictionary<string, string> Headers =
            new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

         public string Header(string name)
         {
            string value;
            return Headers.TryGetValue(name, out value) ? value : null;
         }
      }

      private static Response Raw(int port, string method, string path, string authorization, string body,
                                  string contentType, string extraHeaders)
      {
         using (var client = new TcpClient())
         {
            Exception last = null;
            for (int attempt = 0; attempt < 40; attempt++)
            {
               try
               {
                  client.Connect("127.0.0.1", port);
                  last = null;
                  break;
               }
               catch (SocketException ex)
               {
                  last = ex;
                  Thread.Sleep(250);
               }
            }

            if (last != null)
               throw last;

            using (NetworkStream stream = client.GetStream())
            using (var memory = new MemoryStream())
            {
               var head = new StringBuilder();
               head.Append(method + " " + path + " HTTP/1.0\r\n");
               head.Append("Host: 127.0.0.1:" + port + "\r\n");
               if (authorization != null)
                  head.Append("Authorization: " + authorization + "\r\n");
               if (extraHeaders != null)
                  head.Append(extraHeaders);

               byte[] bodyBytes = body == null ? new byte[0] : Encoding.UTF8.GetBytes(body);
               if (body != null)
               {
                  head.Append("Content-Type: " + contentType + "\r\n");
                  head.Append("Content-Length: " + bodyBytes.Length + "\r\n");
               }

               head.Append("Connection: close\r\n\r\n");

               byte[] headBytes = Encoding.ASCII.GetBytes(head.ToString());
               stream.Write(headBytes, 0, headBytes.Length);
               if (bodyBytes.Length > 0)
                  stream.Write(bodyBytes, 0, bodyBytes.Length);

               byte[] buffer = new byte[8192];
               int read;
               while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                  memory.Write(buffer, 0, read);

               string raw = Encoding.UTF8.GetString(memory.ToArray());
               var response = new Response();

               int separator = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
               response.HeaderText = separator >= 0 ? raw.Substring(0, separator) : raw;
               response.Body = separator >= 0 ? raw.Substring(separator + 4) : "";

               string[] lines = response.HeaderText.Split(new[] { "\r\n" }, StringSplitOptions.None);
               if (lines.Length > 0)
               {
                  string[] parts = lines[0].Split(' ');
                  if (parts.Length >= 2)
                     int.TryParse(parts[1], out response.Status);
               }

               for (int index = 1; index < lines.Length; index++)
               {
                  int colon = lines[index].IndexOf(':');
                  if (colon > 0)
                     response.Headers[lines[index].Substring(0, colon).Trim()] =
                        lines[index].Substring(colon + 1).Trim();
               }

               return response;
            }
         }
      }
   }
}

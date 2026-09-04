// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Threading;
using System.Linq;
using hMailServer;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Shared;

namespace RegressionTests.IMAP
{
   [TestFixture]
   [Description("RFC 6154 SPECIAL-USE: the \\Sent / \\Drafts / \\Junk / \\Trash / \\Archive / \\All attributes, CREATE ... USE, and the LIST SPECIAL-USE selection and return options")]
   public class SpecialUse : TestFixtureBase
   {
      private ImapClientSimulator LogonAs(string address)
      {
         var simulator = new ImapClientSimulator();
         Assert.IsTrue(simulator.ConnectAndLogon(address, "test"));

         return simulator;
      }

      private static int CountOccurrences(string text, string value)
      {
         var count = 0;
         var position = text.IndexOf(value, StringComparison.Ordinal);

         while (position >= 0)
         {
            count++;
            position = text.IndexOf(value, position + value.Length, StringComparison.Ordinal);
         }

         return count;
      }

      /// <summary>
      ///    The "* LIST" line for one mailbox, or null if the mailbox was not listed.
      ///    Attributes are asserted against this single line rather than against the
      ///    whole response, so that "Sent has \Sent" cannot pass because some other
      ///    folder in the same listing happened to carry the attribute.
      /// </summary>
      private static string ListLineFor(string response, string folderName)
      {
         return response.Split(new[] {"\r\n"}, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(line => line.StartsWith("* LIST ") && line.EndsWith("\"" + folderName + "\""));
      }

      private static int CountListLinesFor(string response, string folderName)
      {
         return response.Split(new[] {"\r\n"}, StringSplitOptions.RemoveEmptyEntries)
            .Count(line => line.StartsWith("* LIST ") && line.EndsWith("\"" + folderName + "\""));
      }

      private static string RequireListLine(string response, string folderName)
      {
         var line = ListLineFor(response, folderName);
         Assert.IsNotNull(line, "The mailbox " + folderName + " was not listed. Response: " + response);

         return line;
      }

      /// <summary>
      ///    How many top-level folder rows the account has with this name. Read through
      ///    the COM folder collection rather than by counting LIST lines, because the
      ///    collection is loaded straight from hm_imapfolders and so answers the
      ///    question the database was asked, not the question the protocol layer
      ///    answered afterwards. Only meaningful once the per-account folder cache has
      ///    been dropped - see DropFolderCache().
      /// </summary>
      private int CountTopLevelFoldersNamed(string address, string folderName)
      {
         var account = _domain.Accounts.get_ItemByAddress(address);
         Assert.IsNotNull(account, "The account " + address + " could not be looked up.");

         var folders = account.IMAPFolders;

         var count = 0;
         for (var i = 0; i < folders.Count; i++)
         {
            if (folders[i].Name == folderName)
               count++;
         }

         return count;
      }

      /// <summary>
      ///    Drops the per-account IMAP folder cache, so that the next read of a folder
      ///    comes from the database instead of from the objects the command that just
      ///    ran happened to leave behind. Reinitialize is the blunt instrument the rest
      ///    of the suite already uses for this; there is no narrower COM call that
      ///    uncaches one account's folders.
      /// </summary>
      private void DropFolderCache()
      {
         _application.Reinitialize();

         // Reinitialize stops and restarts every listener. Wait, bounded, for the IMAP
         // port to come back, so that a test which needs it fails for the reason it is
         // testing and not on a connect race.
         for (var i = 0; i < 200; i++)
         {
            if (new ImapClientSimulator().TestConnect(143))
               return;

            Thread.Sleep(50);
         }

         Assert.Fail("The IMAP listener did not come back after Reinitialize().");
      }

      [Test]
      [Description("Both RFC 6154 capability names are advertised")]
      public void CapabilityAdvertisesSpecialUseAndCreateSpecialUse()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "su1@example.test", "test");

         var simulator = LogonAs(account.Address);
         var capabilities = simulator.GetCapabilities();
         simulator.Disconnect();

         // SPECIAL-USE was already advertised. CREATE-SPECIAL-USE is the second name
         // RFC 6154 section 6 defines, and it is the one a client checks before it will
         // send CREATE ... USE - so without it, the clients that want to create their
         // own sent folder (Apple Mail, Outlook mobile) never ask the server to
         // designate it and go back to naming it and hoping. This assertion fails
         // against the unfixed server, which advertised only SPECIAL-USE.
         StringAssert.Contains("SPECIAL-USE", capabilities);
         StringAssert.Contains("CREATE-SPECIAL-USE", capabilities);
      }

      [Test]
      [Description("The pre-6.2.19 folder-name guesses still produce attributes for clients that never ask")]
      public void FolderNamesStillProduceSpecialUseAttributes()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "su2@example.test", "test");

         var simulator = LogonAs(account.Address);

         Assert.IsTrue(simulator.CreateFolder("Sent"));
         Assert.IsTrue(simulator.CreateFolder("Drafts"));
         Assert.IsTrue(simulator.CreateFolder("Trash"));
         Assert.IsTrue(simulator.CreateFolder("Junk"));
         Assert.IsTrue(simulator.CreateFolder("Archive"));

         var response = simulator.SendSingleCommand("A01 LIST \"\" \"*\"");
         simulator.Disconnect();

         // This one passes against the unfixed server too, and that is deliberate: the
         // designation lookup was rewritten from a per-folder name test into a
         // resolve-once-per-mailbox map, and the requirement is that an account which
         // has never had a designation assigned sees no change at all. It fails if the
         // rewrite dropped the name fallback, attached it to the wrong folder, or made
         // it conditional on the client sending RETURN (SPECIAL-USE) - which is what
         // every client older than this release relies on.
         StringAssert.Contains("\\Sent", RequireListLine(response, "Sent"));
         StringAssert.Contains("\\Drafts", RequireListLine(response, "Drafts"));
         StringAssert.Contains("\\Trash", RequireListLine(response, "Trash"));
         StringAssert.Contains("\\Junk", RequireListLine(response, "Junk"));
         StringAssert.Contains("\\Archive", RequireListLine(response, "Archive"));

         // One attribute of each kind in the whole listing, which also proves none of
         // them leaked onto INBOX.
         Assert.AreEqual(1, CountOccurrences(response, "\\Sent"), response);
         Assert.AreEqual(1, CountOccurrences(response, "\\Drafts"), response);
         Assert.AreEqual(1, CountOccurrences(response, "\\Trash"), response);
         Assert.AreEqual(1, CountOccurrences(response, "\\Junk"), response);
         Assert.AreEqual(1, CountOccurrences(response, "\\Archive"), response);
      }

      [Test]
      [Description("LSUB still reports the special-use attributes it reported before the rewrite")]
      public void LsubStillProducesSpecialUseAttributes()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "su2b@example.test", "test");

         var simulator = LogonAs(account.Address);

         Assert.IsTrue(simulator.CreateFolder("Sent"));
         Assert.IsTrue(simulator.Subscribe("Sent"));

         var response = simulator.SendSingleCommand("A01 LSUB \"\" \"*\"");
         simulator.Disconnect();

         // A guard, not a fix. LIST and LSUB used to share one function with nine
         // positional parameters, two of them controlling which of the two commands was
         // being answered; they now share a named request struct instead. The failure
         // this catches is the rewrite wiring the designation lookup into the LIST path
         // only, which no test on LIST output could ever notice.
         StringAssert.Contains("* LSUB", response);
         StringAssert.Contains("\\Sent", response);
      }

      [Test]
      [Description("The name guess stays at the top level: a subfolder called Sent is not the sent mailbox")]
      public void SubfolderNamedSentDoesNotGetTheSentAttribute()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "su3@example.test", "test");

         var simulator = LogonAs(account.Address);
         Assert.IsTrue(simulator.CreateFolder("Projects.Sent"));

         var response = simulator.SendSingleCommand("A01 LIST \"\" \"*\"");
         simulator.Disconnect();

         // Also a guard rather than a fix: widening the name guess to every depth would
         // start telling clients that somebody's "Projects.Sent" archive is the mailbox
         // to file sent mail into, and "Projects.Trash" the one to empty.
         Assert.IsFalse(RequireListLine(response, "Projects.Sent").Contains("\\Sent"), response);
         Assert.AreEqual(0, CountOccurrences(response, "\\Sent"), response);
      }

      [Test]
      [Description("Two folder names that both mean \"sent\" produce the attribute once")]
      public void TwoFolderNamesForTheSameUseYieldOneAttribute()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "su4@example.test", "test");

         var simulator = LogonAs(account.Address);

         // Created in this order on purpose: "Sent Items" exists first, so a
         // first-folder-wins implementation would pick it and this test would catch
         // that the canonical name is supposed to win instead.
         Assert.IsTrue(simulator.CreateFolder("Sent Items"));
         Assert.IsTrue(simulator.CreateFolder("Sent"));

         var response = simulator.SendSingleCommand("A01 LIST \"\" \"*\"");
         simulator.Disconnect();

         // Fails against the unfixed server: the old code tested each folder's name in
         // isolation, so a mailbox holding both "Sent" and "Sent Items" - exactly what
         // happens once another client has created its own sent folder - was told
         // \Sent twice. RFC 6154 gives the client no way to choose between them, so it
         // picks one arbitrarily and the user's mail ends up in both.
         Assert.AreEqual(1, CountOccurrences(response, "\\Sent"), response);

         StringAssert.Contains("\\Sent", RequireListLine(response, "Sent"));
         Assert.IsFalse(RequireListLine(response, "Sent Items").Contains("\\Sent"), response);
      }

      [Test]
      [Description("CREATE ... USE (\\Sent) designates a folder whose name says nothing")]
      public void CreateWithUseStoresTheDesignation()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "su5@example.test", "test");

         var simulator = LogonAs(account.Address);

         // A Spanish mailbox: the folder name matches nothing in the name table, which
         // is the whole reason RFC 6154 exists.
         var createResult = simulator.SendSingleCommand("A01 CREATE \"Enviados\" (USE (\\Sent))");
         Assert.IsTrue(createResult.StartsWith("A01 OK"), createResult);

         var response = simulator.SendSingleCommand("A02 LIST \"\" \"*\"");
         simulator.Disconnect();

         // Fails against the unfixed server twice over: CREATE rejected any second
         // parameter with "A01 BAD CREATE Command requires 1 parameter.", and there was
         // nowhere to store a designation even if it had accepted one.
         StringAssert.Contains("\\Sent", RequireListLine(response, "Enviados"));
      }

      [Test]
      [Description("CREATE ... USE writes exactly one hm_imapfolders row")]
      public void CreateWithUseWritesExactlyOneFolderRow()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "su16@example.test", "test");
         var address = account.Address;

         var simulator = LogonAs(address);
         var createResult = simulator.SendSingleCommand("A01 CREATE \"Enviados\" (USE (\\Sent))");
         Assert.IsTrue(createResult.StartsWith("A01 OK"), createResult);
         simulator.Disconnect();

         // The cache has to go before the count means anything: while the folder tree is
         // cached there is exactly one IMAPFolder object for the folder no matter how
         // many rows stand behind it, so a duplicate row is invisible until the tree is
         // loaded again.
         DropFolderCache();

         // The defect this pins down: the designation used to be written with the
         // whole-row folder save, which chooses between INSERT and UPDATE by asking the
         // cached folder object for its database id - and the code that creates the
         // folder never checks that its own insert succeeded, so the object can be in
         // the cache with id 0. The designation write is then a second INSERT for a
         // folder that already has a row: a duplicate on a backend without the unique
         // index over (folderaccountid, folderparentid, foldername), and on the backends
         // that do have that index, a command that fails after the mailbox was created.
         // The designation now goes through a single-column UPDATE keyed on folderid,
         // which can do neither.
         //
         // Against 6.2.18 this test fails at the CREATE above, which answered BAD to any
         // second parameter.
         Assert.AreEqual(1, CountTopLevelFoldersNamed(address, "Enviados"),
            "Expected exactly one hm_imapfolders row for the designated folder.");

         // And the same invariant seen from the protocol side. Two rows would produce
         // two "* LIST" lines with different attributes (only the first row can own
         // \Sent), which no client can make sense of.
         simulator = LogonAs(address);
         var response = simulator.SendSingleCommand("A02 LIST \"\" \"*\"");
         simulator.Disconnect();

         Assert.AreEqual(1, CountListLinesFor(response, "Enviados"), response);
         StringAssert.Contains("\\Sent", RequireListLine(response, "Enviados"));
      }

      [Test]
      [Description("The USE parameter takes a list, and every attribute in it is returned")]
      public void CreateWithUseAcceptsSeveralAttributes()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "su6@example.test", "test");

         var simulator = LogonAs(account.Address);

         var createResult = simulator.SendSingleCommand("A01 CREATE \"Everything\" (USE (\\Archive \\All))");
         Assert.IsTrue(createResult.StartsWith("A01 OK"), createResult);

         var response = simulator.SendSingleCommand("A02 LIST \"\" \"*\"");
         simulator.Disconnect();

         // Fails against the unfixed server: it had no USE parameter at all. The
         // multi-attribute form matters because RFC 4466's grammar for USE is a list,
         // and \All is one of the attributes that can only ever arrive explicitly - no
         // folder name is allowed to imply it, because no folder here really holds
         // every message in the store.
         var line = RequireListLine(response, "Everything");
         StringAssert.Contains("\\All", line);
         StringAssert.Contains("\\Archive", line);
      }

      [Test]
      [Description("A second folder cannot claim a special use that is already assigned")]
      public void SecondCreateWithTheSameUseIsRefused()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "su7@example.test", "test");

         var simulator = LogonAs(account.Address);

         var firstResult = simulator.SendSingleCommand("A01 CREATE \"Enviados\" (USE (\\Sent))");
         Assert.IsTrue(firstResult.StartsWith("A01 OK"), firstResult);

         var secondResult = simulator.SendSingleCommand("A02 CREATE \"Gesendet\" (USE (\\Sent))");

         var response = simulator.SendSingleCommand("A03 LIST \"\" \"*\"");
         simulator.Disconnect();

         // Fails against the unfixed server, which had no designation to conflict with.
         // The refusal must carry the USEATTR response code: that is what tells the
         // client the command itself was fine and only the requested use was
         // unavailable, where a bare NO makes clients retry.
         Assert.IsTrue(secondResult.StartsWith("A02 NO"), secondResult);
         StringAssert.Contains("[USEATTR]", secondResult);

         // RFC 6154 section 3: if the special use cannot be granted, the mailbox must
         // not be created either.
         Assert.IsNull(ListLineFor(response, "Gesendet"), response);
         Assert.AreEqual(1, CountOccurrences(response, "\\Sent"), response);
      }

      [Test]
      [Description("An explicit designation beats the folder-name guess for the same attribute")]
      public void ExplicitDesignationSuppressesTheNameGuess()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "su8@example.test", "test");

         var simulator = LogonAs(account.Address);

         // An English-named folder some other client left behind, plus the folder this
         // client actually intends to use.
         Assert.IsTrue(simulator.CreateFolder("Sent"));

         var createResult = simulator.SendSingleCommand("A01 CREATE \"Enviados\" (USE (\\Sent))");
         Assert.IsTrue(createResult.StartsWith("A01 OK"), createResult);

         var response = simulator.SendSingleCommand("A02 LIST \"\" \"*\"");
         simulator.Disconnect();

         // Fails against the unfixed server, and this is the case that actually costs
         // users mail: with the name guess still in force alongside an explicit
         // designation, LIST reports \Sent on both "Sent" and "Enviados" and the client
         // files some messages into each.
         Assert.AreEqual(1, CountOccurrences(response, "\\Sent"), response);
         StringAssert.Contains("\\Sent", RequireListLine(response, "Enviados"));
         Assert.IsFalse(RequireListLine(response, "Sent").Contains("\\Sent"), response);
      }

      [Test]
      [Description("An unsupported use attribute is refused with USEATTR and creates nothing")]
      public void UnknownUseAttributeIsRefusedWithUseAttrAndCreatesNothing()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "su9@example.test", "test");

         var simulator = LogonAs(account.Address);

         var createResult = simulator.SendSingleCommand("A01 CREATE \"Something\" (USE (\\NoSuchUse))");

         var response = simulator.SendSingleCommand("A02 LIST \"\" \"*\"");
         simulator.Disconnect();

         // Fails against the unfixed server, which answered BAD for any CREATE with a
         // second parameter. The distinction is not cosmetic: RFC 6154 requires
         // NO [USEATTR] here, and a client that receives BAD concludes the server does
         // not understand CREATE at all and stops, rather than retrying without USE.
         Assert.IsTrue(createResult.StartsWith("A01 NO"), createResult);
         StringAssert.Contains("[USEATTR]", createResult);
         Assert.IsNull(ListLineFor(response, "Something"), response);
      }

      [Test]
      [Description("A CREATE parameter that is not USE is still a syntax error")]
      public void UnrecognisedCreateParameterIsRejectedAsBad()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "su10@example.test", "test");

         var simulator = LogonAs(account.Address);

         var createResult = simulator.SendSingleCommand("A01 CREATE \"Something\" (NOTAPARAMETER)");

         var response = simulator.SendSingleCommand("A02 LIST \"\" \"*\"");
         simulator.Disconnect();

         // Passes against the unfixed server as well - it rejected every second
         // parameter. Kept as a guard on the new parameter parsing, which must not
         // become so liberal that it silently ignores a parameter it does not
         // understand and hands back a plain folder the client then treats as special.
         Assert.IsTrue(createResult.StartsWith("A01 BAD"), createResult);
         Assert.IsNull(ListLineFor(response, "Something"), response);
      }

      [Test]
      [Description("LIST (SPECIAL-USE) returns only the mailboxes that have a special use")]
      public void SpecialUseSelectionOptionReturnsOnlySpecialFolders()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "su11@example.test", "test");

         var simulator = LogonAs(account.Address);

         Assert.IsTrue(simulator.CreateFolder("Sent"));
         Assert.IsTrue(simulator.CreateFolder("Holiday photos"));

         var response = simulator.SendSingleCommand("A01 LIST (SPECIAL-USE) \"\" \"*\"");
         simulator.Disconnect();

         // Fails against the unfixed server: the selection option was matched with a
         // substring search for "SUBSCRIBED", so "(SPECIAL-USE)" matched nothing, the
         // request was handled as a plain LIST, and the client was sent the whole
         // mailbox back - INBOX and "Holiday photos" included.
         Assert.IsNotNull(ListLineFor(response, "Sent"), response);
         Assert.IsNull(ListLineFor(response, "INBOX"), response);
         Assert.IsNull(ListLineFor(response, "Holiday photos"), response);
         StringAssert.Contains("A01 OK", response);
      }

      [Test]
      [Description("The SPECIAL-USE selection and return options can be sent together")]
      public void SpecialUseSelectionAndReturnOptionsCombine()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "su12@example.test", "test");

         var simulator = LogonAs(account.Address);

         Assert.IsTrue(simulator.CreateFolder("Drafts"));
         Assert.IsTrue(simulator.CreateFolder("Holiday photos"));

         var response = simulator.SendSingleCommand("A01 LIST (SPECIAL-USE) \"\" \"*\" RETURN (SPECIAL-USE)");
         simulator.Disconnect();

         // Fails against the unfixed server for the same reason as the test above: the
         // selection option had no effect, so "Holiday photos" came back too. The
         // combined form is what a client sends when it wants just the special folders,
         // so it has to work as a whole and not only as two independent halves.
         StringAssert.Contains("\\Drafts", RequireListLine(response, "Drafts"));
         Assert.IsNull(ListLineFor(response, "Holiday photos"), response);
         StringAssert.Contains("A01 OK", response);
      }

      [Test]
      [Description("LIST (SPECIAL-USE) with an empty pattern does not fall back to the hierarchy root")]
      public void SpecialUseSelectionWithEmptyPatternReturnsNothing()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "su13@example.test", "test");

         var simulator = LogonAs(account.Address);
         var response = simulator.SendSingleCommand("A01 LIST (SPECIAL-USE) \"\" \"\"");
         simulator.Disconnect();

         // Fails against the unfixed server, which answered the empty pattern with
         // "* LIST (\Noselect) \".\" \"\"" - the hierarchy-delimiter reply from
         // RFC 3501. That is the right answer to a plain LIST and the wrong one here:
         // the hierarchy root is \Noselect and can never have a special use, so a
         // client filtering on SPECIAL-USE would have to know to throw it away.
         Assert.IsFalse(response.Contains("* LIST"), response);
         StringAssert.Contains("A01 OK", response);
      }

      [Test]
      [Description("A designation survives a folder-cache reload and follows the folder through RENAME")]
      public void DesignationSurvivesCacheReloadAndRename()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "su14@example.test", "test");
         var address = account.Address;

         var simulator = LogonAs(address);
         var createResult = simulator.SendSingleCommand("A01 CREATE \"Enviados\" (USE (\\Sent))");
         Assert.IsTrue(createResult.StartsWith("A01 OK"), createResult);
         simulator.Disconnect();

         // Dropping the cache rather than just opening a second connection: the folder
         // tree is cached per account for the lifetime of the process, so a second
         // connection would be answered out of the same in-memory objects the CREATE had
         // just updated and would prove nothing about the database. This is what makes
         // the test cover the hm_imapfolders.folderspecialuse column and its four
         // upgrade scripts.
         DropFolderCache();

         simulator = LogonAs(address);
         var afterReload = simulator.SendSingleCommand("A02 LIST \"\" \"*\"");

         // Fails against the unfixed server: nothing was stored, so nothing could come
         // back once the cache had been dropped.
         StringAssert.Contains("\\Sent", RequireListLine(afterReload, "Enviados"));

         Assert.IsTrue(simulator.RenameFolder("Enviados", "Enviados nuevos"));

         var afterRename = simulator.SendSingleCommand("A03 LIST \"\" \"*\"");
         simulator.Disconnect();

         // The designation belongs to the folder, not to its name, so a rename has to
         // carry it across. Had it been keyed by name - or held in a settings row keyed
         // by path - the user would silently lose their sent folder the first time they
         // renamed it. RENAME saves the folder through the whole-row save, which is also
         // why that save must not write the designation column: a stale cached copy
         // would clear it here.
         StringAssert.Contains("\\Sent", RequireListLine(afterRename, "Enviados nuevos"));
         Assert.AreEqual(1, CountOccurrences(afterRename, "\\Sent"), afterRename);
      }

      [Test]
      [Description("CREATE ... USE on a mailbox that already exists designates it instead of refusing")]
      public void CreateWithUseDesignatesAnExistingFolder()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "su17@example.test", "test");
         var address = account.Address;

         var simulator = LogonAs(address);

         // The folder the user has had for years, full of mail, named in Spanish.
         Assert.IsTrue(simulator.CreateFolder("Enviados"));

         var designateResult = simulator.SendSingleCommand("A01 CREATE \"Enviados\" (USE (\\Sent))");
         Assert.IsTrue(designateResult.StartsWith("A01 OK"), designateResult);

         // Sending it twice must be harmless: a client that lost its connection mid-way
         // retries, and two of the user's devices may both decide to designate the same
         // folder.
         var repeatResult = simulator.SendSingleCommand("A02 CREATE \"Enviados\" (USE (\\Sent))");
         Assert.IsTrue(repeatResult.StartsWith("A02 OK"), repeatResult);

         simulator.Disconnect();

         DropFolderCache();

         // Fails against the unfixed server and against the reviewed attempt, both of
         // which answered "NO Folder already exists." - which leaves the client with
         // only one way to get a designated sent folder, namely creating a second one
         // beside this one and splitting the user's outgoing mail across the two. RFC
         // 6154 defines no way to designate an existing mailbox at all, so this is a
         // deliberate extension; see the comment in IMAPCommandCREATE::ExecuteCommand.
         Assert.AreEqual(1, CountTopLevelFoldersNamed(address, "Enviados"),
            "Designating an existing folder must not create a second row for it.");

         simulator = LogonAs(address);
         var response = simulator.SendSingleCommand("A03 LIST \"\" \"*\"");
         simulator.Disconnect();

         Assert.AreEqual(1, CountListLinesFor(response, "Enviados"), response);
         StringAssert.Contains("\\Sent", RequireListLine(response, "Enviados"));
      }

      [Test]
      [Description("Designating an existing folder adds to its designations rather than replacing them")]
      public void DesignatingAnExistingFolderKeepsItsOtherDesignations()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "su18@example.test", "test");

         var simulator = LogonAs(account.Address);

         var createResult = simulator.SendSingleCommand("A01 CREATE \"Todo\" (USE (\\Archive))");
         Assert.IsTrue(createResult.StartsWith("A01 OK"), createResult);

         var designateResult = simulator.SendSingleCommand("A02 CREATE \"Todo\" (USE (\\All))");
         Assert.IsTrue(designateResult.StartsWith("A02 OK"), designateResult);

         var response = simulator.SendSingleCommand("A03 LIST \"\" \"*\"");
         simulator.Disconnect();

         // The requested attributes are added, not assigned. A client saying "this is
         // the all-mail folder" has said nothing about whether it is also the archive,
         // and silently dropping the \Archive another client set would send that client
         // off to create its own archive folder - the duplicate this whole extension
         // exists to prevent.
         var line = RequireListLine(response, "Todo");
         StringAssert.Contains("\\All", line);
         StringAssert.Contains("\\Archive", line);
      }

      [Test]
      [Description("Designating an existing folder is refused when another folder owns the attribute")]
      public void DesignatingAnExistingFolderIsRefusedOnConflict()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "su19@example.test", "test");

         var simulator = LogonAs(account.Address);

         var firstResult = simulator.SendSingleCommand("A01 CREATE \"Enviados\" (USE (\\Sent))");
         Assert.IsTrue(firstResult.StartsWith("A01 OK"), firstResult);

         Assert.IsTrue(simulator.CreateFolder("Gesendet"));

         var conflictResult = simulator.SendSingleCommand("A02 CREATE \"Gesendet\" (USE (\\Sent))");

         var response = simulator.SendSingleCommand("A03 LIST \"\" \"*\"");
         simulator.Disconnect();

         // The existing-folder path has to run the same one-owner-per-attribute check as
         // the create path, and it has to exclude the folder being designated from that
         // check or re-designating a folder would conflict with itself. This test is the
         // one that fails if the exclusion is written the other way round.
         Assert.IsTrue(conflictResult.StartsWith("A02 NO"), conflictResult);
         StringAssert.Contains("[USEATTR]", conflictResult);

         Assert.AreEqual(1, CountOccurrences(response, "\\Sent"), response);
         StringAssert.Contains("\\Sent", RequireListLine(response, "Enviados"));
         Assert.IsFalse(RequireListLine(response, "Gesendet").Contains("\\Sent"), response);
      }

      [Test]
      [Description("A plain CREATE on an existing mailbox is still refused")]
      public void PlainCreateOnAnExistingFolderIsStillRefused()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "su20@example.test", "test");

         var simulator = LogonAs(account.Address);

         Assert.IsTrue(simulator.CreateFolder("Enviados"));

         var secondResult = simulator.SendSingleCommand("A01 CREATE \"Enviados\"");
         simulator.Disconnect();

         // The guard on the deviation above. RFC 3501 requires NO when the mailbox
         // already exists, every client relies on it to decide whether it needs to
         // create a folder, and only the presence of a USE parameter is allowed to
         // change the answer.
         Assert.IsTrue(secondResult.StartsWith("A01 NO"), secondResult);
         StringAssert.Contains("already exists", secondResult);
      }

      [Test]
      [Description("A shared public folder cannot be given a special use")]
      public void PublicFolderCannotBeGivenASpecialUse()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "su15@example.test", "test");

         var publicFolders = _settings.PublicFolders;
         var sharedFolder = publicFolders.Add("Share1");
         sharedFolder.Save();

         var permission = sharedFolder.Permissions.Add();
         permission.PermissionAccountID = account.ID;
         permission.PermissionType = eACLPermissionType.ePermissionTypeUser;
         permission.set_Permission(eACLPermission.ePermissionLookup, true);
         permission.set_Permission(eACLPermission.ePermissionCreate, true);
         permission.Save();

         var simulator = LogonAs(account.Address);

         // Create permission is granted, so the only thing left to refuse the command
         // for is the USE parameter itself.
         var createResult = simulator.SendSingleCommand("A01 CREATE \"#Public.Share1.Sent\" (USE (\\Sent))");

         var response = simulator.SendSingleCommand("A02 LIST \"\" \"*\"");
         simulator.Disconnect();

         // Fails against the unfixed server, which answered BAD (it rejected the
         // parameter, not the request). A special use describes one user's mailbox: a
         // public folder marked \Trash would be advertised as the deletion target to
         // every account that can see it, read-only ones included.
         Assert.IsTrue(createResult.StartsWith("A01 NO"), createResult);
         StringAssert.Contains("[USEATTR]", createResult);
         Assert.IsNull(ListLineFor(response, "#Public.Share1.Sent"), response);
      }

      [Test]
      [Description("An existing public folder cannot be given a special use either")]
      public void ExistingPublicFolderCannotBeGivenASpecialUse()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "su21@example.test", "test");

         var publicFolders = _settings.PublicFolders;
         var sharedFolder = publicFolders.Add("Share1");
         sharedFolder.Save();

         var permission = sharedFolder.Permissions.Add();
         permission.PermissionAccountID = account.ID;
         permission.PermissionType = eACLPermissionType.ePermissionTypeUser;
         permission.set_Permission(eACLPermission.ePermissionLookup, true);
         permission.set_Permission(eACLPermission.ePermissionCreate, true);
         permission.Save();

         var simulator = LogonAs(account.Address);

         var createResult = simulator.SendSingleCommand("A01 CREATE \"#Public.Share1\" (USE (\\Trash))");

         var response = simulator.SendSingleCommand("A02 LIST \"\" \"*\"");
         simulator.Disconnect();

         // The designate-an-existing-folder path reaches a different refusal from the
         // create path - it asks the folder object whether it is public rather than
         // re-parsing the path - so it needs its own test. A shared \Trash would be
         // advertised to every account that can see the folder as the place to move
         // deletions to, and then emptied by whichever of them acts first.
         Assert.IsTrue(createResult.StartsWith("A01 NO"), createResult);
         StringAssert.Contains("[USEATTR]", createResult);
         Assert.IsFalse(response.Contains("\\Trash"), response);
      }
   }
}

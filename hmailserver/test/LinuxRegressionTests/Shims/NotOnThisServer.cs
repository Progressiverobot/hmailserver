// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Collections.Generic;
using NUnit.Framework;

namespace RegressionTests.Shared
{
   /// <summary>
   ///    The one way a shim says "this fixture reaches something the REST API cannot
   ///    do": Assert.Ignore with a reason that names the missing surface, so that the
   ///    test counts as skipped with that reason and never as passed. NUnit turns an
   ///    IgnoreException from a test body, a SetUp or a OneTimeSetUp into an ignored
   ///    test (or fixture) rather than a failure, which is exactly the distinction
   ///    this project's result must keep: a failure here is the server's.
   ///
   ///    The reasons are collected in one place so that a run's skip list reads as a
   ///    list of API gaps, and so that when one of them closes there is one string to
   ///    delete.
   /// </summary>
   public static class NotOnThisServer
   {
      public static void Ignore(string reason)
      {
         Assert.Ignore("Not runnable against this server: " + reason);
      }

      public const string NoSettingsWrite =
         "needs a server setting written, and this server's REST API has no PUT /api/v1/settings (it arrived with wave 162)";

      public const string NoRouteCreate =
         "needs an SMTP route, and this server's REST API has no POST /api/v1/routes (it arrived with wave 162)";

      public const string NoAliasCreate =
         "needs an alias, and this server's REST API has no POST /api/v1/domains/{domain}/aliases (it arrived with wave 162)";

      public const string NoDomainCreate =
         "needs a domain created, and this server's REST API has no POST /api/v1/domains";

      public const string NoTlsListener =
         "needs TLS listeners, which the REST API cannot add and this server has none of";

      public const string NoAccountMaxSize =
         "needs an account with a maximum size, which POST /api/v1/domains/{domain}/accounts does not set";

      public const string NoSieveEvaluate =
         "needs Utilities.EvaluateSieveScript, a COM-only call with no REST equivalent";

      public const string NoMailServerLookup =
         "needs Utilities.GetMailServer, a COM-only call with no REST equivalent";

      // ---- What wave 164's survey found the REST API has no route for ----

      public const string NoServerDirectories =
         "reads Settings.Directories - the server's program, data or event directory - and no REST route reports them: GET /api/v1/settings carries no directories and GET /api/v1/settings/logging carries only the log directory";

      public const string NoIniSettings =
         "reads or writes hMailServer.ini through Settings.GetIniSetting/SetIniSetting/DeleteIniSetting, and no REST route reaches the INI";

      public const string NoLogonFailureList =
         "needs Settings.ClearLogonFailureList, and no REST route clears the server's logon-failure list";

      public const string NoAdministratorPassword =
         "needs Settings.SetAdministratorPassword, and no REST route sets the administrator password";

      public const string NoRelayerPassword =
         "needs Settings.SetSMTPRelayerPassword, and PUT /api/v1/settings does not take the relayer password";

      public const string NoScripting =
         "needs the server's event-handler scripting (Settings.Scripting), which no REST route configures, reloads or checks";

      public const string NoCrashSimulation =
         "needs Settings.CrashSimulationMode, the COM-only switch that makes the server fault on purpose";

      public const string NoAdministratorTotp =
         "enrols or disables the administrator's second factor, which no REST route does";

      public const string NoAccountTotp =
         "enrols or reads an account's second factor, which no REST route does";

      public const string NoDirectorySync =
         "needs Settings.TestLdapDirectory / PreviewDirectorySync / ApplyDirectorySync, which no REST route offers";

      public const string NoUserInterfaceLanguage =
         "reads Settings.UserInterfaceLanguage, which no REST route reports";

      public const string NoLiveLog =
         "needs the live log, a COM callback the server pushes to the Control Panel, which HTTP has no equivalent of";

      public const string NoLogDeviceWrite =
         "sets Logging.Device, and PUT /api/v1/settings/logging does not take the log device";

      public const string NoBackupSettings =
         "needs the backup settings written (Settings.Backup), and the REST API only reports a backup through GET /api/v1/backup";

      public const string NoCacheControl =
         "needs Settings.Cache - the server's domain and account caches - which no REST route reads or clears";

      public const string NoAntiVirusSettings =
         "needs Settings.AntiVirus, and no REST route carries the anti-virus settings or the blocked-attachment list";

      public const string NoGroups =
         "needs Settings.Groups, and no REST route carries the account groups";

      public const string NoServerMessages =
         "needs Settings.ServerMessages, and no REST route carries the server's message texts";

      public const string NoMessageIndexing =
         "needs Settings.MessageIndexing, and no REST route switches indexing on or runs it";

      public const string NoIncomingRelays =
         "needs Settings.IncomingRelays, and no REST route carries the incoming relays";

      public const string NoGreylistTriplets =
         "needs AntiSpam.ClearGreyListingTriplets, and no REST route clears them";

      public const string NoSpamAssassinProbe =
         "needs AntiSpam.TestSpamAssassinConnection, and no REST route probes SpamAssassin";

      public const string NoDkimVerifyCall =
         "needs AntiSpam.DKIMVerify, a COM-only call that verifies a raw message";

      public const string NoBlacklistCollections =
         "needs the anti-spam address lists (DNS blacklists, SURBL servers, blocked senders, white list, greylisting white list), and no REST route carries any of them";

      public const string NoQuarantineObject =
         "needs AntiSpam.Quarantine as a COM collection; the REST API has GET /api/v1/quarantine and a release, but nothing that stands for the object";

      public const string NoDomainIds =
         "needs a domain's database id, and the REST API addresses a domain by name only";

      public const string NoAccountIds =
         "needs an account's database id, and the REST API addresses an account by address only";

      public const string NoAliasIds =
         "needs an alias's database id, and the REST API addresses an alias by name only";

      public const string NoListIds =
         "needs a distribution list's database id, and the REST API addresses a list by address only";

      public const string NoDomainUpdate =
         "changes a domain, and this server's REST API has no PUT /api/v1/domains/{domain}";

      public const string NoSecondRouteForADomain =
         "adds a second SMTP route for a domain that already has one, which POST /api/v1/routes refuses (\"a route for that domain name already exists\") where COM allows it";

      public const string WindowsPathInTheFixture =
         "names a file with a backslash in its path, which is a directory separator only on Windows";

      public const string NoDomainRename =
         "renames a domain, and PUT /api/v1/domains/{domain} takes active and postmaster only - the route says of itself that the name cannot be changed there";

      public const string NoAccountUpdate =
         "changes an account, and this server's REST API has no PUT /api/v1/accounts/{address}";

      public const string NoAliasUpdate =
         "changes an existing alias, and the REST API has POST and DELETE for aliases but no PUT";

      public const string NoListObject =
         "needs a distribution list as an object - its mode, its authentication requirement or its recipients - and the REST API creates and deletes a list but carries none of those";

      public const string NoDomainAliases =
         "needs a domain alias, and no REST route lists, creates or deletes one";

      public const string NoPortCreate =
         "needs a TCP/IP port, and this server's REST API has no POST /api/v1/ports";

      public const string NoPortDefaults =
         "needs TCPIPPorts.SetDefault - every listener replaced by the installer's three, on ports 25, 110 and 143 - which no REST route does, and which would move this server onto privileged ports";

      public const string NoIpRangeCreate =
         "needs an IP range, and this server's REST API has no POST /api/v1/ipranges";

      public const string NoIpRangeUpdate =
         "changes an IP range that already exists, and the REST API has GET, POST and DELETE for /api/v1/ipranges but no PUT";

      public const string NoIpRangeDefaults =
         "needs SecurityRanges.SetDefault, which no REST route does";

      public const string NoCertificateCreate =
         "needs an SSL certificate, and this server's REST API has no POST /api/v1/certificates";

      public const string NoCertificateUpdate =
         "changes a certificate that already exists, and the REST API has POST and DELETE for certificates but no PUT";

      public const string NoRouteAddresses =
         "needs a route's address list as an object, and the REST API takes the addresses only in the body that creates or replaces the route";

      public const string NoRuleCreate =
         "needs a global rule, and this server's REST API has no POST /api/v1/rules";

      public const string NoAccountRules =
         "needs an account's own rules, and /api/v1/rules carries the global rules only";

      public const string NoFolderWrite =
         "needs an IMAP folder created or deleted through the account object, and the REST API has GET /api/v1/me/folders and nothing that writes a folder";

      public const string NoFetchAccounts =
         "needs external (fetch) accounts, and no REST route carries them";

      public const string NoAppPasswords =
         "needs an account's application passwords, and no REST route carries them";

      public const string NoDirectoryLink =
         "needs an account linked to a directory (IsAD, ADUsername, ADDomain), and PUT /api/v1/accounts/{address} does not take them";

      public const string NoAccountLastLogon =
         "reads Account.LastLogonTime, which no REST route reports";

      public const string NoDeleteMessages =
         "needs Account.DeleteMessages, and the REST API deletes a message at a time through /api/v1/me/messages/{id}";

      public const string NoExportMessages =
         "needs Account.ExportMessages, which writes files on the server and has no REST route";

      public const string NoReinitialize =
         "needs the server reinitialised, and this server's REST API has no POST /api/v1/server/reinitialize";

      public const string NoServiceControl =
         "starts or stops the server itself, which the REST API cannot do - it is served by the process being stopped";

      public const string NoComAuthenticate =
         "calls Application.Authenticate, the COM logon, which HTTP Basic on every request stands for here";

      public const string NoDatabaseObject =
         "reads Application.Database - the database type, the schema version, a query - and no REST route reports any of it";

      public const string NoDiagnostics =
         "needs Application.Diagnostics, the COM-only self-test, which no REST route runs";

      public const string NoClientSessionCount =
         "counts the server's own outbound sessions, and GET /api/v1/status reports only the SMTP, IMAP and POP3 server session counts";

      public const string NoUpdateObject =
         "drives the updater through Status.CheckForUpdate / DownloadUpdate, and the REST update routes are a different shape (GET /api/v1/update and POST /api/v1/update/check)";

      public const string NoFolderAcl =
         "needs an IMAP folder's access-control list, and no REST route reads or writes one";

      public const string NoServerRestart =
         "restarts the server process, which it does because the server reads hMailServer.ini only at start; POST /api/v1/server/reinitialize restarts the services inside the same process and does not re-read the INI, so it is not the same thing";

      public const string NoMessageObject =
         "needs a message as a COM object - its file on disk, its headers, its recipients - and the REST message routes serve an account's own messages only, without those";

      public const string NoServerStartTime =
         "reads Status.StartTime, which GET /api/v1/status does not report";

      public const string NoExternalScanner =
         "needs an external scanner running beside the server, which the Windows suite starts as a Windows service and this environment does not provide";

      public const string NoUtilityCall =
         "needs a COM-only Utilities call, which no REST route offers";

      public static string NoSettingsKey(string key, string group)
      {
         return "needs the setting " + key + ", which /api/v1/settings" +
                (group == "server" ? string.Empty : "/" + group) + " does not carry";
      }

      public const string NoStatusThreadId =
         "reads Status.ThreadID, the COM server's thread id, which GET /api/v1/status does not report";

      public const string NoSuiteDns =
         "needs the suite's fake DNS zone served to the server's resolver, which this environment does not wire up";

      public const string ComWordingAsserted =
         "asserts the COM wording of a refusal that POST /api/v1/domains/{domain}/accounts makes in its own words before the server's own check runs";

      public const string NoEmptyPassword =
         "creates an account with an empty password, which POST /api/v1/domains/{domain}/accounts refuses (a password is required)";

      /// <summary>
      ///    The fixtures and tests the shims cannot stop at the point of use, ignored
      ///    from TestFixtureBase.SetUp before they start, with the reason:
      ///
      ///      - the Sieve fixtures reach Utilities.EvaluateSieveScript late-bound, and
      ///        an IgnoreException thrown inside Type.InvokeMember comes back wrapped
      ///        in a TargetInvocationException, which NUnit counts as a failure;
      ///      - the tests that need an SMTP route start a simulated remote server
      ///        before asking for the route, so the Ignore at the route would leave a
      ///        listener to dispose with an accept pending;
      ///      - the persistence tests that assert the COM wording of a refusal get the
      ///        REST route's wording first, and the empty-password test is refused at
      ///        creation.
      ///
      ///    Keyed by the fixture's full class name, or by class name and method name
      ///    joined with a dot.
      /// </summary>
      private static readonly Dictionary<string, string> Registry = new Dictionary<string, string>
      {
         { "RegressionTests.Sieve.SieveEvaluation", NoSieveEvaluate },
         { "RegressionTests.Sieve.SieveExtensionActions", NoSieveEvaluate },
         { "RegressionTests.Sieve.SieveVacation", NoSieveEvaluate },
         { "RegressionTests.SMTP.OutboundSize", NoRouteCreate },
         { "RegressionTests.SSL.SmtpDeliverySslTests", NoRouteCreate },
         { "RegressionTests.SMTP.BinaryMime.TestAuthenticatedBinarySubmissionIsRelayedViaBdat", NoRouteCreate },
         { "RegressionTests.SMTP.BinaryMime.TestAuthenticatedBinaryRelayToARemoteWithoutBinaryMimeBounces", NoRouteCreate },
         { "RegressionTests.SMTP.BinaryMime.TestDistributionListExternalMemberBouncesWith554_5_6_3", NoRouteCreate },
         { "RegressionTests.Infrastructure.Persistence.AccountNameValidation.TestAccountWithoutAddress", ComWordingAsserted },
         { "RegressionTests.Infrastructure.Persistence.AccountNameValidation.TestAccountBelongingToAnotherDomain", ComWordingAsserted },
         { "RegressionTests.Infrastructure.Persistence.AccountNameValidation.TestAccountContainingBackwardSlashInDomainName", ComWordingAsserted },
         { "RegressionTests.Infrastructure.Persistence.AccountNameValidation.TestAccountContainingForwardSlashInDomainName", ComWordingAsserted },
         { "RegressionTests.Security.Basics.TestEmptyPassword", NoEmptyPassword },
         { "RegressionTests.Sieve.SieveSyntax.DeeplyNestedBlocksAreRefusedRatherThanRecursedInto", ScriptOverRequestCeiling },
         { "RegressionTests.API.Unicode.TestDecodeSpecificMessage", BareLfFromNewLine },
         { "RegressionTests.IMAP.Fetch.IfInReplyToFieldContainsQuoteThenFetchHeadersShouldEncodeIt", BareLfFromNewLine },
         { "RegressionTests.IMAP.Fetch.PartialFetch_HeaderFields", BareLfFromNewLine },
         { "RegressionTests.IMAP.Fetch.PartialFetch_HeaderFieldsNot", BareLfFromNewLine },
         { "RegressionTests.IMAP.Fetch.RequestingSameHeaderFieldMultipleTimesShouldReturnItOnce", BareLfFromNewLine },
         { "RegressionTests.IMAP.Fetch.TestFetchEnvelopeWithDateContainingQuote", BareLfFromNewLine },
         { "RegressionTests.IMAP.Fetch.TestFetchHeaderFields", BareLfFromNewLine },
         { "RegressionTests.IMAP.Fetch.TestFetchHeaderFieldsNot", BareLfFromNewLine },
         { "RegressionTests.SSL.CertificateTypes.SetupSSLCertificateWithPassword", WindowsPathInTheFixture },
         { "RegressionTests.IMAP.SequenceSets.UidExpungeStarAffectsOnlyTheLastMessage", DeletedMessagesAreNotListed },
         { "RegressionTests.Infrastructure.Persistence.DomainNameValidation.TestDomainWithoutName", ComWordingAsserted },
         { "RegressionTests.POP3.Basics.TestAuthPlainSaslPrepNfkcUsername", NoUnicodeNormalisation },
         // Its calls are inside a try/catch that expects a COMException, so the
         // Ignore the shim raises is caught and reported as a failure. Like
         // SqlLayerErrorHandling, it has to be stopped before it starts.
         { "RegressionTests.Infrastructure.IniSettingsOverCom.UnstorableNamesAndValuesAreRefused", NoIniSettings },
         // Every test in it runs a statement through Application.Database and reads
         // what came back; the Ignore the shim raises for that is caught by the
         // fixture's own try/catch and reported as a failure, so the fixture is
         // stopped before it starts instead.
         { "RegressionTests.Infrastructure.SqlLayerErrorHandling", NoDatabaseObject }
      };

      /// <summary>
      ///    COM's IMAPFolder.Messages is every message in the folder. The REST
      ///    listing the shim reads in its place is every message a reader should
      ///    see, which is not the same collection: a message flagged \Deleted and
      ///    not yet expunged is in the first and deliberately not in the second,
      ///    because that route exists to draw a mailbox in a browser and a webmail
      ///    that showed deleted mail would be wrong. So a test that flags messages
      ///    \Deleted and then counts the folder is asking a question no route here
      ///    answers, and it is skipped rather than failed: the difference is a
      ///    decision, not a defect, and weakening the assertion would hide the day
      ///    it becomes one.
      /// </summary>
      /// <summary>
      ///    RFC 4013 SASLprep normalises a credential with Unicode NFKC before
      ///    comparing it, so a fullwidth letter and its ASCII spelling are the
      ///    same user name. On Windows that step is NormalizeString from
      ///    Normaliz.lib. There is no equivalent in the C library and no
      ///    character database to write one from, so this build refuses a
      ///    credential containing anything but ASCII rather than comparing it
      ///    unnormalised - and says so in the log, naming the roadmap row. The
      ///    refusal is the designed behaviour; a test that asserts the folding
      ///    is asserting a capability this platform does not have yet.
      /// </summary>
      public const string NoUnicodeNormalisation =
         "needs Unicode NFKC for SASLprep, which this build does not have on Linux - the server deliberately refuses a non-ASCII credential instead of comparing it unnormalised; see the roadmap's \"Linux and AArch64\" section";

      public const string DeletedMessagesAreNotListed =
         "counts a folder's messages after flagging some \\Deleted, and the REST listing this shim reads in place of COM's collection deliberately omits deleted messages - no route answers COM's question";

      /// <summary>
      ///    The test is right where it was written and cannot run anywhere else: it
      ///    builds the message it sends with Environment.NewLine, which is CRLF on
      ///    Windows and a bare LF on this host - and a bare LF is not a line to an
      ///    SMTP server, which answers "554 Rejected - Message containing bare LF's"
      ///    and is right to. The server is not at fault and the assertion is not
      ///    weakened; the message the fixture composes is simply not a message here.
      ///    TestSetup.CreateLargeDummyMailBody shows the fix - spell out \r\n - which
      ///    is a change to the Windows suite rather than to this project.
      /// </summary>
      public const string BareLfFromNewLine =
         "builds the message it sends with Environment.NewLine, which is a bare LF on this host; the server rightly refuses it with 554, so the test can only run where Environment.NewLine is CRLF";

      public const string ScriptOverRequestCeiling =
         "checks a 70 KB script, which is over the REST API's request ceiling - PUT /api/v1/me/filters answers 413 request too large before the parser sees it, and the COM CheckSieveSyntax has no ceiling";

      /// <summary>
      ///    Tests that are right on a Windows test host and wrong on any other, because
      ///    they build a message with AppendLine - CRLF on Windows, a bare LF elsewhere,
      ///    which the server rightly refuses with 554. The server is not at fault and
      ///    the test is not wrong where it was written, so it is skipped with the reason
      ///    only when the host is not Windows.
      /// </summary>

      // A registered reason that names a route skips only on a server without
      // that route; the registry is written for the oldest server the project
      // runs against, and the newest answers it.
      private static bool StillApplies(string reason)
      {
         if (reason == NoRouteCreate)
            return !ServerApi.HasRouteWriteRoutes;
         if (reason == NoAliasCreate)
            return !ServerApi.HasAliasWriteRoutes;
         if (reason == NoSettingsWrite)
            return !ServerApi.HasSettingsWriteRoutes;
         return true;
      }

      public static void SkipIfRegistered()
      {
         var test = TestContext.CurrentContext.Test;
         string reason;

         if (test.ClassName != null && Registry.TryGetValue(test.ClassName, out reason) && StillApplies(reason))
            Ignore(reason);

         if (test.ClassName != null && test.MethodName != null && Registry.TryGetValue(test.ClassName + "." + test.MethodName, out reason) && StillApplies(reason))
            Ignore(reason);

      }
   }
}

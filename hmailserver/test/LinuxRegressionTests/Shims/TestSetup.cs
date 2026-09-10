// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Text.Json;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using hMailServer;
using NUnit.Framework;
using RegressionTests.Infrastructure;

namespace RegressionTests.Shared
{
   /// <summary>
   ///    The fixture layer, REST-backed. The Windows TestSetup authenticates over COM,
   ///    resets some forty settings before every test, deletes and recreates the test
   ///    domain, and makes accounts and aliases through COM objects. This one does what
   ///    the REST API can do of that and says, per member, what it cannot:
   ///
   ///      - the settings reset is not done: the API writes no settings, so a run here
   ///        depends on the server being configured as a fresh --create-database
   ///        leaves it, which is what the Windows reset puts back anyway;
   ///      - the test domain is example.test, deleted and recreated before every test
   ///        where the server has the domain write routes, and otherwise emptied of
   ///        its accounts and lists, which removes their messages with them;
   ///      - accounts and distribution lists go through their routes; aliases, routes
   ///        and an account's maximum size have none and end in Assert.Ignore.
   ///
   ///    Everything a test makes is remembered and removed by TearDown, so a failed
   ///    test leaves nothing for the next.
   /// </summary>
   public class TestSetup
   {
      public const string TestDomainName = "example.test";

      private static int _freePort = 20000;

      private readonly Application _application = new Application();
      private readonly List<string> _accountsMade = new List<string>();
      private readonly List<string> _listsMade = new List<string>();
      private readonly List<string> _domainsMade = new List<string>();

      public Domain TestDomain { get; } = new Domain { Name = TestDomainName, Active = true };

      public void Authenticate()
      {
         var answer = ServerApi.Get("/api/v1/status");

         if (answer.Status == 401 || answer.Status == 403)
            Assert.Fail("hMailServer API authentication failed at " + TestTarget.RestBaseUrl +
                        ": the administrator password is not the one HMTEST_ADMIN_PASSWORD says.");

         answer.Expect(200, "GET /api/v1/status");
      }

      public Application GetApp()
      {
         return _application;
      }

      /// <summary>
      ///    What runs before every test. The delivery queue is drained first, for the
      ///    reason the Windows base class gives at length: a message still queued when
      ///    its recipient is deleted becomes an HM5165 in the next test's log.
      /// </summary>
      public Domain PerformBasicSetup()
      {
         DeleteMessagesInQueue();
         RemoveAllRoutes();
         RestoreServerRows();
         ResetSettings();

         var domain = AddTestDomain();

         CustomAsserts.AssertRecipientsInDeliveryQueue(0);

         return domain;
      }

      /// <summary>
      ///    The test domain, empty. With the domain write routes this is the Windows
      ///    ClearDomains-then-AddDomain, which takes every account, alias, list and
      ///    directory with it. Without them the domain has to be there already -
      ///    docs/RegressionEnvironment.md says how it is provisioned - and what the API
      ///    can delete inside it is deleted.
      /// </summary>
      public Domain AddTestDomain()
      {
         _accountsMade.Clear();
         _listsMade.Clear();

         if (ServerApi.HasDomainWriteRoutes)
         {
            foreach (var domain in ServerApi.Array(ServerApi.Get("/api/v1/domains").Expect(200, "GET /api/v1/domains")))
            {
               var name = ServerApi.StringOf(domain, "name");
               ServerApi.Delete("/api/v1/domains/" + name).Expect(200, "DELETE /api/v1/domains/" + name);
            }

            _domainsMade.Clear();
            return AddDomain(TestDomainName);
         }

         var present = false;
         foreach (var domain in ServerApi.Array(ServerApi.Get("/api/v1/domains").Expect(200, "GET /api/v1/domains")))
            if (string.Equals(ServerApi.StringOf(domain, "name"), TestDomainName, StringComparison.OrdinalIgnoreCase))
               present = true;

         if (!present)
            Assert.Fail("The test domain " + TestDomainName + " does not exist on " + TestTarget.Describe() +
                        ", and this server's REST API has no route that creates one. Provision it as " +
                        "docs/RegressionEnvironment.md (\"On Linux\") describes, then run again.");

         EmptyTheTestDomain();
         return TestDomain;
      }

      private void EmptyTheTestDomain()
      {
         foreach (var account in ServerApi.Array(ServerApi.Get("/api/v1/domains/" + TestDomainName + "/accounts")
                     .Expect(200, "listing the accounts of " + TestDomainName)))
         {
            var address = ServerApi.StringOf(account, "address");
            ServerApi.Delete("/api/v1/accounts/" + address).Expect(200, "DELETE /api/v1/accounts/" + address);
         }

         foreach (var list in ServerApi.Array(ServerApi.Get("/api/v1/domains/" + TestDomainName + "/lists")
                     .Expect(200, "listing the distribution lists of " + TestDomainName)))
         {
            var address = ServerApi.StringOf(list, "address");
            ServerApi.Delete("/api/v1/lists/" + address).Expect(200, "DELETE /api/v1/lists/" + address);
         }
      }

      /// <summary>
      ///    Removes what the test made, in the order that leaves nothing behind: the
      ///    queue first (a queued message names the accounts), then accounts and lists,
      ///    then any extra domain. Called from TearDown; a failure here is reported and
      ///    does not replace the test's own result.
      /// </summary>
      public void RemoveWhatTheTestMade()
      {
         DeleteMessagesInQueue();

         foreach (var address in _accountsMade.ToArray())
         {
            var answer = ServerApi.Delete("/api/v1/accounts/" + address);
            if (answer.Status != 200 && answer.Status != 404)
               Console.WriteLine("Could not delete " + address + " after the test: " + answer.Status + " " + answer.Body);
         }

         _accountsMade.Clear();

         foreach (var address in _listsMade.ToArray())
         {
            var answer = ServerApi.Delete("/api/v1/lists/" + address);
            if (answer.Status != 200 && answer.Status != 404)
               Console.WriteLine("Could not delete the list " + address + " after the test: " + answer.Status + " " + answer.Body);
         }

         _listsMade.Clear();

         foreach (var name in _domainsMade.ToArray())
         {
            if (string.Equals(name, TestDomainName, StringComparison.OrdinalIgnoreCase))
               continue;

            var answer = ServerApi.Delete("/api/v1/domains/" + name);
            if (answer.Status != 200 && answer.Status != 404)
               Console.WriteLine("Could not delete the domain " + name + " after the test: " + answer.Status + " " + answer.Body);
         }

         _domainsMade.Clear();
      }

      public bool HasAccount(string address)
      {
         return _accountsMade.Contains(address.ToLowerInvariant());
      }

      /// <summary>
      ///    The passwords of the accounts this run made, by address.
      ///
      ///    The /api/v1/me routes authenticate as the account, so a shim that reads an
      ///    account's folders or messages needs its password - and a fixture very often
      ///    looks an account up again through the collection (Accounts.get_ItemByAddress)
      ///    rather than keeping the object it made. The listing carries no password, and
      ///    nothing should invent one, so the account this run created is matched by
      ///    address to the password it was created with. An account the run did not make
      ///    has no password here, and the member that needs one skips saying so.
      /// </summary>
      private static readonly Dictionary<string, string> PasswordsByAddress =
         new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

      internal static void RememberPassword(string address, string password)
      {
         lock (PasswordsByAddress)
            PasswordsByAddress[address] = password;
      }

      internal static string PasswordFor(string address)
      {
         lock (PasswordsByAddress)
         {
            string password;
            return address != null && PasswordsByAddress.TryGetValue(address, out password) ? password : null;
         }
      }

      public Account AddAccount(Domain domain, string address, string password)
      {
         return CreateAccount_(domain, address, password, 0);
      }
      private Account CreateAccount_(Domain domain, string address, string password, int maxSize)
      {
         var answer = ServerApi.Post("/api/v1/domains/" + domain.Name + "/accounts",
            "{\"address\":" + ServerApi.Quote(address) + ",\"password\":" + ServerApi.Quote(password) +
            (maxSize != 0 ? ",\"max_size_mb\":" + maxSize : "") + "}");

         if (answer.Status == 201)
         {
            _accountsMade.Add(address.ToLowerInvariant());
            RememberPassword(address, password);
            return new Account().Seed(address, password, true, domain.Name);
         }

         if (answer.Status == 400)
         {
            // What the COM Save raises for the same refusal: InterfaceAccount::Save
            // prefixes the server's own sentence with this, and the persistence
            // fixtures assert on the whole. The route makes two refusals of its own
            // before the server's check runs (a missing field, an address outside
            // the domain), in its own words; the tests that assert the COM wording
            // for those are in NotOnThisServer's registry.
            throw new COMException("Failed to save object. " + answer.Error);
         }

         if (answer.Status == 409)
            throw new COMException("Failed to save object. The account address is already in use.");

         answer.Expect(201, "POST /api/v1/domains/" + domain.Name + "/accounts");
         return null;
      }

      public Account AddAccount(Domain domain, string address, string password, int maxSize)
      {
         // The create route reads max_size_mb since the server gained PUT
         // /api/v1/accounts/{address} (wave 162); before that it advertised
         // the field and ignored it, which a test must not mistake for a limit.
         if (maxSize != 0 && !ServerApi.HasAccountUpdateRoute)
            NotOnThisServer.Ignore(NotOnThisServer.NoAccountMaxSize);

         return CreateAccount_(domain, address, password, maxSize);
      }

      public Alias AddAlias(Domain domain, string name, string value)
      {
         if (!ServerApi.HasAliasWriteRoutes)
            NotOnThisServer.Ignore(NotOnThisServer.NoAliasCreate);
         var answer = ServerApi.Post("/api/v1/domains/" + domain.Name + "/aliases",
            "{\"name\":" + ServerApi.Quote(name) + ",\"value\":" + ServerApi.Quote(value) + ",\"active\":true}");
         if (answer.Status == 400)
            throw new COMException("Failed to save object. " + answer.Error);
         if (answer.Status == 409)
            throw new COMException("Failed to save object. The alias address is already in use.");
         answer.Expect(201, "POST /api/v1/domains/" + domain.Name + "/aliases " + name);
         return new Alias { Name = name, Value = value, Active = true };
      }

      public Domain AddDomain(string name)
      {
         if (!ServerApi.HasDomainWriteRoutes)
            NotOnThisServer.Ignore(NotOnThisServer.NoDomainCreate);

         ServerApi.Post("/api/v1/domains", "{\"name\":" + ServerApi.Quote(name) + ",\"active\":true}")
            .Expect(201, "POST /api/v1/domains " + name);

         _domainsMade.Add(name);

         return string.Equals(name, TestDomainName, StringComparison.OrdinalIgnoreCase)
            ? TestDomain
            : new Domain { Name = name, Active = true };
      }

      public DistributionList AddDistributionList(Domain domain, string address, List<string> recipients)
      {
         var members = new StringBuilder("[");
         for (var i = 0; i < recipients.Count; i++)
         {
            if (i > 0)
               members.Append(',');
            members.Append(ServerApi.Quote(recipients[i]));
         }
         members.Append(']');

         ServerApi.Post("/api/v1/domains/" + domain.Name + "/lists",
               "{\"address\":" + ServerApi.Quote(address) + ",\"members\":" + members + "}")
            .Expect(201, "POST /api/v1/domains/" + domain.Name + "/lists " + address);

         _listsMade.Add(address);

         return new DistributionList { Address = address, Active = true };
      }

      internal static Route AddRoutePointingAtLocalhost(int numberOfTries, int port, bool treatSecurityAsLocal)
      {
         return AddRoutePointingAtLocalhost(numberOfTries, port, treatSecurityAsLocal, eConnectionSecurity.eCSNone);
      }

      internal static Route AddRoutePointingAtLocalhost(int numberOfTries, int port, bool treatSecurityAsLocal,
         eConnectionSecurity connectionSecurity)
      {
         // The route the Windows TestSetup adds through COM, field for field.
         if (!ServerApi.HasRouteWriteRoutes)
            NotOnThisServer.Ignore(NotOnThisServer.NoRouteCreate);
         var local = treatSecurityAsLocal ? "true" : "false";
         var answer = ServerApi.Post("/api/v1/routes",
            "{\"domain_name\":\"dummy-example.com\",\"target_smtp_host\":\"127.0.0.1\"," +
            "\"target_smtp_port\":" + port + ",\"number_of_tries\":" + numberOfTries + ",\"minutes_between_try\":5," +
            "\"treat_recipient_as_local_domain\":" + local + ",\"treat_security_as_local_domain\":" + local + "," +
            "\"connection_security\":" + ServerApi.Quote(ConnectionSecurityName(connectionSecurity)) + "}");

         // COM lets a second route be added for a domain that already has one; the
         // route refuses it in as many words. A test that asks for two is asking for
         // something this API does not do, and says so rather than quietly running
         // against the first route.
         if (answer.Status == 409)
            NotOnThisServer.Ignore(NotOnThisServer.NoSecondRouteForADomain);

         answer.Expect(201, "POST /api/v1/routes");
         return new Route { DomainName = "dummy-example.com" };
      }
      internal static string ConnectionSecurityName(eConnectionSecurity connectionSecurity)
      {
         switch (connectionSecurity)
         {
            case eConnectionSecurity.eCSTLS: return "tls";
            case eConnectionSecurity.eCSSTARTTLSOptional: return "starttls_optional";
            case eConnectionSecurity.eCSSTARTTLSRequired: return "starttls_required";
            default: return "none";
         }
      }
      // What the Windows PerformBasicSetup puts back through COM before every
      // test, over the three settings groups: read each group, write back only
      // the keys that differ, and write nothing at all when none does - which
      // is the ordinary case, so the cost is three reads. A server without the
      // routes keeps its settings and every test that needs one skips.
      private static readonly (string Group, string Key, string Json)[] SuiteDefaults =
      {
         ("/api/v1/settings", "verify_remote_ssl_certificate", "false"),
         ("/api/v1/settings", "auto_ban_on_logon_failure", "false"),
         ("/api/v1/settings", "smtp_no_of_tries", "0"),
         ("/api/v1/settings", "smtp_minutes_between_try", "60"),
         ("/api/v1/settings", "mirror_email_address", "\"\""),
         ("/api/v1/settings", "smtp_relayer", "\"\""),
         ("/api/v1/settings", "smtp_relayer_connection_security", "\"none\""),
         ("/api/v1/settings", "max_delivery_threads", "50"),
         ("/api/v1/settings", "imap_public_folder_name", "\"#Public\""),
         ("/api/v1/settings", "imap_hierarchy_delimiter", "\".\""),
         ("/api/v1/settings", "max_number_of_invalid_commands", "3"),
         ("/api/v1/settings", "disconnect_invalid_clients", "false"),
         ("/api/v1/settings", "max_smtp_recipients_in_batch", "100"),
         ("/api/v1/settings", "welcome_smtp", "\"\""),
         ("/api/v1/settings", "welcome_pop3", "\"\""),
         ("/api/v1/settings", "welcome_imap", "\"\""),
         ("/api/v1/settings/logging", "enabled", "true"),
         ("/api/v1/settings/logging", "log_application", "true"),
         ("/api/v1/settings/logging", "log_smtp", "true"),
         ("/api/v1/settings/logging", "log_pop3", "true"),
         ("/api/v1/settings/logging", "log_imap", "true"),
         ("/api/v1/settings/logging", "log_tcpip", "true"),
         ("/api/v1/settings/logging", "log_debug", "true"),
         ("/api/v1/settings/logging", "log_awstats", "true"),
         ("/api/v1/settings/antispam", "spam_mark_threshold", "10000"),
         ("/api/v1/settings/antispam", "spam_delete_threshold", "10000"),
         ("/api/v1/settings/antispam", "check_host_in_helo", "false"),
         ("/api/v1/settings/antispam", "greylisting_enabled", "false"),
         ("/api/v1/settings/antispam", "bypass_greylisting_on_mail_from_mx", "false"),
         ("/api/v1/settings/antispam", "spamassassin_enabled", "false"),
         ("/api/v1/settings/antispam", "tarpit_count", "0"),
         ("/api/v1/settings/antispam", "tarpit_delay", "0"),
         ("/api/v1/settings/antispam", "check_mx_records", "false"),
         ("/api/v1/settings/antispam", "use_spf", "false"),
         ("/api/v1/settings/antispam", "check_ptr", "false"),
         ("/api/v1/settings/antispam", "maximum_message_size_kb", "1024"),
      };

      private static readonly string[] SettingsGroups =
         { "/api/v1/settings", "/api/v1/settings/logging", "/api/v1/settings/antispam" };

      /// <summary>The five keys GET /api/v1/settings/logging reports and PUT refuses: they describe the files, they do not set them.</summary>
      private static readonly string[] ReadOnlyKeys =
         { "directory", "current_default_log", "current_error_log", "current_event_log", "current_awstats_log" };

      /// <summary>
      ///    Every key of the three groups as they stood when the run began, with the
      ///    suite's own overrides already applied. This is what a test is given.
      /// </summary>
      private static Dictionary<string, Dictionary<string, string>> _baseline;

      private static Dictionary<string, string> ReadGroup(string group)
      {
         var values = new Dictionary<string, string>();
         var answer = ServerApi.Get(group).Expect(200, "GET " + group);

         if (!answer.Json.HasValue || answer.Json.Value.ValueKind != JsonValueKind.Object)
            return values;

         foreach (var property in answer.Json.Value.EnumerateObject())
         {
            if (System.Array.IndexOf(ReadOnlyKeys, property.Name) >= 0)
               continue;

            values[property.Name] = property.Value.ValueKind == JsonValueKind.String
               ? ServerApi.Quote(property.Value.GetString())
               : property.Value.GetRawText();
         }

         return values;
      }

      /// <summary>
      ///    What the Windows PerformBasicSetup does with some forty explicit
      ///    assignments: put the server's settings back to what the suite expects
      ///    before every test, so that a fixture which changes one and does not put
      ///    it back cannot decide what the next fixture sees.
      ///
      ///    Here the whole of each group is compared rather than a list of keys: the
      ///    first call reads the three groups as the run found them, applies the
      ///    suite's own overrides (the spam thresholds and the rest of SuiteDefaults)
      ///    and keeps the result as the baseline; every later call writes back only
      ///    the keys that differ from it, which in the ordinary case is none and
      ///    costs three reads. The baseline is the server's own state, not a table
      ///    of values this project believes in, so it cannot drift from what a fresh
      ///    --create-database leaves.
      /// </summary>
      public void ResetSettings()
      {
         if (!ServerApi.HasSettingsWriteRoutes)
            return;

         if (_baseline == null)
         {
            ApplySuiteDefaults();

            _baseline = new Dictionary<string, Dictionary<string, string>>();
            foreach (var group in SettingsGroups)
               _baseline[group] = ReadGroup(group);

            return;
         }

         foreach (var group in SettingsGroups)
         {
            var wanted = _baseline[group];
            var current = ReadGroup(group);
            var body = new StringBuilder();

            foreach (var entry in current)
            {
               string expected;
               if (!wanted.TryGetValue(entry.Key, out expected) || expected == entry.Value)
                  continue;

               if (body.Length > 0)
                  body.Append(',');
               body.Append(ServerApi.Quote(entry.Key)).Append(':').Append(expected);
            }

            if (body.Length == 0)
               continue;

            Write(group, body.ToString());
         }
      }

      private static void ApplySuiteDefaults()
      {
         foreach (var group in SettingsGroups)
         {
            var current = ReadGroup(group);
            var body = new StringBuilder();

            foreach (var wanted in SuiteDefaults)
            {
               if (wanted.Group != group)
                  continue;

               string now;
               if (!current.TryGetValue(wanted.Key, out now) || now == wanted.Json)
                  continue;

               if (body.Length > 0)
                  body.Append(',');
               body.Append(ServerApi.Quote(wanted.Key)).Append(':').Append(wanted.Json);
            }

            if (body.Length > 0)
               Write(group, body.ToString());
         }
      }

      private static void Write(string group, string body)
      {
         var answer = ServerApi.Put(group, "{" + body + "}");

         // One key can be refused for a reason that is not this test's to
         // answer for: the hierarchy delimiter cannot change while a folder
         // holds the new character, and a fixture that left such a folder
         // behind would otherwise fail every test after it rather than its
         // own. The rest of the group is applied on the next attempt.
         if (answer.Status != 200 && !answer.Body.Contains("hierarchy delimiter"))
            answer.Expect(200, "PUT " + group + " (the suite's defaults)");
      }

      // ---- The rows a test can add beside the settings, put back the same way ----
      //
      // The Windows PerformBasicSetup calls SecurityRanges.SetDefault(),
      // TCPIPPorts.SetDefault(), RemoveAllRules() and SSLCertificates.Clear()
      // before every test, because a range or a rule a fixture leaves behind
      // decides what every later fixture sees - an IP range that refuses SMTP
      // makes every send in the rest of the run fail, which is exactly what the
      // first run of this project did once the range routes existed. There is no
      // SetDefault route, so the run's own starting set is the default: the ids
      // present when the first test ran are kept and anything else is deleted.

      private static Dictionary<string, List<string>> _rowsAtStart;

      /// <summary>
      ///    One row as a body its create route would take: every property the listing
      ///    gives except the id, which the server allocates. Two rows with the same
      ///    body are the same row for this purpose.
      /// </summary>
      private static string BodyOf(JsonElement element)
      {
         var body = new StringBuilder("{");

         foreach (var property in element.EnumerateObject())
         {
            // The id is the server's, and treat_security_as_local_domain is the same
            // field as treat_recipient_as_local_domain under its older name; sending
            // both would be sending it twice.
            if (property.Name == "id" || property.Name == "treat_security_as_local_domain")
               continue;

            if (body.Length > 1)
               body.Append(',');

            body.Append(ServerApi.Quote(property.Name)).Append(':');
            body.Append(property.Value.ValueKind == JsonValueKind.String
               ? ServerApi.Quote(property.Value.GetString())
               : property.Value.GetRawText());
         }

         return body.Append('}').ToString();
      }

      private static List<string> RowsOf(string route, out List<long> ids)
      {
         var bodies = new List<string>();
         ids = new List<long>();

         foreach (var element in ServerApi.Array(ServerApi.Get(route).Expect(200, "GET " + route)))
         {
            bodies.Add(BodyOf(element));
            ids.Add(ServerApi.LongOf(element, "id"));
         }

         return bodies;
      }

      /// <summary>
      ///    The rows of one collection put back exactly as the run found them: what is
      ///    there and should not be is deleted, and what is missing is created again
      ///    from the body the listing gave for it. This is TCPIPPorts.SetDefault and
      ///    SecurityRanges.SetDefault, against the set this environment starts with
      ///    rather than the installer's.
      ///
      ///    It matters more than it looks. The first run of the expanded project ended
      ///    with both default IP ranges deleted by one fixture and a global rule that
      ///    deleted every message left behind by another, and every SMTP test after
      ///    those two failed with an empty response - from a server that was refusing
      ///    the connection because no range matched it.
      /// </summary>
      private static void RestoreRows(string route)
      {
         List<long> ids;
         var now = RowsOf(route, out ids);

         if (_rowsAtStart == null)
            _rowsAtStart = new Dictionary<string, List<string>>();

         List<string> wanted;
         if (!_rowsAtStart.TryGetValue(route, out wanted))
         {
            _rowsAtStart[route] = now;
            return;
         }

         var missing = new List<string>(wanted);
         var surplus = new List<long>();

         for (var i = 0; i < now.Count; i++)
         {
            if (missing.Contains(now[i]))
               missing.Remove(now[i]);
            else
               surplus.Add(ids[i]);
         }

         foreach (var id in surplus)
         {
            var answer = ServerApi.Delete(route + "/" + id);

            // A certificate a listener still binds cannot go; the ports are put back
            // first, so the next pass removes it. The Windows Clear has the same
            // constraint and gives the same answer.
            if (answer.Status != 200 && answer.Status != 404 && answer.Status != 409)
               answer.Expect(200, "DELETE " + route + "/" + id);
         }

         foreach (var body in missing)
         {
            var answer = ServerApi.Post(route, body);

            if (answer.Status != 201 && answer.Status != 409)
               answer.Expect(201, "POST " + route + " (putting back a row the run started with)");
         }
      }

      // ---- The server's ini, put back the way the rows are ----
      //
      // A fixture that writes hMailServer.ini (ServerIniFile) then restarts the
      // server to have it read: the restart is not something this project can do,
      // so that test stops there - but the file it wrote stays written, and the
      // next start of the server would read it. The file as the run found it is
      // kept and written back before every test. The running server does not
      // re-read the ini, so putting it back is invisible to it and cannot change
      // what any test sees.

      private static string _iniPath;
      private static string _iniAtStart;

      private static void RestoreServerIni()
      {
         var path = Environment.GetEnvironmentVariable("HMTEST_SERVER_INI");

         if (string.IsNullOrWhiteSpace(path) || !File.Exists(path.Trim()))
            return;

         _iniPath = path.Trim();

         if (_iniAtStart == null)
         {
            _iniAtStart = File.ReadAllText(_iniPath);
            return;
         }

         if (File.ReadAllText(_iniPath) != _iniAtStart)
            File.WriteAllText(_iniPath, _iniAtStart);
      }

      public void RestoreServerRows()
      {
         RestoreServerIni();
         RemoveAllRules();
         RestoreRows("/api/v1/ports");
         RestoreRows("/api/v1/certificates");
         RestoreRows("/api/v1/ipranges");
      }

      public void RemoveAllRules()
      {
         if (!ServerApi.HasRoute("/api/v1/rules", "post"))
            return;

         foreach (var rule in ServerApi.Array(ServerApi.Get("/api/v1/rules").Expect(200, "GET /api/v1/rules")))
         {
            var id = ServerApi.LongOf(rule, "id");
            ServerApi.Delete("/api/v1/rules/" + id).Expect(200, "DELETE /api/v1/rules/" + id);
         }
      }

      public void RemoveAllRoutes()
      {
         if (!ServerApi.HasRouteWriteRoutes)
            return;
         foreach (var route in ServerApi.Array(ServerApi.Get("/api/v1/routes").Expect(200, "GET /api/v1/routes")))
         {
            var id = ServerApi.LongOf(route, "id");
            ServerApi.Delete("/api/v1/routes/" + id).Expect(200, "DELETE /api/v1/routes/" + id);
         }
      }

      // ---- The delivery queue, through /api/v1/queue ----

      private static List<long> QueuedMessageIds(out int recipients)
      {
         var ids = new List<long>();
         recipients = 0;

         var answer = ServerApi.Get("/api/v1/queue").Expect(200, "GET /api/v1/queue");

         foreach (var message in ServerApi.Array(answer, "messages"))
         {
            ids.Add(ServerApi.LongOf(message, "id"));

            var recipientList = ServerApi.StringOf(message, "recipients") ?? string.Empty;
            recipients += recipientList.Length == 0 ? 0 : recipientList.Split(',').Length;
         }

         return ids;
      }

      /// <summary>What the Windows one does with ResetDeliveryTime and SubmitEMail: every queued message is tried now.</summary>
      public static void SendMessagesInQueue()
      {
         int recipients;
         foreach (var id in QueuedMessageIds(out recipients))
            ServerApi.Post("/api/v1/queue/" + id + "/retry", null);
      }

      public static void DeleteMessagesInQueue()
      {
         int recipients;
         foreach (var id in QueuedMessageIds(out recipients))
            ServerApi.Delete("/api/v1/queue/" + id);
      }

      public static int GetNumberOfMessagesInDeliveryQueue()
      {
         int recipients;
         QueuedMessageIds(out recipients);
         return recipients;
      }

      // ---- The helpers the Windows TestSetup has and the fixtures call ----

      /// <summary>
      ///    What the Windows PerformBasicSetup calls to put the anti-spam settings
      ///    back; here it is the anti-spam half of ResetSettings, over the same
      ///    PUT /api/v1/settings/antispam.
      /// </summary>
      public void DisableSpamProtection()
      {
         ResetSettings();
      }

      /// <summary>
      ///    The Windows one escapes a backslash for the database the server is on,
      ///    which it learns from Application.Database.DatabaseType. No REST route
      ///    reports the database type, so a fixture that needs the escaping is
      ///    stopped rather than given the wrong one.
      /// </summary>
      public static string Escape(string input)
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoDatabaseObject);
         return input;
      }

      public Domain AddDomain(string name, bool active)
      {
         var domain = AddDomain(name);

         if (active)
            return domain;

         domain.Active = false;
         domain.Save();
         return domain;
      }

      /// <summary>
      ///    A file on this host, read whole. The Windows one exists because the log
      ///    files are open while the server writes them; the same read is right here
      ///    when the path the server gave points at this machine.
      /// </summary>
      public static string ReadExistingTextFile(string fileName)
      {
         using (var stream = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
         using (var reader = new StreamReader(stream))
            return reader.ReadToEnd();
      }

      public static string UniqueString()
      {
         return Guid.NewGuid().ToString().Replace("{", "").Replace("}", "").Replace("-", "");
      }

      /// <summary>
      ///    CRLF written out, where the Windows one uses AppendLine: on a Linux test host
      ///    AppendLine is a bare LF, the server rightly answers "554 Rejected - Message
      ///    containing bare LF's", and the first run on Linux failed the two tests that
      ///    use this body for exactly that.
      /// </summary>
      public static string CreateLargeDummyMailBody()
      {
         var sb = new StringBuilder();
         for (var i = 0; i < 10000; i++)
            sb.Append("0123456789012345678901234567890123456789012345678901234567890123456789\r\n");

         return sb.ToString();
      }

      public static int GetNextFreePort()
      {
         _freePort++;
         return _freePort;
      }

      /// <summary>
      ///    The address the server is reachable on. The Windows one walks the network
      ///    interfaces for a private address that answers on port 25, which is both a
      ///    check that the machine has a network and the address the fixtures give the
      ///    server to connect back to. Here the target is known and there is no
      ///    interface to probe, so it is the configured host.
      /// </summary>
      internal static IPAddress GetLocalIpAddress()
      {
         return TestPorts.HostAddress;
      }

      public static string GetResource(string resourceName)
      {
         var assembly = Assembly.GetExecutingAssembly();

         using (var stream = assembly.GetManifestResourceStream("RegressionTests." + resourceName))
         {
            if (stream == null)
               throw new FileNotFoundException("Embedded resource RegressionTests." + resourceName +
                                               " is not in " + assembly.GetName().Name);

            using (var reader = new StreamReader(stream))
               return reader.ReadToEnd();
         }
      }
   }
}

// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
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

      public Account AddAccount(Domain domain, string address, string password)
      {
         var answer = ServerApi.Post("/api/v1/domains/" + domain.Name + "/accounts",
            "{\"address\":" + ServerApi.Quote(address) + ",\"password\":" + ServerApi.Quote(password) + "}");

         if (answer.Status == 201)
         {
            _accountsMade.Add(address.ToLowerInvariant());
            return new Account { Address = address, Password = password, Active = true };
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
         if (maxSize != 0)
            NotOnThisServer.Ignore(NotOnThisServer.NoAccountMaxSize);

         return AddAccount(domain, address, password);
      }

      public Alias AddAlias(Domain domain, string name, string value)
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoAliasCreate);
         return null;
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
         NotOnThisServer.Ignore(NotOnThisServer.NoRouteCreate);
         return null;
      }

      internal static Route AddRoutePointingAtLocalhost(int numberOfTries, int port, bool treatSecurityAsLocal,
         eConnectionSecurity connectionSecurity)
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoRouteCreate);
         return null;
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

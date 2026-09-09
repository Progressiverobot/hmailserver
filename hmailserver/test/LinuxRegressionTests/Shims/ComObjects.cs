// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using RegressionTests.Shared;

// The hMailServer namespace, as the fixtures compile against it. On Windows these
// are the COM interop types generated from hMailServer.exe's type library; here they
// are the members the linked fixtures actually touch, and nothing more, each one
// either backed by the REST API or ending in NotOnThisServer.Ignore with the reason.
// A member that is missing from here is a fixture that was not meant to be linked.
namespace hMailServer
{
   public enum eConnectionSecurity
   {
      eCSNone = 0,
      eCSTLS = 1,
      eCSSTARTTLSOptional = 2,
      eCSSTARTTLSRequired = 3
   }

   public class Application
   {
      public Settings Settings { get; } = new Settings();
      public Utilities Utilities { get; } = new Utilities();
      public Status Status { get; } = new Status();
   }

   public class Status
   {
      public int ThreadID
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoStatusThreadId);
            return 0;
         }
      }
   }

   /// <summary>
   ///    Server settings. GET /api/v1/settings is a read-only snapshot of a few of
   ///    them and none of the ones these fixtures read, so every member here is a
   ///    write the API cannot make or a read it cannot serve.
   /// </summary>
   public class Settings
   {
      public AntiSpam AntiSpam { get; } = new AntiSpam();

      public string IMAPHierarchyDelimiter
      {
         get { NotOnThisServer.Ignore(NotOnThisServer.NoSettingsWrite); return null; }
         set { NotOnThisServer.Ignore(NotOnThisServer.NoSettingsWrite); }
      }

      public string IMAPPublicFolderName
      {
         get { NotOnThisServer.Ignore(NotOnThisServer.NoSettingsWrite); return null; }
         set { NotOnThisServer.Ignore(NotOnThisServer.NoSettingsWrite); }
      }

      public bool TlsOptionPreferServerCiphersEnabled
      {
         get { NotOnThisServer.Ignore(NotOnThisServer.NoSettingsWrite); return false; }
         set { NotOnThisServer.Ignore(NotOnThisServer.NoSettingsWrite); }
      }

      public bool TlsOptionPrioritizeChaChaEnabled
      {
         get { NotOnThisServer.Ignore(NotOnThisServer.NoSettingsWrite); return false; }
         set { NotOnThisServer.Ignore(NotOnThisServer.NoSettingsWrite); }
      }
   }

   public class AntiSpam
   {
      public int SpamDeleteThreshold
      {
         get { NotOnThisServer.Ignore(NotOnThisServer.NoSettingsWrite); return 0; }
         set { NotOnThisServer.Ignore(NotOnThisServer.NoSettingsWrite); }
      }

      public bool DKIMVerificationEnabled
      {
         get { NotOnThisServer.Ignore(NotOnThisServer.NoSettingsWrite); return false; }
         set { NotOnThisServer.Ignore(NotOnThisServer.NoSettingsWrite); }
      }

      public int DKIMVerificationFailureScore
      {
         get { NotOnThisServer.Ignore(NotOnThisServer.NoSettingsWrite); return 0; }
         set { NotOnThisServer.Ignore(NotOnThisServer.NoSettingsWrite); }
      }

      public int SpamMarkThreshold
      {
         get { NotOnThisServer.Ignore(NotOnThisServer.NoSettingsWrite); return 0; }
         set { NotOnThisServer.Ignore(NotOnThisServer.NoSettingsWrite); }
      }

      public bool AddHeaderSpam
      {
         get { NotOnThisServer.Ignore(NotOnThisServer.NoSettingsWrite); return false; }
         set { NotOnThisServer.Ignore(NotOnThisServer.NoSettingsWrite); }
      }

      public bool AddHeaderReason
      {
         get { NotOnThisServer.Ignore(NotOnThisServer.NoSettingsWrite); return false; }
         set { NotOnThisServer.Ignore(NotOnThisServer.NoSettingsWrite); }
      }

      public bool DMARCEnabled
      {
         get { NotOnThisServer.Ignore(NotOnThisServer.NoSettingsWrite); return false; }
         set { NotOnThisServer.Ignore(NotOnThisServer.NoSettingsWrite); }
      }
   }

   /// <summary>
   ///    The Sieve fixtures reach this late-bound - utilities.GetType().InvokeMember(
   ///    "CheckSieveSyntax", ...) - because on Windows they must not depend on a
   ///    regenerated type library. The names and signatures here are therefore load
   ///    bearing: InvokeMember finds them by name.
   ///
   ///    CheckSieveSyntax has a REST equivalent: PUT /api/v1/me/filters puts a script
   ///    through the same validation ManageSieve's PUTSCRIPT uses and answers 400 with
   ///    the reason when it does not parse. The check needs an account to be made as,
   ///    so one is made for the purpose in the test domain; TearDown removes it with
   ///    everything else the test made. EvaluateSieveScript - run a script against a
   ///    raw message and report the action - has no REST equivalent.
   /// </summary>
   public class Utilities
   {
      private const string SyntaxAccountAddress = "sieve-syntax@example.test";
      private const string SyntaxAccountPassword = "sieve-syntax-secret";

      public string CheckSieveSyntax(string script)
      {
         var setup = SingletonProvider<TestSetup>.Instance;

         if (!setup.HasAccount(SyntaxAccountAddress))
            setup.AddAccount(setup.TestDomain, SyntaxAccountAddress, SyntaxAccountPassword);

         var answer = ServerApi.AsAccount(SyntaxAccountAddress, SyntaxAccountPassword, HttpMethod.Put,
            "/api/v1/me/filters", "{\"script\":" + ServerApi.Quote(script) + "}");

         if (answer.Ok)
            return string.Empty;

         if (answer.Status == 400)
            return answer.Error;

         throw new InvalidOperationException("PUT /api/v1/me/filters answered " + answer.Status + ": " + answer.Body);
      }

      public string EvaluateSieveScript(string script, string rawMessage)
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoSieveEvaluate);
         return null;
      }

      public string GetMailServer(string address)
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoMailServerLookup);
         return null;
      }
   }

   public class Domain
   {
      public string Name { get; set; }
      public bool Active { get; set; }
   }

   public class Alias
   {
      public string Name { get; set; }
      public string Value { get; set; }
      public bool Active { get; set; }
   }

   public class DistributionList
   {
      public string Address { get; set; }
      public bool Active { get; set; }
   }

   public class Route
   {
      public string DomainName { get; set; }
   }

   public class Account
   {
      public string Address { get; set; }
      public string Password { get; set; }
      public bool Active { get; set; }
      public int MaxSize { get; set; }

      /// <summary>
      ///    The mailbox's size in bytes, as GET /api/v1/me reports it under
      ///    quota.used_bytes - the same figure the COM property reads from the account
      ///    row, kept by the server as messages arrive and go.
      /// </summary>
      public long Size
      {
         get
         {
            var answer = ServerApi.AsAccount(Address, Password, HttpMethod.Get, "/api/v1/me")
               .Expect(200, "GET /api/v1/me as " + Address);

            JsonElement quota;
            if (answer.Json.HasValue && answer.Json.Value.TryGetProperty("quota", out quota))
               return ServerApi.LongOf(quota, "used_bytes");

            return 0;
         }
      }

      public Messages Messages => new Messages(this);

      /// <summary>
      ///    The account's active Sieve script, which the Sieve fixtures reach late-bound
      ///    (InvokeMember "SieveScript" with GetProperty / SetProperty). GET and PUT
      ///    /api/v1/me/filters are the same script under the same validation the COM
      ///    property applies; an empty script removes the filter on both.
      /// </summary>
      public string SieveScript
      {
         get
         {
            var answer = ServerApi.AsAccount(Address, Password, HttpMethod.Get, "/api/v1/me/filters")
               .Expect(200, "GET /api/v1/me/filters as " + Address);

            return answer.Json.HasValue ? ServerApi.StringOf(answer.Json.Value, "active") ?? string.Empty : string.Empty;
         }
         set
         {
            var answer = ServerApi.AsAccount(Address, Password, HttpMethod.Put, "/api/v1/me/filters",
               "{\"script\":" + ServerApi.Quote(value) + "}");

            if (answer.Status == 400)
               throw new System.Runtime.InteropServices.COMException("Failed to save object. " + answer.Error);

            answer.Expect(200, "PUT /api/v1/me/filters as " + Address);
         }
      }
   }

   /// <summary>
   ///    The messages of an account's INBOX, read and deleted through the account's
   ///    own /api/v1/me routes. On Windows Account.Messages is the account's whole
   ///    message table in id order; the fixtures that use it here (API/Messages) put
   ///    everything in the INBOX, and the listing is put in ascending id order to
   ///    match. A delete is ?permanent=1, because the COM DeleteByDBID removes the
   ///    row rather than moving it to Trash.
   /// </summary>
   public class Messages
   {
      private readonly Account _account;

      public Messages(Account account)
      {
         _account = account;
      }

      private List<Message> Load()
      {
         var folders = ServerApi.AsAccount(_account.Address, _account.Password, HttpMethod.Get, "/api/v1/me/folders")
            .Expect(200, "GET /api/v1/me/folders as " + _account.Address);

         long inboxId = 0;
         foreach (var folder in ServerApi.Array(folders, "folders"))
         {
            if (string.Equals(ServerApi.StringOf(folder, "name"), "INBOX", StringComparison.OrdinalIgnoreCase))
               inboxId = ServerApi.LongOf(folder, "id");
         }

         if (inboxId == 0)
            throw new InvalidOperationException(_account.Address + " has no INBOX in GET /api/v1/me/folders: " + folders.Body);

         var listing = ServerApi.AsAccount(_account.Address, _account.Password, HttpMethod.Get,
            "/api/v1/me/folders/" + inboxId + "/messages?limit=200").Expect(200, "listing the INBOX of " + _account.Address);

         var result = new List<Message>();
         foreach (var entry in ServerApi.Array(listing, "messages"))
            result.Add(new Message { ID = ServerApi.LongOf(entry, "id") });

         result.Sort((a, b) => a.ID.CompareTo(b.ID));
         return result;
      }

      public int Count => Load().Count;

      public Message this[int index] => Load()[index];

      public Message get_ItemByDBID(long id)
      {
         foreach (var message in Load())
            if (message.ID == id)
               return message;
         return null;
      }

      public void DeleteByDBID(long id)
      {
         ServerApi.AsAccount(_account.Address, _account.Password, HttpMethod.Delete,
            "/api/v1/me/messages/" + id + "?permanent=1").Expect(200, "DELETE /api/v1/me/messages/" + id);
      }
   }

   public class Message
   {
      public long ID { get; set; }
   }
}

// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Linq;
using System.Text.Json;
using System.Collections.Generic;
using RegressionTests.Shared;

// The rest of the COM surface the linked fixtures name: the collections and objects
// the REST API has no route for at all. They exist so that a fixture whose other
// tests do run can compile; every member here stops the test that reaches it, with
// the reason, and none of them pretends the server was asked anything.
namespace hMailServer
{
   public class BlockedAttachments
   {
      public BlockedAttachment Add()
      {
         throw NotOnThisServer.Skipped(NotOnThisServer.NoAntiVirusSettings);
      }

      public int Count
      {
         get
         {
            throw NotOnThisServer.Skipped(NotOnThisServer.NoAntiVirusSettings);
         }
      }

      public void DeleteByDBID(long id)
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoAntiVirusSettings);
      }

      public void Clear()
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoAntiVirusSettings);
      }
   }

   public class BlockedAttachment
   {
      public long ID { get; set; }
      public string Wildcard { get; set; }
      public string Description { get; set; }

      public void Save()
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoAntiVirusSettings);
      }
   }

   /// <summary>An account's own rules, which /api/v1/rules does not carry.</summary>
   public class AccountRules
   {
      public Rule Add()
      {
         throw NotOnThisServer.Skipped(NotOnThisServer.NoAccountRules);
      }

      public int Count
      {
         get
         {
            throw NotOnThisServer.Skipped(NotOnThisServer.NoAccountRules);
         }
      }

      public Rule get_ItemByName(string name)
      {
         throw NotOnThisServer.Skipped(NotOnThisServer.NoAccountRules);
      }

      [System.Runtime.CompilerServices.IndexerName("At")]
      public Rule this[int index]
      {
         get { throw NotOnThisServer.Skipped(NotOnThisServer.NoAccountRules); }
      }

      public Rule get_Item(int index)
      {
         throw NotOnThisServer.Skipped(NotOnThisServer.NoAccountRules);
      }

      public void DeleteByDBID(long id)
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoAccountRules);
      }

      public void Clear()
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoAccountRules);
      }

      public void Refresh()
      {
      }
   }

   /// <summary>
   ///    Account.FetchAccounts over /api/v1/accounts/{address}/fetch-accounts: the
   ///    external POP3 or IMAP mailboxes the server collects into the account.
   ///    Add gives an unsaved object; Save is the POST, or the PUT once it has an
   ///    id; Delete and DownloadNow are the DELETE and the POST .../download. A
   ///    refusal comes back as the COMException a fixture would see over COM.
   /// </summary>
   public class FetchAccounts
   {
      private readonly Account _account;

      internal FetchAccounts(Account account)
      {
         _account = account;
      }

      internal string Base => "/api/v1/accounts/" + _account.Address + "/fetch-accounts";

      internal static void RouteOrSkip()
      {
         if (!ServerApi.HasRoute("/api/v1/accounts/{address}/fetch-accounts", "get"))
            throw NotOnThisServer.Skipped(NotOnThisServer.NoFetchAccounts);
      }

      private List<FetchAccount> Load()
      {
         RouteOrSkip();
         var answer = ServerApi.Get(Base).Expect(200, "GET " + Base);
         return ServerApi.Array(answer).Select(entry => FetchAccount.From(_account, entry)).ToList();
      }

      public FetchAccount Add()
      {
         RouteOrSkip();
         return new FetchAccount(_account);
      }

      public int Count => Load().Count;

      [System.Runtime.CompilerServices.IndexerName("At")]
      public FetchAccount this[int index] => Load()[index];

      public FetchAccount get_Item(int index)
      {
         return Load()[index];
      }

      public FetchAccount get_ItemByName(string name)
      {
         return Load().FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
      }

      public FetchAccount get_ItemByDBID(long id)
      {
         return Load().FirstOrDefault(f => f.ID == id);
      }

      public void DeleteByDBID(long id)
      {
         RouteOrSkip();
         ServerApi.Delete(Base + "/" + id).Expect(200, "DELETE " + Base + "/" + id);
      }

      public void Clear()
      {
         foreach (var fetchAccount in Load())
            fetchAccount.Delete();
      }

      public void Refresh()
      {
      }
   }

   public class FetchAccount
   {
      private readonly Account _account;

      internal FetchAccount(Account account)
      {
         _account = account;
         Enabled = true;
         MinutesBetweenFetch = 30;
         MIMERecipientHeaders = "To,CC,X-RCPT-TO,X-Envelope-To";
      }

      internal static FetchAccount From(Account account, JsonElement entry)
      {
         var fetchAccount = new FetchAccount(account);
         fetchAccount.Read(entry);
         return fetchAccount;
      }

      private string Base => new FetchAccounts(_account).Base;

      private void Read(JsonElement entry)
      {
         ID = ServerApi.LongOf(entry, "id");
         Name = ServerApi.StringOf(entry, "name");
         ServerAddress = ServerApi.StringOf(entry, "server_address");
         Port = (int) ServerApi.LongOf(entry, "port");
         ServerType = ServerApi.StringOf(entry, "server_type") == "imap" ? 1 : 0;
         Username = ServerApi.StringOf(entry, "username");
         Enabled = ServerApi.FlagOf(entry, "enabled");
         MinutesBetweenFetch = (int) ServerApi.LongOf(entry, "minutes_between_fetch");
         DaysToKeepMessages = (int) ServerApi.LongOf(entry, "days_to_keep_messages");
         ConnectionSecurity = SecurityOf(ServerApi.StringOf(entry, "connection_security"));
         ProcessMIMERecipients = ServerApi.FlagOf(entry, "process_mime_recipients");
         ProcessMIMEDate = ServerApi.FlagOf(entry, "process_mime_date");
         UseAntiSpam = ServerApi.FlagOf(entry, "use_antispam");
         UseAntiVirus = ServerApi.FlagOf(entry, "use_antivirus");
         EnableRouteRecipients = ServerApi.FlagOf(entry, "enable_route_recipients");
         MIMERecipientHeaders = ServerApi.StringOf(entry, "mime_recipient_headers");
         MirrorFolders = ServerApi.FlagOf(entry, "mirror_folders");
         _locked = ServerApi.FlagOf(entry, "locked");
         _nextDownloadTime = ServerApi.StringOf(entry, "next_download_time");
      }

      private static eConnectionSecurity SecurityOf(string word)
      {
         switch (word)
         {
            case "tls": return eConnectionSecurity.eCSTLS;
            case "starttls_optional": return eConnectionSecurity.eCSSTARTTLSOptional;
            case "starttls_required": return eConnectionSecurity.eCSSTARTTLSRequired;
            default: return eConnectionSecurity.eCSNone;
         }
      }

      private static string WordOf(eConnectionSecurity security)
      {
         switch (security)
         {
            case eConnectionSecurity.eCSTLS: return "tls";
            case eConnectionSecurity.eCSSTARTTLSOptional: return "starttls_optional";
            case eConnectionSecurity.eCSSTARTTLSRequired: return "starttls_required";
            default: return "none";
         }
      }

      public long ID { get; private set; }
      public string Name { get; set; }
      public string Username { get; set; }
      public string Password { get; set; }
      public string ServerAddress { get; set; }
      public int Port { get; set; }
      public bool Enabled { get; set; }
      public eConnectionSecurity ConnectionSecurity { get; set; }
      public int MinutesBetweenFetch { get; set; }
      public bool ProcessMIMERecipients { get; set; }
      public bool ProcessMIMEDate { get; set; }
      public bool UseAntiSpam { get; set; }
      public bool UseAntiVirus { get; set; }
      public bool EnableRouteRecipients { get; set; }
      public bool MirrorFolders { get; set; }
      public int DaysToKeepMessages { get; set; }
      public string MIMERecipientHeaders { get; set; }

      /// <summary>0 is POP3 and 1 is IMAP, as over COM.</summary>
      public int ServerType { get; set; }

      /// <summary>What the COM property is: tls, or none.</summary>
      public bool UseSSL
      {
         get { return ConnectionSecurity == eConnectionSecurity.eCSTLS; }
         set { ConnectionSecurity = value ? eConnectionSecurity.eCSTLS : eConnectionSecurity.eCSNone; }
      }

      private bool _locked;
      private string _nextDownloadTime;

      /// <summary>Read from the server each time, as the fixtures poll it.</summary>
      public bool IsLocked
      {
         get
         {
            Reload();
            return _locked;
         }
      }

      public string NextDownloadTime
      {
         get
         {
            Reload();
            return _nextDownloadTime;
         }
      }

      private void Reload()
      {
         if (ID == 0)
            return;
         var answer = ServerApi.Get(Base + "/" + ID).Expect(200, "GET " + Base + "/" + ID);
         Read(answer.Json.Value);
      }

      private string Body()
      {
         var fields = new List<string>
         {
            "\"name\":" + ServerApi.Quote(Name ?? ""),
            "\"server_address\":" + ServerApi.Quote(ServerAddress ?? ""),
            "\"port\":" + Port,
            "\"server_type\":" + ServerApi.Quote(ServerType == 1 ? "imap" : "pop3"),
            "\"username\":" + ServerApi.Quote(Username ?? ""),
            "\"enabled\":" + (Enabled ? "true" : "false"),
            "\"minutes_between_fetch\":" + MinutesBetweenFetch,
            "\"days_to_keep_messages\":" + DaysToKeepMessages,
            "\"connection_security\":" + ServerApi.Quote(WordOf(ConnectionSecurity)),
            "\"process_mime_recipients\":" + (ProcessMIMERecipients ? "true" : "false"),
            "\"process_mime_date\":" + (ProcessMIMEDate ? "true" : "false"),
            "\"use_antispam\":" + (UseAntiSpam ? "true" : "false"),
            "\"use_antivirus\":" + (UseAntiVirus ? "true" : "false"),
            "\"enable_route_recipients\":" + (EnableRouteRecipients ? "true" : "false"),
            "\"mime_recipient_headers\":" + ServerApi.Quote(MIMERecipientHeaders ?? ""),
            "\"mirror_folders\":" + (MirrorFolders ? "true" : "false")
         };
         if (Password != null)
            fields.Add("\"password\":" + ServerApi.Quote(Password));
         return "{" + string.Join(",", fields) + "}";
      }

      public void Save()
      {
         FetchAccounts.RouteOrSkip();
         var answer = ID == 0
            ? ServerApi.Post(Base, Body())
            : ServerApi.Put(Base + "/" + ID, Body());
         if (answer.Status == 400)
            throw new System.Runtime.InteropServices.COMException(answer.Error);
         answer.Expect(ID == 0 ? 201 : 200, (ID == 0 ? "POST " : "PUT ") + Base);
         Read(answer.Json.Value);
      }

      public void Delete()
      {
         FetchAccounts.RouteOrSkip();
         if (ID == 0)
            return;
         ServerApi.Delete(Base + "/" + ID).Expect(200, "DELETE " + Base + "/" + ID);
      }

      public void DownloadNow()
      {
         FetchAccounts.RouteOrSkip();
         ServerApi.Post(Base + "/" + ID + "/download", "{}").Expect(202, "POST " + Base + "/" + ID + "/download");
      }
   }

   public class AppPasswords
   {
      public AppPassword Add()
      {
         throw NotOnThisServer.Skipped(NotOnThisServer.NoAppPasswords);
      }

      public int Count
      {
         get
         {
            throw NotOnThisServer.Skipped(NotOnThisServer.NoAppPasswords);
         }
      }

      [System.Runtime.CompilerServices.IndexerName("At")]
      public AppPassword this[int index]
      {
         get { throw NotOnThisServer.Skipped(NotOnThisServer.NoAppPasswords); }
      }

      public AppPassword get_Item(int index)
      {
         throw NotOnThisServer.Skipped(NotOnThisServer.NoAppPasswords);
      }

      public void DeleteByDBID(long id)
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoAppPasswords);
      }

      public void Clear()
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoAppPasswords);
      }

      public void Refresh()
      {
      }
   }

   public class AppPassword
   {
      public long ID { get; set; }
      public string Name { get; set; }
      public string Password { get; set; }
      public DateTime LastUsed { get; set; }
      public DateTime CreatedTime { get; set; }

      public string Generate()
      {
         throw NotOnThisServer.Skipped(NotOnThisServer.NoAppPasswords);
      }

      public void SetPassword(string password)
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoAppPasswords);
      }

      public void Save()
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoAppPasswords);
      }
   }

   public class Group
   {
      public long ID { get; set; }
      public string Name { get; set; }

      public void Save()
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoGroups);
      }
   }

   /// <summary>An IMAP folder's ACL, which no REST route reads or writes.</summary>
   public class IMAPFolderPermissions
   {
      public int Count
      {
         get
         {
            throw NotOnThisServer.Skipped(NotOnThisServer.NoFolderAcl);
         }
      }

      public IMAPFolderPermission Add()
      {
         throw NotOnThisServer.Skipped(NotOnThisServer.NoFolderAcl);
      }

      [System.Runtime.CompilerServices.IndexerName("At")]
      public IMAPFolderPermission this[int index]
      {
         get { throw NotOnThisServer.Skipped(NotOnThisServer.NoFolderAcl); }
      }

      public IMAPFolderPermission get_Item(int index)
      {
         throw NotOnThisServer.Skipped(NotOnThisServer.NoFolderAcl);
      }

      public void DeleteByDBID(long id)
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoFolderAcl);
      }

      public void Clear()
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoFolderAcl);
      }
   }

   public class IMAPFolderPermission
   {
      public long ID { get; set; }
      public eACLPermissionType PermissionType { get; set; }
      public long PermissionAccountID { get; set; }
      public long PermissionGroupID { get; set; }

      public void Save()
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoFolderAcl);
      }

      public void set_Permission(eACLPermission permission, bool value)
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoFolderAcl);
      }

      public bool get_Permission(eACLPermission permission)
      {
         throw NotOnThisServer.Skipped(NotOnThisServer.NoFolderAcl);
      }
   }
}

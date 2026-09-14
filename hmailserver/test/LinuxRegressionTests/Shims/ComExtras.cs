// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
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

   /// <summary>
   ///    Account.AppPasswords over GET, POST and DELETE
   ///    /api/v1/accounts/{address}/app-passwords - the administrative routes,
   ///    which take the administrator's credential every request of this run
   ///    carries and not the account's password, as the COM collection does.
   ///    Read from the server on every use, as the COM collection is.
   /// </summary>
   public class AppPasswords
   {
      private readonly Account _account;

      internal AppPasswords(Account account)
      {
         _account = account;
      }

      internal string Base => "/api/v1/accounts/" + _account.Address + "/app-passwords";

      internal static void RouteOrSkip()
      {
         if (!ServerApi.HasRoute("/api/v1/accounts/{address}/app-passwords", "post"))
            NotOnThisServer.Ignore(NotOnThisServer.NoAppPasswords);
      }

      private List<AppPassword> Load()
      {
         RouteOrSkip();
         var answer = ServerApi.Get(Base).Expect(200, "GET " + Base);
         return ServerApi.Array(answer, "app_passwords").Select(entry => AppPassword.From(this, entry)).ToList();
      }

      public AppPassword Add()
      {
         RouteOrSkip();
         return new AppPassword(this);
      }

      public int Count => Load().Count;

      [System.Runtime.CompilerServices.IndexerName("At")]
      public AppPassword this[int index] => Load()[index];

      public AppPassword get_Item(int index)
      {
         return Load()[index];
      }

      public AppPassword get_ItemByDBID(long id)
      {
         return Load().FirstOrDefault(password => password.ID == id);
      }

      public void Delete(int index)
      {
         DeleteByDBID(Load()[index].ID);
      }

      public void DeleteByDBID(long id)
      {
         RouteOrSkip();
         ServerApi.Delete(Base + "/" + id).Expect(200, "DELETE " + Base + "/" + id);
      }

      public void Clear()
      {
         foreach (var password in Load())
            DeleteByDBID(password.ID);
      }

      public void Refresh()
      {
      }
   }

   /// <summary>
   ///    One app password. Over COM the secret is put on the unsaved object by
   ///    Generate or SetPassword and the row is written by Save; the route does
   ///    both in one POST, so the request is made where the secret is chosen and
   ///    Save has nothing left to do. SetPassword's two checks - the floor of
   ///    twelve characters and the password policy - are the route's own, so a
   ///    refused secret is the COMException it is over COM and nothing is stored.
   /// </summary>
   public class AppPassword
   {
      private readonly AppPasswords _owner;

      internal AppPassword(AppPasswords owner)
      {
         _owner = owner;
         Active = true;
         CreatedTime = string.Empty;
         LastUsedTime = string.Empty;
      }

      internal static AppPassword From(AppPasswords owner, JsonElement entry)
      {
         var password = new AppPassword(owner);
         password.Read(entry);
         return password;
      }

      private void Read(JsonElement entry)
      {
         ID = ServerApi.LongOf(entry, "id");
         Name = ServerApi.StringOf(entry, "name");
         CreatedTime = ServerApi.StringOf(entry, "created") ?? string.Empty;
         LastUsedTime = ServerApi.StringOf(entry, "last_used") ?? string.Empty;
         Active = ServerApi.FlagOf(entry, "active", true);
      }

      public long ID { get; private set; }
      public string Name { get; set; }
      public string CreatedTime { get; private set; }
      public string LastUsedTime { get; private set; }
      public bool Active { get; set; }

      // The POST: the name and the active flag, and the chosen secret when
      // there is one - left out, the server generates one as Generate does.
      private string Issue(string chosen)
      {
         AppPasswords.RouteOrSkip();

         var body = "{\"name\":" + ServerApi.Quote(Name ?? string.Empty) +
                    ",\"active\":" + (Active ? "true" : "false") +
                    (chosen == null ? string.Empty : ",\"password\":" + ServerApi.Quote(chosen)) + "}";

         var answer = ServerApi.Post(_owner.Base, body);
         if (answer.Status == 400)
            throw new System.Runtime.InteropServices.COMException(answer.Error);

         answer.Expect(201, "POST " + _owner.Base);
         Read(answer.Json.Value);
         return ServerApi.StringOf(answer.Json.Value, "password");
      }

      public string Generate()
      {
         return Issue(null);
      }

      public void SetPassword(string password)
      {
         Issue(password);
      }

      /// <summary>
      ///    A stored row: the PUT of its name and active flag, the two things the
      ///    COM setters change on one. A new row was written already by Generate
      ///    or SetPassword; a Save with neither is what the store refuses over COM
      ///    too, a row with no hash.
      /// </summary>
      public void Save()
      {
         if (ID != 0)
         {
            AppPasswords.RouteOrSkip();

            var path = _owner.Base + "/" + ID;
            var answer = ServerApi.Put(path, "{\"name\":" + ServerApi.Quote(Name ?? string.Empty) + ",\"active\":" + (Active ? "true" : "false") + "}");
            if (answer.Status == 400)
               throw new System.Runtime.InteropServices.COMException("Failed to save object. " + answer.Error);

            answer.Expect(200, "PUT " + path);
            Read(answer.Json.Value);
            return;
         }

         throw new System.Runtime.InteropServices.COMException(
            "An app password with no stored hash would authenticate nothing, and a row that cannot be used is a row nobody will think to delete.");
      }

      public void Delete()
      {
         if (ID != 0)
            _owner.DeleteByDBID(ID);
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

   /// <summary>
   ///    IMAPFolder.Permissions over GET and POST
   ///    /api/v1/accounts/{address}/folders/{id}/permissions and PUT and DELETE
   ///    .../permissions/{pid}: the folder's ACL rows, read from the server on
   ///    every use as ACLManager reads them for every decision. The
   ///    administrator's credential, as the COM collection takes it; the folder
   ///    is one of the account's own, which is the only kind this project's
   ///    IMAPFolder ever stands for (the public namespace has no route).
   /// </summary>
   public class IMAPFolderPermissions
   {
      private readonly Account _account;
      private readonly long _folderId;

      internal IMAPFolderPermissions(Account account, long folderId)
      {
         _account = account;
         _folderId = folderId;
      }

      internal long FolderID => _folderId;

      internal string Base => "/api/v1/accounts/" + _account.Address + "/folders/" + _folderId + "/permissions";

      internal static void RouteOrSkip()
      {
         if (!ServerApi.HasRoute("/api/v1/accounts/{address}/folders/{id}/permissions", "post"))
            NotOnThisServer.Ignore(NotOnThisServer.NoFolderAcl);
      }

      private List<IMAPFolderPermission> Load()
      {
         RouteOrSkip();
         var answer = ServerApi.Get(Base).Expect(200, "GET " + Base);
         return ServerApi.Array(answer).Select(entry => IMAPFolderPermission.From(this, entry)).ToList();
      }

      public int Count => Load().Count;

      public IMAPFolderPermission Add()
      {
         RouteOrSkip();
         return new IMAPFolderPermission(this);
      }

      [System.Runtime.CompilerServices.IndexerName("At")]
      public IMAPFolderPermission this[int index] => Load()[index];

      public IMAPFolderPermission get_Item(int index)
      {
         return Load()[index];
      }

      public IMAPFolderPermission get_ItemByDBID(long id)
      {
         return Load().FirstOrDefault(permission => permission.ID == id);
      }

      public void Delete(int index)
      {
         DeleteByDBID(Load()[index].ID);
      }

      public void DeleteByDBID(long id)
      {
         RouteOrSkip();
         ServerApi.Delete(Base + "/" + id).Expect(200, "DELETE " + Base + "/" + id);
      }

      public void Clear()
      {
         foreach (var permission in Load())
            DeleteByDBID(permission.ID);
      }

      public void Refresh()
      {
      }
   }

   /// <summary>
   ///    One ACL row. The rights are kept as the COM Value bit mask the
   ///    eACLPermission values are, and sent by name; Save is the POST for a new
   ///    row and the PUT for one the server already has, and a 400 - the shape
   ///    the store insists on, an unknown account, the folder's owner - is the
   ///    COMException it is over COM, with nothing stored.
   /// </summary>
   public class IMAPFolderPermission
   {
      private static readonly KeyValuePair<string, eACLPermission>[] Rights =
      {
         new KeyValuePair<string, eACLPermission>("lookup", eACLPermission.ePermissionLookup),
         new KeyValuePair<string, eACLPermission>("read", eACLPermission.ePermissionRead),
         new KeyValuePair<string, eACLPermission>("write_seen", eACLPermission.ePermissionWriteSeen),
         new KeyValuePair<string, eACLPermission>("write_others", eACLPermission.ePermissionWriteOthers),
         new KeyValuePair<string, eACLPermission>("insert", eACLPermission.ePermissionInsert),
         new KeyValuePair<string, eACLPermission>("post", eACLPermission.ePermissionPost),
         new KeyValuePair<string, eACLPermission>("create", eACLPermission.ePermissionCreate),
         new KeyValuePair<string, eACLPermission>("delete_mailbox", eACLPermission.ePermissionDeleteMailbox),
         new KeyValuePair<string, eACLPermission>("write_deleted", eACLPermission.ePermissionWriteDeleted),
         new KeyValuePair<string, eACLPermission>("expunge", eACLPermission.ePermissionExpunge),
         new KeyValuePair<string, eACLPermission>("administer", eACLPermission.ePermissionAdminister)
      };

      private readonly IMAPFolderPermissions _owner;

      internal IMAPFolderPermission(IMAPFolderPermissions owner)
      {
         _owner = owner;
         PermissionType = eACLPermissionType.ePermissionTypeUser;
      }

      internal static IMAPFolderPermission From(IMAPFolderPermissions owner, JsonElement entry)
      {
         var permission = new IMAPFolderPermission(owner);
         permission.Read(entry);
         return permission;
      }

      private void Read(JsonElement entry)
      {
         ID = ServerApi.LongOf(entry, "id");
         PermissionType = TypeOf(ServerApi.StringOf(entry, "type"));
         PermissionAccountID = ServerApi.LongOf(entry, "account_id");
         PermissionGroupID = ServerApi.LongOf(entry, "group_id");

         long value = 0;
         JsonElement rights;
         if (entry.TryGetProperty("rights", out rights) && rights.ValueKind == JsonValueKind.Object)
         {
            foreach (var right in Rights)
            {
               if (ServerApi.FlagOf(rights, right.Key))
                  value |= (long) right.Value;
            }
         }
         Value = value;
      }

      private static eACLPermissionType TypeOf(string word)
      {
         switch (word)
         {
            case "group": return eACLPermissionType.ePermissionTypeGroup;
            case "anyone": return eACLPermissionType.ePermissionTypeAnyone;
            default: return eACLPermissionType.ePermissionTypeUser;
         }
      }

      private static string WordOf(eACLPermissionType type)
      {
         switch (type)
         {
            case eACLPermissionType.ePermissionTypeGroup: return "group";
            case eACLPermissionType.ePermissionTypeAnyone: return "anyone";
            default: return "user";
         }
      }

      public long ID { get; private set; }
      public long ShareFolderID => _owner.FolderID;
      public eACLPermissionType PermissionType { get; set; }
      public long PermissionAccountID { get; set; }
      public long PermissionGroupID { get; set; }
      public long Value { get; set; }

      public bool get_Permission(eACLPermission permission)
      {
         return (Value & (long) permission) != 0;
      }

      public void set_Permission(eACLPermission permission, bool value)
      {
         if (value)
            Value |= (long) permission;
         else
            Value &= ~(long) permission;
      }

      private string Body()
      {
         var rights = string.Join(",", Rights.Select(right => "\"" + right.Key + "\":" + (get_Permission(right.Value) ? "true" : "false")));

         return "{\"type\":" + ServerApi.Quote(WordOf(PermissionType)) +
                ",\"account_id\":" + PermissionAccountID +
                ",\"group_id\":" + PermissionGroupID +
                ",\"rights\":{" + rights + "}}";
      }

      public void Save()
      {
         IMAPFolderPermissions.RouteOrSkip();

         var answer = ID == 0
            ? ServerApi.Post(_owner.Base, Body())
            : ServerApi.Put(_owner.Base + "/" + ID, Body());

         if (answer.Status == 400)
            throw new System.Runtime.InteropServices.COMException("Failed to save object. " + answer.Error);

         answer.Expect(ID == 0 ? 201 : 200, (ID == 0 ? "POST " : "PUT ") + _owner.Base);
         Read(answer.Json.Value);
      }

      public void Delete()
      {
         if (ID != 0)
            _owner.DeleteByDBID(ID);
      }
   }
}

// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using RegressionTests.Shared;

// The collections the Windows suite reaches through COM - the server's domains and
// what is in them, its listeners, its IP ranges, its certificates, its routes and its
// global rules - over the REST routes that write the same rows. Every member here
// either goes through a route and reports what the route says, or ends in
// NotOnThisServer.Ignore naming the route that does not exist. Nothing is faked and
// nothing is weakened: a collection whose route has no PUT cannot save an edit, and
// says so rather than pretending the edit took.
namespace hMailServer
{
   /// <summary>
   ///    Shared by every object here: the fields a COM object would have written on
   ///    Save() but no route takes are remembered by name, so Save() can say which
   ///    ones stopped it rather than silently dropping them.
   /// </summary>
   public abstract class RestBackedObject
   {
      private readonly List<string> _unsupported = new List<string>();

      protected void Unsupported(string property)
      {
         if (!_unsupported.Contains(property))
            _unsupported.Add(property);
      }

      protected void SkipIfAnythingUnsupported(string route)
      {
         if (_unsupported.Count == 0)
            return;

         NotOnThisServer.Ignore("sets " + string.Join(", ", _unsupported.ToArray()) + ", which " + route +
                                " does not take");
      }
   }

   // ---- Domains ----

   public class Domains
   {
      private static List<JsonElement> All()
      {
         return ServerApi.Array(ServerApi.Get("/api/v1/domains").Expect(200, "GET /api/v1/domains"));
      }

      private static Domain From(JsonElement element)
      {
         return new Domain
         {
            Name = ServerApi.StringOf(element, "name"),
            Active = element.TryGetProperty("active", out var active) && active.ValueKind == JsonValueKind.True,
            Postmaster = ServerApi.StringOf(element, "postmaster") ?? string.Empty
         };
      }

      public int Count => All().Count;

      [System.Runtime.CompilerServices.IndexerName("At")]
      public Domain this[int index] => From(All()[index]);

      public Domain get_Item(int index)
      {
         return From(All()[index]);
      }

      public Domain get_ItemByName(string name)
      {
         foreach (var element in All())
            if (string.Equals(ServerApi.StringOf(element, "name"), name, StringComparison.OrdinalIgnoreCase))
               return From(element);

         throw new System.Runtime.InteropServices.COMException("Item not found. " + name);
      }

      public Domain Add()
      {
         // The COM Add hands back an unsaved object; the row appears at Save().
         return new Domain { Active = true, Unsaved = true };
      }

      public void DeleteByDBID(long id)
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoDomainIds);
      }

      public void Delete(int index)
      {
         var domain = get_Item(index);
         ServerApi.Delete("/api/v1/domains/" + domain.Name).Expect(200, "DELETE /api/v1/domains/" + domain.Name);
      }

      public void Refresh()
      {
      }

      public void Clear()
      {
         foreach (var element in All())
         {
            var name = ServerApi.StringOf(element, "name");
            ServerApi.Delete("/api/v1/domains/" + name).Expect(200, "DELETE /api/v1/domains/" + name);
         }
      }
   }

   // ---- Accounts in a domain ----

   public class Accounts
   {
      private readonly string _domain;

      internal Accounts(string domain)
      {
         _domain = domain;
      }

      private List<JsonElement> All()
      {
         return ServerApi.Array(ServerApi.Get("/api/v1/domains/" + _domain + "/accounts")
            .Expect(200, "GET /api/v1/domains/" + _domain + "/accounts"));
      }

      private Account From(JsonElement element)
      {
         var address = ServerApi.StringOf(element, "address");

         // The listing carries no password, and the /api/v1/me routes need one. The
         // account this run made is matched to the password it was made with; any
         // other account has none and the member that needs one skips. Seeded rather
         // than assigned, so that a Save() of this object writes only what the test
         // then changes.
         return new Account().Seed(address,
            RegressionTests.Shared.TestSetup.PasswordFor(address),
            element.TryGetProperty("active", out var active) && active.ValueKind == JsonValueKind.True,
            _domain);
      }

      public int Count => All().Count;

      [System.Runtime.CompilerServices.IndexerName("At")]
      public Account this[int index] => From(All()[index]);

      public Account get_Item(int index)
      {
         return From(All()[index]);
      }

      /// <summary>
      ///    The COM parameterised property, which a fixture writes as
      ///    Accounts.ItemByAddress["x@y"]; C# reaches it through a small object with
      ///    an indexer, since only an interop type can have one directly.
      /// </summary>
      public AccountsByAddress ItemByAddress => new AccountsByAddress(this);

      public Account get_ItemByAddress(string address)
      {
         foreach (var element in All())
            if (string.Equals(ServerApi.StringOf(element, "address"), address, StringComparison.OrdinalIgnoreCase))
               return From(element);

         throw new System.Runtime.InteropServices.COMException("Item not found. " + address);
      }

      public Account Add()
      {
         return new Account { DomainName = _domain, Active = true, Unsaved = true };
      }

      public void DeleteByDBID(long id)
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoAccountIds);
      }

      public void Delete(int index)
      {
         var account = get_Item(index);
         ServerApi.Delete("/api/v1/accounts/" + account.Address)
            .Expect(200, "DELETE /api/v1/accounts/" + account.Address);
      }

      public void Refresh()
      {
      }
   }

   public class AccountsByAddress
   {
      private readonly Accounts _accounts;

      internal AccountsByAddress(Accounts accounts)
      {
         _accounts = accounts;
      }

      public Account this[string address] => _accounts.get_ItemByAddress(address);
   }

   // ---- Aliases in a domain ----

   public class Aliases
   {
      private readonly string _domain;

      internal Aliases(string domain)
      {
         _domain = domain;
      }

      private List<JsonElement> All()
      {
         return ServerApi.Array(ServerApi.Get("/api/v1/domains/" + _domain + "/aliases")
            .Expect(200, "GET /api/v1/domains/" + _domain + "/aliases"));
      }

      private Alias From(JsonElement element)
      {
         return new Alias
         {
            Name = ServerApi.StringOf(element, "name"),
            Value = ServerApi.StringOf(element, "value"),
            Active = element.TryGetProperty("active", out var active) && active.ValueKind == JsonValueKind.True,
            DomainName = _domain
         };
      }

      public int Count => All().Count;

      [System.Runtime.CompilerServices.IndexerName("At")]
      public Alias this[int index] => From(All()[index]);

      public Alias get_Item(int index)
      {
         return From(All()[index]);
      }

      public Alias get_ItemByName(string name)
      {
         foreach (var element in All())
            if (string.Equals(ServerApi.StringOf(element, "name"), name, StringComparison.OrdinalIgnoreCase))
               return From(element);

         throw new System.Runtime.InteropServices.COMException("Item not found. " + name);
      }

      public Alias Add()
      {
         if (!ServerApi.HasAliasWriteRoutes)
            NotOnThisServer.Ignore(NotOnThisServer.NoAliasCreate);

         return new Alias { DomainName = _domain, Active = true, Unsaved = true };
      }

      public void DeleteByDBID(long id)
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoAliasIds);
      }

      public void Delete(int index)
      {
         var alias = get_Item(index);
         ServerApi.Delete("/api/v1/aliases/" + alias.Name).Expect(200, "DELETE /api/v1/aliases/" + alias.Name);
      }

      public void Refresh()
      {
      }
   }

   // ---- Distribution lists in a domain ----

   public class DistributionLists
   {
      private readonly string _domain;

      internal DistributionLists(string domain)
      {
         _domain = domain;
      }

      private List<JsonElement> All()
      {
         return ServerApi.Array(ServerApi.Get("/api/v1/domains/" + _domain + "/lists")
            .Expect(200, "GET /api/v1/domains/" + _domain + "/lists"));
      }

      public int Count => All().Count;

      [System.Runtime.CompilerServices.IndexerName("At")]
      public DistributionList this[int index] => new DistributionList { Address = ServerApi.StringOf(All()[index], "address"), Active = true };

      public DistributionList get_Item(int index)
      {
         return new DistributionList { Address = ServerApi.StringOf(All()[index], "address"), Active = true };
      }

      public DistributionList get_ItemByAddress(string address)
      {
         foreach (var element in All())
            if (string.Equals(ServerApi.StringOf(element, "address"), address, StringComparison.OrdinalIgnoreCase))
               return new DistributionList { Address = address, Active = true };

         throw new System.Runtime.InteropServices.COMException("Item not found. " + address);
      }

      public DistributionList Add()
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoListObject);
         return null;
      }

      public void DeleteByDBID(long id)
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoListIds);
      }

      public void Delete(int index)
      {
         var list = get_Item(index);
         ServerApi.Delete("/api/v1/lists/" + list.Address).Expect(200, "DELETE /api/v1/lists/" + list.Address);
      }

      public void Refresh()
      {
      }
   }

   /// <summary>
   ///    A domain's aliases-of-the-domain (example.org delivering as example.com).
   ///    The REST API has no route for them at all: not a listing, not a create.
   /// </summary>
   public class DomainAliases
   {
      public int Count
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoDomainAliases);
            return 0;
         }
      }

      public DomainAlias Add()
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoDomainAliases);
         return null;
      }

      public DomainAlias get_ItemByName(string name)
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoDomainAliases);
         return null;
      }

      public void DeleteByDBID(long id)
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoDomainAliases);
      }
   }

   public class DomainAlias
   {
      public long ID { get; set; }
      public string AliasName { get; set; }
      public long DomainID { get; set; }
      public string DomainName { get; set; }

      public void Save()
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoDomainAliases);
      }

      public void Delete()
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoDomainAliases);
      }
   }

   public enum eACLPermissionType
   {
      ePermissionTypeUser = 0,
      ePermissionTypeGroup = 1,
      ePermissionTypeAnyone = 2
   }

   public enum eACLPermission
   {
      ePermissionLookup = 1,
      ePermissionRead = 2,
      ePermissionWriteSeen = 4,
      ePermissionWriteOthers = 8,
      ePermissionInsert = 16,
      ePermissionPost = 32,
      ePermissionCreate = 64,
      ePermissionDeleteMailbox = 128,
      ePermissionWriteDeleted = 256,
      ePermissionExpunge = 512,
      ePermissionAdminister = 1024
   }

   public enum eDomainSignatureMethod
   {
      eSMUnknown = 0,
      eSMSetIfNotSpecifiedInAccount = 1,
      eSMOverwriteAccountSignature = 2,
      eSMAppendToAccountSignature = 3
   }

   public enum eMessageFlag
   {
      eMFSeen = 1,
      eMFDeleted = 2,
      eMFFlagged = 4,
      eMFAnswered = 8,
      eMFDraft = 16,
      eMFRecent = 32,
      eMFVirusScan = 64,
      eMFSpam = 128
   }

   public enum eDKIMResult
   {
      eDKNeutral = 0,
      eDKPass = 1,
      eDKTempFail = 2,
      eDKPermFail = 3
   }

   public enum eDKIMCanonicalizationMethod
   {
      eCanonicalizationSimple = 1,
      eCanonicalizationRelaxed = 2
   }

   // ---- The listeners: /api/v1/ports ----

   public class TCPIPPorts
   {
      private static List<JsonElement> All()
      {
         return ServerApi.Array(ServerApi.Get("/api/v1/ports").Expect(200, "GET /api/v1/ports"));
      }

      private static TCPIPPort From(JsonElement element)
      {
         return new TCPIPPort
         {
            ID = ServerApi.LongOf(element, "id"),
            Protocol = TCPIPPort.ProtocolOf(ServerApi.StringOf(element, "protocol")),
            Address = ServerApi.StringOf(element, "address"),
            PortNumber = (int) ServerApi.LongOf(element, "port"),
            ConnectionSecurity = TCPIPPort.SecurityOf(ServerApi.StringOf(element, "connection_security")),
            SSLCertificateID = ServerApi.LongOf(element, "certificate_id")
         };
      }

      public int Count => All().Count;

      [System.Runtime.CompilerServices.IndexerName("At")]
      public TCPIPPort this[int index] => From(All()[index]);

      public TCPIPPort get_Item(int index)
      {
         return From(All()[index]);
      }

      public TCPIPPort get_ItemByDBID(long id)
      {
         foreach (var element in All())
            if (ServerApi.LongOf(element, "id") == id)
               return From(element);

         throw new System.Runtime.InteropServices.COMException("Item not found. " + id);
      }

      public TCPIPPort Add()
      {
         if (!ServerApi.HasRoute("/api/v1/ports", "post"))
            NotOnThisServer.Ignore(NotOnThisServer.NoPortCreate);

         return new TCPIPPort { Unsaved = true };
      }

      public void DeleteByDBID(long id)
      {
         ServerApi.Delete("/api/v1/ports/" + id).Expect(200, "DELETE /api/v1/ports/" + id);
      }

      public void Delete(int index)
      {
         DeleteByDBID(get_Item(index).ID);
      }

      /// <summary>
      ///    The COM SetDefault throws away every listener and puts back the three the
      ///    installer makes, on ports 25, 110 and 143. No route does that, and a shim
      ///    that wrote those three would put this environment's server on privileged
      ///    ports it cannot bind.
      /// </summary>
      public void SetDefault()
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoPortDefaults);
      }

      public void Refresh()
      {
      }
   }

   public enum eSessionType
   {
      eSTUnknown = 0,
      eSTSMTP = 1,
      eSTSMTPClient = 2,
      eSTPOP3 = 3,
      eSTPOP3Client = 4,
      eSTIMAP = 5
   }

   public class TCPIPPort : RestBackedObject
   {
      internal bool Unsaved;

      public long ID { get; set; }
      public string Address { get; set; } = "0.0.0.0";
      public int PortNumber { get; set; }
      public eSessionType Protocol { get; set; } = eSessionType.eSTSMTP;
      public eConnectionSecurity ConnectionSecurity { get; set; } = eConnectionSecurity.eCSNone;
      public long SSLCertificateID { get; set; }
      public string ClientCertificatePolicy { get; set; } = "off";
      public string ClientCertificateCAFile { get; set; } = string.Empty;

      /// <summary>The COM flag that stands for eCSTLS, which is how the port is stored.</summary>
      public bool UseSSL
      {
         get { return ConnectionSecurity == eConnectionSecurity.eCSTLS; }
         set { ConnectionSecurity = value ? eConnectionSecurity.eCSTLS : eConnectionSecurity.eCSNone; }
      }

      public void Delete()
      {
         ServerApi.Delete("/api/v1/ports/" + ID).Expect(200, "DELETE /api/v1/ports/" + ID);
      }

      internal static eSessionType ProtocolOf(string name)
      {
         switch (name)
         {
            case "pop3": return eSessionType.eSTPOP3;
            case "imap": return eSessionType.eSTIMAP;
            default: return eSessionType.eSTSMTP;
         }
      }

      internal static string NameOf(eSessionType protocol)
      {
         switch (protocol)
         {
            case eSessionType.eSTPOP3: return "pop3";
            case eSessionType.eSTIMAP: return "imap";
            default: return "smtp";
         }
      }

      internal static eConnectionSecurity SecurityOf(string name)
      {
         switch (name)
         {
            case "tls": return eConnectionSecurity.eCSTLS;
            case "starttls_optional": return eConnectionSecurity.eCSSTARTTLSOptional;
            case "starttls_required": return eConnectionSecurity.eCSSTARTTLSRequired;
            default: return eConnectionSecurity.eCSNone;
         }
      }

      public void Save()
      {
         if (!Unsaved)
         {
            // PUT /api/v1/ports/{id} takes the whole port, so an edit is the same
            // body as a create - and refuses the same things in the same words as
            // the Control Panel, which is what the fixtures assert on.
            var edited = ServerApi.Put("/api/v1/ports/" + ID, Body());

            if (edited.Status == 400 || edited.Status == 409)
               throw new System.Runtime.InteropServices.COMException("Failed to save object. " + edited.Error);

            edited.Expect(200, "PUT /api/v1/ports/" + ID);
            return;
         }

         var answer = ServerApi.Post("/api/v1/ports", Body());

         if (answer.Status == 400 || answer.Status == 409)
            throw new System.Runtime.InteropServices.COMException("Failed to save object. " + answer.Error);

         answer.Expect(201, "POST /api/v1/ports " + PortNumber);
         ID = ServerApi.LongOf(answer.Json.Value, "id");
         Unsaved = false;
      }

      private string Body()
      {
         return "{\"protocol\":" + ServerApi.Quote(NameOf(Protocol)) +
                ",\"address\":" + ServerApi.Quote(Address) +
                ",\"port\":" + PortNumber +
                ",\"connection_security\":" + ServerApi.Quote(RegressionTests.Shared.TestSetup.ConnectionSecurityName(ConnectionSecurity)) +
                ",\"certificate_id\":" + SSLCertificateID + "}";
      }
   }

   // ---- The IP ranges: /api/v1/ipranges ----

   public class SecurityRanges
   {
      private static List<JsonElement> All()
      {
         return ServerApi.Array(ServerApi.Get("/api/v1/ipranges").Expect(200, "GET /api/v1/ipranges"));
      }

      private static SecurityRange From(JsonElement element)
      {
         return new SecurityRange
         {
            ID = ServerApi.LongOf(element, "id"),
            Name = ServerApi.StringOf(element, "name"),
            LowerIP = ServerApi.StringOf(element, "lower"),
            UpperIP = ServerApi.StringOf(element, "upper"),
            Priority = (int) ServerApi.LongOf(element, "priority"),
            AllowSMTPConnections = Flag(element, "allow_smtp"),
            AllowIMAPConnections = Flag(element, "allow_imap"),
            AllowPOP3Connections = Flag(element, "allow_pop3"),
            AllowDeliveryFromLocalToLocal = Flag(element, "deliver_local_to_local"),
            AllowDeliveryFromLocalToRemote = Flag(element, "deliver_local_to_remote"),
            AllowDeliveryFromRemoteToLocal = Flag(element, "deliver_remote_to_local"),
            AllowDeliveryFromRemoteToRemote = Flag(element, "deliver_remote_to_remote"),
            RequireSMTPAuthLocalToLocal = Flag(element, "require_auth_local_to_local"),
            RequireSMTPAuthLocalToExternal = Flag(element, "require_auth_local_to_remote"),
            RequireSMTPAuthExternalToLocal = Flag(element, "require_auth_remote_to_local"),
            RequireSMTPAuthExternalToExternal = Flag(element, "require_auth_remote_to_remote"),
            RequireSSLTLSForAuth = Flag(element, "require_tls_for_auth"),
            SpamProtection = Flag(element, "spam_protection"),
            VirusProtection = Flag(element, "virus_protection"),
            Existing = true
         };
      }

      private static bool Flag(JsonElement element, string name)
      {
         return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
      }

      public int Count => All().Count;

      [System.Runtime.CompilerServices.IndexerName("At")]
      public SecurityRange this[int index] => From(All()[index]);

      public SecurityRange get_Item(int index)
      {
         return From(All()[index]);
      }

      public SecurityRange get_ItemByName(string name)
      {
         foreach (var element in All())
            if (string.Equals(ServerApi.StringOf(element, "name"), name, StringComparison.OrdinalIgnoreCase))
               return From(element);

         return null;
      }

      public SecurityRange Add()
      {
         if (!ServerApi.HasRoute("/api/v1/ipranges", "post"))
            NotOnThisServer.Ignore(NotOnThisServer.NoIpRangeCreate);

         return new SecurityRange();
      }

      public void DeleteByDBID(long id)
      {
         ServerApi.Delete("/api/v1/ipranges/" + id).Expect(200, "DELETE /api/v1/ipranges/" + id);
      }

      public void Delete(int index)
      {
         DeleteByDBID(get_Item(index).ID);
      }

      public void SetDefault()
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoIpRangeDefaults);
      }

      public void Refresh()
      {
      }
   }

   public class SecurityRange
   {
      /// <summary>True for a range read back from the API, which therefore cannot be saved again.</summary>
      internal bool Existing;

      public long ID { get; set; }
      public string Name { get; set; }
      public string LowerIP { get; set; }
      public string UpperIP { get; set; }
      public int Priority { get; set; }
      public bool AllowSMTPConnections { get; set; } = true;
      public bool AllowIMAPConnections { get; set; } = true;
      public bool AllowPOP3Connections { get; set; } = true;
      public bool AllowDeliveryFromLocalToLocal { get; set; } = true;
      public bool AllowDeliveryFromLocalToRemote { get; set; } = true;
      public bool AllowDeliveryFromRemoteToLocal { get; set; } = true;
      public bool AllowDeliveryFromRemoteToRemote { get; set; }
      public bool RequireSMTPAuthLocalToLocal { get; set; }
      public bool RequireSMTPAuthLocalToExternal { get; set; }
      public bool RequireSMTPAuthExternalToLocal { get; set; }
      public bool RequireSMTPAuthExternalToExternal { get; set; } = true;
      public bool RequireSSLTLSForAuth { get; set; }
      public bool SpamProtection { get; set; } = true;
      public bool VirusProtection { get; set; } = true;

      // The names the COM object gives the same two flags.
      public bool EnableSpamProtection
      {
         get { return SpamProtection; }
         set { SpamProtection = value; }
      }

      public bool EnableVirusProtection
      {
         get { return VirusProtection; }
         set { VirusProtection = value; }
      }

      public bool EnableAntiVirus
      {
         get { return VirusProtection; }
         set { VirusProtection = value; }
      }

      public bool EnableAntiSpam
      {
         get { return SpamProtection; }
         set { SpamProtection = value; }
      }
      public bool Expires { get; set; }
      public DateTime ExpiresTime { get; set; }

      /// <summary>
      ///    Answered from the four RequireSMTPAuth* the range enforces, as the COM
      ///    object answers them since 5.1 - a read, not a write.
      /// </summary>
      public bool RequireAuthForDeliveryToLocal => RequireSMTPAuthLocalToLocal || RequireSMTPAuthExternalToLocal;

      public bool RequireAuthForDeliveryToRemote => RequireSMTPAuthLocalToExternal || RequireSMTPAuthExternalToExternal;

      public void Save()
      {
         if (Existing)
            NotOnThisServer.Ignore(NotOnThisServer.NoIpRangeUpdate);

         var body = new StringBuilder("{");
         body.Append("\"name\":").Append(ServerApi.Quote(Name));
         body.Append(",\"lower\":").Append(ServerApi.Quote(LowerIP));
         body.Append(",\"upper\":").Append(ServerApi.Quote(UpperIP));
         body.Append(",\"priority\":").Append(Priority);
         Add(body, "allow_smtp", AllowSMTPConnections);
         Add(body, "allow_imap", AllowIMAPConnections);
         Add(body, "allow_pop3", AllowPOP3Connections);
         Add(body, "deliver_local_to_local", AllowDeliveryFromLocalToLocal);
         Add(body, "deliver_local_to_remote", AllowDeliveryFromLocalToRemote);
         Add(body, "deliver_remote_to_local", AllowDeliveryFromRemoteToLocal);
         Add(body, "deliver_remote_to_remote", AllowDeliveryFromRemoteToRemote);
         Add(body, "require_auth_local_to_local", RequireSMTPAuthLocalToLocal);
         Add(body, "require_auth_local_to_remote", RequireSMTPAuthLocalToExternal);
         Add(body, "require_auth_remote_to_local", RequireSMTPAuthExternalToLocal);
         Add(body, "require_auth_remote_to_remote", RequireSMTPAuthExternalToExternal);
         Add(body, "require_tls_for_auth", RequireSSLTLSForAuth);
         Add(body, "spam_protection", SpamProtection);
         Add(body, "virus_protection", VirusProtection);
         body.Append('}');

         var answer = ServerApi.Post("/api/v1/ipranges", body.ToString());

         if (answer.Status == 400)
            throw new System.Runtime.InteropServices.COMException("Failed to save object. " + answer.Error);

         answer.Expect(201, "POST /api/v1/ipranges " + Name);
         ID = ServerApi.LongOf(answer.Json.Value, "id");
         Existing = true;
      }

      private static void Add(StringBuilder body, string name, bool value)
      {
         body.Append(',').Append(ServerApi.Quote(name)).Append(':').Append(value ? "true" : "false");
      }

      public void Delete()
      {
         ServerApi.Delete("/api/v1/ipranges/" + ID).Expect(200, "DELETE /api/v1/ipranges/" + ID);
      }
   }

   // ---- The certificates: /api/v1/certificates ----

   public class SSLCertificates
   {
      private static List<JsonElement> All()
      {
         return ServerApi.Array(ServerApi.Get("/api/v1/certificates").Expect(200, "GET /api/v1/certificates"));
      }

      public int Count => All().Count;

      [System.Runtime.CompilerServices.IndexerName("At")]
      public SSLCertificate this[int index] => From(All()[index]);

      public SSLCertificate get_Item(int index)
      {
         return From(All()[index]);
      }

      public SSLCertificate get_ItemByName(string name)
      {
         foreach (var element in All())
            if (string.Equals(ServerApi.StringOf(element, "name"), name, StringComparison.OrdinalIgnoreCase))
               return From(element);

         return null;
      }

      private static SSLCertificate From(JsonElement element)
      {
         return new SSLCertificate
         {
            ID = ServerApi.LongOf(element, "id"),
            Name = ServerApi.StringOf(element, "name"),
            CertificateFile = ServerApi.StringOf(element, "certificate_file"),
            PrivateKeyFile = ServerApi.StringOf(element, "private_key_file"),
            Existing = true
         };
      }

      public SSLCertificate Add()
      {
         if (!ServerApi.HasRoute("/api/v1/certificates", "post"))
            NotOnThisServer.Ignore(NotOnThisServer.NoCertificateCreate);

         return new SSLCertificate();
      }

      public void DeleteByDBID(long id)
      {
         ServerApi.Delete("/api/v1/certificates/" + id).Expect(200, "DELETE /api/v1/certificates/" + id);
      }

      public void Delete(int index)
      {
         DeleteByDBID(get_Item(index).ID);
      }

      public void Clear()
      {
         foreach (var element in All())
         {
            var id = ServerApi.LongOf(element, "id");
            var answer = ServerApi.Delete("/api/v1/certificates/" + id);

            // A certificate a listener is bound to cannot go; the Windows Clear has
            // the same constraint and the fixtures clear the ports first.
            if (answer.Status != 200 && answer.Status != 404)
               throw new System.Runtime.InteropServices.COMException("Failed to delete object. " + answer.Error);
         }
      }

      public void Refresh()
      {
      }
   }

   public class SSLCertificate
   {
      internal bool Existing;

      public long ID { get; set; }
      public string Name { get; set; }
      public string CertificateFile { get; set; }
      public string PrivateKeyFile { get; set; }
      public string PrivateKeyPassword { get; set; }

      public void Delete()
      {
         ServerApi.Delete("/api/v1/certificates/" + ID).Expect(200, "DELETE /api/v1/certificates/" + ID);
      }

      public void Save()
      {
         if (Existing)
            NotOnThisServer.Ignore(NotOnThisServer.NoCertificateUpdate);

         var answer = ServerApi.Post("/api/v1/certificates",
            "{\"name\":" + ServerApi.Quote(Name) +
            ",\"certificate_file\":" + ServerApi.Quote(CertificateFile) +
            ",\"private_key_file\":" + ServerApi.Quote(PrivateKeyFile) + "}");

         if (answer.Status == 400 || answer.Status == 409)
            throw new System.Runtime.InteropServices.COMException("Failed to save object. " + answer.Error);

         answer.Expect(201, "POST /api/v1/certificates " + Name);
         ID = ServerApi.LongOf(answer.Json.Value, "id");
         Existing = true;
      }
   }

   // ---- The routes: /api/v1/routes ----

   public class Routes
   {
      private static List<JsonElement> All()
      {
         return ServerApi.Array(ServerApi.Get("/api/v1/routes").Expect(200, "GET /api/v1/routes"));
      }

      private static Route From(JsonElement element)
      {
         return new Route
         {
            ID = ServerApi.LongOf(element, "id"),
            DomainName = ServerApi.StringOf(element, "domain_name"),
            TargetSMTPHost = ServerApi.StringOf(element, "target_smtp_host"),
            TargetSMTPPort = (int) ServerApi.LongOf(element, "target_smtp_port"),
            NumberOfTries = (int) ServerApi.LongOf(element, "number_of_tries"),
            MinutesBetweenTry = (int) ServerApi.LongOf(element, "minutes_between_try"),
            AllAddresses = element.TryGetProperty("all_addresses", out var all) && all.ValueKind == JsonValueKind.True,
            TreatRecipientAsLocalDomain = element.TryGetProperty("treat_recipient_as_local_domain", out var r) &&
                                          r.ValueKind == JsonValueKind.True,
            TreatSenderAsLocalDomain = element.TryGetProperty("treat_sender_as_local_domain", out var s) &&
                                       s.ValueKind == JsonValueKind.True,
            ConnectionSecurity = TCPIPPort.SecurityOf(ServerApi.StringOf(element, "connection_security")),
            Existing = true
         };
      }

      public int Count => All().Count;

      [System.Runtime.CompilerServices.IndexerName("At")]
      public Route this[int index] => From(All()[index]);

      public Route get_Item(int index)
      {
         return From(All()[index]);
      }

      public Route get_ItemByName(string domainName)
      {
         foreach (var element in All())
            if (string.Equals(ServerApi.StringOf(element, "domain_name"), domainName, StringComparison.OrdinalIgnoreCase))
               return From(element);

         return null;
      }

      public Route ItemByName(string domainName)
      {
         return get_ItemByName(domainName);
      }

      public Route Add()
      {
         if (!ServerApi.HasRouteWriteRoutes)
            NotOnThisServer.Ignore(NotOnThisServer.NoRouteCreate);

         return new Route();
      }

      public void DeleteByDBID(long id)
      {
         ServerApi.Delete("/api/v1/routes/" + id).Expect(200, "DELETE /api/v1/routes/" + id);
      }

      public void Delete(int index)
      {
         DeleteByDBID(get_Item(index).ID);
      }

      public void Refresh()
      {
      }
   }

   // ---- The global rules: /api/v1/rules ----

   public class Rules
   {
      private static List<JsonElement> All()
      {
         return ServerApi.Array(ServerApi.Get("/api/v1/rules").Expect(200, "GET /api/v1/rules"));
      }

      public int Count => All().Count;

      [System.Runtime.CompilerServices.IndexerName("At")]
      public Rule this[int index] => Rule.From(All()[index]);

      public Rule get_Item(int index)
      {
         return Rule.From(All()[index]);
      }

      public Rule get_ItemByName(string name)
      {
         foreach (var element in All())
            if (string.Equals(ServerApi.StringOf(element, "name"), name, StringComparison.Ordinal))
               return Rule.From(element);

         return null;
      }

      public Rule Add()
      {
         if (!ServerApi.HasRoute("/api/v1/rules", "post"))
            NotOnThisServer.Ignore(NotOnThisServer.NoRuleCreate);

         return new Rule();
      }

      public Rule get_ItemByDBID(long id)
      {
         foreach (var element in All())
            if (ServerApi.LongOf(element, "id") == id)
               return Rule.From(element);

         return null;
      }

      public void DeleteByDBID(long id)
      {
         ServerApi.Delete("/api/v1/rules/" + id).Expect(200, "DELETE /api/v1/rules/" + id);
      }

      public void Delete(int index)
      {
         DeleteByDBID(get_Item(index).ID);
      }

      public void Clear()
      {
         foreach (var element in All())
         {
            var id = ServerApi.LongOf(element, "id");
            ServerApi.Delete("/api/v1/rules/" + id).Expect(200, "DELETE /api/v1/rules/" + id);
         }
      }

      public void Refresh()
      {
      }
   }

   public enum eRuleMatchType
   {
      eMTUnknown = 0,
      eMTEquals = 1,
      eMTContains = 2,
      eMTLessThan = 3,
      eMTGreaterThan = 4,
      eMTRegExMatch = 5,
      eMTNotContains = 6,
      eMTNotEquals = 7,
      eMTWildcard = 8
   }

   public enum eRulePredefinedField
   {
      eFTUnknown = 0,
      eFTFrom = 1,
      eFTTo = 2,
      eFTCC = 3,
      eFTSubject = 4,
      eFTBody = 5,
      eFTMessageSize = 6,
      eFTRecipientList = 7,
      eFTDeliveryAttempts = 8
   }

   public enum eRuleActionType
   {
      eRAUnknown = 0,
      eRADeleteEmail = 1,
      eRAForwardEmail = 2,
      eRAReply = 3,
      eRAMoveToImapFolder = 4,
      eRARunScriptFunction = 5,
      eRAStopRuleProcessing = 6,
      eRASetHeaderValue = 7,
      eRASendUsingRoute = 8,
      eRACreateCopy = 9,
      eRABindToAddress = 10
   }

   public enum eDistributionListMode
   {
      eLMPublic = 0,
      eLMMembership = 1,
      eLMAnnouncement = 2,
      eLMDomainMembers = 3,
      eLMServerMembers = 4
   }

   public enum eAdminLevel
   {
      hAdminLevelNormal = 0,
      hAdminLevelDomainAdmin = 1,
      hAdminLevelServerAdmin = 2
   }

   public enum eAntivirusAction
   {
      hDeleteEmail = 0,
      hDeleteAttachments = 1
   }

   public enum eServerState
   {
      hStateUnknown = 0,
      hStateStopped = 1,
      hStateStarting = 2,
      hStateRunning = 3,
      hStateStopping = 4
   }

   public enum eDKIMAlgorithm
   {
      eSHA1 = 1,
      eSHA256 = 2
   }

   public enum eMaintenanceOperation
   {
      eUpdateIMAPFolderUID = 1
   }

   public class Rule
   {
      internal bool Existing;

      public long ID { get; set; }
      public long AccountID { get; set; }
      public string Name { get; set; }
      public bool Active { get; set; } = true;
      public bool UseAND { get; set; } = true;
      public RuleCriterias Criterias { get; } = new RuleCriterias();
      public RuleActions Actions { get; } = new RuleActions();

      internal static Rule From(JsonElement element)
      {
         return new Rule
         {
            ID = ServerApi.LongOf(element, "id"),
            Name = ServerApi.StringOf(element, "name"),
            Active = element.TryGetProperty("active", out var active) && active.ValueKind == JsonValueKind.True,
            UseAND = !element.TryGetProperty("all_criteria", out var all) || all.ValueKind != JsonValueKind.False,
            Existing = true
         };
      }

      public void Save()
      {
         var body = new StringBuilder("{\"name\":");
         body.Append(ServerApi.Quote(Name));
         body.Append(",\"active\":").Append(Active ? "true" : "false");
         body.Append(",\"all_criteria\":").Append(UseAND ? "true" : "false");
         body.Append(",\"criteria\":[");
         for (var i = 0; i < Criterias.Items.Count; i++)
         {
            if (i > 0)
               body.Append(',');
            body.Append(Criterias.Items[i].ToJson());
         }
         body.Append("],\"actions\":[");
         for (var i = 0; i < Actions.Items.Count; i++)
         {
            if (i > 0)
               body.Append(',');
            body.Append(Actions.Items[i].ToJson());
         }
         body.Append("]}");

         var answer = Existing
            ? ServerApi.Put("/api/v1/rules/" + ID, body.ToString())
            : ServerApi.Post("/api/v1/rules", body.ToString());

         if (answer.Status == 400)
            throw new System.Runtime.InteropServices.COMException("Failed to save object. " + answer.Error);

         if (Existing)
         {
            answer.Expect(200, "PUT /api/v1/rules/" + ID);
            return;
         }

         answer.Expect(201, "POST /api/v1/rules " + Name);
         ID = ServerApi.LongOf(answer.Json.Value, "id");
         Existing = true;
      }

      public void Delete()
      {
         ServerApi.Delete("/api/v1/rules/" + ID).Expect(200, "DELETE /api/v1/rules/" + ID);
      }
   }

   public class RuleCriterias
   {
      internal readonly List<RuleCriteria> Items = new List<RuleCriteria>();

      public int Count => Items.Count;

      [System.Runtime.CompilerServices.IndexerName("At")]
      public RuleCriteria this[int index] => Items[index];

      public RuleCriteria get_Item(int index)
      {
         return Items[index];
      }

      public RuleCriteria Add()
      {
         var criteria = new RuleCriteria();
         Items.Add(criteria);
         return criteria;
      }

      public void Clear()
      {
         Items.Clear();
      }
   }

   public class RuleCriteria
   {
      /// <summary>
      ///    True when the criterion names one of the predefined fields rather than a
      ///    header; the route decides the same way, from whether header is given.
      /// </summary>
      public bool UsePredefined
      {
         get { return string.IsNullOrEmpty(HeaderField); }
         set
         {
            if (value)
               HeaderField = null;
         }
      }

      public eRulePredefinedField PredefinedField { get; set; }
      public string HeaderField { get; set; }
      public eRuleMatchType MatchType { get; set; }
      public string MatchValue { get; set; }

      /// <summary>Nothing to send on its own: the rule is written whole by Rule.Save.</summary>
      public void Save()
      {
      }

      internal string ToJson()
      {
         var field = string.IsNullOrEmpty(HeaderField) ? FieldName(PredefinedField) : "header";

         var json = new StringBuilder("{\"field\":");
         json.Append(ServerApi.Quote(field));
         if (field == "header")
            json.Append(",\"header\":").Append(ServerApi.Quote(HeaderField));
         json.Append(",\"match\":").Append(ServerApi.Quote(MatchName(MatchType)));
         json.Append(",\"value\":").Append(ServerApi.Quote(MatchValue));
         json.Append('}');
         return json.ToString();
      }

      private static string FieldName(eRulePredefinedField field)
      {
         switch (field)
         {
            case eRulePredefinedField.eFTTo: return "to";
            case eRulePredefinedField.eFTCC: return "cc";
            case eRulePredefinedField.eFTSubject: return "subject";
            case eRulePredefinedField.eFTBody: return "body";
            case eRulePredefinedField.eFTMessageSize: return "message_size";
            case eRulePredefinedField.eFTRecipientList: return "recipient_list";
            case eRulePredefinedField.eFTDeliveryAttempts: return "delivery_attempts";
            default: return "from";
         }
      }

      private static string MatchName(eRuleMatchType match)
      {
         switch (match)
         {
            case eRuleMatchType.eMTNotEquals: return "not_equals";
            case eRuleMatchType.eMTContains: return "contains";
            case eRuleMatchType.eMTNotContains: return "not_contains";
            case eRuleMatchType.eMTLessThan: return "less_than";
            case eRuleMatchType.eMTGreaterThan: return "greater_than";
            case eRuleMatchType.eMTRegExMatch: return "regex";
            case eRuleMatchType.eMTWildcard: return "wildcard";
            default: return "equals";
         }
      }
   }

   public class RuleActions
   {
      internal readonly List<RuleAction> Items = new List<RuleAction>();

      public int Count => Items.Count;

      [System.Runtime.CompilerServices.IndexerName("At")]
      public RuleAction this[int index] => Items[index];

      public RuleAction get_Item(int index)
      {
         return Items[index];
      }

      public RuleAction Add()
      {
         var action = new RuleAction();
         Items.Add(action);
         return action;
      }

      public void Clear()
      {
         Items.Clear();
      }
   }

   public class RuleAction
   {
      public eRuleActionType Type { get; set; }
      public string To { get; set; }
      public string FromName { get; set; }
      public string FromAddress { get; set; }
      public string Subject { get; set; }
      public string Body { get; set; }
      public string IMAPFolder { get; set; }
      public string ScriptFunctionName { get; set; }

      public string ScriptFunction
      {
         get { return ScriptFunctionName; }
         set { ScriptFunctionName = value; }
      }
      public string HeaderName { get; set; }
      public string Value { get; set; }
      public long RouteID { get; set; }

      public void Save()
      {
      }

      internal string ToJson()
      {
         var json = new StringBuilder("{\"type\":");
         json.Append(ServerApi.Quote(TypeName(Type)));

         switch (Type)
         {
            case eRuleActionType.eRAForwardEmail:
               json.Append(",\"to\":").Append(ServerApi.Quote(To));
               break;
            case eRuleActionType.eRAReply:
               json.Append(",\"from_name\":").Append(ServerApi.Quote(FromName ?? string.Empty));
               json.Append(",\"from_address\":").Append(ServerApi.Quote(FromAddress));
               json.Append(",\"subject\":").Append(ServerApi.Quote(Subject ?? string.Empty));
               json.Append(",\"body\":").Append(ServerApi.Quote(Body ?? string.Empty));
               break;
            case eRuleActionType.eRAMoveToImapFolder:
               json.Append(",\"folder\":").Append(ServerApi.Quote(IMAPFolder));
               break;
            case eRuleActionType.eRARunScriptFunction:
               json.Append(",\"script_function\":").Append(ServerApi.Quote(ScriptFunctionName));
               break;
            case eRuleActionType.eRASetHeaderValue:
               json.Append(",\"header\":").Append(ServerApi.Quote(HeaderName));
               json.Append(",\"value\":").Append(ServerApi.Quote(Value ?? string.Empty));
               break;
            case eRuleActionType.eRASendUsingRoute:
               json.Append(",\"route_id\":").Append(RouteID);
               break;
            case eRuleActionType.eRABindToAddress:
               json.Append(",\"value\":").Append(ServerApi.Quote(Value ?? string.Empty));
               break;
         }

         json.Append('}');
         return json.ToString();
      }

      private static string TypeName(eRuleActionType type)
      {
         switch (type)
         {
            case eRuleActionType.eRAMoveToImapFolder: return "move_to_folder";
            case eRuleActionType.eRAForwardEmail: return "forward";
            case eRuleActionType.eRAReply: return "reply";
            case eRuleActionType.eRASendUsingRoute: return "send_using_route";
            case eRuleActionType.eRARunScriptFunction: return "script_function";
            case eRuleActionType.eRAStopRuleProcessing: return "stop";
            case eRuleActionType.eRASetHeaderValue: return "set_header";
            case eRuleActionType.eRABindToAddress: return "bind_to_address";
            case eRuleActionType.eRACreateCopy: return "copy";
            default: return "delete";
         }
      }
   }

   // ---- An account's folders: GET /api/v1/me/folders, and nothing that writes ----

   /// <summary>
   ///    Read through the account's own GET /api/v1/me/folders, which lists every
   ///    folder with its name, path, message count and unseen count. Nothing here
   ///    creates or deletes a folder: the REST API has no POST or DELETE under
   ///    /api/v1/me/folders, and a folder made over IMAP instead of through the
   ///    interface the fixture asked for would be a different test.
   /// </summary>
   public class IMAPFolders
   {
      private readonly Account _account;
      private readonly long _parentId;

      internal IMAPFolders(Account account, long parentId)
      {
         _account = account;
         _parentId = parentId;
      }

      /// <summary>
      ///    The route answers with a tree, not a flat list: "folders" holds the
      ///    top-level folders and each entry carries its own children under
      ///    "subfolders". So the root collection is that array, and a subfolder
      ///    collection is the "subfolders" of the entry with that id, found by
      ///    walking the tree. (parent_id is not what selects them: a top-level
      ///    folder's parent id is -1, not 0.)
      /// </summary>
      private List<JsonElement> All()
      {
         _account.RequireOwnCredentials("lists the account's folders");

         var answer = ServerApi.AsAccount(_account.Address, _account.Password, HttpMethod.Get, "/api/v1/me/folders")
            .Expect(200, "GET /api/v1/me/folders as " + _account.Address);

         var top = ServerApi.Array(answer, "folders");

         if (_parentId == RootParent)
            return top;

         var parent = Find(top, _parentId);
         return parent.HasValue ? ServerApi.Array(parent.Value, "subfolders") : new List<JsonElement>();
      }

      /// <summary>What the root collection uses for "no parent"; no folder has this id.</summary>
      internal const long RootParent = -1;

      private static JsonElement? Find(List<JsonElement> folders, long id)
      {
         foreach (var folder in folders)
         {
            if (ServerApi.LongOf(folder, "id") == id)
               return folder;

            var found = Find(ServerApi.Array(folder, "subfolders"), id);
            if (found.HasValue)
               return found;
         }

         return null;
      }

      public int Count => All().Count;

      [System.Runtime.CompilerServices.IndexerName("At")]
      public IMAPFolder this[int index] => From(All()[index]);

      public IMAPFolder get_Item(int index)
      {
         return From(All()[index]);
      }

      public IMAPFolder get_ItemByName(string name)
      {
         foreach (var folder in All())
            if (string.Equals(ServerApi.StringOf(folder, "name"), name, StringComparison.OrdinalIgnoreCase))
               return From(folder);

         throw new System.Runtime.InteropServices.COMException("Item not found. " + name);
      }

      private IMAPFolder From(JsonElement element)
      {
         return new IMAPFolder
         {
            ID = ServerApi.LongOf(element, "id"),
            Name = ServerApi.StringOf(element, "name"),
            CurrentUID = ServerApi.LongOf(element, "uidvalidity"),
            ParentID = ServerApi.LongOf(element, "parent_id"),
            Account = _account
         };
      }

      public IMAPFolder Add(string name)
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoFolderWrite);
         return null;
      }

      public void DeleteByDBID(long id)
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoFolderWrite);
      }

      public void Delete(int index)
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoFolderWrite);
      }

      public void Refresh()
      {
      }
   }

   public class IMAPFolder
   {
      internal Account Account;

      public long ID { get; set; }
      public string Name { get; set; }
      public long CurrentUID { get; set; }

      /// <summary>
      ///    The id of the folder this one hangs under, or -1 at the top level -
      ///    which is the route's own spelling of "no parent" and the same value
      ///    IMAPFolders uses for the root collection.
      /// </summary>
      public long ParentID { get; set; }

      public IMAPFolders SubFolders => new IMAPFolders(Account, ID);

      public IMAPFolderPermissions Permissions { get; } = new IMAPFolderPermissions();

      public Messages Messages => new Messages(Account, ID);

      public void Save()
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoFolderWrite);
      }

      public void Delete()
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoFolderWrite);
      }
   }
}

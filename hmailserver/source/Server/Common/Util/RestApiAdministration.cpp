// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// The REST API's domain update in full, the domain aliases and the IP range update: what the Control Panel's domain and IP range pages do. See RestApiServer.h.
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// Three things the write surface of wave 162 left out, each the whole of a
// COM object rather than two of its fields:
//
//   - PUT /api/v1/domains/{domain} took active and postmaster and said of
//     itself that the name could not be changed there. It now takes every
//     scalar InterfaceDomain saves - the limits and their switches, plus
//     addressing, greylisting, the signature, DKIM, retention, the relay and
//     the domain's automatic reply - and a new name: renaming is
//     Domain::SetName and PersistentDomain::SaveObject, which runs the same
//     cascade (NameChanger::RenameDomain) that InterfaceDomain::put_Name and
//     Save run, so every address in the domain follows it. active stays
//     required, as the route always had it, so that the callers written to
//     the two-field contract keep working unchanged.
//   - GET/POST /api/v1/domains/{domain}/domain-aliases and DELETE
//     .../domain-aliases/{name}: InterfaceDomainAliases::Add + Save, and
//     Delete, through PersistentDomainAlias, which marks the alias cache for
//     reload so the next RCPT TO sees the alias.
//   - PUT /api/v1/ipranges/{id}: the setters and Save of InterfaceSecurityRange,
//     through PersistentSecurityRange::SaveObject with its own limitation
//     check; a field left out keeps its value.
//
// Bodies are parsed as JSON documents. A member present with the wrong type,
// or a key that is not a field, is a 400 naming it, and nothing is applied
// until everything in the body has been read.

#include "StdAfx.h"
#include "RestApiServer.h"
#include "JsonDocument.h"
#include "Unicode.h"
#include "../BO/Domains.h"
#include "../BO/Domain.h"
#include "../BO/DomainAliases.h"
#include "../BO/DomainAlias.h"
#include "../BO/SecurityRanges.h"
#include "../BO/SecurityRange.h"
#include "../Persistence/PersistentDomain.h"
#include "../Persistence/PersistentDomainAlias.h"
#include "../Persistence/PersistentSecurityRange.h"
#include "../Persistence/PersistenceMode.h"
#include "../TCPIP/IPAddress.h"

#include <cmath>
#include <functional>
#include <string>
#include <vector>

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   namespace
   {
      typedef std::function<AnsiString(const String &)> Quote;

      AnsiString ErrorBody(const Quote &quote, const String &sentence)
      {
         AnsiString body;
         body.Format("{\"error\":\"%hs\"}", quote(sentence).c_str());
         return body;
      }

      String Utf8ToString(const std::string &utf8)
      {
         String value;
         Unicode::MultiByteToWide(AnsiString(utf8.c_str()), value);
         return value;
      }

      bool ParseObjectBody(const AnsiString &requestBody, JsonValue &body)
      {
         std::string parseError;
         std::string text(requestBody.c_str(), (size_t) requestBody.GetLength());
         return JsonValue::Parse(text, body, parseError) && body.IsObject();
      }

      // The typed reads: untouched when the member is absent or null; false,
      // with error naming the member, when it is present with another type.
      bool ReadString(const JsonValue &object, const char *key, String &out, AnsiString &error)
      {
         const JsonValue *member = object.Get(key);
         if (!member || member->IsNull())
            return true;

         if (!member->IsString())
         {
            error.Format("%hs must be a string", key);
            return false;
         }

         out = Utf8ToString(member->AsString());
         return true;
      }

      bool ReadBool(const JsonValue &object, const char *key, bool &out, AnsiString &error)
      {
         const JsonValue *member = object.Get(key);
         if (!member || member->IsNull())
            return true;

         if (!member->IsBool())
         {
            error.Format("%hs must be true or false", key);
            return false;
         }

         out = member->AsBool();
         return true;
      }

      bool ReadInteger(const JsonValue &object, const char *key, long minimum, long maximum, long &out, AnsiString &error)
      {
         const JsonValue *member = object.Get(key);
         if (!member || member->IsNull())
            return true;

         double number = member->IsNumber() ? member->AsNumber() : 0.0;

         if (!member->IsNumber() || std::floor(number) != number ||
             number < (double) minimum || number > (double) maximum)
         {
            error.Format("%hs must be a whole number between %ld and %ld", key, minimum, maximum);
            return false;
         }

         out = (long) number;
         return true;
      }

      bool UnknownKey(const JsonValue &object, const char *const *known, size_t knownCount, AnsiString &error)
      {
         for (const std::pair<std::string, JsonValue> &member : object.Members())
         {
            bool found = false;
            for (size_t i = 0; i < knownCount; i++)
            {
               if (member.first == known[i])
               {
                  found = true;
                  break;
               }
            }
            if (!found)
            {
               error = AnsiString("unknown field: ") + AnsiString(member.first.substr(0, 48).c_str());
               return true;
            }
         }
         return false;
      }

      const char *ConnectionSecurityWord(ConnectionSecurity security)
      {
         switch (security)
         {
         case CSSSL:
            return "tls";
         case CSSTARTTLSOptional:
            return "starttls_optional";
         case CSSTARTTLSRequired:
            return "starttls_required";
         case CSNone:
         default:
            return "none";
         }
      }

      bool ParseConnectionSecurityWord(const String &word, ConnectionSecurity &security)
      {
         AnsiString value = word;

         if (value.CompareNoCase("none") == 0)
            security = CSNone;
         else if (value.CompareNoCase("tls") == 0)
            security = CSSSL;
         else if (value.CompareNoCase("starttls_optional") == 0)
            security = CSSTARTTLSOptional;
         else if (value.CompareNoCase("starttls_required") == 0)
            security = CSSTARTTLSRequired;
         else
            return false;

         return true;
      }

      const char *SignatureMethodWord(Domain::DomainSignatureMethod method)
      {
         switch (method)
         {
         case Domain::SMOverwriteAccountSignature:
            return "overwrite";
         case Domain::SMSetIfNotSpecifiedInAccount:
            return "set_if_not_specified";
         case 3:
            // eSMAppendToAccountSignature over COM, a value the business object's
            // enum does not name but the Control Panel stores.
            return "append";
         default:
            return "unknown";
         }
      }

      const char *DkimAlgorithmWord(int algorithm)
      {
         // HashCreator::HashType: SHA1 is 1, SHA256 is 2. Nothing else signs.
         return algorithm == 1 ? "sha1" : "sha256";
      }

      // The domain's members, the names the API uses. The relay password is
      // accepted and never emitted.
      const char *const DomainKeys[] =
      {
         "name", "active", "postmaster", "max_message_size_kb", "max_size_mb", "max_account_size_mb",
         "max_accounts", "max_aliases", "max_lists", "max_accounts_enabled", "max_aliases_enabled", "max_lists_enabled",
         "plus_addressing_enabled", "plus_addressing_character", "use_greylisting",
         "signature_enabled", "signature_method", "signature_plain_text", "signature_html",
         "signature_add_to_replies", "signature_add_to_local_mail",
         "dkim_enabled", "dkim_selector", "dkim_private_key_file", "dkim_signing_algorithm",
         "message_retention_days", "relay_host", "relay_port", "relay_requires_auth", "relay_username",
         "relay_password", "relay_connection_security",
         "vacation_enabled", "vacation_subject", "vacation_message"
      };

      // Applies the body to the domain. Everything is read and checked first;
      // the first problem is the answer and nothing has been applied when it
      // is given. renamed says whether the name changed, for the log line.
      bool ApplyDomainBody(const JsonValue &body, std::shared_ptr<Domain> domain, AnsiString &error, bool &renamed)
      {
         if (UnknownKey(body, DomainKeys, sizeof(DomainKeys) / sizeof(DomainKeys[0]), error))
            return false;

         String name = domain->GetName(), postmaster = domain->GetPostmaster();
         String plusChar = domain->GetPlusAddressingChar();
         String signaturePlain = domain->GetSignaturePlainText(), signatureHtml = domain->GetSignatureHTML();
         String signatureMethod, dkimAlgorithm, relaySecurity;
         String dkimSelector = String(domain->GetDKIMSelector()), dkimKeyFile = domain->GetDKIMPrivateKeyFile();
         String relayHost = domain->GetRelayHost(), relayUser = domain->GetRelayUsername(), relayPassword = domain->GetRelayPassword();
         String vacationSubject = domain->GetVacationSubject(), vacationMessage = domain->GetVacationMessage();

         if (!ReadString(body, "name", name, error) ||
             !ReadString(body, "postmaster", postmaster, error) ||
             !ReadString(body, "plus_addressing_character", plusChar, error) ||
             !ReadString(body, "signature_method", signatureMethod, error) ||
             !ReadString(body, "signature_plain_text", signaturePlain, error) ||
             !ReadString(body, "signature_html", signatureHtml, error) ||
             !ReadString(body, "dkim_selector", dkimSelector, error) ||
             !ReadString(body, "dkim_private_key_file", dkimKeyFile, error) ||
             !ReadString(body, "dkim_signing_algorithm", dkimAlgorithm, error) ||
             !ReadString(body, "relay_host", relayHost, error) ||
             !ReadString(body, "relay_username", relayUser, error) ||
             !ReadString(body, "relay_password", relayPassword, error) ||
             !ReadString(body, "relay_connection_security", relaySecurity, error) ||
             !ReadString(body, "vacation_subject", vacationSubject, error) ||
             !ReadString(body, "vacation_message", vacationMessage, error))
            return false;

         long maxMessageSize = domain->GetMaxMessageSize(), maxSize = domain->GetMaxSizeMB(), maxAccountSize = domain->GetMaxAccountSize();
         long maxAccounts = domain->GetMaxNoOfAccounts(), maxAliases = domain->GetMaxNoOfAliases(), maxLists = domain->GetMaxNoOfDistributionLists();
         long retention = domain->GetMessageRetentionDays(), relayPort = domain->GetRelayPort();

         if (!ReadInteger(body, "max_message_size_kb", 0, 2000000000L, maxMessageSize, error) ||
             !ReadInteger(body, "max_size_mb", 0, 2000000000L, maxSize, error) ||
             !ReadInteger(body, "max_account_size_mb", 0, 2000000000L, maxAccountSize, error) ||
             !ReadInteger(body, "max_accounts", 0, 2000000000L, maxAccounts, error) ||
             !ReadInteger(body, "max_aliases", 0, 2000000000L, maxAliases, error) ||
             !ReadInteger(body, "max_lists", 0, 2000000000L, maxLists, error) ||
             !ReadInteger(body, "message_retention_days", 0, 100000, retention, error) ||
             !ReadInteger(body, "relay_port", 0, 65535, relayPort, error))
            return false;

         bool active = domain->GetIsActive();
         bool maxAccountsEnabled = domain->GetMaxNoOfAccountsEnabled(), maxAliasesEnabled = domain->GetMaxNoOfAliasesEnabled();
         bool maxListsEnabled = domain->GetMaxNoOfDistributionListsEnabled();
         bool plusAddressing = domain->GetUsePlusAddressing(), greylisting = domain->GetASUseGreyListing();
         bool signatureEnabled = domain->GetEnableSignature(), signatureReplies = domain->GetAddSignaturesToReplies();
         bool signatureLocal = domain->GetAddSignaturesToLocalMail(), dkimEnabled = domain->GetDKIMEnabled();
         bool relayAuth = domain->GetRelayRequiresAuth(), vacationOn = domain->GetVacationMessageIsOn();

         if (!ReadBool(body, "active", active, error) ||
             !ReadBool(body, "max_accounts_enabled", maxAccountsEnabled, error) ||
             !ReadBool(body, "max_aliases_enabled", maxAliasesEnabled, error) ||
             !ReadBool(body, "max_lists_enabled", maxListsEnabled, error) ||
             !ReadBool(body, "plus_addressing_enabled", plusAddressing, error) ||
             !ReadBool(body, "use_greylisting", greylisting, error) ||
             !ReadBool(body, "signature_enabled", signatureEnabled, error) ||
             !ReadBool(body, "signature_add_to_replies", signatureReplies, error) ||
             !ReadBool(body, "signature_add_to_local_mail", signatureLocal, error) ||
             !ReadBool(body, "dkim_enabled", dkimEnabled, error) ||
             !ReadBool(body, "relay_requires_auth", relayAuth, error) ||
             !ReadBool(body, "vacation_enabled", vacationOn, error))
            return false;

         Domain::DomainSignatureMethod method = domain->GetSignatureMethod();
         if (!signatureMethod.IsEmpty())
         {
            AnsiString word = signatureMethod;
            if (word.CompareNoCase("overwrite") == 0)
               method = Domain::SMOverwriteAccountSignature;
            else if (word.CompareNoCase("set_if_not_specified") == 0)
               method = Domain::SMSetIfNotSpecifiedInAccount;
            else if (word.CompareNoCase("append") == 0)
               method = (Domain::DomainSignatureMethod) 3;
            else
            {
               error = "signature_method must be set_if_not_specified, overwrite or append";
               return false;
            }
         }

         int algorithm = domain->GetDKIMSigningAlgorithm();
         if (!dkimAlgorithm.IsEmpty())
         {
            AnsiString word = dkimAlgorithm;
            if (word.CompareNoCase("sha1") == 0)
               algorithm = 1;
            else if (word.CompareNoCase("sha256") == 0)
               algorithm = 2;
            else
            {
               error = "dkim_signing_algorithm must be sha1 or sha256";
               return false;
            }
         }

         ConnectionSecurity security = domain->GetRelayConnectionSecurity();
         if (!relaySecurity.IsEmpty() && !ParseConnectionSecurityWord(relaySecurity, security))
         {
            error = "relay_connection_security must be one of none, starttls_optional, starttls_required, tls";
            return false;
         }

         // As InterfaceDomain::put_Name trims; an empty name is no domain.
         name.Trim();
         if (name.IsEmpty())
         {
            error = "name must not be empty";
            return false;
         }

         plusChar.Trim();
         if (plusChar.GetLength() > 1)
         {
            error = "plus_addressing_character is one character";
            return false;
         }

         renamed = name.CompareNoCase(domain->GetName()) != 0;

         domain->SetName(name);
         domain->SetIsActive(active);
         domain->SetPostmaster(postmaster);
         domain->SetMaxMessageSize((int) maxMessageSize);
         domain->SetMaxSizeMB((int) maxSize);
         domain->SetMaxAccountSize((int) maxAccountSize);
         domain->SetMaxNoOfAccounts((int) maxAccounts);
         domain->SetMaxNoOfAliases((int) maxAliases);
         domain->SetMaxNoOfDistributionLists((int) maxLists);
         domain->SetMaxNoOfAccountsEnabled(maxAccountsEnabled);
         domain->SetMaxNoOfAliasesEnabled(maxAliasesEnabled);
         domain->SetMaxNoOfDistributionListsEnabled(maxListsEnabled);
         domain->SetUsePlusAddressing(plusAddressing);
         domain->SetPlusAddressingChar(plusChar);
         domain->SetASUseGreyListing(greylisting);
         domain->SetEnableSignature(signatureEnabled);
         domain->SetSignatureMethod(method);
         domain->SetSignaturePlainText(signaturePlain);
         domain->SetSignatureHTML(signatureHtml);
         domain->SetAddSignaturesToReplies(signatureReplies);
         domain->SetAddSignaturesToLocalMail(signatureLocal);
         domain->SetDKIMEnabled(dkimEnabled);
         domain->SetDKIMSelector(dkimSelector);
         domain->SetDKIMPrivateKeyFile(dkimKeyFile);
         domain->SetDKIMSigningAlgorithm(algorithm);
         domain->SetMessageRetentionDays((int) retention);
         domain->SetRelayHost(relayHost);
         domain->SetRelayPort(relayPort);
         domain->SetRelayRequiresAuth(relayAuth);
         domain->SetRelayUsername(relayUser);
         domain->SetRelayPassword(relayPassword);
         domain->SetRelayConnectionSecurity(security);
         domain->SetVacationMessageIsOn(vacationOn);
         domain->SetVacationSubject(vacationSubject);
         domain->SetVacationMessage(vacationMessage);
         return true;
      }

      std::shared_ptr<Domain> DomainByName(const String &domainName)
      {
         Domains domains;
         domains.Refresh();
         return domains.GetItemByName(domainName);
      }

      AnsiString DomainAliasJson(std::shared_ptr<DomainAlias> alias, const Quote &quote)
      {
         AnsiString entry;
         entry.Format("{\"id\":%I64d,\"name\":\"%hs\"}", alias->GetID(), quote(alias->GetAlias()).c_str());
         return entry;
      }
   }

   // ------------------------------------------------------------------------
   // The domain, whole.
   // ------------------------------------------------------------------------

   AnsiString
   RestApiServer::DomainEntryJson_(const std::shared_ptr<Domain> &domain)
   {
      Quote quote = [](const String &value) { return JsonEscape_(Utf8_(value)); };

      AnsiString entry;
      entry.Format("{\"name\":\"%hs\",\"active\":%hs,\"postmaster\":\"%hs\",\"max_message_size_kb\":%d,\"max_size_mb\":%d,\"max_account_size_mb\":%d",
         quote(domain->GetName()).c_str(),
         domain->GetIsActive() ? "true" : "false",
         quote(domain->GetPostmaster()).c_str(),
         domain->GetMaxMessageSize(),
         domain->GetMaxSizeMB(),
         domain->GetMaxAccountSize());

      AnsiString limits;
      limits.Format(",\"max_accounts\":%d,\"max_aliases\":%d,\"max_lists\":%d,\"message_retention_days\":%d,\"relay_port\":%ld",
         domain->GetMaxNoOfAccounts(),
         domain->GetMaxNoOfAliases(),
         domain->GetMaxNoOfDistributionLists(),
         domain->GetMessageRetentionDays(),
         domain->GetRelayPort());
      entry += limits;

      auto flag = [&entry](const char *name, bool value)
      {
         entry += ",\"";
         entry += name;
         entry += value ? "\":true" : "\":false";
      };
      auto text = [&entry, &quote](const char *name, const String &value)
      {
         entry += ",\"";
         entry += name;
         entry += "\":\"" + quote(value) + "\"";
      };

      flag("max_accounts_enabled", domain->GetMaxNoOfAccountsEnabled());
      flag("max_aliases_enabled", domain->GetMaxNoOfAliasesEnabled());
      flag("max_lists_enabled", domain->GetMaxNoOfDistributionListsEnabled());
      flag("plus_addressing_enabled", domain->GetUsePlusAddressing());
      text("plus_addressing_character", domain->GetPlusAddressingChar());
      flag("use_greylisting", domain->GetASUseGreyListing());
      flag("signature_enabled", domain->GetEnableSignature());
      text("signature_method", String(SignatureMethodWord(domain->GetSignatureMethod())));
      text("signature_plain_text", domain->GetSignaturePlainText());
      text("signature_html", domain->GetSignatureHTML());
      flag("signature_add_to_replies", domain->GetAddSignaturesToReplies());
      flag("signature_add_to_local_mail", domain->GetAddSignaturesToLocalMail());
      flag("dkim_enabled", domain->GetDKIMEnabled());
      text("dkim_selector", String(domain->GetDKIMSelector()));
      text("dkim_private_key_file", domain->GetDKIMPrivateKeyFile());
      text("dkim_signing_algorithm", String(DkimAlgorithmWord(domain->GetDKIMSigningAlgorithm())));
      text("relay_host", domain->GetRelayHost());
      flag("relay_requires_auth", domain->GetRelayRequiresAuth());
      text("relay_username", domain->GetRelayUsername());
      text("relay_connection_security", String(ConnectionSecurityWord(domain->GetRelayConnectionSecurity())));
      flag("vacation_enabled", domain->GetVacationMessageIsOn());
      text("vacation_subject", domain->GetVacationSubject());
      text("vacation_message", domain->GetVacationMessage());
      entry += "}";
      return entry;
   }

   HttpResponse
   RestApiServer::HandleUpdateDomain_(const String &domainName, const AnsiString &requestBody)
   {
      Quote quote = [](const String &value) { return JsonEscape_(Utf8_(value)); };

      JsonValue body;
      if (!ParseObjectBody(requestBody, body))
         return BuildResponse_(400, "{\"error\":\"the body must be a JSON object\"}");

      // active has always had to be named, as enabled has to be for the
      // automatic reply, so that a client that sends {"active":false} to switch
      // a domain off does not also blank anything; the contract stays.
      const JsonValue *active = body.Get("active");
      if (!active || active->IsNull())
         return BuildResponse_(400, "{\"error\":\"active is required\"}");

      std::shared_ptr<Domain> domain = DomainByName(domainName);
      if (!domain)
         return BuildResponse_(404, "{\"error\":\"domain not found\"}");

      String oldName = domain->GetName();
      AnsiString error;
      bool renamed = false;
      if (!ApplyDomainBody(body, domain, error, renamed))
         return BuildResponse_(400, ErrorBody(quote, String(error)));

      String saveError;
      if (!PersistentDomain::SaveObject(domain, saveError, PersistenceModeNormal))
      {
         if (!saveError.IsEmpty())
         {
            LOG_APPLICATION("RestApi: Refused to update domain " + oldName + ": " + saveError);
            return BuildResponse_(400, ErrorBody(quote, saveError));
         }
         return BuildResponse_(500, "{\"error\":\"failed to save domain\"}");
      }

      if (renamed)
      {
         LOG_APPLICATION("RestApi: Domain " + oldName + " renamed to " + domain->GetName() + ".");
      }
      else
      {
         LOG_APPLICATION("RestApi: Domain " + domain->GetName() + " updated, active: " +
            String(domain->GetIsActive() ? _T("true") : _T("false")) + ".");
      }

      return BuildResponse_(200, DomainEntryJson_(domain));
   }

   // ------------------------------------------------------------------------
   // Domain aliases.
   // ------------------------------------------------------------------------

   HttpResponse
   RestApiServer::HandleListDomainAliases_(const String &domainName)
   {
      Quote quote = [](const String &value) { return JsonEscape_(Utf8_(value)); };

      std::shared_ptr<Domain> domain = DomainByName(domainName);
      if (!domain)
         return BuildResponse_(404, "{\"error\":\"domain not found\"}");

      DomainAliases aliases(domain->GetID());
      aliases.Refresh();

      AnsiString body = "[";
      int count = 0;
      for (std::shared_ptr<DomainAlias> alias : aliases.GetSnapshot())
      {
         if (!alias)
            continue;
         if (count > 0)
            body += ",";
         body += DomainAliasJson(alias, quote);
         count++;
      }
      body += "]";
      return BuildResponse_(200, body);
   }

   HttpResponse
   RestApiServer::HandleCreateDomainAlias_(const String &domainName, const AnsiString &requestBody)
   {
      Quote quote = [](const String &value) { return JsonEscape_(Utf8_(value)); };

      std::shared_ptr<Domain> domain = DomainByName(domainName);
      if (!domain)
         return BuildResponse_(404, "{\"error\":\"domain not found\"}");

      JsonValue body;
      if (!ParseObjectBody(requestBody, body))
         return BuildResponse_(400, "{\"error\":\"the body must be a JSON object\"}");

      String name;
      AnsiString error;
      if (!ReadString(body, "name", name, error))
         return BuildResponse_(400, ErrorBody(quote, String(error)));

      name.Trim();
      if (name.IsEmpty())
         return BuildResponse_(400, "{\"error\":\"name is required\"}");

      // As InterfaceDomainAliases::Add makes one and InterfaceDomainAlias::Save
      // saves it: owned by this domain, refused by the persistence layer's own
      // check when the name is a domain or an alias already.
      std::shared_ptr<DomainAlias> alias = std::shared_ptr<DomainAlias>(new DomainAlias());
      alias->SetDomainID(domain->GetID());
      alias->SetAlias(name);

      String saveError;
      if (!PersistentDomainAlias::SaveObject(alias, saveError, PersistenceModeNormal))
      {
         if (!saveError.IsEmpty())
            return BuildResponse_(400, ErrorBody(quote, saveError));
         return BuildResponse_(500, "{\"error\":\"failed to save the domain alias\"}");
      }

      LOG_APPLICATION("RestApi: Domain alias " + name + " created for " + domain->GetName() + ".");

      return BuildResponse_(201, DomainAliasJson(alias, quote));
   }

   HttpResponse
   RestApiServer::HandleDeleteDomainAlias_(const String &domainName, const String &aliasName)
   {
      std::shared_ptr<Domain> domain = DomainByName(domainName);
      if (!domain)
         return BuildResponse_(404, "{\"error\":\"domain not found\"}");

      DomainAliases aliases(domain->GetID());
      aliases.Refresh();

      std::shared_ptr<DomainAlias> alias = aliases.GetItemByName(aliasName);
      if (!alias)
         return BuildResponse_(404, "{\"error\":\"domain alias not found\"}");

      if (!PersistentDomainAlias::DeleteObject(alias))
         return BuildResponse_(500, "{\"error\":\"failed to delete the domain alias\"}");

      LOG_APPLICATION("RestApi: Domain alias " + aliasName + " of " + domain->GetName() + " deleted.");

      return BuildResponse_(200, "{\"deleted\":true}");
   }

   // ------------------------------------------------------------------------
   // IP ranges: one entry as every range route emits it, and the update.
   // ------------------------------------------------------------------------

   AnsiString
   RestApiServer::IpRangeEntryJson_(const std::shared_ptr<SecurityRange> &range)
   {
      AnsiString entry;
      entry.Format("{\"id\":%I64d,\"name\":\"%hs\",\"lower\":\"%hs\",\"upper\":\"%hs\",\"priority\":%d",
         range->GetID(),
         JsonEscape_(Utf8_(range->GetName())).c_str(),
         JsonEscape_(Utf8_(range->GetLowerIPString())).c_str(),
         JsonEscape_(Utf8_(range->GetUpperIPString())).c_str(),
         (int) range->GetPriority());

      auto flag = [&entry](const char *name, bool value)
      {
         entry += ",\"";
         entry += name;
         entry += value ? "\":true" : "\":false";
      };

      flag("allow_smtp", range->GetAllowSMTP());
      flag("allow_imap", range->GetAllowIMAP());
      flag("allow_pop3", range->GetAllowPOP3());
      flag("deliver_local_to_local", range->GetAllowOption(SecurityRange::IPRANGE_RELAY_LOCAL_TO_LOCAL));
      flag("deliver_local_to_remote", range->GetAllowOption(SecurityRange::IPRANGE_RELAY_LOCAL_TO_REMOTE));
      flag("deliver_remote_to_local", range->GetAllowOption(SecurityRange::IPRANGE_RELAY_REMOTE_TO_LOCAL));
      flag("deliver_remote_to_remote", range->GetAllowOption(SecurityRange::IPRANGE_RELAY_REMOTE_TO_REMOTE));
      flag("require_auth_local_to_local", range->GetRequireSMTPAuthLocalToLocal());
      flag("require_auth_local_to_remote", range->GetRequireSMTPAuthLocalToExternal());
      flag("require_auth_remote_to_local", range->GetRequireSMTPAuthExternalToLocal());
      flag("require_auth_remote_to_remote", range->GetRequireSMTPAuthExternalToExternal());
      flag("require_tls_for_auth", range->GetRequireTLSForAuth());
      flag("spam_protection", range->GetSpamProtection());
      flag("virus_protection", range->GetVirusProtection());
      flag("expires", range->GetExpires());
      entry += "}";
      return entry;
   }

   HttpResponse
   RestApiServer::HandleUpdateIpRange_(__int64 rangeId, const AnsiString &requestBody)
   {
      Quote quote = [](const String &value) { return JsonEscape_(Utf8_(value)); };

      JsonValue body;
      if (!ParseObjectBody(requestBody, body))
         return BuildResponse_(400, "{\"error\":\"the body must be a JSON object\"}");

      static const char *const keys[] =
      {
         "name", "lower", "upper", "priority", "allow_smtp", "allow_imap", "allow_pop3",
         "deliver_local_to_local", "deliver_local_to_remote", "deliver_remote_to_local", "deliver_remote_to_remote",
         "require_auth_local_to_local", "require_auth_local_to_remote", "require_auth_remote_to_local", "require_auth_remote_to_remote",
         "require_tls_for_auth", "spam_protection", "virus_protection"
      };
      AnsiString error;
      if (UnknownKey(body, keys, sizeof(keys) / sizeof(keys[0]), error))
         return BuildResponse_(400, ErrorBody(quote, String(error)));

      SecurityRanges ranges;
      ranges.Refresh();

      std::shared_ptr<SecurityRange> range = ranges.GetItemByDBID(rangeId);
      if (!range)
         return BuildResponse_(404, "{\"error\":\"ip range not found\"}");

      String name = range->GetName(), lowerText = range->GetLowerIPString(), upperText = range->GetUpperIPString();
      if (!ReadString(body, "name", name, error) ||
          !ReadString(body, "lower", lowerText, error) ||
          !ReadString(body, "upper", upperText, error))
         return BuildResponse_(400, ErrorBody(quote, String(error)));

      name.Trim();
      if (name.IsEmpty())
         return BuildResponse_(400, "{\"error\":\"name must not be empty\"}");

      IPAddress lower;
      IPAddress upper;
      if (!lower.TryParse(AnsiString(lowerText), false) || !upper.TryParse(AnsiString(upperText), false))
         return BuildResponse_(400, "{\"error\":\"lower and upper must be IP addresses\"}");

      long priority = range->GetPriority();
      if (!ReadInteger(body, "priority", -2000000000L, 2000000000L, priority, error))
         return BuildResponse_(400, ErrorBody(quote, String(error)));

      bool allowSmtp = range->GetAllowSMTP(), allowImap = range->GetAllowIMAP(), allowPop3 = range->GetAllowPOP3();
      bool l2l = range->GetAllowOption(SecurityRange::IPRANGE_RELAY_LOCAL_TO_LOCAL);
      bool l2r = range->GetAllowOption(SecurityRange::IPRANGE_RELAY_LOCAL_TO_REMOTE);
      bool r2l = range->GetAllowOption(SecurityRange::IPRANGE_RELAY_REMOTE_TO_LOCAL);
      bool r2r = range->GetAllowOption(SecurityRange::IPRANGE_RELAY_REMOTE_TO_REMOTE);
      bool authL2l = range->GetRequireSMTPAuthLocalToLocal(), authL2r = range->GetRequireSMTPAuthLocalToExternal();
      bool authR2l = range->GetRequireSMTPAuthExternalToLocal(), authR2r = range->GetRequireSMTPAuthExternalToExternal();
      bool tlsForAuth = range->GetRequireTLSForAuth(), spam = range->GetSpamProtection(), virus = range->GetVirusProtection();

      if (!ReadBool(body, "allow_smtp", allowSmtp, error) ||
          !ReadBool(body, "allow_imap", allowImap, error) ||
          !ReadBool(body, "allow_pop3", allowPop3, error) ||
          !ReadBool(body, "deliver_local_to_local", l2l, error) ||
          !ReadBool(body, "deliver_local_to_remote", l2r, error) ||
          !ReadBool(body, "deliver_remote_to_local", r2l, error) ||
          !ReadBool(body, "deliver_remote_to_remote", r2r, error) ||
          !ReadBool(body, "require_auth_local_to_local", authL2l, error) ||
          !ReadBool(body, "require_auth_local_to_remote", authL2r, error) ||
          !ReadBool(body, "require_auth_remote_to_local", authR2l, error) ||
          !ReadBool(body, "require_auth_remote_to_remote", authR2r, error) ||
          !ReadBool(body, "require_tls_for_auth", tlsForAuth, error) ||
          !ReadBool(body, "spam_protection", spam, error) ||
          !ReadBool(body, "virus_protection", virus, error))
         return BuildResponse_(400, ErrorBody(quote, String(error)));

      range->SetName(name);
      range->SetLowerIP(lower);
      range->SetUpperIP(upper);
      range->SetPriority(priority);
      range->SetAllowSMTP(allowSmtp);
      range->SetAllowIMAP(allowImap);
      range->SetAllowPOP3(allowPop3);
      range->SetAllowRelayL2L(l2l);
      range->SetAllowRelayL2R(l2r);
      range->SetAllowRelayR2L(r2l);
      range->SetAllowRelayR2R(r2r);
      range->SetRequireSMTPAuthLocalToLocal(authL2l);
      range->SetRequireSMTPAuthLocalToExternal(authL2r);
      range->SetRequireSMTPAuthExternalToLocal(authR2l);
      range->SetRequireSMTPAuthExternalToExternal(authR2r);
      range->SetRequireTLSForAuth(tlsForAuth);
      range->SetSpamProtection(spam);
      range->SetVirusProtection(virus);

      String result;
      if (!PersistentSecurityRange::SaveObject(range, result, PersistenceModeNormal))
         return BuildResponse_(400, ErrorBody(quote, result));

      LOG_APPLICATION("RestApi: IP range '" + range->GetName() + "' updated.");

      return BuildResponse_(200, IpRangeEntryJson_(range));
   }

   AnsiString
   RestApiServer::OpenApiAdministrationPaths_()
   {
      static const char *paths =
         ",\"/api/v1/domains/{domain}/domain-aliases\":{"
         "\"get\":{\"summary\":\"The domain's aliases - other names the domain answers to\",\"description\":\"Each entry: id, name. A key restricted to named domains reaches its own domains only.\",\"responses\":{\"200\":{\"description\":\"Array of domain aliases\"},\"404\":{\"description\":\"Unknown domain\"}}},"
         "\"post\":{\"summary\":\"Add a domain alias\",\"description\":\"Body: name. What DomainAliases.Add and Save do over COM, with the same refusal when the name is already a domain or an alias; in effect for the next RCPT TO.\",\"requestBody\":{\"content\":{\"application/json\":{\"schema\":{\"type\":\"object\",\"required\":[\"name\"],\"properties\":{\"name\":{\"type\":\"string\"}}}}}},\"responses\":{\"201\":{\"description\":\"Created: id, name\"},\"400\":{\"description\":\"name missing, or refused (error says why)\"},\"404\":{\"description\":\"Unknown domain\"}}}},"
         "\"/api/v1/domains/{domain}/domain-aliases/{name}\":{\"delete\":{\"summary\":\"Remove a domain alias\",\"responses\":{\"200\":{\"description\":\"Deleted\"},\"404\":{\"description\":\"Unknown domain, or no alias of that name in it\"}}}}";

      return AnsiString(paths);
   }
}

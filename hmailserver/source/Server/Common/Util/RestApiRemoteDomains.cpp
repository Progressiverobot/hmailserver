// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

// The REST API's remote domain policies. See RestApiServer.h and
// Common/BO/RemoteDomainPolicy.h.
//
// One record per named remote domain: the TLS this server demands of it
// outbound and inbound, the largest message and the concurrency it will
// attempt, whether an automatic reply or a forward may go there, and whether a
// recipient is verified with the domain's own server before mail for it is
// accepted.
//
// Every handler works on the ONE collection SMTPConfiguration owns - the
// collection ExternalDelivery, SMTPConnection and the forwarder read for every
// message - so a policy created here is in effect for the next message without a
// restart, exactly as a policy saved in the Control Panel is. That is the same
// contract the routes have, and for the same reason: an administrator who
// requires TLS for a domain expects the next message to obey, not the next
// message after a service restart.
//
// The verbs mirror the routes (RestApiRoutes.cpp): GET the collection, POST one,
// PUT the whole record, DELETE it. A PUT replaces every field, so a field the
// body leaves out takes the create's default rather than keeping what was
// stored - which is what "replace" means, and what the Deck's form sends.
//
// Beside them, two the routes have no equivalent of:
//   GET /api/v1/remote-domains/effective?domain=x   the record that GOVERNS a
//       domain, which is not the record NAMED by it. Patterns and specificity
//       decide, and an administrator who has written "*" and "bank.example"
//       should be able to ask which one a delivery will use without finding out
//       from a message that did not go.
//   POST /api/v1/remote-domains/verification-cache/clear   forgets every
//       remembered callout verdict. The primary is fixed; stop refusing.

#include "StdAfx.h"
#include "RestApiServer.h"
#include "JsonDocument.h"
#include "Unicode.h"
#include "../BO/RemoteDomainPolicies.h"
#include "../BO/RemoteDomainPolicy.h"
#include "../Persistence/PersistentRemoteDomainPolicy.h"
#include "../Persistence/PersistenceMode.h"
#include "../../SMTP/RecipientCallout.h"
#include "../../SMTP/SMTPConfiguration.h"

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

      // The words the API uses for RemoteTlsRequirement, in both directions.
      // The numbers are a storage detail; "verified" is the thing an
      // administrator means.
      const char *TlsRequirementWord(RemoteTlsRequirement requirement)
      {
         switch (requirement)
         {
         case RemoteTlsEncrypted:
            return "encrypted";
         case RemoteTlsVerified:
            return "verified";
         case RemoteTlsDane:
            return "dane";
         case RemoteTlsDefault:
         default:
            return "none";
         }
      }

      bool ParseTlsRequirementWord(const std::string &word, RemoteTlsRequirement &requirement)
      {
         AnsiString value = word.c_str();

         if (value.CompareNoCase("none") == 0)
            requirement = RemoteTlsDefault;
         else if (value.CompareNoCase("encrypted") == 0)
            requirement = RemoteTlsEncrypted;
         else if (value.CompareNoCase("verified") == 0)
            requirement = RemoteTlsVerified;
         else if (value.CompareNoCase("dane") == 0)
            requirement = RemoteTlsDane;
         else
            return false;

         return true;
      }

      String Utf8ToString(const std::string &utf8)
      {
         String value;
         Unicode::MultiByteToWide(AnsiString(utf8.c_str()), value);
         return value;
      }

      bool ParseObjectBody(const AnsiString &requestBody, JsonValue &body, AnsiString &error)
      {
         std::string parseError;
         std::string text(requestBody.c_str(), (size_t) requestBody.GetLength());

         if (!JsonValue::Parse(text, body, parseError) || !body.IsObject())
         {
            error = "the body must be a JSON object";
            return false;
         }

         return true;
      }

      std::string FirstUnknownKey(const JsonValue &object, const char *const *known, size_t knownCount)
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
               return member.first;
         }

         return "";
      }

      bool HasMember(const JsonValue &object, const char *key)
      {
         const JsonValue *member = object.Get(key);
         return member != nullptr && !member->IsNull();
      }

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

      AnsiString ErrorBody(const Quote &quote, const String &sentence)
      {
         AnsiString body;
         body.Format("{\"error\":\"%hs\"}", quote(sentence).c_str());
         return body;
      }

      // A policy as the listing renders it, so that what a create or an update
      // answers is exactly what the next listing will show.
      AnsiString PolicyEntryJson(std::shared_ptr<RemoteDomainPolicy> policy, const Quote &quote)
      {
         AnsiString entry;
         entry.Format("{\"id\":%I64d,\"domain_name\":\"%hs\",\"description\":\"%hs\"",
            policy->GetID(),
            quote(policy->GetDomainName()).c_str(),
            quote(policy->GetDescription()).c_str());

         auto flag = [&entry](const char *name, bool value)
         {
            entry += ",\"";
            entry += name;
            entry += value ? "\":true" : "\":false";
         };

         flag("active", policy->GetActive());

         entry += ",\"outbound_tls\":\"";
         entry += TlsRequirementWord(policy->GetOutboundTlsRequirement());
         entry += "\"";

         flag("require_inbound_tls", policy->GetRequireInboundTls());

         AnsiString numbers;
         numbers.Format(",\"max_message_size_kb\":%d,\"max_connections\":%d,\"max_messages_per_minute\":%d",
            (int) policy->GetMaxMessageSizeKB(),
            (int) policy->GetMaxConnections(),
            (int) policy->GetMaxMessagesPerMinute());
         entry += numbers;

         flag("allow_automatic_replies", policy->GetAllowAutomaticReplies());
         flag("allow_forwarding", policy->GetAllowForwarding());
         flag("callout_enabled", policy->GetCalloutEnabled());

         entry += ",\"callout_host\":\"" + quote(policy->GetCalloutHost()) + "\"";

         AnsiString calloutNumbers;
         calloutNumbers.Format(",\"callout_port\":%d,\"callout_timeout_seconds\":%d,\"callout_cache_minutes\":%d,\"callout_max_per_minute\":%d}",
            (int) policy->GetCalloutPort(),
            (int) policy->GetCalloutTimeoutSeconds(),
            (int) policy->GetCalloutCacheMinutes(),
            (int) policy->GetCalloutMaxPerMinute());
         entry += calloutNumbers;

         return entry;
      }

      void CopyPolicy(const RemoteDomainPolicy &from, RemoteDomainPolicy &to)
      {
         to.SetID(from.GetID());
         to.SetDomainName(from.GetDomainName());
         to.SetDescription(from.GetDescription());
         to.SetActive(from.GetActive());
         to.SetOutboundTlsRequirement(from.GetOutboundTlsRequirement());
         to.SetRequireInboundTls(from.GetRequireInboundTls());
         to.SetMaxMessageSizeKB(from.GetMaxMessageSizeKB());
         to.SetMaxConnections(from.GetMaxConnections());
         to.SetMaxMessagesPerMinute(from.GetMaxMessagesPerMinute());
         to.SetAllowAutomaticReplies(from.GetAllowAutomaticReplies());
         to.SetAllowForwarding(from.GetAllowForwarding());
         to.SetCalloutEnabled(from.GetCalloutEnabled());
         to.SetCalloutHost(from.GetCalloutHost());
         to.SetCalloutPort(from.GetCalloutPort());
         to.SetCalloutTimeoutSeconds(from.GetCalloutTimeoutSeconds());
         to.SetCalloutCacheMinutes(from.GetCalloutCacheMinutes());
         to.SetCalloutMaxPerMinute(from.GetCalloutMaxPerMinute());
      }

      // The whole record from the body onto policy: what a POST creates and what
      // a PUT replaces. Every member the body does not name takes the default a
      // new record has (RemoteDomainPolicy's constructor), which is a policy that
      // demands nothing and verifies nothing - so a body that names only a domain
      // creates a record that changes no behaviour, and every refusal in this
      // server comes from something somebody typed.
      bool ApplyPolicyBody(const JsonValue &body, std::shared_ptr<RemoteDomainPolicy> policy, AnsiString &error)
      {
         static const char *const knownKeys[] =
         {
            "id", "domain_name", "description", "active", "outbound_tls", "require_inbound_tls",
            "max_message_size_kb", "max_connections", "max_messages_per_minute",
            "allow_automatic_replies", "allow_forwarding",
            "callout_enabled", "callout_host", "callout_port", "callout_timeout_seconds",
            "callout_cache_minutes", "callout_max_per_minute"
         };

         std::string unknown = FirstUnknownKey(body, knownKeys, sizeof(knownKeys) / sizeof(knownKeys[0]));
         if (!unknown.empty())
         {
            error = "unknown field: " + AnsiString(unknown.substr(0, 48).c_str());
            return false;
         }

         String domainName;
         String description;
         String calloutHost;
         bool active = true;
         bool requireInboundTls = false;
         bool allowAutomaticReplies = true;
         bool allowForwarding = true;
         bool calloutEnabled = false;
         long maxMessageSizeKB = 0;
         long maxConnections = 0;
         long maxMessagesPerMinute = 0;
         long calloutPort = 25;
         long calloutTimeoutSeconds = 10;
         long calloutCacheMinutes = 60;
         long calloutMaxPerMinute = 10;
         RemoteTlsRequirement outboundTls = RemoteTlsDefault;

         if (!ReadString(body, "domain_name", domainName, error) ||
             !ReadString(body, "description", description, error) ||
             !ReadBool(body, "active", active, error) ||
             !ReadBool(body, "require_inbound_tls", requireInboundTls, error) ||
             !ReadInteger(body, "max_message_size_kb", 0, 2097152, maxMessageSizeKB, error) ||
             !ReadInteger(body, "max_connections", 0, 10000, maxConnections, error) ||
             !ReadInteger(body, "max_messages_per_minute", 0, 1000000, maxMessagesPerMinute, error) ||
             !ReadBool(body, "allow_automatic_replies", allowAutomaticReplies, error) ||
             !ReadBool(body, "allow_forwarding", allowForwarding, error) ||
             !ReadBool(body, "callout_enabled", calloutEnabled, error) ||
             !ReadString(body, "callout_host", calloutHost, error) ||
             !ReadInteger(body, "callout_port", 1, 65535, calloutPort, error) ||
             // The ceiling is RecipientCallout's own, because the timeout is
             // spent inside an SMTP session a sender is waiting on.
             !ReadInteger(body, "callout_timeout_seconds", 1, RecipientCallout::CalloutMaxTimeoutSeconds, calloutTimeoutSeconds, error) ||
             !ReadInteger(body, "callout_cache_minutes", 0, 10080, calloutCacheMinutes, error) ||
             !ReadInteger(body, "callout_max_per_minute", 0, 10000, calloutMaxPerMinute, error))
         {
            return false;
         }

         if (HasMember(body, "outbound_tls"))
         {
            const JsonValue *member = body.Get("outbound_tls");
            if (!member->IsString() || !ParseTlsRequirementWord(member->AsString(), outboundTls))
            {
               error = "outbound_tls must be one of none, encrypted, verified, dane";
               return false;
            }
         }

         domainName.Trim();
         calloutHost.Trim();

         if (domainName.IsEmpty())
         {
            error = "domain_name is required";
            return false;
         }

         // A space in a domain pattern is always a typo, and a pattern with one
         // matches nothing - so the policy an administrator believes they wrote
         // would govern no domain at all, silently.
         if (domainName.Find(_T(" ")) >= 0 || domainName.Find(_T("@")) >= 0)
         {
            error = "domain_name must be a domain name or a wildcard pattern, not an address";
            return false;
         }

         policy->SetDomainName(domainName);
         policy->SetDescription(description);
         policy->SetActive(active);
         policy->SetOutboundTlsRequirement(outboundTls);
         policy->SetRequireInboundTls(requireInboundTls);
         policy->SetMaxMessageSizeKB(maxMessageSizeKB);
         policy->SetMaxConnections(maxConnections);
         policy->SetMaxMessagesPerMinute(maxMessagesPerMinute);
         policy->SetAllowAutomaticReplies(allowAutomaticReplies);
         policy->SetAllowForwarding(allowForwarding);
         policy->SetCalloutEnabled(calloutEnabled);
         policy->SetCalloutHost(calloutHost);
         policy->SetCalloutPort(calloutPort);
         policy->SetCalloutTimeoutSeconds(calloutTimeoutSeconds);
         policy->SetCalloutCacheMinutes(calloutCacheMinutes);
         policy->SetCalloutMaxPerMinute(calloutMaxPerMinute);

         return true;
      }
   }

   AnsiString
   RestApiServer::OpenApiRemoteDomainsPaths_()
   {
      return
         ",\"/api/v1/remote-domains\":{"
         "\"get\":{\"summary\":\"List the remote domain policies\",\"description\":\"What this server will do when it talks to a named remote domain, as the running server holds them. Each entry: id, domain_name (may carry a wildcard), description, active, outbound_tls (none, encrypted, verified or dane), require_inbound_tls, max_message_size_kb, max_connections, max_messages_per_minute, allow_automatic_replies, allow_forwarding, callout_enabled, callout_host, callout_port, callout_timeout_seconds, callout_cache_minutes and callout_max_per_minute. A policy is not a route: a route says where mail for a domain goes, a policy says what this server will and will not do when it gets there, and a domain may have both. Server-wide; refused for domain-restricted keys.\",\"responses\":{\"200\":{\"description\":\"Array of policies\"}}},"
         "\"post\":{\"summary\":\"Create a remote domain policy\",\"description\":\"Body: domain_name (required; a domain, or a wildcard pattern such as *.example.com - the most specific active match governs a delivery). description; active (default true); outbound_tls (default none - none leaves the delivery as it would have been, encrypted requires a successful STARTTLS, verified also requires a certificate that chains and names the host, dane requires a DNSSEC-validated TLSA record; the requirement composes with MTA-STS and DANE as the strongest of the three and can never lower what the domain itself published); require_inbound_tls (default false - mail FROM this domain on an unencrypted session is answered 530); max_message_size_kb (default 0, no limit - a larger message is never offered and is bounced 5.3.4); max_connections and max_messages_per_minute (default 0, unlimited - beyond either, delivery is deferred 4.4.5); allow_automatic_replies and allow_forwarding (default true); callout_enabled (default false), callout_host (empty means the domain's own MX hosts, skipping this server), callout_port (default 25), callout_timeout_seconds (default 10, at most 60), callout_cache_minutes (default 60) and callout_max_per_minute (default 10). Persisted and in effect for the next message, as a policy saved in the Control Panel is. Server-wide; refused for domain-restricted keys.\",\"requestBody\":{\"content\":{\"application/json\":{\"schema\":{\"type\":\"object\",\"required\":[\"domain_name\"],\"properties\":{\"domain_name\":{\"type\":\"string\"},\"description\":{\"type\":\"string\"},\"active\":{\"type\":\"boolean\"},\"outbound_tls\":{\"type\":\"string\",\"enum\":[\"none\",\"encrypted\",\"verified\",\"dane\"]},\"require_inbound_tls\":{\"type\":\"boolean\"},\"max_message_size_kb\":{\"type\":\"integer\"},\"max_connections\":{\"type\":\"integer\"},\"max_messages_per_minute\":{\"type\":\"integer\"},\"allow_automatic_replies\":{\"type\":\"boolean\"},\"allow_forwarding\":{\"type\":\"boolean\"},\"callout_enabled\":{\"type\":\"boolean\"},\"callout_host\":{\"type\":\"string\"},\"callout_port\":{\"type\":\"integer\"},\"callout_timeout_seconds\":{\"type\":\"integer\"},\"callout_cache_minutes\":{\"type\":\"integer\"},\"callout_max_per_minute\":{\"type\":\"integer\"}}}}}},\"responses\":{\"201\":{\"description\":\"Created: the policy as the listing shows it\"},\"400\":{\"description\":\"domain_name missing, a field of the wrong type or out of range, an unknown field, or the save refused (the reason is in error)\"},\"409\":{\"description\":\"A policy for that domain name exists\"}}}}"
         ",\"/api/v1/remote-domains/effective\":{"
         "\"get\":{\"summary\":\"The policy that governs a domain\",\"description\":\"Which record a delivery to ?domain=example.com will actually use. Not the same question as the record NAMED by that domain: an exact name beats any pattern, a longer pattern beats a shorter one, and an inactive record is skipped so that the next most specific active one governs - switching off one domain's entry never exempts it from a broader pattern. Answers 404 when no policy governs the domain, which means the delivery is unconstrained. Server-wide; refused for domain-restricted keys.\",\"responses\":{\"200\":{\"description\":\"The governing policy, as the listing shows it\"},\"400\":{\"description\":\"domain is required\"},\"404\":{\"description\":\"No policy governs that domain\"}}}}"
         ",\"/api/v1/remote-domains/verification-cache/clear\":{"
         "\"post\":{\"summary\":\"Forget every remembered recipient verification verdict\",\"description\":\"The recipient callout remembers what a domain's own server said about an address for callout_cache_minutes. This forgets all of it, so the next message asks again - what an administrator wants the moment the primary that was refusing everything is fixed. Answers the number of verdicts that were forgotten. Server-wide; refused for domain-restricted keys.\",\"responses\":{\"200\":{\"description\":\"cleared: how many verdicts were forgotten\"}}}}"
         ",\"/api/v1/remote-domains/{id}\":{"
         "\"put\":{\"summary\":\"Replace a remote domain policy\",\"description\":\"The whole record, with the same fields, defaults and checks as the create. A field the body does not name takes the create's default rather than keeping what was stored. The running policy is left as it was when the body is refused. Server-wide; refused for domain-restricted keys.\",\"requestBody\":{\"content\":{\"application/json\":{\"schema\":{\"type\":\"object\",\"required\":[\"domain_name\"],\"properties\":{\"domain_name\":{\"type\":\"string\"},\"description\":{\"type\":\"string\"},\"active\":{\"type\":\"boolean\"},\"outbound_tls\":{\"type\":\"string\",\"enum\":[\"none\",\"encrypted\",\"verified\",\"dane\"]},\"require_inbound_tls\":{\"type\":\"boolean\"},\"max_message_size_kb\":{\"type\":\"integer\"},\"max_connections\":{\"type\":\"integer\"},\"max_messages_per_minute\":{\"type\":\"integer\"},\"allow_automatic_replies\":{\"type\":\"boolean\"},\"allow_forwarding\":{\"type\":\"boolean\"},\"callout_enabled\":{\"type\":\"boolean\"},\"callout_host\":{\"type\":\"string\"},\"callout_port\":{\"type\":\"integer\"},\"callout_timeout_seconds\":{\"type\":\"integer\"},\"callout_cache_minutes\":{\"type\":\"integer\"},\"callout_max_per_minute\":{\"type\":\"integer\"}}}}}},\"responses\":{\"200\":{\"description\":\"The policy as saved\"},\"400\":{\"description\":\"As for the create\"},\"404\":{\"description\":\"Unknown id\"},\"409\":{\"description\":\"Another policy has that domain name\"}}},"
         "\"delete\":{\"summary\":\"Delete a remote domain policy\",\"description\":\"The domain is delivered to as it would have been with no policy at all. Server-wide; refused for domain-restricted keys.\",\"responses\":{\"200\":{\"description\":\"Deleted\"},\"404\":{\"description\":\"Unknown id\"}}}}";
   }

   HttpResponse
   RestApiServer::HandleListRemoteDomains_()
   {
      Quote quote = [](const String &value) { return JsonEscape_(Utf8_(value)); };

      std::shared_ptr<RemoteDomainPolicies> policies =
         Configuration::Instance()->GetSMTPConfiguration()->GetRemoteDomainPolicies();

      if (!policies)
         return BuildResponse_(200, "[]");

      AnsiString body = "[";
      int count = 0;

      for (std::shared_ptr<RemoteDomainPolicy> policy : policies->GetSnapshot())
      {
         if (!policy)
            continue;

         if (count > 0)
            body += ",";

         body += PolicyEntryJson(policy, quote);
         count++;
      }

      body += "]";

      return BuildResponse_(200, body);
   }

   HttpResponse
   RestApiServer::HandleEffectiveRemoteDomain_(const String &domainName)
   {
      Quote quote = [](const String &value) { return JsonEscape_(Utf8_(value)); };

      String domain = domainName;
      domain.Trim();
      domain.ToLower();

      if (domain.IsEmpty())
         return BuildResponse_(400, "{\"error\":\"domain is required\"}");

      std::shared_ptr<RemoteDomainPolicies> policies =
         Configuration::Instance()->GetSMTPConfiguration()->GetRemoteDomainPolicies();

      std::shared_ptr<RemoteDomainPolicy> policy =
         policies ? policies->GetPolicyForDomain(domain) : std::shared_ptr<RemoteDomainPolicy>();

      if (!policy)
         return BuildResponse_(404, "{\"error\":\"no policy governs that domain\"}");

      return BuildResponse_(200, PolicyEntryJson(policy, quote));
   }

   HttpResponse
   RestApiServer::HandleClearRemoteDomainVerificationCache_()
   {
      int forgotten = RecipientCallout::Instance()->GetCacheSize();

      RecipientCallout::Instance()->ClearCache();

      LOG_APPLICATION("RestApi: the recipient verification cache was cleared (" +
         StringParser::IntToString(forgotten) + " verdict(s) forgotten).");

      AnsiString body;
      body.Format("{\"cleared\":%d}", forgotten);

      return BuildResponse_(200, body);
   }

   HttpResponse
   RestApiServer::HandleCreateRemoteDomain_(const AnsiString &requestBody)
   {
      Quote quote = [](const String &value) { return JsonEscape_(Utf8_(value)); };

      JsonValue body;
      AnsiString error;

      if (!ParseObjectBody(requestBody, body, error))
         return BuildResponse_(400, ErrorBody(quote, String(error)));

      std::shared_ptr<RemoteDomainPolicies> policies =
         Configuration::Instance()->GetSMTPConfiguration()->GetRemoteDomainPolicies();

      if (!policies)
         return BuildResponse_(500, "{\"error\":\"the remote domain policies are not loaded\"}");

      std::shared_ptr<RemoteDomainPolicy> policy = std::shared_ptr<RemoteDomainPolicy>(new RemoteDomainPolicy());

      if (!ApplyPolicyBody(body, policy, error))
         return BuildResponse_(400, ErrorBody(quote, String(error)));

      // A duplicate is refused before the save rather than by it, so that it is
      // a 409 and not a 400 carrying the limitation check's sentence - the check
      // itself still runs inside SaveObject, against the same collection.
      if (policies->GetItemByName(policy->GetDomainName()))
         return BuildResponse_(409, "{\"error\":\"a policy for that domain name already exists\"}");

      String saveError;

      if (!PersistentRemoteDomainPolicy::SaveObject(policy, saveError, PersistenceModeNormal))
      {
         if (!saveError.IsEmpty())
         {
            LOG_APPLICATION("RestApi: Refused to create remote domain policy " + policy->GetDomainName() + ": " + saveError);
            return BuildResponse_(400, ErrorBody(quote, saveError));
         }

         return BuildResponse_(500, "{\"error\":\"failed to save the remote domain policy\"}");
      }

      // From here the running delivery sees it.
      policies->AddItem(policy);

      LOG_APPLICATION("RestApi: Remote domain policy for " + policy->GetDomainName() + " created.");

      return BuildResponse_(201, PolicyEntryJson(policy, quote));
   }

   HttpResponse
   RestApiServer::HandleUpdateRemoteDomain_(__int64 policyId, const AnsiString &requestBody)
   {
      Quote quote = [](const String &value) { return JsonEscape_(Utf8_(value)); };

      std::shared_ptr<RemoteDomainPolicies> policies =
         Configuration::Instance()->GetSMTPConfiguration()->GetRemoteDomainPolicies();

      if (!policies)
         return BuildResponse_(500, "{\"error\":\"the remote domain policies are not loaded\"}");

      std::shared_ptr<RemoteDomainPolicy> existing = policies->GetItemByDBID(policyId);
      if (!existing)
         return BuildResponse_(404, "{\"error\":\"remote domain policy not found\"}");

      JsonValue body;
      AnsiString error;

      if (!ParseObjectBody(requestBody, body, error))
         return BuildResponse_(400, ErrorBody(quote, String(error)));

      // The body is applied to a copy and the copy is what is saved, as the
      // route update does it: a refused body must leave the running policy -
      // which is what delivery reads on the next message - exactly as it was.
      std::shared_ptr<RemoteDomainPolicy> updated = std::shared_ptr<RemoteDomainPolicy>(new RemoteDomainPolicy());
      CopyPolicy(*existing, *updated);

      if (!ApplyPolicyBody(body, updated, error))
         return BuildResponse_(400, ErrorBody(quote, String(error)));

      std::shared_ptr<RemoteDomainPolicy> other = policies->GetItemByName(updated->GetDomainName());
      if (other && other->GetID() != policyId)
         return BuildResponse_(409, "{\"error\":\"another policy has that domain name\"}");

      String saveError;

      if (!PersistentRemoteDomainPolicy::SaveObject(updated, saveError, PersistenceModeNormal))
      {
         if (!saveError.IsEmpty())
         {
            LOG_APPLICATION("RestApi: Refused to update remote domain policy " + existing->GetDomainName() + ": " + saveError);
            return BuildResponse_(400, ErrorBody(quote, saveError));
         }

         return BuildResponse_(500, "{\"error\":\"failed to save the remote domain policy\"}");
      }

      CopyPolicy(*updated, *existing);

      LOG_APPLICATION("RestApi: Remote domain policy for " + existing->GetDomainName() + " updated.");

      return BuildResponse_(200, PolicyEntryJson(existing, quote));
   }

   HttpResponse
   RestApiServer::HandleDeleteRemoteDomain_(__int64 policyId)
   {
      std::shared_ptr<RemoteDomainPolicies> policies =
         Configuration::Instance()->GetSMTPConfiguration()->GetRemoteDomainPolicies();

      if (!policies)
         return BuildResponse_(500, "{\"error\":\"the remote domain policies are not loaded\"}");

      std::shared_ptr<RemoteDomainPolicy> policy = policies->GetItemByDBID(policyId);
      if (!policy)
         return BuildResponse_(404, "{\"error\":\"remote domain policy not found\"}");

      String domainName = policy->GetDomainName();

      if (!policies->DeleteItemByDBID(policyId))
         return BuildResponse_(500, "{\"error\":\"failed to delete the remote domain policy\"}");

      LOG_APPLICATION("RestApi: Remote domain policy for " + domainName + " deleted.");

      return BuildResponse_(200, "{\"deleted\":true}");
   }
}

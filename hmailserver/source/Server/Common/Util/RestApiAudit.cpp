// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

// The audit trail and the alerts, over REST.
//
//   GET /api/v1/audit                who changed what, newest first, with
//                                    paging and filters.
//   GET /api/v1/audit/verify         walks the hash chain and names the first
//                                    row that does not check out.
//   GET /api/v1/alerts/rules         the conditions, their thresholds and what
//                                    each one does.
//   PUT /api/v1/alerts/rules/<name>  change one, or create one for a condition
//                                    nothing has shipped.
//   GET /api/v1/alerts/events        what has fired, newest first.
//
// Every one is administrator-only: see IsApiKeyRoute_ in RestApiServer.cpp for
// why an API key of any scope is refused, and refused with a 401.
//
// A webhook secret is never returned. It can be set and it can be cleared, and
// the answer says whether one is configured - which is what an administrator
// needs to know and what a reader of the page must not be able to take away.

#include "StdAfx.h"

#include "RestApiServer.h"

#include "AuditTrail.h"
#include "AlertManager.h"
#include "JsonDocument.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   namespace
   {
      AnsiString AuditEntryJson(const AuditTrail::Entry &entry, AnsiString (*escape)(const AnsiString &))
      {
         AnsiString json;
         AnsiString numbers;

         numbers.Format("{\"id\":%I64d,\"time\":%I64d,", entry.id, entry.time);
         json += numbers;

         json += "\"actor\":\"" + escape(AnsiString(entry.actor)) + "\",";
         json += "\"actor_type\":\"" + escape(AnsiString(entry.actor_kind)) + "\",";
         json += "\"interface\":\"" + escape(AnsiString(entry.interface_name)) + "\",";
         json += "\"address\":\"" + escape(AnsiString(entry.address)) + "\",";
         json += "\"object_type\":\"" + escape(AnsiString(entry.object_type)) + "\",";
         json += "\"object\":\"" + escape(AnsiString(entry.object_name)) + "\",";
         json += "\"action\":\"" + escape(AnsiString(entry.action)) + "\",";
         json += "\"detail\":\"" + escape(AnsiString(entry.detail)) + "\",";
         json += "\"hash\":\"" + escape(AnsiString(entry.hash)) + "\",";
         json += "\"previous_hash\":\"" + escape(AnsiString(entry.previous_hash)) + "\"";
         json += "}";

         return json;
      }

      AnsiString AlertRuleJson(const AlertManager::Rule &rule, AnsiString (*escape)(const AnsiString &))
      {
         AnsiString json;

         json += "{\"condition\":\"" + escape(AnsiString(rule.condition)) + "\",";
         json += AnsiString("\"enabled\":") + (rule.enabled ? "true" : "false") + ",";

         AnsiString numbers;
         numbers.Format("\"threshold\":%d,\"actions\":%d,\"cooldown_minutes\":%d,", rule.threshold, rule.actions, rule.cooldown);
         json += numbers;

         json += AnsiString("\"digest\":") + (rule.digest ? "true" : "false") + ",";
         json += "\"address\":\"" + escape(AnsiString(rule.address)) + "\",";
         json += "\"webhook\":\"" + escape(AnsiString(rule.webhook)) + "\",";

         // The secret itself, never. Whether there is one, always: an
         // administrator asking "is this webhook signed" gets an answer, and a
         // reader of the page gets nothing they could sign with.
         json += AnsiString("\"webhook_secret_set\":") + (rule.secret.IsEmpty() ? "false" : "true");
         json += "}";

         return json;
      }

      AnsiString AlertEventJson(const AlertManager::Event &event, AnsiString (*escape)(const AnsiString &))
      {
         const char *hookState = "none";
         switch (event.hook_state)
         {
         case AlertManager::HookPending:      hookState = "pending"; break;
         case AlertManager::HookDelivered:    hookState = "delivered"; break;
         case AlertManager::HookDeadLettered: hookState = "dead-lettered"; break;
         default: break;
         }

         AnsiString json;
         AnsiString numbers;

         numbers.Format("{\"id\":%I64d,\"time\":%I64d,\"severity\":%d,", event.id, event.time, event.severity);
         json += numbers;

         json += "\"condition\":\"" + escape(AnsiString(event.condition)) + "\",";
         json += AnsiString("\"state\":\"") + (event.state == AlertManager::StateCleared ? "cleared" : "raised") + "\",";
         json += "\"summary\":\"" + escape(AnsiString(event.summary)) + "\",";
         json += "\"detail\":\"" + escape(AnsiString(event.detail)) + "\",";
         json += AnsiString("\"notified\":") + (event.notified ? "true" : "false") + ",";
         json += AnsiString("\"held_for_digest\":") + (event.digested ? "true" : "false") + ",";
         json += AnsiString("\"webhook\":\"") + hookState + "\",";

         AnsiString tries;
         tries.Format("\"webhook_attempts\":%d", event.hook_tries);
         json += tries;

         json += "}";

         return json;
      }
   }

   HttpResponse
   RestApiServer::HandleAudit_(const Route &route, const AnsiString &requestBody)
   {
      AnsiString (*escape)(const AnsiString &) = &RestApiServer::JsonEscape_;

      switch (route.kind)
      {
      case RouteAuditList:
         {
            AuditTrail::Filter filter;
            filter.actor = String(QueryParameter_(route.query, "actor"));
            filter.object_type = String(QueryParameter_(route.query, "object_type"));
            filter.action = String(QueryParameter_(route.query, "action"));

            AnsiString since = QueryParameter_(route.query, "since");
            if (!since.IsEmpty())
               filter.since = _atoi64(since.c_str());

            AnsiString until = QueryParameter_(route.query, "until");
            if (!until.IsEmpty())
               filter.until = _atoi64(until.c_str());

            AnsiString limit = QueryParameter_(route.query, "limit");
            if (!limit.IsEmpty())
               filter.limit = atoi(limit.c_str());

            AnsiString offset = QueryParameter_(route.query, "offset");
            if (!offset.IsEmpty())
               filter.offset = atoi(offset.c_str());

            std::vector<AuditTrail::Entry> entries;
            int total = 0;

            if (!AuditTrail::Instance()->Query(filter, entries, total))
               return BuildResponse_(500, "{\"error\":\"the audit trail could not be read\"}");

            AnsiString body;
            AnsiString head;
            head.Format("{\"total\":%d,\"offset\":%d,\"entries\":[", total, filter.offset < 0 ? 0 : filter.offset);
            body += head;

            for (size_t i = 0; i < entries.size(); i++)
            {
               if (i > 0)
                  body += ",";

               body += AuditEntryJson(entries[i], escape);
            }

            body += "]}";

            return BuildResponse_(200, body);
         }

      case RouteAuditVerify:
         {
            __int64 firstBroken = 0;
            __int64 rowsChecked = 0;
            String reason;

            bool intact = AuditTrail::Instance()->Verify(firstBroken, rowsChecked, reason);

            AnsiString body;
            body.Format("{\"intact\":%hs,\"rows_checked\":%I64d,\"first_broken_id\":%I64d,\"reason\":\"%hs\"}",
               intact ? "true" : "false", rowsChecked, firstBroken, escape(Utf8_(reason)).c_str());

            return BuildResponse_(200, body);
         }

      case RouteAlertRuleList:
         {
            std::vector<AlertManager::Rule> rules;

            if (!AlertManager::Instance()->GetRules(rules))
               return BuildResponse_(500, "{\"error\":\"the alert rules could not be read\"}");

            AnsiString body = "{\"rules\":[";

            for (size_t i = 0; i < rules.size(); i++)
            {
               if (i > 0)
                  body += ",";

               body += AlertRuleJson(rules[i], escape);
            }

            body += "]}";

            return BuildResponse_(200, body);
         }

      case RouteAlertRuleUpdate:
         {
            // Conditions are stored lower-case; see AlertManager::SaveRule.
            String condition = String(route.identifier);
            condition.MakeLower();

            AlertManager::Rule rule;
            bool exists = AlertManager::Instance()->GetRule(condition, rule);

            rule.condition = condition;

            JsonValue document;
            std::string parseError;
            if (!JsonValue::Parse(std::string(requestBody.c_str(), requestBody.GetLength()), document, parseError) || !document.IsObject())
               return BuildResponse_(400, "{\"error\":\"the body must be a JSON object\"}");

            // A field the body leaves out keeps the value it has, so a client
            // that wants to switch one condition on does not have to send back
            // every other field of the rule - and cannot blank a webhook secret
            // by forgetting it.
            const JsonValue *member = nullptr;

            if ((member = document.Get("enabled")) != nullptr)
               rule.enabled = member->AsBool();
            if ((member = document.Get("digest")) != nullptr)
               rule.digest = member->AsBool();
            if ((member = document.Get("threshold")) != nullptr)
               rule.threshold = (int) member->AsInt64();
            if ((member = document.Get("actions")) != nullptr)
               rule.actions = (int) member->AsInt64();
            if ((member = document.Get("cooldown_minutes")) != nullptr)
               rule.cooldown = (int) member->AsInt64();
            if ((member = document.Get("address")) != nullptr)
               rule.address = String(AnsiString(member->AsString().c_str()));
            if ((member = document.Get("webhook")) != nullptr)
               rule.webhook = String(AnsiString(member->AsString().c_str()));

            // Write-only, as a secret should be: it can be set, it can be
            // cleared with "", and it is never handed back.
            if ((member = document.Get("webhook_secret")) != nullptr)
               rule.secret = String(AnsiString(member->AsString().c_str()));

            String refusal;
            if (!AlertManager::Instance()->SaveRule(rule, refusal))
            {
               AnsiString error;
               error.Format("{\"error\":\"%hs\"}", escape(Utf8_(refusal)).c_str());
               return BuildResponse_(400, error);
            }

            LOG_APPLICATION("RestApi: Alert rule '" + condition + "' " + String(exists ? _T("changed") : _T("created")) + ".");

            AlertManager::Rule saved;
            AlertManager::Instance()->GetRule(condition, saved);

            return BuildResponse_(exists ? 200 : 201, AlertRuleJson(saved, escape));
         }

      case RouteAlertEventList:
         {
            int limit = 100;

            AnsiString limitText = QueryParameter_(route.query, "limit");
            if (!limitText.IsEmpty())
               limit = atoi(limitText.c_str());

            std::vector<AlertManager::Event> events;

            if (!AlertManager::Instance()->GetEvents(limit, events))
               return BuildResponse_(500, "{\"error\":\"the alert events could not be read\"}");

            AnsiString body = "{\"events\":[";

            for (size_t i = 0; i < events.size(); i++)
            {
               if (i > 0)
                  body += ",";

               body += AlertEventJson(events[i], escape);
            }

            body += "]}";

            return BuildResponse_(200, body);
         }

      case RouteAlertTest:
         {
            JsonValue document;
            std::string parseError;
            if (!JsonValue::Parse(std::string(requestBody.c_str(), requestBody.GetLength()), document, parseError) || !document.IsObject())
               return BuildResponse_(400, "{\"error\":\"the body must be a JSON object\"}");

            String condition = String(AnsiString(document.GetString("condition").c_str()));
            condition.Trim();
            condition.MakeLower();

            if (condition.IsEmpty())
               return BuildResponse_(400, "{\"error\":\"name the condition to raise\"}");

            String summary = String(AnsiString(document.GetString("summary",
               "Raised on request, to prove that alerting reaches somebody.").c_str()));

            // Through exactly the path a real condition takes - the same rule,
            // the same cool-down, the same ceiling - so that what arrives is
            // what would arrive. A condition with no rule, or a rule that is
            // off, raises nothing and says so rather than pretending.
            bool raised = document.GetBool("occurrence")
               ? AlertManager::Instance()->RaiseOccurrence(condition, ErrorManager::Low, summary, _T("Raised through POST /api/v1/alerts/test."))
               : AlertManager::Instance()->Raise(condition, ErrorManager::Low, summary, _T("Raised through POST /api/v1/alerts/test."));

            AnsiString body;
            body.Format("{\"raised\":%hs}", raised ? "true" : "false");

            return BuildResponse_(200, body);
         }

      case RouteAlertRun:
         {
            // One pass of AlertTask, now. The scheduled one runs every minute;
            // this is for the administrator who has just configured a recipient
            // or a webhook and wants to know before the minute is out.
            int evaluated = AlertManager::Instance()->EvaluateScheduledConditions();
            int notified = AlertManager::Instance()->SendPendingNotifications();
            int delivered = AlertManager::Instance()->RunWebhookDeliveries();
            int digested = AlertManager::Instance()->SendDigestIfDue();

            AnsiString body;
            body.Format("{\"conditions_raised\":%d,\"notifications_sent\":%d,\"webhooks_delivered\":%d,\"digest_events\":%d}",
               evaluated, notified, delivered, digested);

            return BuildResponse_(200, body);
         }

      default:
         break;
      }

      return BuildResponse_(404, "{\"error\":\"not found\"}");
   }

   AnsiString
   RestApiServer::OpenApiAuditPaths_()
   {
      static const char *paths =
         ",\"/api/v1/audit\":{\"get\":{\"summary\":\"The audit trail\","
         "\"description\":\"Every administrative change, newest first, with the administrator or key that made it, the interface it came over, the address it came from, and the hash chain each row carries. Administrator credential only.\","
         "\"parameters\":["
         "{\"name\":\"actor\",\"in\":\"query\",\"schema\":{\"type\":\"string\"},\"description\":\"Substring of the actor, so key: finds every API key.\"},"
         "{\"name\":\"object_type\",\"in\":\"query\",\"schema\":{\"type\":\"string\"},\"description\":\"domain, account, setting, rule and so on.\"},"
         "{\"name\":\"action\",\"in\":\"query\",\"schema\":{\"type\":\"string\"},\"description\":\"created, updated or deleted.\"},"
         "{\"name\":\"since\",\"in\":\"query\",\"schema\":{\"type\":\"integer\"},\"description\":\"Seconds since the epoch, inclusive.\"},"
         "{\"name\":\"until\",\"in\":\"query\",\"schema\":{\"type\":\"integer\"},\"description\":\"Seconds since the epoch, inclusive.\"},"
         "{\"name\":\"limit\",\"in\":\"query\",\"schema\":{\"type\":\"integer\"},\"description\":\"Rows to return, 1 to 1000. Default 100.\"},"
         "{\"name\":\"offset\",\"in\":\"query\",\"schema\":{\"type\":\"integer\"},\"description\":\"Rows to skip.\"}"
         "],\"responses\":{\"200\":{\"description\":\"The page of entries and the total that match.\"}}}}"
         ",\"/api/v1/audit/verify\":{\"get\":{\"summary\":\"Verify the audit chain\","
         "\"description\":\"Walks the hash chain from the first row and reports the first row whose stored hash is not the hash its contents produce, or which does not carry the hash of the row before it. Administrator credential only.\","
         "\"responses\":{\"200\":{\"description\":\"intact, rows_checked, first_broken_id and the reason.\"}}}}"
         ",\"/api/v1/alerts/rules\":{\"get\":{\"summary\":\"The alert rules\","
         "\"description\":\"One row per condition: whether it is on, its threshold, whether it mails (1), calls a webhook (2) or both (3), its cool-down and whether it waits for the daily digest. A webhook secret is never returned; webhook_secret_set says whether one is configured. Administrator credential only.\","
         "\"responses\":{\"200\":{\"description\":\"The rules.\"}}}}"
         ",\"/api/v1/alerts/rules/{condition}\":{\"put\":{\"summary\":\"Change or create an alert rule\","
         "\"description\":\"Fields left out keep the value they have. webhook_secret is write-only; an empty string clears it. A condition that does not exist yet is created, which is how a condition raised by an event script or a future check is configured without a schema change. Administrator credential only.\","
         "\"parameters\":[{\"name\":\"condition\",\"in\":\"path\",\"required\":true,\"schema\":{\"type\":\"string\"}}],"
         "\"responses\":{\"200\":{\"description\":\"The rule as it now stands.\"},\"201\":{\"description\":\"The rule was created.\"},\"400\":{\"description\":\"The rule was refused, with the reason.\"}}}}"
         ",\"/api/v1/alerts/test\":{\"post\":{\"summary\":\"Raise a condition on request\","
         "\"description\":\"Raises the named condition through exactly the path a real one takes - the same rule, the same cool-down, the same ceiling - so that what arrives is what would arrive. Answers raised:false when the condition has no rule, its rule is off, or the condition is already raised. Administrator credential only.\","
         "\"responses\":{\"200\":{\"description\":\"Whether an event was recorded.\"},\"400\":{\"description\":\"No condition was named.\"}}}}"
         ",\"/api/v1/alerts/run\":{\"post\":{\"summary\":\"Run one alerting pass now\","
         "\"description\":\"Evaluates the scheduled conditions, sends what is waiting, attempts the webhooks that are due and sends the digest if this is its hour - the same pass the scheduled task makes every minute. Administrator credential only.\","
         "\"responses\":{\"200\":{\"description\":\"What the pass did.\"}}}}"
         ",\"/api/v1/alerts/events\":{\"get\":{\"summary\":\"What has fired\","
         "\"description\":\"Alert events, newest first: the condition, whether it was raised or cleared, whether it has been notified or is held for the digest, and how the webhook delivery went. Administrator credential only.\","
         "\"parameters\":[{\"name\":\"limit\",\"in\":\"query\",\"schema\":{\"type\":\"integer\"},\"description\":\"Events to return, 1 to 500. Default 100.\"}],"
         "\"responses\":{\"200\":{\"description\":\"The events.\"}}}}";

      return AnsiString(paths);
   }
}

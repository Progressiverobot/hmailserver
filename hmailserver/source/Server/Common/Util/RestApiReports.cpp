// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

// GET /api/v1/reports - what happened on this server, per domain and per day.
//
// Reports.h says where every number comes from and which ones this server
// cannot honestly produce. This file is the route: a window, a domain filter,
// seven sections and a summary, and one table shape that both the JSON and the
// CSV are written from, so a spreadsheet and a page can never be shown
// different numbers for the same question.
//
// WHY ONE TABLE SHAPE. Each section produces columns and rows as text, once.
// The JSON writer emits that; the CSV writer emits the same thing with commas.
// The alternative - a hand-written JSON document per section and a second
// hand-written CSV per section - is sixteen places for an arithmetic change to
// land in only fifteen of.
//
// THE DOMAIN FILTER, and the one rule it must not get wrong. Only LOCAL
// domains are reported. An address at a domain this server does not host
// appears in the trace constantly - it is the other end of every conversation
// - and folding it into a per-domain report would tell an administrator that
// gmail.com received four hundred messages on this server. So every fold is
// against the set of domains this server hosts, then against the domain the
// request asked for, then against the domains the caller's key permits. A
// domain-restricted key that names no domain gets its own domains and nothing
// else, which is the treatment GET /api/v1/domains already gives it.

#include "StdAfx.h"

#include "RestApiServer.h"
#include "Reports.h"
#include "MessageTrace.h"
#include "Time.h"

#include "../Application/IniFileSettings.h"
#include "../BO/Domain.h"
#include "../BO/Domains.h"

#include <algorithm>
#include <map>
#include <set>
#include <vector>

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   namespace
   {
      // The JSON string escaper, handed in by the member function that owns
      // it. The same arrangement RestApiGroups.cpp makes with its Bridge: the
      // helpers below are not members of RestApiServer and so cannot reach its
      // private escaper, and writing a second escaper here would be a second
      // opinion about what a quotation mark is.
      typedef AnsiString (*Escaper)(const AnsiString &);

      // ----------------------------------------------------------- the sections

      struct SectionInfo
      {
         const char *name;
         bool server_wide;
         const char *source;
         const char *summary;
      };

      const SectionInfo SECTIONS[] =
      {
         { "traffic", false, "hm_messagetrace",
           "Messages delivered in and out, and deliveries that failed, per day and per local domain." },
         { "failures", false, "hm_messagetrace",
           "Delivery failures grouped by the SMTP status code the attempt ended with, per local domain." },
         { "senders", false, "hm_messagetrace",
           "The addresses that sent the most, with how many of their deliveries failed." },
         { "recipients", false, "hm_messagetrace",
           "The addresses that received the most, with how many deliveries to them failed." },
         { "mailboxes", false, "hm_messages",
           "The largest mailboxes as they are now, and the same totalled per domain. A size now, not a history." },
         { "volume", true, "hm_metricsamples",
           "Messages processed, delivered, deferred and bounced, spam detected and viruses removed, per day. Server-wide: these counters carry no domain." },
         { "storage", true, "hm_metricsamples",
           "The size of the message store per day, and what it grew by over the window. Server-wide." }
      };

      const int SectionCount = (int) (sizeof(SECTIONS) / sizeof(SECTIONS[0]));

      const SectionInfo *FindSection(const AnsiString &name)
      {
         for (int i = 0; i < SectionCount; i++)
         {
            if (name.CompareNoCase(SECTIONS[i].name) == 0)
               return &SECTIONS[i];
         }

         return 0;
      }

      // ----------------------------------------------------------------- dates
      //
      // Days are counted with civil-calendar arithmetic rather than with
      // mktime, because mktime reads a date in the machine's time zone and can
      // answer a different day either side of a daylight-saving change. A
      // report's day is a calendar day and nothing else.

      long long DaysFromCivil(int year, int month, int day)
      {
         year -= month <= 2 ? 1 : 0;

         const long long era = (year >= 0 ? year : year - 399) / 400;
         const unsigned yearOfEra = (unsigned) (year - era * 400);
         const unsigned dayOfYear = (unsigned) ((153 * (month + (month > 2 ? -3 : 9)) + 2) / 5 + day - 1);
         const unsigned dayOfEra = yearOfEra * 365 + yearOfEra / 4 - yearOfEra / 100 + dayOfYear;

         return era * 146097 + (long long) dayOfEra - 719468;
      }

      void CivilFromDays(long long days, int &year, int &month, int &day)
      {
         days += 719468;

         const long long era = (days >= 0 ? days : days - 146096) / 146097;
         const unsigned dayOfEra = (unsigned) (days - era * 146097);
         const unsigned yearOfEra = (dayOfEra - dayOfEra / 1460 + dayOfEra / 36524 - dayOfEra / 146096) / 365;
         const long long civilYear = (long long) yearOfEra + era * 400;
         const unsigned dayOfYear = dayOfEra - (365 * yearOfEra + yearOfEra / 4 - yearOfEra / 100);
         const unsigned monthPrime = (5 * dayOfYear + 2) / 153;

         day = (int) (dayOfYear - (153 * monthPrime + 2) / 5 + 1);
         // The cast comes first: monthPrime is unsigned and the adjustment is
         // negative for the last three months, and unsigned arithmetic with a
         // negative addend is both a warning and, at /WX, a build failure.
         month = (int) monthPrime + (monthPrime < 10 ? 3 : -9);
         year = (int) (civilYear + (month <= 2 ? 1 : 0));
      }

      bool ParseDay(const AnsiString &text, int &year, int &month, int &day)
      {
         if (text.GetLength() != 10 || text.GetAt(4) != '-' || text.GetAt(7) != '-')
            return false;

         for (int i = 0; i < 10; i++)
         {
            if (i == 4 || i == 7)
               continue;

            char c = text.GetAt(i);

            if (c < '0' || c > '9')
               return false;
         }

         year = atoi(text.Mid(0, 4).c_str());
         month = atoi(text.Mid(5, 2).c_str());
         day = atoi(text.Mid(8, 2).c_str());

         if (year < 1970 || year > 9999 || month < 1 || month > 12 || day < 1 || day > 31)
            return false;

         // The round trip refuses 31 February without a table of month lengths.
         int checkYear = 0, checkMonth = 0, checkDay = 0;
         CivilFromDays(DaysFromCivil(year, month, day), checkYear, checkMonth, checkDay);

         return checkYear == year && checkMonth == month && checkDay == day;
      }

      AnsiString DayText(long long days)
      {
         int year = 0, month = 0, day = 0;
         CivilFromDays(days, year, month, day);

         AnsiString text;
         text.Format("%04d-%02d-%02d", year, month, day);
         return text;
      }

      // The server's own calendar day, which is the clock that stamped the
      // trace rows and the metric samples alike.
      AnsiString TodayOnThisServer()
      {
         AnsiString now = AnsiString(Time::GetCurrentDate());

         if (now.GetLength() >= 10)
            return now.Mid(0, 10);

         return "1970-01-01";
      }

      AnsiString WholeNumber(__int64 value)
      {
         AnsiString text;
         text.Format("%I64d", value);
         return text;
      }

      AnsiString RoundedNumber(double value)
      {
         AnsiString text;
         text.Format("%.0f", value < 0 ? 0 : value);
         return text;
      }

      // ------------------------------------------------------------- the table

      struct Table
      {
         std::vector<AnsiString> columns;
         std::vector<bool> numeric;
         std::vector<std::vector<AnsiString> > rows;

         void Column(const char *name, bool isNumber)
         {
            columns.push_back(AnsiString(name));
            numeric.push_back(isNumber);
         }
      };

      struct Section
      {
         Section() : enabled(true), truncated(false) { }

         AnsiString name;
         AnsiString source;
         AnsiString note;
         bool enabled;
         bool truncated;
         Table table;
         AnsiString extra;     // further JSON members, each beginning with a comma
      };

      // The SMTP status code a failed delivery ended with, in words. A code
      // this table does not know is described by its class, which is the one
      // thing RFC 5321 guarantees about a code nobody has seen before; 0 means
      // the call site had no code to record, not that the code was zero.
      const char *ReasonFor(int status)
      {
         switch (status)
         {
         case 0:   return "no status code was recorded";
         case 250: return "delivered";
         case 421: return "the service was not available; the connection was closed";
         case 450: return "the mailbox was busy or unavailable; try again";
         case 451: return "the server had a local error; try again";
         case 452: return "the server had insufficient storage; try again";
         case 454: return "TLS was not available";
         case 500: return "the command was not understood";
         case 501: return "a parameter was wrong";
         case 503: return "the commands came in the wrong order";
         case 504: return "a parameter is not implemented";
         case 521: return "the host does not accept mail";
         case 530: return "authentication was required";
         case 535: return "authentication failed";
         case 550: return "the mailbox was unavailable, or the message was refused";
         case 551: return "the user is not local";
         case 552: return "the message was too large, or the mailbox was full";
         case 553: return "the address was not allowed";
         case 554: return "the transaction failed";
         default:  break;
         }

         if (status >= 400 && status < 500)
            return "a temporary failure; the sender may try again";

         if (status >= 500 && status < 600)
            return "a permanent failure";

         return "an unrecognised status code";
      }

      // ------------------------------------------------------------------- CSV

      AnsiString CsvCell(const AnsiString &value)
      {
         bool quote = false;

         for (int i = 0; i < value.GetLength(); i++)
         {
            char c = value.GetAt(i);

            if (c == ',' || c == '"' || c == '\r' || c == '\n')
            {
               quote = true;
               break;
            }
         }

         // A leading =, +, - or @ is what a spreadsheet reads as a formula, and
         // these cells hold addresses somebody else chose. Prefixed with an
         // apostrophe, which every spreadsheet reads as "this is text": a
         // report of who sent the most mail must not be a way to run a formula
         // on the administrator's machine.
         bool formula = !value.IsEmpty() &&
            (value.GetAt(0) == '=' || value.GetAt(0) == '+' || value.GetAt(0) == '-' || value.GetAt(0) == '@');

         if (!quote && !formula)
            return value;

         AnsiString out = "\"";

         if (formula)
            out += "'";

         for (int i = 0; i < value.GetLength(); i++)
         {
            char c = value.GetAt(i);

            if (c == '"')
               out += '"';

            out += c;
         }

         out += "\"";
         return out;
      }

      AnsiString TableAsCsv(const Table &table)
      {
         AnsiString out;

         for (size_t i = 0; i < table.columns.size(); i++)
         {
            if (i > 0)
               out += ",";

            out += CsvCell(table.columns[i]);
         }

         out += "\r\n";

         for (const std::vector<AnsiString> &row : table.rows)
         {
            for (size_t i = 0; i < row.size(); i++)
            {
               if (i > 0)
                  out += ",";

               out += CsvCell(row[i]);
            }

            out += "\r\n";
         }

         return out;
      }

      // --------------------------------------------------------------- context

      // Everything one request needs, read once. A section that is not asked
      // for costs nothing: the trace is read only when a trace section is
      // wanted, the mailboxes only when the mailbox section is.
      struct Context
      {
         Context() : escape(0), top(10), csv(false), trace_loaded(false), trace_ok(false),
                     trace_truncated(false), mailboxes_loaded(false), mailboxes_ok(false) { }

         Escaper escape;

         Reports::Range range;
         AnsiString from_day;
         AnsiString to_day;         // inclusive, as the caller asked for it
         AnsiString domain;         // "" is every domain the caller may see
         int top;
         bool csv;

         std::vector<String> allowed;     // the caller's key restriction; empty is every domain
         std::set<AnsiString> local;      // the domains this server hosts, lower case

         bool trace_loaded;
         bool trace_ok;
         bool trace_truncated;
         std::vector<Reports::TraceRow> trace;

         bool mailboxes_loaded;
         bool mailboxes_ok;
         std::vector<Reports::MailboxRow> mailboxes;

         // Whether this domain may appear in the answer at all: hosted here,
         // asked for, and permitted to this credential. All three, every time.
         bool Wanted(const AnsiString &candidate) const
         {
            if (candidate.IsEmpty())
               return false;

            if (local.find(candidate) == local.end())
               return false;

            if (!domain.IsEmpty() && domain.CompareNoCase(candidate) != 0)
               return false;

            if (allowed.empty())
               return true;

            for (const String &one : allowed)
            {
               if (one.CompareNoCase(String(candidate).c_str()) == 0)
                  return true;
            }

            return false;
         }
      };

      void LoadTrace(Context &context)
      {
         if (context.trace_loaded)
            return;

         context.trace_loaded = true;
         context.trace_ok = Reports::ReadTrace(context.range, context.trace, context.trace_truncated);
      }

      void LoadMailboxes(Context &context)
      {
         if (context.mailboxes_loaded)
            return;

         context.mailboxes_loaded = true;
         context.mailboxes_ok = Reports::ReadMailboxes(context.mailboxes);
      }

      bool IsDelivered(const String &event)
      {
         return event.CompareNoCase(_T("delivered")) == 0;
      }

      bool IsFailed(const String &event)
      {
         return event.CompareNoCase(_T("failed")) == 0;
      }

      const char *TraceNote(const Context &context)
      {
         if (!MessageTrace::GetEnabled())
            return "The message trace is switched off (MessageTraceEnabled), so nothing is being recorded and this "
                   "section is empty. It is off by default because it records who corresponds with whom.";

         if (!context.trace_ok)
            return "The message trace could not be read.";

         return "Counted from the message trace: one row per recipient per delivery attempt, so a message to three "
                "recipients is three deliveries. Only deliveries and failures are recorded; a message still in the "
                "queue is in neither column, and there is no spam or virus event in this table.";
      }

      // --------------------------------------------------------------- traffic

      struct TrafficCell
      {
         TrafficCell() : incoming(0), outgoing(0), failed_incoming(0), failed_outgoing(0) { }

         __int64 incoming;
         __int64 outgoing;
         __int64 failed_incoming;
         __int64 failed_outgoing;
      };

      Section BuildTraffic(Context &context)
      {
         LoadTrace(context);

         Section section;
         section.name = "traffic";
         section.source = "hm_messagetrace";
         section.enabled = MessageTrace::GetEnabled();
         section.truncated = context.trace_truncated;
         section.note = TraceNote(context);

         section.table.Column("day", false);
         section.table.Column("domain", false);
         section.table.Column("incoming", true);
         section.table.Column("outgoing", true);
         section.table.Column("failed_incoming", true);
         section.table.Column("failed_outgoing", true);

         std::map<AnsiString, TrafficCell> cells;
         TrafficCell totals;

         for (const Reports::TraceRow &row : context.trace)
         {
            bool delivered = IsDelivered(row.event);
            bool failed = IsFailed(row.event);

            if (!delivered && !failed)
               continue;

            AnsiString day;
            day.Format("%04d-%02d-%02d", row.year, row.month, row.day);

            AnsiString senderDomain = AnsiString(Reports::DomainOf(row.sender));
            AnsiString recipientDomain = AnsiString(Reports::DomainOf(row.recipient));

            // A local sender writing to a local recipient counts once as this
            // domain's outgoing and once as its incoming, which is what
            // happened: the server carried it both ways.
            if (context.Wanted(recipientDomain))
            {
               TrafficCell &cell = cells[day + "\t" + recipientDomain];

               if (delivered) { cell.incoming += row.count; totals.incoming += row.count; }
               else           { cell.failed_incoming += row.count; totals.failed_incoming += row.count; }
            }

            if (context.Wanted(senderDomain))
            {
               TrafficCell &cell = cells[day + "\t" + senderDomain];

               if (delivered) { cell.outgoing += row.count; totals.outgoing += row.count; }
               else           { cell.failed_outgoing += row.count; totals.failed_outgoing += row.count; }
            }
         }

         // std::map orders by the key, which is day then domain: the order a
         // reader wants and the order a chart needs.
         for (std::map<AnsiString, TrafficCell>::const_iterator it = cells.begin(); it != cells.end(); ++it)
         {
            int tab = it->first.Find("\t");

            std::vector<AnsiString> row;
            row.push_back(it->first.Mid(0, tab));
            row.push_back(it->first.Mid(tab + 1));
            row.push_back(WholeNumber(it->second.incoming));
            row.push_back(WholeNumber(it->second.outgoing));
            row.push_back(WholeNumber(it->second.failed_incoming));
            row.push_back(WholeNumber(it->second.failed_outgoing));

            section.table.rows.push_back(row);
         }

         section.extra.Format(",\"totals\":{\"incoming\":%I64d,\"outgoing\":%I64d,\"failed_incoming\":%I64d,\"failed_outgoing\":%I64d}",
            totals.incoming, totals.outgoing, totals.failed_incoming, totals.failed_outgoing);

         return section;
      }

      // -------------------------------------------------------------- failures

      struct FailureCell
      {
         FailureCell() : status(0), incoming(0), outgoing(0) { }

         int status;
         __int64 incoming;
         __int64 outgoing;
      };

      Section BuildFailures(Context &context)
      {
         LoadTrace(context);

         Section section;
         section.name = "failures";
         section.source = "hm_messagetrace";
         section.enabled = MessageTrace::GetEnabled();
         section.truncated = context.trace_truncated;
         section.note = "The reason is the SMTP status code the attempt ended with, put into words here. A message "
                        "refused at RCPT - an unknown recipient, a blocked sender - is a failure carrying the code "
                        "of that refusal; there was never a queued message to fail later.";

         section.table.Column("domain", false);
         section.table.Column("status", true);
         section.table.Column("reason", false);
         section.table.Column("incoming", true);
         section.table.Column("outgoing", true);
         section.table.Column("total", true);

         std::map<AnsiString, FailureCell> cells;

         for (const Reports::TraceRow &row : context.trace)
         {
            if (!IsFailed(row.event))
               continue;

            AnsiString senderDomain = AnsiString(Reports::DomainOf(row.sender));
            AnsiString recipientDomain = AnsiString(Reports::DomainOf(row.recipient));

            // Zero-padded so that the map's ordering is by number rather than
            // by the text of the number, which would put 550 before 99.
            AnsiString status;
            status.Format("%06d", row.status);

            if (context.Wanted(recipientDomain))
            {
               FailureCell &cell = cells[recipientDomain + "\t" + status];
               cell.status = row.status;
               cell.incoming += row.count;
            }

            if (context.Wanted(senderDomain))
            {
               FailureCell &cell = cells[senderDomain + "\t" + status];
               cell.status = row.status;
               cell.outgoing += row.count;
            }
         }

         for (std::map<AnsiString, FailureCell>::const_iterator it = cells.begin(); it != cells.end(); ++it)
         {
            int tab = it->first.Find("\t");

            std::vector<AnsiString> row;
            row.push_back(it->first.Mid(0, tab));
            row.push_back(WholeNumber((__int64) it->second.status));
            row.push_back(AnsiString(ReasonFor(it->second.status)));
            row.push_back(WholeNumber(it->second.incoming));
            row.push_back(WholeNumber(it->second.outgoing));
            row.push_back(WholeNumber(it->second.incoming + it->second.outgoing));

            section.table.rows.push_back(row);
         }

         return section;
      }

      // ----------------------------------------------------- senders/recipients

      struct AddressCell
      {
         AddressCell() : messages(0), failed(0) { }

         AnsiString domain;
         __int64 messages;
         __int64 failed;
      };

      bool MoreMessages(const std::pair<AnsiString, AddressCell> &left, const std::pair<AnsiString, AddressCell> &right)
      {
         if (left.second.messages != right.second.messages)
            return left.second.messages > right.second.messages;

         // A tie broken by the address, so that two runs of the same report
         // over the same rows put the same names in the same order.
         return left.first < right.first;
      }

      Section BuildAddresses(Context &context, bool senders)
      {
         LoadTrace(context);

         Section section;
         section.name = senders ? "senders" : "recipients";
         section.source = "hm_messagetrace";
         section.enabled = MessageTrace::GetEnabled();
         section.truncated = context.trace_truncated;
         section.note = senders
            ? "The addresses this server delivered the most mail FROM, counted per recipient. Only senders in a "
              "local domain are listed: a report about this server's domains should not name the world's."
            : "The addresses this server delivered the most mail TO. Only recipients in a local domain are listed.";

         section.table.Column(senders ? "sender" : "recipient", false);
         section.table.Column("domain", false);
         section.table.Column("messages", true);
         section.table.Column("failed", true);

         std::map<AnsiString, AddressCell> cells;

         for (const Reports::TraceRow &row : context.trace)
         {
            bool delivered = IsDelivered(row.event);
            bool failed = IsFailed(row.event);

            if (!delivered && !failed)
               continue;

            String raw = senders ? row.sender : row.recipient;

            AnsiString domain = AnsiString(Reports::DomainOf(raw));

            if (!context.Wanted(domain))
               continue;

            AnsiString address = AnsiString(raw);
            address.ToLower();

            AddressCell &cell = cells[address];
            cell.domain = domain;

            if (delivered)
               cell.messages += row.count;
            else
               cell.failed += row.count;
         }

         std::vector<std::pair<AnsiString, AddressCell> > ordered(cells.begin(), cells.end());
         std::sort(ordered.begin(), ordered.end(), MoreMessages);

         for (size_t i = 0; i < ordered.size() && (int) i < context.top; i++)
         {
            std::vector<AnsiString> row;
            row.push_back(ordered[i].first);
            row.push_back(ordered[i].second.domain);
            row.push_back(WholeNumber(ordered[i].second.messages));
            row.push_back(WholeNumber(ordered[i].second.failed));

            section.table.rows.push_back(row);
         }

         section.extra.Format(",\"addresses_seen\":%I64d,\"top\":%d", (__int64) ordered.size(), context.top);

         return section;
      }

      // ------------------------------------------------------------- mailboxes

      bool BiggerMailbox(const Reports::MailboxRow &left, const Reports::MailboxRow &right)
      {
         if (left.bytes != right.bytes)
            return left.bytes > right.bytes;

         return left.address.CompareNoCase(right.address.c_str()) < 0;
      }

      struct DomainTotal
      {
         DomainTotal() : mailboxes(0), messages(0), bytes(0) { }

         __int64 mailboxes;
         __int64 messages;
         __int64 bytes;
      };

      Section BuildMailboxes(Context &context)
      {
         LoadMailboxes(context);

         Section section;
         section.name = "mailboxes";
         section.source = "hm_messages";
         section.enabled = context.mailboxes_ok;
         section.note = "The size of every mailbox AS IT IS NOW, not over the window: nothing has ever recorded "
                        "what a mailbox held yesterday, so this section ignores the dates. A mailbox holding no "
                        "messages does not appear, because there is no row to count.";

         section.table.Column("address", false);
         section.table.Column("domain", false);
         section.table.Column("messages", true);
         section.table.Column("bytes", true);
         section.table.Column("megabytes", true);

         std::vector<Reports::MailboxRow> wanted;
         std::map<AnsiString, DomainTotal> perDomain;

         for (const Reports::MailboxRow &row : context.mailboxes)
         {
            AnsiString domain = AnsiString(row.domain);

            if (!context.Wanted(domain))
               continue;

            wanted.push_back(row);

            DomainTotal &total = perDomain[domain];
            total.mailboxes++;
            total.messages += row.messages;
            total.bytes += row.bytes;
         }

         std::sort(wanted.begin(), wanted.end(), BiggerMailbox);

         for (size_t i = 0; i < wanted.size() && (int) i < context.top; i++)
         {
            std::vector<AnsiString> row;
            row.push_back(AnsiString(wanted[i].address));
            row.push_back(AnsiString(wanted[i].domain));
            row.push_back(WholeNumber(wanted[i].messages));
            row.push_back(WholeNumber(wanted[i].bytes));
            row.push_back(WholeNumber(wanted[i].bytes / (1024 * 1024)));

            section.table.rows.push_back(row);
         }

         // The per-domain roll-up rides along as a second table in the JSON.
         // The CSV of this section is the mailboxes, which is the table an
         // administrator asked for; the roll-up is short enough to read.
         AnsiString domainsJson = ",\"domains\":[";
         bool first = true;

         for (std::map<AnsiString, DomainTotal>::const_iterator it = perDomain.begin(); it != perDomain.end(); ++it)
         {
            AnsiString entry;
            entry.Format("%s{\"domain\":\"%hs\",\"mailboxes\":%I64d,\"messages\":%I64d,\"bytes\":%I64d,\"megabytes\":%I64d}",
               first ? "" : ",",
               context.escape(it->first).c_str(),
               it->second.mailboxes, it->second.messages, it->second.bytes,
               it->second.bytes / (1024 * 1024));

            domainsJson += entry;
            first = false;
         }

         domainsJson += "]";

         AnsiString counted;
         counted.Format(",\"mailboxes_seen\":%I64d,\"top\":%d", (__int64) wanted.size(), context.top);

         section.extra = counted + domainsJson;

         return section;
      }

      // ---------------------------------------------------------------- volume

      struct MetricColumn
      {
         const char *column;
         const char *metric;
      };

      const MetricColumn VOLUME_METRICS[] =
      {
         { "processed", "processed_messages_total" },
         { "delivered", "messages_delivered_total" },
         { "deferred", "messages_deferred_total" },
         { "bounced", "messages_bounced_total" },
         { "spam", "spam_messages_total" },
         { "viruses", "viruses_removed_total" }
      };

      const int VolumeMetricCount = (int) (sizeof(VOLUME_METRICS) / sizeof(VOLUME_METRICS[0]));

      Section BuildVolume(Context &context)
      {
         Section section;
         section.name = "volume";
         section.source = "hm_metricsamples";
         section.enabled = IniFileSettings::Instance()->GetMetricsHistoryDays() > 0;
         section.note = "Counted from the server-wide metric history. These counters carry no domain, so this "
                        "section is the whole server whatever domain the request named - which is why the spam and "
                        "virus figures cannot be given per domain. A day's figure is the counter's increase across "
                        "that day, with a restart read as a restart rather than as a negative rate.";

         section.table.Column("day", false);

         for (int i = 0; i < VolumeMetricCount; i++)
            section.table.Column(VOLUME_METRICS[i].column, true);

         std::map<AnsiString, std::vector<double> > byDay;

         for (int i = 0; i < VolumeMetricCount; i++)
         {
            std::vector<Reports::DayValue> days;
            Reports::ReadCounterIncreaseByDay(String(VOLUME_METRICS[i].metric), context.range, days);

            for (const Reports::DayValue &value : days)
            {
               std::vector<double> &row = byDay[AnsiString(value.day)];

               if (row.empty())
                  row.resize(VolumeMetricCount, 0);

               row[i] = value.value;
            }
         }

         for (std::map<AnsiString, std::vector<double> >::const_iterator it = byDay.begin(); it != byDay.end(); ++it)
         {
            std::vector<AnsiString> row;
            row.push_back(it->first);

            for (int i = 0; i < VolumeMetricCount; i++)
               row.push_back(RoundedNumber(it->second[i]));

            section.table.rows.push_back(row);
         }

         section.extra.Format(",\"retention_days\":%d", IniFileSettings::Instance()->GetMetricsHistoryDays());

         return section;
      }

      // --------------------------------------------------------------- storage

      Section BuildStorage(Context &context)
      {
         Section section;
         section.name = "storage";
         section.source = "hm_metricsamples";
         section.enabled = IniFileSettings::Instance()->GetMetricsHistoryDays() > 0;
         section.note = "The size of the whole message store, sampled into the metric history as store_bytes and "
                        "store_messages and recomputed at most once an hour. Server-wide: there is no per-domain "
                        "sample, so a domain's growth over time cannot be answered - only its size now, which is "
                        "in the mailboxes section.";

         section.table.Column("day", false);
         section.table.Column("bytes", true);
         section.table.Column("megabytes", true);
         section.table.Column("messages", true);

         std::vector<Reports::DayValue> bytes;
         std::vector<Reports::DayValue> messages;

         Reports::ReadGaugeByDay(_T("store_bytes"), context.range, bytes);
         Reports::ReadGaugeByDay(_T("store_messages"), context.range, messages);

         std::map<AnsiString, double> messagesByDay;

         for (const Reports::DayValue &value : messages)
            messagesByDay[AnsiString(value.day)] = value.value;

         double firstBytes = 0;
         double lastBytes = 0;
         bool haveFirst = false;

         for (const Reports::DayValue &value : bytes)
         {
            if (!haveFirst)
            {
               firstBytes = value.value;
               haveFirst = true;
            }

            lastBytes = value.value;

            AnsiString day = AnsiString(value.day);

            std::vector<AnsiString> row;
            row.push_back(day);
            row.push_back(RoundedNumber(value.value));
            row.push_back(RoundedNumber(value.value / (1024 * 1024)));
            row.push_back(RoundedNumber(messagesByDay.find(day) == messagesByDay.end() ? 0.0 : messagesByDay[day]));

            section.table.rows.push_back(row);
         }

         // The growth is the last day's size less the first day's, which is
         // the honest answer to "how much did it grow over the window" and is
         // negative when mail was deleted.
         section.extra.Format(",\"growth_bytes\":%I64d,\"first_day_bytes\":%I64d,\"last_day_bytes\":%I64d,\"retention_days\":%d",
            (__int64) (lastBytes - firstBytes), (__int64) firstBytes, (__int64) lastBytes,
            IniFileSettings::Instance()->GetMetricsHistoryDays());

         return section;
      }

      Section BuildSectionByName(Context &context, const AnsiString &name)
      {
         if (name.CompareNoCase("traffic") == 0)
            return BuildTraffic(context);

         if (name.CompareNoCase("failures") == 0)
            return BuildFailures(context);

         if (name.CompareNoCase("senders") == 0)
            return BuildAddresses(context, true);

         if (name.CompareNoCase("recipients") == 0)
            return BuildAddresses(context, false);

         if (name.CompareNoCase("mailboxes") == 0)
            return BuildMailboxes(context);

         if (name.CompareNoCase("volume") == 0)
            return BuildVolume(context);

         return BuildStorage(context);
      }

      // ------------------------------------------------------------------ JSON

      AnsiString TableAsJson(const Table &table, const Context &context)
      {
         AnsiString json = "\"columns\":[";

         for (size_t i = 0; i < table.columns.size(); i++)
         {
            if (i > 0)
               json += ",";

            json += "\"" + context.escape(table.columns[i]) + "\"";
         }

         json += "],\"rows\":[";

         for (size_t r = 0; r < table.rows.size(); r++)
         {
            if (r > 0)
               json += ",";

            json += "{";

            for (size_t c = 0; c < table.columns.size() && c < table.rows[r].size(); c++)
            {
               if (c > 0)
                  json += ",";

               json += "\"" + context.escape(table.columns[c]) + "\":";

               if (table.numeric[c])
                  json += table.rows[r][c].IsEmpty() ? AnsiString("0") : table.rows[r][c];
               else
                  json += "\"" + context.escape(table.rows[r][c]) + "\"";
            }

            json += "}";
         }

         json += "]";

         return json;
      }

      AnsiString SectionAsJson(const Section &section, const Context &context)
      {
         AnsiString json;
         json.Format("{\"section\":\"%hs\",\"source\":\"%hs\",\"enabled\":%hs,\"truncated\":%hs,\"note\":\"%hs\",",
            context.escape(section.name).c_str(),
            context.escape(section.source).c_str(),
            section.enabled ? "true" : "false",
            section.truncated ? "true" : "false",
            context.escape(section.note).c_str());

         json += TableAsJson(section.table, context);
         json += section.extra;
         json += "}";

         return json;
      }
   }

   bool
   RestApiServer::IsServerWideReportSection_(const AnsiString &section)
   {
      // The summary carries every section the caller may see, so it is not
      // itself server-wide: a domain-restricted key gets the per-domain
      // sections of it and is told which ones were left out and why.
      const SectionInfo *info = FindSection(section);

      return info != 0 && info->server_wide;
   }

   AnsiString
   RestApiServer::OpenApiReportsPaths_()
   {
      return AnsiString(
         ",\"/api/v1/reports\":{\"get\":{\"summary\":\"What this server can report, and what it cannot\","
         "\"description\":\"The sections, what each is counted from, and the state of the three sources - the message trace (off by default), "
         "the metric history and the message store - with how far back each can see. Read it before reading a section: a report drawn while its "
         "source was switched off is empty, which is not the same as nothing having happened.\",\"responses\":{\"200\":{\"description\":\"The index\"}}}}"

         ",\"/api/v1/reports/{section}\":{\"get\":{\"summary\":\"One report section\","
         "\"description\":\"section is traffic, failures, senders, recipients, mailboxes, volume, storage or summary. "
         "Query parameters: from and to (YYYY-MM-DD, inclusive; the default is the last 30 days and the window may not exceed 366 days), "
         "domain (one local domain; the default is every domain the credential may see), top (how many rows the senders, recipients and mailboxes "
         "sections return: 1 to 200, default 10) and format (json, the default, or csv - csv answers text/csv with the same columns and rows, and is "
         "refused for summary, which is not one table). "
         "traffic, failures, senders and recipients are counted from hm_messagetrace, which is OFF by default (MessageTraceEnabled) and which records "
         "only deliveries and failures - there is no spam or virus event in it. mailboxes is the size of every mailbox as it is NOW, not over the "
         "window. volume and storage are counted from the server-wide metric history and carry no domain at all, which is why spam and virus counts "
         "and storage growth cannot be given per domain; both are refused for a domain-restricted key. Every per-domain section counts only domains "
         "this server hosts, and a domain-restricted key sees its own domains and no others.\","
         "\"responses\":{\"200\":{\"description\":\"The section\"},\"400\":{\"description\":\"A date, a window, a top or a format the route does not take\"},"
         "\"403\":{\"description\":\"A domain-restricted key asked for a server-wide section, or for another domain\"},"
         "\"404\":{\"description\":\"No such section, or no such domain on this server\"}}}}");
   }

   HttpResponse
   RestApiServer::HandleReport_(const std::vector<String> &domains, const AnsiString &section, const AnsiString &query)
   {
      Context context;
      context.allowed = domains;
      context.escape = &RestApiServer::JsonEscape_;

      // ---- the window
      AnsiString fromText = QueryParameter_(query, "from");
      AnsiString toText = QueryParameter_(query, "to");

      AnsiString today = TodayOnThisServer();

      int year = 0, month = 0, day = 0;

      if (toText.IsEmpty())
         toText = today;

      if (!ParseDay(toText, year, month, day))
         return BuildResponse_(400, "{\"error\":\"to must be a date, as YYYY-MM-DD\"}");

      long long toDays = DaysFromCivil(year, month, day);
      long long fromDays = toDays - 29;

      if (!fromText.IsEmpty())
      {
         if (!ParseDay(fromText, year, month, day))
            return BuildResponse_(400, "{\"error\":\"from must be a date, as YYYY-MM-DD\"}");

         fromDays = DaysFromCivil(year, month, day);
      }

      if (fromDays > toDays)
         return BuildResponse_(400, "{\"error\":\"from is after to\"}");

      if (toDays - fromDays >= 366)
         return BuildResponse_(400, "{\"error\":\"the window may not exceed 366 days\"}");

      context.from_day = DayText(fromDays);
      context.to_day = DayText(toDays);

      // Half-open, so that the last day is a whole day: 00:00:00 on the first
      // day to 00:00:00 on the day after the last.
      context.range.from = String(context.from_day + " 00:00:00");
      context.range.to = String(DayText(toDays + 1) + " 00:00:00");

      // ---- top
      AnsiString topText = QueryParameter_(query, "top");

      if (!topText.IsEmpty())
      {
         context.top = atoi(topText.c_str());

         if (context.top < 1 || context.top > 200)
            return BuildResponse_(400, "{\"error\":\"top must be between 1 and 200\"}");
      }

      // ---- format
      AnsiString format = QueryParameter_(query, "format");

      if (!format.IsEmpty() && format.CompareNoCase("json") != 0 && format.CompareNoCase("csv") != 0)
         return BuildResponse_(400, "{\"error\":\"format must be json or csv\"}");

      context.csv = format.CompareNoCase("csv") == 0;

      // ---- the domains this server hosts
      Domains allDomains;
      allDomains.Refresh();

      for (int i = 0; i < allDomains.GetCount(); i++)
      {
         std::shared_ptr<Domain> one = allDomains.GetItem(i);

         if (!one)
            continue;

         AnsiString name = AnsiString(one->GetName());
         name.ToLower();
         context.local.insert(name);
      }

      // ---- the domain asked for
      AnsiString wanted = QueryParameter_(query, "domain");
      wanted.Trim();
      wanted.ToLower();

      if (!wanted.IsEmpty() && context.local.find(wanted) == context.local.end())
         return BuildResponse_(404, "{\"error\":\"no such domain on this server\"}");

      context.domain = wanted;

      // ---- the index
      if (section.IsEmpty())
      {
         if (context.csv)
            return BuildResponse_(400, "{\"error\":\"the index is not a table; ask for a section with format=csv\"}");

         AnsiString sections = "[";

         for (int i = 0; i < SectionCount; i++)
         {
            AnsiString entry;
            entry.Format("%s{\"name\":\"%hs\",\"scope\":\"%hs\",\"source\":\"%hs\",\"csv\":true,\"summary\":\"%hs\"}",
               i > 0 ? "," : "",
               SECTIONS[i].name,
               SECTIONS[i].server_wide ? "server" : "domain",
               SECTIONS[i].source,
               JsonEscape_(AnsiString(SECTIONS[i].summary)).c_str());

            sections += entry;
         }

         sections += "]";

         AnsiString body;
         body.Format("{\"from\":\"%hs\",\"to\":\"%hs\",\"today\":\"%hs\",\"sections\":%hs,"
            "\"sources\":{"
            "\"message_trace\":{\"table\":\"hm_messagetrace\",\"enabled\":%hs,\"retention_days\":%d,\"oldest\":\"%hs\"},"
            "\"metric_history\":{\"table\":\"hm_metricsamples\",\"enabled\":%hs,\"retention_days\":%d,\"oldest\":\"%hs\"},"
            "\"message_store\":{\"table\":\"hm_messages\",\"enabled\":true,\"retention_days\":0,\"oldest\":\"\"}},"
            "\"not_answerable\":["
            "\"Spam and virus counts per domain. The counters are server-wide and the message trace has no spam or virus event, so there is no honest way to attribute either to a domain.\","
            "\"Storage growth per domain. The store-size sample is server-wide; a domain's size is known only as it is now.\","
            "\"Anything from before a source was switched on, or older than that source's retention.\","
            "\"A message followed across a forward, a distribution list or a rule that re-sends: each of those makes a new queue entry with a new id, and the trace correlates by queue id.\""
            "]}",
            context.from_day.c_str(), context.to_day.c_str(), today.c_str(), sections.c_str(),
            MessageTrace::GetEnabled() ? "true" : "false",
            IniFileSettings::Instance()->GetMessageTraceRetentionDays(),
            JsonEscape_(AnsiString(Reports::OldestTraceRow())).c_str(),
            IniFileSettings::Instance()->GetMetricsHistoryDays() > 0 ? "true" : "false",
            IniFileSettings::Instance()->GetMetricsHistoryDays(),
            JsonEscape_(AnsiString(Reports::OldestSample())).c_str());

         return BuildResponse_(200, body);
      }

      // ---- one section, or the summary
      bool summary = section.CompareNoCase("summary") == 0;

      if (!summary && FindSection(section) == 0)
      {
         AnsiString names;

         for (int i = 0; i < SectionCount; i++)
            names += (names.IsEmpty() ? "\"" : ",\"") + AnsiString(SECTIONS[i].name) + "\"";

         return BuildResponse_(404, "{\"error\":\"no such report section\",\"sections\":[" + names + ",\"summary\"]}");
      }

      if (summary && context.csv)
         return BuildResponse_(400, "{\"error\":\"the summary is not one table; ask for a section with format=csv\"}");

      AnsiString head;
      head.Format("\"from\":\"%hs\",\"to\":\"%hs\",\"domain\":\"%hs\",\"top\":%d",
         context.from_day.c_str(), context.to_day.c_str(), JsonEscape_(context.domain).c_str(), context.top);

      if (summary)
      {
         AnsiString body = "{\"section\":\"summary\"," + head + ",\"sections\":{";
         AnsiString omitted = "[";

         bool first = true;
         bool firstOmitted = true;

         for (int i = 0; i < SectionCount; i++)
         {
            // The rule Authorize_ applies to a section asked for by name,
            // applied here to each section of the summary so that the two
            // cannot drift: a domain-restricted key is TOLD which sections it
            // did not get and why, rather than handed a document that is
            // quietly smaller.
            if (SECTIONS[i].server_wide && !domains.empty())
            {
               AnsiString entry;
               entry.Format("%s{\"section\":\"%hs\",\"reason\":\"counted from server-wide metrics that carry no domain, and this credential is restricted to named domains\"}",
                  firstOmitted ? "" : ",", SECTIONS[i].name);

               omitted += entry;
               firstOmitted = false;
               continue;
            }

            Section built = BuildSectionByName(context, AnsiString(SECTIONS[i].name));

            if (!first)
               body += ",";

            body += "\"" + AnsiString(SECTIONS[i].name) + "\":" + SectionAsJson(built, context);
            first = false;
         }

         omitted += "]";

         body += "},\"omitted\":" + omitted + "}";

         return BuildResponse_(200, body);
      }

      Section built = BuildSectionByName(context, section);

      if (context.csv)
      {
         HttpResponse response;
         response.status = 200;
         response.content_type = "text/csv; charset=utf-8";
         response.body = TableAsCsv(built.table);

         // The section name comes from a fixed table and the two dates parsed
         // as dates, so nothing here can carry a CR or an LF into a header.
         AnsiString headers = "Cache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\n";
         headers += "Content-Disposition: attachment; filename=\"hmailserver-" + built.name + "-" +
                    context.from_day + "-to-" + context.to_day + ".csv\"\r\n";

         response.extra_headers = headers;

         return response;
      }

      // The window and the domain belong in every answer and not only in the
      // summary: a CSV saved a fortnight ago and a JSON answered now both have
      // to say what they are of.
      AnsiString body = SectionAsJson(built, context);
      body = "{" + head + "," + body.Mid(1);

      return BuildResponse_(200, body);
   }
}

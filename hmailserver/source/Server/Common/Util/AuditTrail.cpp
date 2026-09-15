// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

// See AuditTrail.h.

#include "StdAfx.h"

#include "AuditTrail.h"

#include "Hashing/HashCreator.h"
#include "../SQL/SQLCommand.h"
#include "../SQL/SQLStatement.h"
#include "../SQL/SQLParameter.h"
#include "../SQL/DALRecordset.h"
#include "../SQL/DatabaseSettings.h"

#include <ctime>

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   thread_local bool AuditTrail::writing_ = false;
   thread_local AuditTrail::Actor AuditTrail::actor_;

   namespace
   {
      const int MaxDetailLength = 2000;
      const int MaxValueLength = 120;

      // A change to one of these is an administrative change: it is
      // configuration, and somebody decided it. Everything else the server
      // writes - messages, recipients, folder rows, greylisting triples, index
      // terms, metric samples - is traffic, and belongs in a log, not here.
      //
      // hm_settings is deliberately ABSENT: PropertySet records a setting with
      // the value it had as well as the value it was given, which a statement
      // cannot show, and a table entry here would record it a second time.
      //
      // hm_audit is absent for the obvious reason.
      struct TableName
      {
         const wchar_t *table;
         const wchar_t *object;
      };

      const TableName AuditedTables[] =
      {
         { L"hm_domains",                     L"domain" },
         { L"hm_domainaliases",               L"domain alias" },
         { L"hm_accounts",                    L"account" },
         { L"hm_aliases",                     L"alias" },
         { L"hm_distributionlists",           L"distribution list" },
         { L"hm_distributionlistsrecipients", L"distribution list recipient" },
         { L"hm_routes",                      L"route" },
         { L"hm_routeaddresses",              L"route address" },
         { L"hm_rules",                       L"rule" },
         { L"hm_rule_criterias",              L"rule criterion" },
         { L"hm_rule_actions",                L"rule action" },
         { L"hm_securityranges",              L"ip range" },
         { L"hm_tcpipports",                  L"tcp/ip port" },
         { L"hm_sslcertificates",             L"ssl certificate" },
         { L"hm_incomingrelays",              L"incoming relay" },
         { L"hm_dnsbl",                       L"dns blacklist" },
         { L"hm_surblservers",                L"surbl server" },
         { L"hm_whiteaddresses",              L"white list address" },
         { L"hm_greyliststats",               L"greylisting white address" },
         { L"hm_blocked_attachments",         L"blocked attachment" },
         { L"hm_blockedsenders",              L"blocked sender" },
         { L"hm_servermessages",              L"server message" },
         { L"hm_groups",                      L"group" },
         { L"hm_group_members",               L"group member" },
         { L"hm_fetchaccounts",               L"fetch account" },
         { L"hm_apppasswords",                L"application password" },
         { L"hm_alertrules",                  L"alert rule" },
      };

      // A column or setting whose value must never reach the table. Matched as a
      // substring of the lower-cased name, so a new column called
      // "accountnewpassword" is covered without anybody remembering to add it.
      const wchar_t *SecretFragments[] =
      {
         L"password", L"passwd", L"secret", L"privatekey", L"private_key",
         L"apikey", L"api_key", L"token", L"totp", L"hash", L"salt", L"pepper",
         L"credential", L"keyfile", L"passphrase",
      };

      String Truncate(const String &value, int maximum)
      {
         if (value.GetLength() <= maximum)
            return value;

         return value.Mid(0, maximum) + _T("...");
      }

      // The whole field, one line: a value carrying a newline would otherwise
      // let one recorded change look like two.
      String OneLine(const String &value)
      {
         String result = value;

         for (int i = 0; i < result.GetLength(); i++)
         {
            if (result[i] == '\r' || result[i] == '\n' || result[i] == '\t')
               result[i] = ' ';
         }

         return result;
      }
   }

   AuditTrail::AuditTrail()
   {

   }

   AuditTrail::~AuditTrail()
   {

   }

   void
   AuditTrail::SetActor(const Actor &actor)
   {
      actor_ = actor;
   }

   void
   AuditTrail::ClearActor()
   {
      actor_ = Actor();
   }

   AuditTrail::Actor
   AuditTrail::GetActor()
   {
      return actor_;
   }

   bool
   AuditTrail::HasActor()
   {
      return actor_.present;
   }

   AuditScope::AuditScope(const AuditTrail::Actor &actor)
   {
      previous_ = AuditTrail::GetActor();
      AuditTrail::SetActor(actor);
   }

   AuditScope::~AuditScope()
   {
      AuditTrail::SetActor(previous_);
   }

   bool
   AuditTrail::IsSecretName(const String &name)
   {
      String lower = name;
      lower.MakeLower();

      for (const wchar_t *fragment : SecretFragments)
      {
         if (lower.Find(fragment) >= 0)
            return true;
      }

      return false;
   }

   String
   AuditTrail::AuditedTable(const String &table)
   {
      String lower = table;
      lower.MakeLower();

      for (const TableName &entry : AuditedTables)
      {
         if (lower == entry.table)
            return String(entry.object);
      }

      return String();
   }

   /*
      "@domainname_2" -> "domainname". The ordinal SQLStatement appends is what
      keeps two columns with a shared prefix apart in the statement text; it is
      not part of the column's name, and an audit row that said "domainname_2"
      would be describing the statement rather than the change.
   */
   String
   AuditTrail::ColumnOfParameter_(const String &parameterName)
   {
      String name = parameterName;

      if (name.StartsWith(_T("@")))
         name = name.Mid(1);

      int lastUnderscore = -1;
      for (int i = 0; i < name.GetLength(); i++)
      {
         if (name[i] == '_')
            lastUnderscore = i;
      }

      if (lastUnderscore > 0 && lastUnderscore < name.GetLength() - 1)
      {
         bool allDigits = true;
         for (int i = lastUnderscore + 1; i < name.GetLength(); i++)
         {
            if (name[i] < '0' || name[i] > '9')
            {
               allDigits = false;
               break;
            }
         }

         if (allDigits)
            name = name.Mid(0, lastUnderscore);
      }

      name.MakeLower();
      return name;
   }

   /*
      What the statement did, in the words of the thing it did it to. Returns the
      object type ("domain") and fills table, action and objectName; an empty
      return means the statement is not an administrative change and nothing is
      recorded.
   */
   String
   AuditTrail::Describe_(const SQLCommand &command, String &table, String &action, String &objectName)
   {
      String sql = command.GetQueryString();
      sql.Trim();

      String lower = sql;
      lower.MakeLower();

      String remainder;

      if (lower.StartsWith(_T("insert into ")))
      {
         action = _T("created");
         remainder = sql.Mid(12);
      }
      else if (lower.StartsWith(_T("update ")))
      {
         action = _T("updated");
         remainder = sql.Mid(7);
      }
      else if (lower.StartsWith(_T("delete from ")))
      {
         action = _T("deleted");
         remainder = sql.Mid(12);
      }
      else
      {
         return String();
      }

      remainder.Trim();

      // The table name is everything up to the first space, '(' or newline.
      int end = remainder.GetLength();
      for (int i = 0; i < remainder.GetLength(); i++)
      {
         wchar_t character = remainder[i];
         if (character == ' ' || character == '(' || character == '\r' || character == '\n' || character == '\t')
         {
            end = i;
            break;
         }
      }

      table = remainder.Mid(0, end);
      table.MakeLower();

      String objectType = AuditedTable(table);
      if (objectType.IsEmpty())
         return String();

      // The name a person would recognise, preferred over the row id: a column
      // ending in "name" or "address" that is not itself a secret.
      const std::list<SQLParameter> &parameters = command.GetParameters();
      for (const SQLParameter &parameter : parameters)
      {
         String column = ColumnOfParameter_(parameter.GetName());

         if (IsSecretName(column))
            continue;

         if (!column.EndsWith(_T("name")) && !column.EndsWith(_T("address")))
            continue;

         if (parameter.GetType() != SQLParameter::ParamTypeString)
            continue;

         String value = parameter.GetStringValue();
         value.Trim();
         if (value.IsEmpty())
            continue;

         objectName = Truncate(OneLine(value), MaxValueLength);
         break;
      }

      if (objectName.IsEmpty())
      {
         // Fall back to the row the WHERE clause names: "... where domainid = 4"
         // or "... where domainid = @DOMAINID".
         int wherePosition = lower.Find(_T(" where "));
         if (wherePosition >= 0)
         {
            String clause = sql.Mid(wherePosition + 7);
            clause.Trim();

            int equals = clause.Find(_T("="));
            if (equals > 0)
            {
               String column = clause.Mid(0, equals);
               column.Trim();
               column.MakeLower();

               String value = clause.Mid(equals + 1);
               value.Trim();

               int valueEnd = value.GetLength();
               for (int i = 0; i < value.GetLength(); i++)
               {
                  wchar_t character = value[i];
                  if (character == ' ' || character == '\r' || character == '\n' || character == '\t')
                  {
                     valueEnd = i;
                     break;
                  }
               }
               value = value.Mid(0, valueEnd);

               if (value.StartsWith(_T("@")))
               {
                  for (const SQLParameter &parameter : parameters)
                  {
                     if (parameter.GetName().CompareNoCase(value.c_str()) != 0)
                        continue;

                     if (parameter.GetType() == SQLParameter::ParamTypeString)
                        value = parameter.GetStringValue();
                     else if (parameter.GetType() == SQLParameter::ParamTypeInt64)
                        value = StringParser::IntToString(parameter.GetInt64Value());
                     else
                        value = StringParser::IntToString(parameter.GetInt32Value());

                     break;
                  }
               }

               // Strip the quotes a literal string carries.
               if (value.GetLength() >= 2 && value[0] == '\'' && value[value.GetLength() - 1] == '\'')
                  value = value.Mid(1, value.GetLength() - 2);

               if (!value.IsEmpty() && !IsSecretName(column))
                  objectName = Truncate(OneLine(column + _T(" ") + value), MaxValueLength);
            }
         }
      }

      return objectType;
   }

   void
   AuditTrail::RecordStatement(const SQLCommand &command, bool succeeded)
   {
      // A change nobody made is not an administrative change. This is the whole
      // of the filter that keeps the server's own writes - delivery, retention,
      // greylisting, the index - out of the record, and it is what stops the
      // INSERT below from recording itself.
      if (!succeeded || !actor_.present || writing_)
         return;

      String table;
      String action;
      String objectName;

      String objectType = Describe_(command, table, action, objectName);
      if (objectType.IsEmpty())
         return;

      // The columns the statement carried, with every secret withheld. A DELETE
      // has none, and says so by saying nothing.
      String detail;
      const std::list<SQLParameter> &parameters = command.GetParameters();
      for (const SQLParameter &parameter : parameters)
      {
         String column = ColumnOfParameter_(parameter.GetName());
         if (column.IsEmpty())
            continue;

         String value;
         if (IsSecretName(column))
         {
            value = _T("(changed)");
         }
         else
         {
            switch (parameter.GetType())
            {
            case SQLParameter::ParamTypeString:
               value = Truncate(OneLine(parameter.GetStringValue()), MaxValueLength);
               break;
            case SQLParameter::ParamTypeInt64:
               value = StringParser::IntToString(parameter.GetInt64Value());
               break;
            case SQLParameter::ParamTypeUnsignedInt32:
               value = StringParser::IntToString((int) parameter.GetUnsignedInt32Value());
               break;
            default:
               value = StringParser::IntToString(parameter.GetInt32Value());
               break;
            }
         }

         if (!detail.IsEmpty())
            detail += _T(", ");

         detail += column + _T("=") + value;

         if (detail.GetLength() > MaxDetailLength)
         {
            detail = Truncate(detail, MaxDetailLength);
            break;
         }
      }

      Write_(objectType, objectName, action, detail);
   }

   void
   AuditTrail::RecordSettingChange(const String &name, const String &oldValue, const String &newValue)
   {
      if (!actor_.present || writing_)
         return;

      String detail;

      if (IsSecretName(name))
      {
         detail = name + _T(": (changed)");
      }
      else
      {
         detail = name + _T(": ") + Truncate(OneLine(oldValue), MaxValueLength) +
                  _T(" -> ") + Truncate(OneLine(newValue), MaxValueLength);
      }

      Write_(_T("setting"), name, _T("updated"), Truncate(detail, MaxDetailLength));
   }

   String
   AuditTrail::HashRow_(const String &previousHash, __int64 time, const String &actor, const String &actorKind,
                        const String &interfaceName, const String &address, const String &objectType,
                        const String &objectName, const String &action, const String &detail)
   {
      // A single field separator that cannot occur in a field: every field is put
      // through OneLine before it is stored, so a newline is the one character
      // none of them holds. Without a separator, "ab" + "c" and "a" + "bc" hash
      // the same, and two different changes could be made to agree.
      String canonical;
      canonical += previousHash;
      canonical += _T("\n");
      canonical += StringParser::IntToString(time);
      canonical += _T("\n");
      canonical += actor;
      canonical += _T("\n");
      canonical += actorKind;
      canonical += _T("\n");
      canonical += interfaceName;
      canonical += _T("\n");
      canonical += address;
      canonical += _T("\n");
      canonical += objectType;
      canonical += _T("\n");
      canonical += objectName;
      canonical += _T("\n");
      canonical += action;
      canonical += _T("\n");
      canonical += detail;

      HashCreator creator(HashCreator::SHA256);
      return String(creator.GenerateHashNoSalt(AnsiString(canonical), HashCreator::hex));
   }

   /*
      The hash of the newest row, read every time rather than cached. One extra
      SELECT per audit row, which is a change an administrator made and so is
      rare; what it buys is that nothing in this process has an opinion about
      the state of the table. A second node writing the same database, a
      retention pass, and a test that empties the table between fixtures all
      then leave a chain that verifies, where a cached head would chain the next
      row onto a hash that is no longer there.
   */
   String
   AuditTrail::ReadHeadHash_()
   {
      // Two statements, not one with a subquery: SQL Server Compact refuses
      // "where auditid = (select max(auditid) from hm_audit)" outright, and on
      // that backend the one statement failed on every write - so every change
      // an administrator made logged an error and chained onto an empty hash.
      // A max() over an empty table is one row holding NULL, not no rows.
      SQLCommand headCommand(_T("select max(auditid) as headid from hm_audit"));

      std::shared_ptr<DALRecordset> head = Application::Instance()->GetDBManager()->OpenRecordset(headCommand);
      if (!head || head->IsEOF() || head->GetIsNull("headid"))
         return String();

      SQLCommand command(_T("select audithash from hm_audit where auditid = @AUDITID"));
      command.AddParameter("@AUDITID", head->GetInt64Value("headid"));

      std::shared_ptr<DALRecordset> recordset = Application::Instance()->GetDBManager()->OpenRecordset(command);
      if (recordset && !recordset->IsEOF())
         return recordset->GetStringValue("audithash");

      return String();
   }

   bool
   AuditTrail::Write_(const String &objectType, const String &objectName, const String &action, const String &detail)
   {
      // Switching recording OFF is the one change that is recorded even when
      // recording is off - and switching it on is recorded before anything
      // else. Otherwise the way to change something unobserved would be to turn
      // the observer off first, which is the first thing anybody would try.
      if (!Configuration::Instance()->GetAuditTrailEnabled() && objectName != PROPERTY_AUDIT_TRAIL_ENABLED)
         return false;

      Actor actor = actor_;

      // The chain is one line of history, so the read of its head, the hash and
      // the insert are one operation. Administrative changes are rare; the
      // serialisation costs nothing anybody can measure, and without it two
      // threads can compute two rows from the same previous hash, which breaks
      // the chain at the moment it is written.
      boost::lock_guard<boost::recursive_mutex> guard(mutex_);

      writing_ = true;

      bool result = false;

      try
      {
         String previousHash = ReadHeadHash_();

         __int64 now = (__int64) time(nullptr);

         String safeActor = OneLine(actor.name);
         String safeObjectType = OneLine(objectType);
         String safeObjectName = OneLine(objectName);
         String safeDetail = OneLine(detail);

         String hash = HashRow_(previousHash, now, safeActor, actor.kind, actor.interface_name, actor.address,
                                safeObjectType, safeObjectName, action, safeDetail);

         SQLStatement statement(SQLStatement::STInsert, _T("hm_audit"));
         statement.AddColumnInt64(_T("audittime"), now);
         statement.AddColumn(_T("auditactor"), safeActor, 255);
         statement.AddColumn(_T("auditactortype"), actor.kind, 32);
         statement.AddColumn(_T("auditinterface"), actor.interface_name, 32);
         statement.AddColumn(_T("auditaddress"), actor.address, 64);
         statement.AddColumn(_T("auditobjecttype"), safeObjectType, 64);
         statement.AddColumn(_T("auditobjectname"), safeObjectName, 255);
         statement.AddColumn(_T("auditaction"), action, 32);
         statement.AddColumn(_T("auditdetail"), safeDetail);
         statement.AddColumn(_T("auditprevhash"), previousHash, 64);
         statement.AddColumn(_T("audithash"), hash, 64);
         statement.SetIdentityColumn(_T("auditid"));

         String errorMessage;
         result = Application::Instance()->GetDBManager()->Execute(statement.GetCommand(), nullptr, 0, errorMessage);

         if (!result)
         {
            ErrorManager::Instance()->ReportError(ErrorManager::High, 6541, "AuditTrail::Write_",
               "An administrative change could not be written to the audit trail: " + errorMessage);
         }
      }
      catch (...)
      {
         writing_ = false;
         throw;
      }

      writing_ = false;

      return result;
   }

   /*
      A row-limited SELECT for each backend. SQLStatement has GetTopRows, but it
      builds the whole statement and takes no WHERE clause; this is the same
      knowledge applied to a statement the caller composes.
   */
   namespace
   {
      String SelectWithLimit(const String &columns, const String &from, const String &where, const String &orderBy, int rows)
      {
         DatabaseSettings::SQLDBType type = IniFileSettings::Instance()->GetDatabaseType();

         String statement;

         switch (type)
         {
         case DatabaseSettings::TypeMSSQLServer:
         case DatabaseSettings::TypeMSSQLCompactEdition:
            // TOP (n) rather than TOP n: SQL Server Compact requires the
            // parentheses, and SQL Server has accepted them since 2005. The
            // same form SQLStatement::SetTopRows produces.
            statement.Format(_T("select top (%d) %s from %s"), rows, columns.c_str(), from.c_str());
            if (!where.IsEmpty())
               statement += _T(" where ") + where;
            if (!orderBy.IsEmpty())
               statement += _T(" order by ") + orderBy;
            break;
         default:
            statement.Format(_T("select %s from %s"), columns.c_str(), from.c_str());
            if (!where.IsEmpty())
               statement += _T(" where ") + where;
            if (!orderBy.IsEmpty())
               statement += _T(" order by ") + orderBy;
            statement += Formatter::Format(" limit {0}", rows);
            break;
         }

         return statement;
      }
   }

   bool
   AuditTrail::Query(const Filter &filter, std::vector<Entry> &entries, int &total)
   {
      entries.clear();
      total = 0;

      String where;
      SQLCommand countCommand;

      auto addClause = [&where] (const String &clause)
      {
         if (!where.IsEmpty())
            where += _T(" and ");

         where += clause;
      };

      if (!filter.object_type.IsEmpty())
         addClause(_T("auditobjecttype = @OBJECTTYPE"));
      if (!filter.action.IsEmpty())
         addClause(_T("auditaction = @ACTION"));
      if (filter.since > 0)
         addClause(_T("audittime >= @SINCE"));
      if (filter.until > 0)
         addClause(_T("audittime <= @UNTIL"));

      // The actor filter is a substring so that "key:" finds every API key and a
      // domain finds every account in it. A LIKE with the caller's text as a
      // parameter, never as part of the statement.
      if (!filter.actor.IsEmpty())
         addClause(_T("auditactor like @ACTOR"));

      auto bind = [&filter] (SQLCommand &command)
      {
         if (!filter.object_type.IsEmpty())
            command.AddParameter("@OBJECTTYPE", filter.object_type);
         if (!filter.action.IsEmpty())
            command.AddParameter("@ACTION", filter.action);
         if (filter.since > 0)
            command.AddParameter("@SINCE", (__int64) filter.since);
         if (filter.until > 0)
            command.AddParameter("@UNTIL", (__int64) filter.until);
         if (!filter.actor.IsEmpty())
            command.AddParameter("@ACTOR", String(_T("%")) + filter.actor + String(_T("%")));
      };

      String countSql = _T("select count(*) as auditcount from hm_audit");
      if (!where.IsEmpty())
         countSql += _T(" where ") + where;

      countCommand.SetQueryString(countSql);
      bind(countCommand);

      std::shared_ptr<DALRecordset> countSet = Application::Instance()->GetDBManager()->OpenRecordset(countCommand);
      if (!countSet)
         return false;

      if (!countSet->IsEOF())
         total = countSet->GetLongValue("auditcount");

      int limit = filter.limit;
      if (limit < 1)
         limit = 1;
      if (limit > 1000)
         limit = 1000;

      int offset = filter.offset < 0 ? 0 : filter.offset;
      if (offset > 100000)
         offset = 100000;

      // Newest first, and the page is taken by reading offset + limit rows and
      // skipping the first offset. OFFSET is spelled four different ways across
      // the four backends and is absent altogether from SQL Server Compact; this
      // is the same answer everywhere.
      SQLCommand command(SelectWithLimit(_T("*"), _T("hm_audit"), where, _T("auditid desc"), offset + limit));
      bind(command);

      std::shared_ptr<DALRecordset> recordset = Application::Instance()->GetDBManager()->OpenRecordset(command);
      if (!recordset)
         return false;

      int skipped = 0;
      while (!recordset->IsEOF())
      {
         if (skipped < offset)
         {
            skipped++;
            recordset->MoveNext();
            continue;
         }

         if ((int) entries.size() >= limit)
            break;

         Entry entry;
         entry.id = recordset->GetInt64Value("auditid");
         entry.time = recordset->GetInt64Value("audittime");
         entry.actor = recordset->GetStringValue("auditactor");
         entry.actor_kind = recordset->GetStringValue("auditactortype");
         entry.interface_name = recordset->GetStringValue("auditinterface");
         entry.address = recordset->GetStringValue("auditaddress");
         entry.object_type = recordset->GetStringValue("auditobjecttype");
         entry.object_name = recordset->GetStringValue("auditobjectname");
         entry.action = recordset->GetStringValue("auditaction");
         entry.detail = recordset->GetStringValue("auditdetail");
         entry.previous_hash = recordset->GetStringValue("auditprevhash");
         entry.hash = recordset->GetStringValue("audithash");

         entries.push_back(entry);

         recordset->MoveNext();
      }

      return true;
   }

   bool
   AuditTrail::Verify(__int64 &firstBroken, __int64 &rowsChecked, String &reason)
   {
      firstBroken = 0;
      rowsChecked = 0;
      reason = _T("");

      SQLCommand command(_T("select * from hm_audit order by auditid asc"));

      std::shared_ptr<DALRecordset> recordset = Application::Instance()->GetDBManager()->OpenRecordset(command);
      if (!recordset)
      {
         reason = _T("the audit trail could not be read");
         return false;
      }

      String expectedPrevious;
      bool first = true;

      while (!recordset->IsEOF())
      {
         __int64 id = recordset->GetInt64Value("auditid");
         String storedPrevious = recordset->GetStringValue("auditprevhash");
         String storedHash = recordset->GetStringValue("audithash");

         if (first)
         {
            // The oldest row is where the chain starts, and what it points back
            // at decides whether that start is the real one. An empty back-hash
            // is the first row ever written. A non-empty one means rows before
            // it are gone - which is what retention does on purpose, and is
            // tampering when retention is off.
            first = false;
            expectedPrevious = storedPrevious;

            if (!storedPrevious.IsEmpty() && Configuration::Instance()->GetAuditRetentionDays() <= 0)
            {
               firstBroken = id;
               reason = _T("the oldest row points back at a row that is not there, and retention is off, so rows have been removed from the front of the trail");
               return false;
            }
         }

         if (storedPrevious != expectedPrevious)
         {
            firstBroken = id;
            reason = _T("the row does not carry the hash of the row before it, so a row between them has been removed");
            return false;
         }

         String computed = HashRow_(storedPrevious,
                                    recordset->GetInt64Value("audittime"),
                                    recordset->GetStringValue("auditactor"),
                                    recordset->GetStringValue("auditactortype"),
                                    recordset->GetStringValue("auditinterface"),
                                    recordset->GetStringValue("auditaddress"),
                                    recordset->GetStringValue("auditobjecttype"),
                                    recordset->GetStringValue("auditobjectname"),
                                    recordset->GetStringValue("auditaction"),
                                    recordset->GetStringValue("auditdetail"));

         if (computed != storedHash)
         {
            firstBroken = id;
            reason = _T("the row's contents do not produce the hash stored with it, so the row has been edited");
            return false;
         }

         expectedPrevious = storedHash;
         rowsChecked++;

         recordset->MoveNext();
      }

      return true;
   }

   int
   AuditTrail::PurgeOlderThan(int days)
   {
      if (days <= 0)
         return 0;

      __int64 cutoff = (__int64) time(nullptr) - ((__int64) days * 24 * 60 * 60);

      boost::lock_guard<boost::recursive_mutex> guard(mutex_);

      SQLCommand countCommand(_T("select count(*) as auditcount from hm_audit where audittime < @CUTOFF"));
      countCommand.AddParameter("@CUTOFF", cutoff);

      int removed = 0;
      std::shared_ptr<DALRecordset> countSet = Application::Instance()->GetDBManager()->OpenRecordset(countCommand);
      if (countSet && !countSet->IsEOF())
         removed = countSet->GetLongValue("auditcount");

      if (removed == 0)
         return 0;

      writing_ = true;

      SQLCommand command(_T("delete from hm_audit where audittime < @CUTOFF"));
      command.AddParameter("@CUTOFF", cutoff);

      bool executed = Application::Instance()->GetDBManager()->Execute(command);

      writing_ = false;

      if (!executed)
         return 0;

      // The oldest surviving row still points back at a row that is now gone,
      // and it is left that way on purpose: rewriting its back-hash would mean
      // recomputing every hash after it, which is exactly the operation the
      // chain exists to make detectable. Verify knows the difference - with a
      // retention setting in force, a front that does not begin at the empty
      // hash is the administrator's own policy rather than tampering.
      LOG_APPLICATION(Formatter::Format("Audit: {0} audit rows older than {1} days were deleted. The chain now begins at the oldest row that remains, which still carries the hash of the row before it.",
         removed, days));

      return removed;
   }
}

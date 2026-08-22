// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"

#include "../BO/AppPassword.h"
#include "PersistentAppPassword.h"
#include "PersistenceMode.h"
#include "../Util/Time.h"

#include <atomic>

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   namespace
   {
      // How stale the recorded "last used" time is allowed to get before a use is
      // written back. See the header for why this is not zero.
      const int last_used_write_interval_minutes = 60;

      // -1 unknown, 0 none exist, 1 at least one exists. See AnyConfigured.
      std::atomic<int> any_configured_(-1);

      // The most app passwords one account may hold.
      //
      // This is a cost bound, not a tidiness rule. Every one of an account's app
      // passwords is verified against a failed logon, and they are stored under the
      // same deliberately-expensive hash as the account password - so the work an
      // attacker can provoke with one wrong guess is proportional to this number.
      // Twenty-five is what the large providers settled on and is far more than the
      // number of mail clients anyone owns; without a bound, an account that had
      // accumulated hundreds would turn each wrong guess into seconds of Argon2id.
      const int max_app_passwords_per_account = 25;
   }

   PersistentAppPassword::PersistentAppPassword(void)
   {
   }

   PersistentAppPassword::~PersistentAppPassword(void)
   {
   }

   bool
   PersistentAppPassword::SaveObject(std::shared_ptr<AppPassword> password, String &result, PersistenceMode mode)
   {
      if (password->GetName().IsEmpty())
      {
         result = "A name is required. It is what the account holder revokes by, so a "
                  "list of unnamed credentials cannot be acted on.";
         return false;
      }

      if (password->GetHash().IsEmpty())
      {
         result = "An app password with no stored hash would authenticate nothing, and a "
                  "row that cannot be used is a row nobody will think to delete.";
         return false;
      }

      // apcreated is NOT NULL, and AddColumnDate writes NULL for a date it cannot
      // parse - so an object saved without one (a restore from a backup written
      // before this column existed, or any caller that forgets) would fail the insert
      // with a constraint error rather than an explanation. Stamped here instead.
      if (password->GetCreatedTime().IsEmpty())
         password->SetCreatedTime(Time::GetCurrentDateTime());

      if (password->GetID() == 0)
      {
         SQLCommand countCommand("select count(*) as apcount from hm_apppasswords where apaccountid = @APACCOUNTID");
         countCommand.AddParameter("@APACCOUNTID", password->GetAccountID());

         std::shared_ptr<DALRecordset> countRecordset =
            Application::Instance()->GetDBManager()->OpenRecordset(countCommand);

         if (countRecordset && countRecordset->GetLongValue("apcount") >= max_app_passwords_per_account)
         {
            result = Formatter::Format("This account already has the maximum of {0} app passwords. "
                                       "Delete one that is no longer used - the list shows which have never "
                                       "authenticated - before issuing another.",
                                       max_app_passwords_per_account);
            return false;
         }
      }

      SQLStatement statement;

      statement.SetTable("hm_apppasswords");

      statement.AddColumnInt64("apaccountid", password->GetAccountID());
      statement.AddColumn("apname", password->GetName());
      statement.AddColumn("aphash", password->GetHash());
      statement.AddColumnInt64("apencryption", password->GetEncryption());
      statement.AddColumnDate("apcreated", Time::GetDateFromSystemDate(password->GetCreatedTime()));
      statement.AddColumnDate("aplastused", Time::GetDateFromSystemDate(password->GetLastUsedTime()));
      statement.AddColumnInt64("apactive", password->GetActive() ? 1 : 0);

      bool newObject = password->GetID() == 0;

      if (newObject)
      {
         statement.SetStatementType(SQLStatement::STInsert);
         statement.SetIdentityColumn("apid");
      }
      else
      {
         statement.SetStatementType(SQLStatement::STUpdate);
         statement.SetWhereClause(Formatter::Format("apid = {0}", password->GetID()));
      }

      __int64 dbid = 0;
      bool retVal = Application::Instance()->GetDBManager()->Execute(statement, newObject ? &dbid : 0);

      if (retVal && newObject)
         password->SetID(dbid);

      if (retVal)
         InvalidateExistenceCache();

      return retVal;
   }

   bool
   PersistentAppPassword::AnyConfigured()
   {
      int cached = any_configured_.load();

      if (cached >= 0)
         return cached == 1;

      // A count rather than a select of the rows: the answer is one integer, and this
      // runs on a failed-authentication path where the table could be large.
      SQLCommand command("select count(*) as apcount from hm_apppasswords");

      std::shared_ptr<DALRecordset> recordset = Application::Instance()->GetDBManager()->OpenRecordset(command);

      if (!recordset)
      {
         // The database could not answer. Nothing is cached, so the next attempt asks
         // again - caching "none" here would silently disable every app password on
         // the server until a restart, which is a worse failure than one extra query.
         return false;
      }

      bool exists = recordset->GetLongValue("apcount") > 0;

      any_configured_.store(exists ? 1 : 0);

      return exists;
   }

   void
   PersistentAppPassword::InvalidateExistenceCache()
   {
      any_configured_.store(-1);
   }

   bool
   PersistentAppPassword::DeleteObject(std::shared_ptr<AppPassword> password)
   {
      SQLCommand command("delete from hm_apppasswords where apid = @APID");
      command.AddParameter("@APID", password->GetID());

      bool retVal = Application::Instance()->GetDBManager()->Execute(command);

      if (retVal)
         InvalidateExistenceCache();

      return retVal;
   }

   bool
   PersistentAppPassword::DeleteByAccountID(__int64 accountID)
   {
      SQLCommand command("delete from hm_apppasswords where apaccountid = @APACCOUNTID");
      command.AddParameter("@APACCOUNTID", accountID);

      bool retVal = Application::Instance()->GetDBManager()->Execute(command);

      if (retVal)
         InvalidateExistenceCache();

      return retVal;
   }

   void
   PersistentAppPassword::RecordUse(std::shared_ptr<AppPassword> password)
   {
      if (!password || password->GetID() == 0)
         return;

      String now = Time::GetCurrentDateTime();

      String recorded = password->GetLastUsedTime();

      if (!recorded.IsEmpty())
      {
         DateTime last = Time::GetDateFromSystemDate(recorded);
         DateTime current = Time::GetDateFromSystemDate(now);

         if (last.GetStatus() == DateTime::valid && current.GetStatus() == DateTime::valid)
         {
            DateTimeSpan age = current - last;

            double ageSeconds = age.GetNumberOfSeconds();

            // A clock that has gone backwards produces a negative age. Treating that
            // as "recent enough" would freeze the column until the clock caught up,
            // so it writes instead: one extra update after a clock change is cheaper
            // than a timestamp that stops moving.
            if (ageSeconds >= 0 && ageSeconds < last_used_write_interval_minutes * 60)
               return;
         }
      }

      password->SetLastUsedTime(now);

      // Written with a targeted UPDATE rather than SaveObject, so that recording a
      // use can never rewrite the hash, the name or the active flag - this runs on
      // an authentication path, from an object another thread may be editing.
      SQLCommand command("update hm_apppasswords set aplastused = @APLASTUSED where apid = @APID");
      command.AddParameter("@APLASTUSED", now);
      command.AddParameter("@APID", password->GetID());

      Application::Instance()->GetDBManager()->Execute(command);
   }
}

// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"
#include "PasswordHistory.h"

#include "../Application/Application.h"
#include "../Application/IniFileSettings.h"
#include "../BO/Account.h"
#include "../SQL/DALRecordset.h"
#include "../SQL/SQLCommand.h"
#include "../SQL/SQLStatement.h"
#include "../SQL/DatabaseConnectionManager.h"
#include "Crypt.h"
#include "Time.h"

#include <ctime>

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   bool
   PasswordHistory::IsReuse(std::shared_ptr<const Account> account, const String &newPassword)
   {
      try
      {
         int depth = IniFileSettings::Instance()->GetPasswordPolicyHistoryCount();

         if (depth <= 0 || !account || newPassword.IsEmpty())
            return false;

         // The current password counts as the most recent history entry. Without
         // this, "you may not reuse your last three passwords" would still allow
         // setting the password to itself, which is the one repeat somebody is
         // actually likely to try.
         Crypt::EncryptionType currentType = (Crypt::EncryptionType) account->GetPasswordEncryption();

         if (!account->GetPassword().IsEmpty() &&
             Crypt::Instance()->Validate(newPassword, account->GetPassword(), currentType))
         {
            return true;
         }

         // Ordered by phid, not by phchanged. The timestamp has one-second resolution,
         // and several password changes inside one second are exactly what an
         // administrator resetting an account does - so ordering by it makes "the
         // last N" ambiguous among ties, and the wrong N get examined. The identity
         // column is monotonic by construction.
         SQLCommand command("select phhash, phencryption from hm_passwordhistory where phaccountid = @ACCOUNTID order by phid desc");
         command.AddParameter("@ACCOUNTID", account->GetID());

         std::shared_ptr<DALRecordset> recordset = Application::Instance()->GetDBManager()->OpenRecordset(command);

         if (!recordset)
            return false;

         int examined = 0;

         while (!recordset->IsEOF() && examined < depth)
         {
            String hash = recordset->GetStringValue("phhash");
            Crypt::EncryptionType type = (Crypt::EncryptionType) recordset->GetLongValue("phencryption");

            // Compared with the verifier rather than by comparing hashes: every
            // scheme here is salted, so the same password hashes differently each
            // time and a string comparison would report "not a repeat" always.
            if (!hash.IsEmpty() && Crypt::Instance()->Validate(newPassword, hash, type))
               return true;

            examined++;
            recordset->MoveNext();
         }

         return false;
      }
      catch (...)
      {
         // Fail open: refusing a password change because the history table could not
         // be read would leave somebody unable to change a password they may be
         // changing precisely because it has been compromised.
         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 6201, "PasswordHistory::IsReuse",
            "The password history could not be checked, so the new password was accepted without that check.");

         return false;
      }
   }

   void
   PasswordHistory::Record(std::shared_ptr<const Account> account)
   {
      try
      {
         int depth = IniFileSettings::Instance()->GetPasswordPolicyHistoryCount();

         if (depth <= 0 || !account || account->GetID() == 0 || account->GetPassword().IsEmpty())
            return;

         SQLStatement statement;
         statement.SetTable("hm_passwordhistory");
         statement.AddColumnInt64("phaccountid", account->GetID());
         statement.AddColumn("phhash", account->GetPassword());
         statement.AddColumnInt64("phencryption", account->GetPasswordEncryption());
         statement.AddColumnDate("phchanged", Time::GetDateFromSystemDate(Time::GetCurrentDateTime()));
         statement.SetStatementType(SQLStatement::STInsert);
         statement.SetIdentityColumn("phid");

         __int64 dbid = 0;

         if (!Application::Instance()->GetDBManager()->Execute(statement, &dbid))
            return;

         // Trimmed here rather than by a scheduled sweep: the table only grows when
         // a password changes, so the moment it grows is the cheapest moment to
         // bound it, and nothing else has to remember that this table exists.
         // Same ordering as the check above, for the same reason: trimming by a
         // timestamp that ties would delete an arbitrary member of the tie rather
         // than the oldest entry.
         SQLCommand countCommand("select phid from hm_passwordhistory where phaccountid = @ACCOUNTID order by phid desc");
         countCommand.AddParameter("@ACCOUNTID", account->GetID());

         std::shared_ptr<DALRecordset> recordset =
            Application::Instance()->GetDBManager()->OpenRecordset(countCommand);

         if (!recordset)
            return;

         std::vector<__int64> surplus;
         int seen = 0;

         while (!recordset->IsEOF())
         {
            if (seen >= depth)
               surplus.push_back(recordset->GetInt64Value("phid"));

            seen++;
            recordset->MoveNext();
         }

         for (__int64 id : surplus)
         {
            SQLCommand deleteCommand("delete from hm_passwordhistory where phid = @PHID");
            deleteCommand.AddParameter("@PHID", id);
            Application::Instance()->GetDBManager()->Execute(deleteCommand);
         }
      }
      catch (...)
      {
         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 6202, "PasswordHistory::Record",
            "The previous password could not be recorded in the reuse history. The change itself was not affected.");
      }
   }

   void
   PasswordHistory::DeleteByAccountID(__int64 accountID)
   {
      // History that outlives its account is rows nobody can attribute, and account
      // ids are reissued - so a new account could inherit a stranger's history and
      // be refused a password for a reason that cannot be explained.
      SQLCommand command("delete from hm_passwordhistory where phaccountid = @ACCOUNTID");
      command.AddParameter("@ACCOUNTID", accountID);

      Application::Instance()->GetDBManager()->Execute(command);
   }

   bool
   PasswordHistory::HasExpired(std::shared_ptr<const Account> account)
   {
      try
      {
         int maximumAgeDays = IniFileSettings::Instance()->GetPasswordPolicyMaximumAgeDays();

         if (maximumAgeDays <= 0 || !account)
            return false;

         // An Active Directory account's password is not stored here and cannot be
         // changed here. Aging it out would refuse a credential this server does not
         // own and cannot help anybody renew - and the directory has its own expiry
         // policy, which is the one that should apply.
         if (account->GetIsAD())
            return false;

         String changed = account->GetPasswordChanged();

         // Unreadable or unstamped is NOT expired. The alternative locks out every
         // account whose row predates the column, which is every account on any
         // server that upgrades - the single worst thing this feature could do.
         if (changed.GetLength() < 19)
            return false;

         std::time_t cutoffTime = std::time(0) - (static_cast<std::time_t>(maximumAgeDays) * 24 * 60 * 60);

         struct tm cutoffTm;

         if (localtime_s(&cutoffTm, &cutoffTime) != 0)
            return false;

         char cutoffText[32];

         if (strftime(cutoffText, sizeof(cutoffText), "%Y-%m-%d %H:%M:%S", &cutoffTm) == 0)
            return false;

         // Timestamp strings in "YYYY-MM-DD HH:MM:SS", where lexicographic order is
         // chronological order - the same comparison the quarantine sweep uses, and
         // for the same reason: it keeps four SQL dialects' date handling out of a
         // decision that can lock somebody out.
         return changed.Compare(String(cutoffText)) < 0;
      }
      catch (...)
      {
         return false;
      }
   }

   void
   PasswordHistory::ReportExpired(std::shared_ptr<const Account> account)
   {
      if (!account)
         return;

      // The client is told nothing that distinguishes this from a wrong password -
      // see the header - so the log is the only place an administrator can find out
      // that somebody is locked out by policy rather than by forgetting. Written at
      // application level rather than as an error, because it is the feature working.
      String message;
      message.Format(_T("The password for %s has passed the configured maximum age and was refused. "
                        "This server has no self-service password change, so it must be reset by an administrator."),
         account->GetAddress().c_str());

      LOG_APPLICATION(message);
   }
}

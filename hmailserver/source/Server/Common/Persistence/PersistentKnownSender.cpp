// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"

#include "PersistentKnownSender.h"

#include "../SQL/DALConnection.h"
#include "../SQL/DALRecordset.h"
#include "../SQL/SQLCommand.h"
#include "../SQL/SQLStatement.h"
#include "../Util/Time.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   namespace
   {
      // The column is 255 characters and an address longer than that is not one
      // anybody can write back to, so the truncation is a storage decision
      // rather than a semantic one. Folded down because a correspondent is the
      // same person whichever case their client used.
      String Canonical(const String &address)
      {
         String result = address;
         result.TrimLeft();
         result.TrimRight();
         result.ToLower();
         return result.Left(255);
      }

      bool AccountHasAnyMemory(__int64 accountID)
      {
         SQLCommand command("select ksid from hm_knownsenders where ksaccountid = @ACCOUNTID");
         command.AddParameter("@ACCOUNTID", accountID);

         std::shared_ptr<DALRecordset> recordset = Application::Instance()->GetDBManager()->OpenRecordset(command);
         if (!recordset)
            return false;

         return !recordset->IsEOF();
      }

      // Returns the row id, or 0 when there is none. -1 when the query failed,
      // which the caller must not read as "not seen before".
      __int64 FindRow(__int64 accountID, const String &address)
      {
         SQLCommand command("select ksid from hm_knownsenders where ksaccountid = @ACCOUNTID and ksaddress = @ADDRESS");
         command.AddParameter("@ACCOUNTID", accountID);
         command.AddParameter("@ADDRESS", address);

         std::shared_ptr<DALRecordset> recordset = Application::Instance()->GetDBManager()->OpenRecordset(command);
         if (!recordset)
            return -1;

         if (recordset->IsEOF())
            return 0;

         return recordset->GetInt64Value("ksid");
      }

      bool Insert(__int64 accountID, const String &address)
      {
         const String now = Time::GetCurrentDateTime();

         SQLStatement statement;
         statement.SetTable("hm_knownsenders");
         statement.AddColumnInt64("ksaccountid", accountID);
         statement.AddColumn("ksaddress", address);
         statement.AddColumn("kscount", (long) 1);
         statement.AddColumn("ksfirstseen", now);
         statement.AddColumn("kslastseen", now);
         statement.SetStatementType(SQLStatement::STInsert);
         statement.SetIdentityColumn("ksid");

         // The unique index is ignored on purpose. Two messages from the same
         // new sender delivered at the same moment both find no row and both
         // insert; the loser of that race is not a fault - the row it wanted
         // now exists - and an error in the log for a message that was
         // delivered correctly is noise an administrator has to rule out.
         return Application::Instance()->GetDBManager()->Execute(statement, 0, DALConnection::DALErrorInSQL);
      }

      void Touch(__int64 rowID)
      {
         SQLCommand command("update hm_knownsenders set kscount = kscount + 1, kslastseen = @LASTSEEN where ksid = @KSID");
         command.AddParameter("@LASTSEEN", Time::GetCurrentDateTime());
         command.AddParameter("@KSID", rowID);

         Application::Instance()->GetDBManager()->Execute(command);
      }
   }

   PersistentKnownSender::Result
   PersistentKnownSender::Remember(__int64 accountID, const String &senderAddress)
   {
      if (accountID <= 0)
         return Unknown;

      const String address = Canonical(senderAddress);
      if (address.IsEmpty())
         return Unknown;

      __int64 rowID = FindRow(accountID, address);

      if (rowID < 0)
         return Unknown;

      if (rowID > 0)
      {
         Touch(rowID);
         return SeenBefore;
      }

      // Not seen. Whether that is worth telling the reader depends on whether
      // this account remembers anybody at all, which is the only extra query
      // this feature makes, and only on the first message from a given sender.
      const bool hadMemory = AccountHasAnyMemory(accountID);

      Insert(accountID, address);

      return hadMemory ? FirstContact : NoMemoryYet;
   }

   void
   PersistentKnownSender::RememberOutgoing(__int64 accountID, const String &recipientAddress)
   {
      if (accountID <= 0)
         return;

      const String address = Canonical(recipientAddress);
      if (address.IsEmpty())
         return;

      __int64 rowID = FindRow(accountID, address);

      if (rowID < 0)
         return;

      if (rowID > 0)
      {
         Touch(rowID);
         return;
      }

      Insert(accountID, address);
   }

   bool
   PersistentKnownSender::DeleteByAccount(__int64 accountID)
   {
      SQLCommand command("delete from hm_knownsenders where ksaccountid = @ACCOUNTID");
      command.AddParameter("@ACCOUNTID", accountID);

      return Application::Instance()->GetDBManager()->Execute(command);
   }

   int
   PersistentKnownSender::CountByAccount(__int64 accountID)
   {
      SQLCommand command("select count(*) as knowncount from hm_knownsenders where ksaccountid = @ACCOUNTID");
      command.AddParameter("@ACCOUNTID", accountID);

      std::shared_ptr<DALRecordset> recordset = Application::Instance()->GetDBManager()->OpenRecordset(command);
      if (!recordset || recordset->IsEOF())
         return 0;

      return recordset->GetLongValue("knowncount");
   }
}

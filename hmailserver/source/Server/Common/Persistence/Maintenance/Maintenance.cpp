// Copyright (c) 2009 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#include "stdafx.h"
#include "Maintenance.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM   
{
   Maintenance::Maintenance()
   {

   }

   Maintenance::~Maintenance()
   {

   }

   bool
   Maintenance::Perform(MaintenanceOperation operation)
   {
      switch (operation)
      {
      case RecalculateFolderUID:
         return RecalculateFolderUID_();
      }

      return false;
   }

   // Goes through all mailboxes and sets the foldercurrentuid to the latest message uid.
   bool
   Maintenance::RecalculateFolderUID_()
   {
      /*
         The rows this repair is about are messages that live in an IMAP folder.
         hm_messages holds more than that: a message awaiting delivery is stored
         with messagefolderid = 0 and messageuid = 0 (Message.cpp's defaults),
         because it is in the queue rather than in anyone's mailbox.

         The query used to group the whole table and then bail out with `return
         false` on the first row whose folder or uid was not positive - so on any
         server with even one message in the delivery queue, the grouped set
         contained a (0, 0) row and the repair failed. Worse than failing: the
         result set has no ORDER BY, so an arbitrary number of folders had already
         been updated before the abort. The run was neither complete nor a
         no-op, and the caller was told only "The maintenance operation failed".

         Excluding those rows in SQL is the fix. It is also more correct than the
         guards were: they treated ordinary queue contents as corruption, when the
         only genuinely bad state - a folder id or uid that is negative - is
         excluded by the same predicate.
      */
      AnsiString recordSQL =
         "SELECT messagefolderid, MAX(messageuid) as messageuid FROM hm_messages "
         "WHERE messagefolderid > 0 AND messageuid > 0 GROUP BY messagefolderid";

      std::shared_ptr<DALRecordset> pRS = Application::Instance()->GetDBManager()->OpenRecordset(SQLCommand(recordSQL));

      if (!pRS)
         return false;

      while (!pRS->IsEOF())
      {
         __int64 messageFolderID = pRS->GetInt64Value("messagefolderid");
         __int64 messageUID = pRS->GetInt64Value("messageuid");

         // Belt and braces: the predicate above already excludes these, so a row
         // reaching here means the database returned something the query said it
         // would not. Skip it rather than abandoning the run, because abandoning
         // half way is what made the old behaviour worse than useless.
         if (messageFolderID <= 0 || messageUID <= 0)
         {
            pRS->MoveNext();
            continue;
         }

         AnsiString sqlUpdate = Formatter::Format("UPDATE hm_imapfolders SET foldercurrentuid = {0} WHERE folderid = {1} AND foldercurrentuid < {0}", messageUID, messageFolderID);

         bool result = Application::Instance()->GetDBManager()->Execute(SQLCommand(sqlUpdate));
         if (result == false)
            return false;

         pRS->MoveNext();
      }

      return true;
   }

}
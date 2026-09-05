// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"

#include "AddressTraceEraser.h"
#include "../Persistence/PersistentArchiveIndex.h"

#include "FileUtilities.h"
#include "../Application/Application.h"
#include "../Application/ErrorManager.h"
#include "../Application/IniFileSettings.h"
#include "../AntiSpam/QuarantineStore.h"
#include "../BO/Alias.h"
#include "../BO/DistributionList.h"
#include "../Cache/Cache.h"
#include "../SQL/SQLCommand.h"
#include "../SQL/DALRecordset.h"
#include "../SQL/DatabaseConnectionManager.h"
#include "../Util/Parsing/StringParser.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   bool
   AddressTraceEraser::Erase(const String &address, bool includeArchive, int &removedCount)
   {
      removedCount = 0;

      if (address.IsEmpty() || address.Find(_T("@")) <= 0)
         return false;

      bool anyFailed = false;

      // Each store is swept independently and a failure in one does not stop
      // the others - stopping half way through an erasure leaves more behind,
      // not less, which is the same reasoning the account-deletion cascade
      // records. What failed is reported at the end.
      std::vector<String> failedStores;

      // Message trace: the sender and recipient of every traced delivery.
      int count = DeleteAddressRows_("hm_messagetrace", "mtsender", "mtrecipient", address);
      if (count < 0)
         failedStores.push_back(_T("the message trace"));
      else
         removedCount += count;

      // Greylisting triplets: sender and recipient addresses, otherwise removed
      // only by age.
      count = DeleteAddressRows_("hm_greylisting_triplets", "glsenderaddress", "glrecipientaddress", address);
      if (count < 0)
         failedStores.push_back(_T("the greylisting triplets"));
      else
         removedCount += count;

      // Distribution-list memberships. This changes what the list delivers,
      // deliberately: an erasure request is precisely a request to stop being
      // on things.
      count = DeleteAddressRows_("hm_distributionlistsrecipients", "distributionlistrecipientaddress", "", address);
      if (count < 0)
         failedStores.push_back(_T("distribution-list memberships"));
      else
         removedCount += count;

      // Aliases where the address is either half: as the NAME it is the
      // person's own address existing as an alias, as the VALUE it is a
      // forward still sending somebody's mail to them.
      count = DeleteAddressRows_("hm_aliases", "aliasname", "aliasvalue", address);
      if (count < 0)
         failedStores.push_back(_T("aliases"));
      else
         removedCount += count;

      // The alias and distribution-list collections are cached; rows deleted
      // underneath a cache would keep resolving until the next restart, which
      // for an erasure is the difference between done and not done.
      Cache<Alias>::Instance()->Clear();
      Cache<DistributionList>::Instance()->Clear();

      bool quarantineFailed = false;
      removedCount += EraseQuarantine_(address, quarantineFailed);
      if (quarantineFailed)
         failedStores.push_back(_T("the quarantine"));

      if (includeArchive)
      {
         bool archiveFailed = false;
         removedCount += EraseArchive_(address, archiveFailed);
         if (archiveFailed)
            failedStores.push_back(_T("the archive"));
      }

      if (!failedStores.empty())
      {
         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 6250, "AddressTraceEraser::Erase",
            Formatter::Format(_T("While erasing the traces of {0}, these could not be fully removed and should be retried: {1}."),
               address, StringParser::JoinVector(failedStores, _T(", "))));
         anyFailed = true;
      }

      String logMessage;
      logMessage.Format(_T("Erased %d stored trace(s) of an address on an operator's request."), removedCount);
      LOG_APPLICATION(logMessage);

      return !anyFailed;
   }

   int
   AddressTraceEraser::DeleteAddressRows_(const AnsiString &table, const AnsiString &firstColumn, const AnsiString &secondColumn, const String &address)
   {
      AnsiString where = firstColumn + " = @ADDRESS1";
      if (!secondColumn.IsEmpty())
         where += " or " + secondColumn + " = @ADDRESS2";

      SQLCommand countCommand("select count(*) as c from " + table + " where " + where);
      countCommand.AddParameter("@ADDRESS1", address);
      if (!secondColumn.IsEmpty())
         countCommand.AddParameter("@ADDRESS2", address);

      std::shared_ptr<DALRecordset> recordset = Application::Instance()->GetDBManager()->OpenRecordset(countCommand);
      if (!recordset)
         return -1;

      const int count = recordset->GetLongValue("c");

      if (count == 0)
         return 0;

      SQLCommand deleteCommand("delete from " + table + " where " + where);
      deleteCommand.AddParameter("@ADDRESS1", address);
      if (!secondColumn.IsEmpty())
         deleteCommand.AddParameter("@ADDRESS2", address);

      if (!Application::Instance()->GetDBManager()->Execute(deleteCommand))
         return -1;

      return count;
   }

   int
   AddressTraceEraser::EraseQuarantine_(const String &address, bool &failed)
   {
      failed = false;

      // Matched in memory rather than with LIKE, because the recipients column
      // is a comma-separated list and "ann@example.test" LIKE-matches inside
      // "joann@example.test". The list call is the administration surface's own,
      // so the shapes agree with what a reviewer sees.
      //
      // The column is truncated at 1000 characters on write; an address that
      // fell off the end of a very long recipient list is not matchable here,
      // by anything.
      const std::vector<QuarantinedMessage> messages = QuarantineStore::List(1000000);

      int removed = 0;

      for (const QuarantinedMessage &message : messages)
      {
         bool matches = (message.sender.CompareNoCase(address) == 0);

         if (!matches)
         {
            std::vector<String> recipients = StringParser::SplitString(message.recipients, ",");
            for (String recipient : recipients)
            {
               recipient.Trim();
               if (recipient.CompareNoCase(address) == 0)
               {
                  matches = true;
                  break;
               }
            }
         }

         if (!matches)
            continue;

         if (QuarantineStore::Delete(message.id))
            removed++;
         else
            failed = true;
      }

      return removed;
   }

   // True when a string cannot safely be one segment of a filesystem path:
   // empty, all dots (. / .. / ...), or containing a separator. Static file
   // scope - it is only needed here.
   static bool IsUnsafePathSegment_(const String &segment)
   {
      if (segment.IsEmpty())
         return true;

      if (segment.Find(_T("\\")) >= 0 || segment.Find(_T("/")) >= 0)
         return true;

      for (int i = 0; i < segment.GetLength(); i++)
         if (segment[i] != _T('.'))
            return false;

      return true;   // reached only when every character was a dot
   }

   int
   AddressTraceEraser::EraseArchive_(const String &address, bool &failed)
   {
      failed = false;

      const String archiveDir = IniFileSettings::Instance()->GetArchiveDir();
      if (archiveDir.IsEmpty())
         return 0;

      // The per-user archive tree is <ArchiveDir>\<domain>\<localpart>, both
      // halves lower-cased by the code that writes it. It holds the received
      // copies and the Sent- copies alike, which is what makes one directory
      // delete the whole per-address sweep.
      std::vector<String> parts = StringParser::SplitString(address, "@");
      if (parts.size() != 2)
         return 0;

      String localPart = parts[0];
      String domainPart = parts[1];
      localPart.ToLower();
      domainPart.ToLower();

      // Neither half may be all-dots or carry a separator: the archive path is
      // built by concatenation, so a local part of ".." would make DeleteDirectory
      // climb to the domain folder and remove every user's archive, and a "\\"
      // would point it anywhere. An address that shaped is not one this server
      // would have written an archive for, so refusing to erase is correct - the
      // caller's COM validation guarantees a single "@" but not this.
      if (IsUnsafePathSegment_(localPart) || IsUnsafePathSegment_(domainPart))
      {
         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 6251, "AddressTraceEraser::EraseArchive_",
            Formatter::Format(_T("The archive was not swept for {0}: its address does not form a safe directory name."), address));
         failed = true;
         return 0;
      }

      // The index rows that name the address go with the files, held copies
      // excepted: a hold is a promise that nothing removes the record.
      PersistentArchiveIndex::RemoveByAddress(address);

      const String userArchive = archiveDir + _T("\\") + domainPart + _T("\\") + localPart;

      if (!FileUtilities::Exists(userArchive))
         return 0;

      if (!FileUtilities::DeleteDirectory(userArchive, true))
      {
         failed = true;
         return 0;
      }

      return 1;
   }
}

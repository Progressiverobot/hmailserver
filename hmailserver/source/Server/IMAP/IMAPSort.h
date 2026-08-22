// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

namespace HM
{
   class IMAPSortParser;
   class Message;
   class MessageMetaData;
   class IMAPConnection;

   class IMAPSort
   {
   public:
      IMAPSort(void);
      ~IMAPSort(void);

      enum SortField
      {
         Unknown = 0,
         From = 1,
         Subject = 2,
         CC = 3,
         To = 4,
         Date = 5,
         Arrival = 6,
         Size = 7

      };

      // abortRequested is asked before each message's header is read, and the sort
      // stops the moment it answers true - the return is then false and the vector
      // order is meaningless. Same contract as IMAPThread::BuildThreads, added for
      // the same reason: the header pass reads one file per matched message, and a
      // ceiling that is only consulted after the pass cannot stop it, only disown a
      // result the connection thread already spent the time producing.
      bool Sort(std::shared_ptr<IMAPConnection> pConnection, std::vector<std::pair<int, std::shared_ptr<Message> > > &vecMessages, String character_set, std::shared_ptr<IMAPSortParser> pParser,
                const std::function<bool()> &abortRequested);

   private:

      bool CacheHeaderFields_(std::shared_ptr<IMAPConnection> pConnection, const std::vector<std::pair<int, std::shared_ptr<Message> > > &vecMessages, const std::map<__int64, String > &databaseMetaData, SortField &sortField, std::map<__int64, String> &mapHeaderFields,
                              const std::function<bool()> &abortRequested);

      SortField GetSortField_(AnsiString sHeaderField);
   };
}
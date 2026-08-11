// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once

namespace HM
{
   class IMAPListLookup
   {
   public:

      IMAPListLookup();
      virtual ~IMAPListLookup();

      // maxItem is the value "*" stands for: the largest UID in the mailbox for a
      // UID set, or the number of messages for a message-sequence set.
      static bool IsItemInList(const std::vector<String> &vecItems, int item, int maxItem);

   private:

   };


}
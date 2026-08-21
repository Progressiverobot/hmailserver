// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com

#include "StdAfx.h"

#include "BlockedSenders.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   BlockedSenders::BlockedSenders()
   {
   }

   BlockedSenders::~BlockedSenders(void)
   {
   }

   bool
   BlockedSenders::Refresh()
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Reads all blocked senders from the database. The bool result matters:
   // BlockedSenderCache uses it to tell "the list is empty" from "the
   // database could not be read", which must not be treated the same.
   //---------------------------------------------------------------------------()
   {
      String sSQL = "select * from hm_blocked_senders order by bsaddress asc";
      return DBLoad_(sSQL);
   }
}

// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"

#include "WhiteListAddresses.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   WhiteListAddresses::WhiteListAddresses()
   {
   }

   WhiteListAddresses::~WhiteListAddresses(void)
   {
   }


   bool
   WhiteListAddresses::Refresh()
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Reads all white list addresses from the database. The bool result lets
   // WhiteListCache tell "the list is empty" from "the database could not
   // be read", which must not be cached the same way.
   //---------------------------------------------------------------------------()
   {
      String sSQL = "select * from hm_whitelist order by whiteloweripaddress1 asc, whiteloweripaddress2 asc";
      return DBLoad_(sSQL);
   }



}
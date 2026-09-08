// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "stdafx.h"
#include "DALRecordsetFactory.h"
// ADORecordset names the ADO smart pointers the Windows precompiled header
// #imports; the roadmap section "Linux and AArch64" leaves that backend out of
// the POSIX build. Nothing below uses the type, only the include had to go.
#ifdef _MSC_VER
#include "ADORecordset.h"
#endif
#include "MySQLRecordset.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   DALRecordsetFactory::DALRecordsetFactory()
   {

   }

   DALRecordsetFactory::~DALRecordsetFactory()
   {

   }


   /*std::shared_ptr<DALRecordset>
   DALRecordsetFactory::CreateRecordset()
   {

   
      return pRS;
   }*/
}
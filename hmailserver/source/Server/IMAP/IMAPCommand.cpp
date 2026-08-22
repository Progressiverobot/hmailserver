// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "stdafx.h"

#include "IMAPCommand.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   IMAPCommand::IMAPCommand()
   {

   }

   IMAPCommand::~IMAPCommand()
   {

   }

   String
   IMAPCommandArgument::Literal(unsigned int Index)
   {
      if (Index >= literals_.size())
         return "";
      else 
         return literals_[Index];
   }

}

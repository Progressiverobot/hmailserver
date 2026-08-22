// Copyright (c) 2005 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// Created 2006-03-25
//
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

#include "VariantDateTime.h"

namespace HM
{
   

   class FileInfo
   {
   public:
	   FileInfo(const String &name, const DateTime &created);
      FileInfo();

      String GetName() {return name_;}
      DateTime GetCreateTime() {return created_;}

   private:
      
      String name_;
      DateTime created_;
   };

}

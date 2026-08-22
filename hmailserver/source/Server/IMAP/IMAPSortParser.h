// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

namespace HM
{
   class IMAPSortParser
   {
   public:
      IMAPSortParser(void);
      ~IMAPSortParser(void);

      void Parse(const String &sExpression);

      std::vector<std::pair<bool,String> > GetSortTypes () {return sort_types_; }
   private:

      // pair: ascending, criteria
      
      std::vector<std::pair<bool,String> > sort_types_;
   };
}
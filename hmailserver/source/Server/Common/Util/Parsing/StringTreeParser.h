// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

namespace HM
{
   class StringNode
   {
   public:
   
      void SetValue(const String &sValue) {value_ = sValue; }

      std::vector<std::shared_ptr<StringNode> > GetSubNodes() {return sub_nodes_;}

   private:

      String value_;
      std::vector<std::shared_ptr<StringNode> > sub_nodes_;
   };

   class StringTreeParser
   {
   public:

      StringTreeParser(void);
      ~StringTreeParser(void);

      void Parse(const String &sCommand, std::vector<std::shared_ptr<StringNode> > & vecNodes);

   private:

      int c_iCurState;
   };
}
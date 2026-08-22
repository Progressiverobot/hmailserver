// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

namespace HM
{
   class ClassTester  
   {
   public:
	   ClassTester();
	   virtual ~ClassTester();

      void DoTests();

   private:

      void LoadSettings_();

      void TestBackup_();

      String mime_data_path_;
   };

}

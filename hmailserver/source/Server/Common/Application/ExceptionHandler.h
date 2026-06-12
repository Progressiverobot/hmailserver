// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once

namespace HM
{
   class ExceptionHandler
   {
   public:
      ExceptionHandler();

      static bool Run(const String &descriptive_name, boost::function<void()>& functionToRun);

   private:

      static void RunWithStandardExceptions(const String &descriptive_name, boost::function<void()>& functionToRun);

      static String GetExceptionText(const String &descriptive_name);

   };
}
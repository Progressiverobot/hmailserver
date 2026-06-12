// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

using System;
using System.Collections.Generic;
using System.Text;
using hMailServer.Administrator;

namespace hMailServer.Administrator
{
  
   static class Instances
   {
      private static IMainForm _mainForm;

      public static IMainForm MainForm
      {
         get
         {
            return _mainForm;
         }
         set
         {
            _mainForm = value;
         }
      }
   }
}

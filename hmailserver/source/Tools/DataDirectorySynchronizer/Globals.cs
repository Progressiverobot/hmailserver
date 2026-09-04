// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Text;

namespace DataDirectorySynchronizer
{
   class Globals
   {
      public enum ModeType
      {
         Import = 1,
         Delete = 2
      };

      public const string AllDomains = "All domains";

      public static ModeType Mode { get; set; }
      public static List<string> SelectedDomains { get; set; }

      // The public folders live beside the domain directories, not under any of
      // them, so they are walked separately and can be left out separately. On by
      // default: a message file under #Public that the database no longer knows was
      // never checked before, in either mode.
      public static bool SynchronizePublicFolders { get; set; }

      private static hMailServer.Application _application;

      static Globals()
      {
         SelectedDomains = new List<string>();
         SynchronizePublicFolders = true;
      }

      public static void SetApp(hMailServer.Application application)
      {
         _application = application;
      }

      public static hMailServer.Application GetApp()
      {
         return _application;
      }



      public static hMailServer.eDBtype GetDatabaseType(string type)
      {
         switch (type)
         {
            case "MSSQL":
               return hMailServer.eDBtype.hDBTypeMSSQL;
            case "MySQL":
               return hMailServer.eDBtype.hDBTypeMySQL;
            case "PGSQL":
               return hMailServer.eDBtype.hDBTypePostgreSQL;
            default:
               throw new Exception("Unknown database type");

         }
      }


   }
}

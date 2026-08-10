// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

namespace ImportTool
{
   internal static class Globals
   {
      private static hMailServer.Application _application;

      public static void SetApp(hMailServer.Application application)
      {
         _application = application;
      }

      public static hMailServer.Application GetApp()
      {
         return _application;
      }
   }
}

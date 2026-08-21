// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

using System;
using System.Collections.Generic;
using System.Text;

namespace hMailServer.Shared
{
   public class CommandLineParser
   {
      private static Dictionary<string, string> _argumentMap = null;

      public static void Parse()
      {
         _argumentMap = new Dictionary<string, string>();

         bool firstArgument = true;
         
         string[] arguments = Environment.GetCommandLineArgs();

         foreach (string argument in arguments)
         {
            if (firstArgument)
            {
               // first argument is the executable path. 
               firstArgument = false;
               continue;
            }

            if (argument.IndexOf(":") > 0)
            {
               string name = argument.Substring(0, argument.IndexOf(":"));
               string value = argument.Substring(argument.IndexOf(":") + 1);

               _argumentMap[name] = value;
            }
            else
            {
               _argumentMap[argument] = string.Empty;
            }
         }
      }

      public static bool ContainsArgument(string argument)
      {
         // Parse on demand rather than throw. Every caller in the tree does call
         // Parse() first, but the alternative to this guard is a NullReferenceException
         // inside the code that decides whether it is safe to show a dialog, and a
         // crash there would be a worse failure than the hang it replaced.
         if (_argumentMap == null)
            Parse();

         return _argumentMap.ContainsKey(argument);
      }

      public static bool IsSilent()
      {
         if (ContainsArgument("/silent"))
            return true;

         return false;
      }

      public static Dictionary<string, string> GetArguments()
      {
         return _argumentMap;
      }

      public static string GetArgument(string name)
      {
         return _argumentMap[name];
      }

   }
}

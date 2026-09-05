// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

namespace HM
{
   // The allow-list behind ScriptAllowedObjects (hMailServer.ini, [Settings]).
   //
   // An event script runs in-process, under the service account, and until now
   // CreateObject (VBScript) and new ActiveXObject (JScript) could instantiate any
   // COM class on the machine - WScript.Shell runs programs, Scripting.FileSystemObject
   // reads and writes the disk, ADODB.Connection opens databases - which made a
   // writable script file the same thing as a shell as LocalSystem. The script
   // engines ask their host before creating an object (IInternetHostSecurityManager,
   // the mechanism Internet Explorer used to sandbox pages), and this is the answer.
   //
   // ScriptAllowedObjects=*                    every class, the behaviour before the setting existed
   // ScriptAllowedObjects=                     no class at all
   // ScriptAllowedObjects=WScript.Shell,MSXML2.ServerXMLHTTP,{0D43FE01-F093-11CF-8940-00A0C9054228}
   //                                           exactly these, named by ProgID or by CLSID
   //
   // The default when the key is absent is "*": a setting that appears in an
   // upgrade must not break the scripts an installation already runs. The row in
   // the roadmap that asked for this is the one that also says the shipped Control
   // Panel templates use WScript.Shell and MSXML2.ServerXMLHTTP.
   class ScriptObjectPolicy
   {
   public:
      // Whether a script may create the class. objectName receives what the
      // decision was about - the class's registered ProgID when it has one, else
      // the CLSID in braces - for the log line and the error text.
      static bool IsAllowed(REFCLSID clsid, String &objectName);

      // The names in the setting, trimmed, in the order written; "*" alone means
      // unrestricted. Exposed for the log line that says what the policy is.
      static bool IsUnrestricted();

   private:
      static String DescribeClass_(REFCLSID clsid);
      static bool EntryMatches_(const String &entry, REFCLSID clsid, const String &progId);
   };
}

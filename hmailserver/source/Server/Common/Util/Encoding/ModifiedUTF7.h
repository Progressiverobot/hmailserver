// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later


namespace HM
{
   class ModifiedUTF7
   {
   public:
      static AnsiString Encode(const String &sUnicodeString);
      static String Decode(const AnsiString &s);

   private:
      static bool IsSpecialCharacter_(const wchar_t);
   };

   class ModifiedUTF7Tester
   {
   public:
      void Test();
   };

}
// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later


namespace HM
{
   class Base64
   {
   public:
      static AnsiString Encode(const char *input, int inputLength);
      static AnsiString Decode(const char *input, int inputLength);

   private:
   };

   class Base64Tester
   {
   public:
      void Test();
   };

}
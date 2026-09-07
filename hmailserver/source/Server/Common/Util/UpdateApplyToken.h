// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

namespace HM
{
   // The credential an update's database upgrade presents.
   //
   // A silent upgrade runs DBUpdater, which must authenticate as the administrator
   // before it moves the schema, and the server holds only a hash of that password.
   // Rather than ask the administrator for the password and put it on a command line
   // and in an install log, the server issues a token when it hands an installer to
   // the update helper: 32 random bytes in a file only the machine's administrators
   // can read, good for one hour and one use. The helper passes it to the installer
   // as /upgradetoken, the installer forwards it to DBUpdater as its password, and
   // Authenticate accepts "token:<hex>" for the administrator exactly once - after
   // which the file is gone. Nothing lasting is stored and nothing is typed.
   class UpdateApplyToken
   {
   public:
      // Issues a fresh token, replacing any earlier one. token is the hex.
      static bool Issue(AnsiString &token, String &error);

      // True once for "token:<hex>" matching the file, while the file is under an
      // hour old; the file is deleted on a match, so the second presentation fails.
      static bool Redeem(const String &presented);

      static void Revoke();
      static String Path();
   };
}

// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// http://www.hmailserver.com

#pragma once

namespace HM
{
   // Thin wrapper around the Windows Data Protection API (DPAPI) used to protect
   // reversible secrets at rest so that they cannot be decrypted off-box.
   //
   // Protection is machine-scoped (CRYPTPROTECT_LOCAL_MACHINE): the protected
   // blob can be unprotected by any process on the SAME machine (the service and
   // the administration tools run under different accounts, so user-scope would
   // not work) but is useless if the configuration file or database is copied to
   // another machine. The keying material never leaves the box, so a stolen
   // hMailServer.ini or database dump no longer exposes the plaintext secrets.
   class DataProtector
   {
   public:
      // Protects the supplied raw bytes and returns a base64-encoded blob in
      // protectedBase64. Returns false (and clears the output) if DPAPI is
      // unavailable for any reason; callers fall back to the legacy scheme.
      static bool Protect(const AnsiString &plainText, AnsiString &protectedBase64);

      // Reverses Protect. Returns false if the input is not a DPAPI blob that
      // this machine can decrypt (e.g. it was produced on another machine or
      // with a different scheme).
      static bool Unprotect(const AnsiString &protectedBase64, AnsiString &plainText);
   };

   class DataProtectorTester
   {
   public:
      void Test();
   };
}

// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

namespace HM
{
   // Protects reversible secrets at rest so that they cannot be decrypted off-box.
   // One class, two implementations, chosen by the platform:
   //
   // On Windows it is a thin wrapper around the Data Protection API (DPAPI),
   // machine-scoped (CRYPTPROTECT_LOCAL_MACHINE): the protected blob can be
   // unprotected by any process on the SAME machine (the service and the
   // administration tools run under different accounts, so user-scope would not
   // work) but is useless if the configuration file or database is copied to
   // another machine. The keying material never leaves the box, so a stolen
   // hMailServer.ini or database dump no longer exposes the plaintext secrets.
   //
   // On Linux there is no DPAPI and no machine key to derive from, so the key is
   // a file: 32 random bytes in <DataFolder>/.hmailserver-secret-key, created
   // with mode 0600 the first time a secret is protected, and the cipher is
   // AES-256-GCM through the OpenSSL the server already links. The protected form
   // is base64 of nonce || ciphertext || tag, a fresh 12-byte nonce per secret.
   // The scope is the file rather than the machine: whoever can read the key file
   // can open the secrets, which is why its mode is checked on every use and a
   // file readable by group or other is refused with the fix named. It also means
   // the secrets DO travel with the key file - copy the data directory and the
   // database together and every stored secret opens on the new machine, which
   // DPAPI could never offer - and that a data directory restored WITHOUT the key
   // file has lost every stored secret, which is a sentence for the backup
   // documentation rather than a defect.
   //
   // The two forms are not interchangeable and are not confused with each other:
   // Crypt::ProtectSecret writes "DPAPI:" in front of one and "LINUX1:" in front
   // of the other, and a value from the other platform is reported by name and
   // comes back empty rather than being fed to the wrong cipher.
   class DataProtector
   {
   public:
      // Protects the supplied raw bytes and returns a base64-encoded blob in
      // protectedBase64. Returns false (and clears the output) if the platform's
      // store is unavailable: DPAPI failing on Windows, or on Linux a key file
      // that cannot be created, cannot be read, or has a mode that lets other
      // users read it. On Linux every such refusal is reported with the file's
      // name, because the caller's only recourse is the administrator.
      static bool Protect(const AnsiString &plainText, AnsiString &protectedBase64);

      // Reverses Protect. Returns false if the input is not a blob that this
      // machine can decrypt (e.g. it was produced on another machine or with a
      // different scheme). A blob that does not open under the key is refused
      // silently, as CryptUnprotectData refuses silently - the callers treat an
      // empty result as "re-enter this password", and say so. A Windows DPAPI
      // blob met on Linux is the one input reported, once, by name: it means a
      // database or INI was carried across from a Windows installation.
      static bool Unprotect(const AnsiString &protectedBase64, AnsiString &plainText);

#ifdef HM_PLATFORM_POSIX
      // Where the key file is read from and created. Empty (the normal state)
      // means <DataFolder>/.hmailserver-secret-key; the self-test points it at a
      // file under the temporary directory so that a test never reads, creates
      // or deletes the real key. Nothing outside DataProtectorTester sets it.
      static void SetKeyFileForTest(const String &path);
      static String GetKeyFilePath();
#endif
   };

   class DataProtectorTester
   {
   public:
      void Test();
   };
}

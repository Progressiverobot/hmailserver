// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"

#include "DataProtector.h"

#include "Encoding/Base64.h"

#ifdef HM_PLATFORM_POSIX

// DPAPI - CryptProtectData and the machine key it derives - is a Windows facility
// with no POSIX counterpart, so the three Windows headers in the other arm are not
// read here. What stands in for it is a key file and AES-256-GCM: the key is 32
// random bytes in a 0600 file under the data directory, made the first time a
// secret is protected, and the cipher is the OpenSSL the server already links
// for TLS and password hashing. See the class comment in the header for what
// that does and does not promise.

#include <fcntl.h>
#include <unistd.h>
#include <sys/stat.h>

#include <openssl/evp.h>
#include <openssl/rand.h>
#include <openssl/err.h>

#include "FileUtilities.h"
#include "Unicode.h"
#include "../Application/IniFileSettings.h"

#else
#include <windows.h>
#include <wincrypt.h>
#include <dpapi.h>
#endif

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
#ifdef HM_PLATFORM_POSIX

   namespace
   {
      // AES-256 takes a 32-byte key; GCM's nonce is 96 bits by construction (any
      // other length is hashed down to 96 first, which buys nothing) and its tag is
      // kept at the full 128 bits rather than one of the truncations the mode
      // permits. The three lengths are the layout of the stored form:
      // nonce || ciphertext || tag, base64-encoded.
      const size_t KeyLength = 32;
      const size_t NonceLength = 12;
      const size_t TagLength = 16;

      // The dot keeps it out of a casual directory listing, and out of the
      // "every domain is a directory under this one" reading of the data folder.
      const char KeyFileName[] = ".hmailserver-secret-key";

      // Authenticated but not encrypted, so a blob is bound to this purpose and
      // this format version: a ciphertext produced by anything else under the same
      // key - there is nothing else, but the check costs nothing - fails its tag.
      // It plays the part the "hMailServer secret" description string plays in
      // the CryptProtectData call.
      const unsigned char AssociatedData[] = "hMailServer secret LINUX1";
      const size_t AssociatedDataLength = sizeof(AssociatedData) - 1;

      // Every DPAPI blob starts with a version word of 1 and the provider GUID
      // {DF9D8CD0-1501-11D1-8C7A-00C04FC297EB} in its little-endian byte layout.
      // It is the only way to recognise "this value was written by Windows" from
      // the value alone, which is what a PasswordEncryption=6 INI line carried
      // over from a Windows installation looks like: no prefix, just the blob.
      const unsigned char DpapiBlobHeader[] =
      {
         0x01, 0x00, 0x00, 0x00,
         0xD0, 0x8C, 0x9D, 0xDF, 0x01, 0x15, 0xD1, 0x11,
         0x8C, 0x7A, 0x00, 0xC0, 0x4F, 0xC2, 0x97, 0xEB
      };

      String key_file_override;

      // Reported once per process rather than once per secret: a database moved
      // from Windows holds one such value per route, fetch account and relaying
      // domain, and the first report says everything the tenth would.
      std::atomic<bool> dpapi_blob_reported(false);

      String
      LastErrnoText_()
      {
         String text = ::strerror(errno);
         return text;
      }

      String
      OpenSslErrorText_()
      {
         char buffer[256] = {};
         ERR_error_string_n(ERR_get_error(), buffer, sizeof(buffer));
         String text = buffer;
         return text;
      }

      // Makes the key file if it does not exist, and never replaces one that does.
      //
      // The bytes go to a temporary name first and reach the final name through
      // link(), which fails with EEXIST when the name is already taken - so two
      // processes protecting their first secret at the same moment (the server and
      // a --create-database run, say) end with one key file between them, whichever
      // won, and the loser reads that one. rename() would have let the loser's key
      // replace the winner's after the winner had already protected something with
      // it. A filesystem with no hard links refuses link() with EPERM or
      // EOPNOTSUPP; there the rename is used after a check that the name is free,
      // which narrows the window to one system call rather than closing it.
      //
      // 0600 is asked of open() and the umask can only tighten it; the mode is
      // checked again, by every reader, on every read, so a chmod after the fact
      // is caught too.
      bool
      CreateKeyFile_(const AnsiString &path, String &failure)
      {
         unsigned char key[KeyLength];
         if (RAND_bytes(key, (int) KeyLength) != 1)
         {
            failure = _T("the random number generator did not produce a key: ") + OpenSslErrorText_();
            return false;
         }

         AnsiString temporary = path;
         temporary.append(".writing-");
         temporary.append(std::to_string(::getpid()));

         int fd = ::open(temporary.c_str(), O_WRONLY | O_CREAT | O_EXCL | O_CLOEXEC, S_IRUSR | S_IWUSR);
         if (fd < 0)
         {
            String reason = LastErrnoText_();
            failure.Format(_T("the temporary file %s could not be created: %s"), String(temporary).c_str(), reason.c_str());
            OPENSSL_cleanse(key, sizeof(key));
            return false;
         }

         size_t written = 0;
         while (written < KeyLength)
         {
            ssize_t count = ::write(fd, key + written, KeyLength - written);
            if (count <= 0)
            {
               if (count < 0 && errno == EINTR)
                  continue;

               String reason = LastErrnoText_();
               failure.Format(_T("the key could not be written to %s: %s"), String(temporary).c_str(), reason.c_str());
               ::close(fd);
               ::unlink(temporary.c_str());
               OPENSSL_cleanse(key, sizeof(key));
               return false;
            }
            written += (size_t) count;
         }

         OPENSSL_cleanse(key, sizeof(key));

         // The key has to be on the disk before its name is, or a crash between the
         // two leaves a name with nothing behind it and every secret protected
         // under a key that was never stored.
         if (::fsync(fd) != 0)
         {
            String reason = LastErrnoText_();
            failure.Format(_T("the key could not be flushed to %s: %s"), String(temporary).c_str(), reason.c_str());
            ::close(fd);
            ::unlink(temporary.c_str());
            return false;
         }

         ::close(fd);

         if (::link(temporary.c_str(), path.c_str()) == 0 || errno == EEXIST)
         {
            ::unlink(temporary.c_str());
            return true;
         }

         if (errno == EPERM || errno == EOPNOTSUPP || errno == ENOTSUP || errno == EMLINK)
         {
            struct stat existing = {};
            if (::lstat(path.c_str(), &existing) == 0)
            {
               ::unlink(temporary.c_str());
               return true;
            }

            if (::rename(temporary.c_str(), path.c_str()) == 0)
               return true;
         }

         String reason = LastErrnoText_();
         failure.Format(_T("%s could not be given its name: %s"), String(path).c_str(), reason.c_str());
         ::unlink(temporary.c_str());
         return false;
      }

      // Reads the 32 bytes, refusing a file that anyone but its owner could read.
      // createIfMissing is true from Protect, whose first call is what makes the
      // key, and false from Unprotect: a secret that exists was protected under a
      // key that existed, so a missing file there means the key was lost - a
      // restore without it, most likely - and making a new one would turn "cannot
      // open" into "opens to garbage" for that secret and every one after it.
      bool
      LoadKey_(std::vector<unsigned char> &key, bool createIfMissing, const String &source)
      {
         const String keyPath = DataProtector::GetKeyFilePath();
         AnsiString narrowPath;
         Unicode::WideToMultiByte(keyPath, narrowPath);

         for (int attempt = 0; attempt < 2; attempt++)
         {
            // O_NOFOLLOW: a symbolic link left at this name would make the mode check
            // below a check of the wrong file.
            int fd = ::open(narrowPath.c_str(), O_RDONLY | O_CLOEXEC | O_NOFOLLOW);
            if (fd < 0)
            {
               if (errno == ENOENT && createIfMissing && attempt == 0)
               {
                  String failure;
                  if (!CreateKeyFile_(narrowPath, failure))
                  {
                     String message;
                     message.Format(_T("The stored-secret key file %s does not exist and could not be created: %s. ")
                        _T("No secret can be protected until it can be. The directory must exist and be writable ")
                        _T("by the account the server runs as; the file is made once, with mode 0600, and never rewritten."),
                        keyPath.c_str(), failure.c_str());
                     ErrorManager::Instance()->ReportError(ErrorManager::High, 6410, source, message);
                     return false;
                  }

                  continue;
               }

               const int openError = errno;
               String reason = LastErrnoText_();
               String message;
               if (openError == ENOENT)
                  message.Format(_T("The stored-secret key file %s does not exist, so a stored secret that was protected under ")
                     _T("it cannot be opened. If the data directory was restored from a backup, the key file has to be restored ")
                     _T("with it; without it every stored password has to be re-entered."), keyPath.c_str());
               else if (openError == ELOOP)
                  message.Format(_T("The stored-secret key file %s is a symbolic link, which is refused: the key must be a plain ")
                     _T("file of 32 bytes with mode 0600 at that name, so that the mode check is a check of the file itself."),
                     keyPath.c_str());
               else
                  message.Format(_T("The stored-secret key file %s could not be opened: %s. The file must be readable by the ")
                     _T("account the server runs as."), keyPath.c_str(), reason.c_str());
               ErrorManager::Instance()->ReportError(ErrorManager::High, 6412, source, message);
               return false;
            }

            struct stat status = {};
            if (::fstat(fd, &status) != 0 || !S_ISREG(status.st_mode))
            {
               ::close(fd);
               String message;
               message.Format(_T("The stored-secret key file %s is not a regular file. It must be a plain file of 32 bytes ")
                  _T("with mode 0600."), keyPath.c_str());
               ErrorManager::Instance()->ReportError(ErrorManager::High, 6412, source, message);
               return false;
            }

            if ((status.st_mode & (S_IRWXG | S_IRWXO)) != 0)
            {
               ::close(fd);
               String message;
               message.Format(_T("The stored-secret key file %s has mode %04o, which lets users other than its owner read ")
                  _T("the key that every stored password is protected under. It is refused until that is fixed: ")
                  _T("chmod 600 %s"),
                  keyPath.c_str(), (unsigned) (status.st_mode & 07777), keyPath.c_str());
               ErrorManager::Instance()->ReportError(ErrorManager::High, 6411, source, message);
               return false;
            }

            // One byte more than the key, so a file that is longer is seen to be
            // longer rather than truncated into a key it never was.
            unsigned char buffer[KeyLength + 1];
            size_t total = 0;
            while (total < sizeof(buffer))
            {
               ssize_t count = ::read(fd, buffer + total, sizeof(buffer) - total);
               if (count < 0 && errno == EINTR)
                  continue;
               if (count <= 0)
                  break;
               total += (size_t) count;
            }
            ::close(fd);

            if (total != KeyLength)
            {
               OPENSSL_cleanse(buffer, sizeof(buffer));
               String message;
               message.Format(_T("The stored-secret key file %s holds %d byte(s); a key this server wrote is exactly 32. ")
                  _T("It is not used. Restore the file from a backup, or delete it and re-enter every stored password."),
                  keyPath.c_str(), (int) total);
               ErrorManager::Instance()->ReportError(ErrorManager::High, 6412, source, message);
               return false;
            }

            key.assign(buffer, buffer + KeyLength);
            OPENSSL_cleanse(buffer, sizeof(buffer));
            return true;
         }

         return false;
      }

      bool
      IsDpapiBlob_(const AnsiString &raw)
      {
         return raw.GetLength() >= (int) sizeof(DpapiBlobHeader) &&
            memcmp(raw.c_str(), DpapiBlobHeader, sizeof(DpapiBlobHeader)) == 0;
      }
   }

   void
   DataProtector::SetKeyFileForTest(const String &path)
   {
      key_file_override = path;
   }

   String
   DataProtector::GetKeyFilePath()
   {
      if (!key_file_override.IsEmpty())
         return key_file_override;

      return FileUtilities::Combine(IniFileSettings::Instance()->GetDataDirectory(), KeyFileName);
   }

#endif

   bool
   DataProtector::Protect(const AnsiString &plainText, AnsiString &protectedBase64)
   {
      protectedBase64 = "";
#ifdef HM_PLATFORM_POSIX

      std::vector<unsigned char> key;
      if (!LoadKey_(key, true, "DataProtector::Protect"))
         return false;

      const size_t plainLength = (size_t) plainText.GetLength();
      std::vector<unsigned char> output(NonceLength + plainLength + TagLength);

      unsigned char *nonce = output.data();
      unsigned char *cipherText = output.data() + NonceLength;
      unsigned char *tag = output.data() + NonceLength + plainLength;

      // A nonce reused under one key is the one thing GCM does not survive; 96
      // random bits per secret, from the same generator that made the key, keeps
      // the reuse probability where the mode's analysis assumes it.
      bool ok = RAND_bytes(nonce, (int) NonceLength) == 1;

      EVP_CIPHER_CTX *ctx = ok ? EVP_CIPHER_CTX_new() : nullptr;
      ok = ok && ctx != nullptr;

      int length = 0;
      ok = ok && EVP_EncryptInit_ex(ctx, EVP_aes_256_gcm(), nullptr, nullptr, nullptr) == 1;
      ok = ok && EVP_CIPHER_CTX_ctrl(ctx, EVP_CTRL_GCM_SET_IVLEN, (int) NonceLength, nullptr) == 1;
      ok = ok && EVP_EncryptInit_ex(ctx, nullptr, nullptr, key.data(), nonce) == 1;
      ok = ok && EVP_EncryptUpdate(ctx, nullptr, &length, AssociatedData, (int) AssociatedDataLength) == 1;
      if (ok && plainLength > 0)
         ok = EVP_EncryptUpdate(ctx, cipherText, &length,
                 reinterpret_cast<const unsigned char*>(plainText.c_str()), (int) plainLength) == 1 &&
              (size_t) length == plainLength;
      ok = ok && EVP_EncryptFinal_ex(ctx, cipherText + plainLength, &length) == 1 && length == 0;
      ok = ok && EVP_CIPHER_CTX_ctrl(ctx, EVP_CTRL_GCM_GET_TAG, (int) TagLength, tag) == 1;

      if (ctx)
         EVP_CIPHER_CTX_free(ctx);
      OPENSSL_cleanse(key.data(), key.size());

      if (!ok)
      {
         String message = _T("AES-256-GCM refused to protect a stored secret, so the value was NOT stored: ") + OpenSslErrorText_();
         ErrorManager::Instance()->ReportError(ErrorManager::High, 6413, "DataProtector::Protect", message);
         return false;
      }

      protectedBase64 = Base64::Encode(reinterpret_cast<const char*>(output.data()), (int) output.size());
      return true;

#else

      DATA_BLOB input;
      input.pbData = reinterpret_cast<BYTE*>(const_cast<char*>(plainText.c_str()));
      input.cbData = static_cast<DWORD>(plainText.GetLength());

      DATA_BLOB output;
      output.pbData = nullptr;
      output.cbData = 0;

      // Machine-scope so the secret stays readable across the (potentially
      // different) accounts used by the service and the administration tools,
      // while remaining bound to this machine.
      if (!CryptProtectData(&input, L"hMailServer secret", nullptr, nullptr, nullptr, CRYPTPROTECT_LOCAL_MACHINE, &output))
         return false;

      protectedBase64 = Base64::Encode(reinterpret_cast<const char*>(output.pbData), static_cast<int>(output.cbData));

      if (output.pbData)
         LocalFree(output.pbData);

      return true;
#endif
   }

   bool
   DataProtector::Unprotect(const AnsiString &protectedBase64, AnsiString &plainText)
   {
      plainText = "";
#ifdef HM_PLATFORM_POSIX

      AnsiString raw = Base64::Decode(protectedBase64.c_str(), protectedBase64.GetLength());

      if (IsDpapiBlob_(raw))
      {
         bool expected = false;
         if (dpapi_blob_reported.compare_exchange_strong(expected, true))
         {
            ErrorManager::Instance()->ReportError(ErrorManager::High, 6414, "DataProtector::Unprotect",
               "A stored secret is a Windows DPAPI blob, which only the Windows machine that wrote it can open: "
               "the database or the INI was carried over from a Windows installation. The secret is intact there and "
               "unreadable here; re-enter it on this machine and it is stored under this machine's key file. "
               "Reported once; there may be more than one such value.");
         }
         return false;
      }

      if ((size_t) raw.GetLength() < NonceLength + TagLength)
         return false;

      std::vector<unsigned char> key;
      if (!LoadKey_(key, false, "DataProtector::Unprotect"))
         return false;

      const unsigned char *nonce = reinterpret_cast<const unsigned char*>(raw.c_str());
      const unsigned char *cipherText = nonce + NonceLength;
      const size_t cipherLength = (size_t) raw.GetLength() - NonceLength - TagLength;
      const unsigned char *tag = cipherText + cipherLength;

      std::vector<unsigned char> output(cipherLength + 1);

      EVP_CIPHER_CTX *ctx = EVP_CIPHER_CTX_new();
      bool ok = ctx != nullptr;

      int length = 0;
      ok = ok && EVP_DecryptInit_ex(ctx, EVP_aes_256_gcm(), nullptr, nullptr, nullptr) == 1;
      ok = ok && EVP_CIPHER_CTX_ctrl(ctx, EVP_CTRL_GCM_SET_IVLEN, (int) NonceLength, nullptr) == 1;
      ok = ok && EVP_DecryptInit_ex(ctx, nullptr, nullptr, key.data(), nonce) == 1;
      ok = ok && EVP_DecryptUpdate(ctx, nullptr, &length, AssociatedData, (int) AssociatedDataLength) == 1;
      if (ok && cipherLength > 0)
         ok = EVP_DecryptUpdate(ctx, output.data(), &length, cipherText, (int) cipherLength) == 1 &&
              (size_t) length == cipherLength;
      // The tag goes in before the final call, which is the call that checks it:
      // a wrong key, a flipped byte anywhere, or a blob from another purpose all
      // come out here as a plain "no", with the plaintext never released.
      ok = ok && EVP_CIPHER_CTX_ctrl(ctx, EVP_CTRL_GCM_SET_TAG, (int) TagLength, const_cast<unsigned char*>(tag)) == 1;
      ok = ok && EVP_DecryptFinal_ex(ctx, output.data() + cipherLength, &length) == 1;

      if (ctx)
         EVP_CIPHER_CTX_free(ctx);
      OPENSSL_cleanse(key.data(), key.size());

      if (!ok)
      {
         // Silent, as the Windows arm is silent: the callers treat an empty secret
         // as "re-enter this password" and report that in their own terms. An
         // OpenSSL error queue entry from the tag check is cleared so it cannot
         // surface under the next unrelated call.
         ERR_clear_error();
         OPENSSL_cleanse(output.data(), output.size());
         return false;
      }

      plainText.assign(reinterpret_cast<const char*>(output.data()), cipherLength);
      OPENSSL_cleanse(output.data(), output.size());
      return true;

#else

      AnsiString raw = Base64::Decode(protectedBase64.c_str(), protectedBase64.GetLength());
      if (raw.GetLength() == 0)
         return false;

      DATA_BLOB input;
      input.pbData = reinterpret_cast<BYTE*>(const_cast<char*>(raw.c_str()));
      input.cbData = static_cast<DWORD>(raw.GetLength());

      DATA_BLOB output;
      output.pbData = nullptr;
      output.cbData = 0;

      if (!CryptUnprotectData(&input, nullptr, nullptr, nullptr, nullptr, 0, &output))
         return false;

      plainText.assign(reinterpret_cast<const char*>(output.pbData), output.cbData);

      if (output.pbData)
         LocalFree(output.pbData);

      return true;
#endif
   }

   void
   DataProtectorTester::Test()
   {
      // Round-trip a secret and prove the protected form is neither the plaintext
      // nor trivially recoverable without the platform's key.
      const AnsiString secret = "S3cr3t-DB-p@ssw0rd \xC3\xA4\xC3\xB6"; // includes UTF-8 bytes

#ifdef HM_PLATFORM_POSIX

      // The same round-trip the Windows arm runs below, against a key file of the
      // test's own under the temporary directory: the real key is never read,
      // never created and never deleted by a self-test. The override is cleared
      // and the file removed whatever happens, including a throw.
      const String keyFile = FileUtilities::GetTempFileName();
      DataProtector::SetKeyFileForTest(keyFile);

      struct Restore
      {
         String file;
         ~Restore()
         {
            DataProtector::SetKeyFileForTest(_T(""));
            FileUtilities::DeleteFile(file);
         }
      } restore = { keyFile };

      AnsiString protectedBlob;
      if (!DataProtector::Protect(secret, protectedBlob))
         throw 0;

      if (protectedBlob.IsEmpty())
         throw 0;

      // The first Protect made the key file, and made it 0600.
      AnsiString narrowKeyFile;
      Unicode::WideToMultiByte(keyFile, narrowKeyFile);
      struct stat status = {};
      if (::stat(narrowKeyFile.c_str(), &status) != 0)
         throw 0;
      if (!S_ISREG(status.st_mode) || (status.st_mode & 07777) != 0600 || status.st_size != (off_t) KeyLength)
         throw 0;

      // The protected blob must not contain the plaintext.
      if (protectedBlob.Find(secret) != -1)
         throw 0;

      // A fresh nonce every time: the same secret protected twice is two
      // different blobs, and both open.
      AnsiString protectedAgain;
      if (!DataProtector::Protect(secret, protectedAgain))
         throw 0;
      if (protectedAgain == protectedBlob)
         throw 0;

      AnsiString recovered;
      if (!DataProtector::Unprotect(protectedBlob, recovered))
         throw 0;
      if (recovered != secret)
         throw 0;

      recovered = "";
      if (!DataProtector::Unprotect(protectedAgain, recovered) || recovered != secret)
         throw 0;

      // A corrupted blob must fail to unprotect, not merely fail to return the
      // secret: with an authenticating cipher the failure is the guarantee, so it
      // is asserted outright where the DPAPI arm can only assert the weaker form.
      // The last bytes of the base64 are the tag; the first are the nonce; a byte
      // in the middle is ciphertext. All three must be caught.
      {
         std::string t(protectedBlob.c_str(), protectedBlob.GetLength());
         const size_t positions[] = { 0, t.size() / 2, t.size() - 2 };
         for (size_t position : positions)
         {
            std::string tampered = t;
            tampered[position] = (tampered[position] == 'A') ? 'B' : 'A';
            AnsiString shouldFail;
            if (DataProtector::Unprotect(AnsiString(tampered.c_str()), shouldFail))
               throw 0;
            if (!shouldFail.IsEmpty())
               throw 0;
         }
      }

      // A blob that is too short to hold a nonce and a tag is refused without
      // being fed to the cipher.
      {
         AnsiString shouldFail;
         if (DataProtector::Unprotect(Base64::Encode("short", 5), shouldFail))
            throw 0;
      }

      // A key file readable by others is refused, for reading and for writing, and
      // is accepted again the moment the mode is fixed - no restart, no cache.
      if (::chmod(narrowKeyFile.c_str(), 0644) != 0)
         throw 0;
      {
         AnsiString shouldFail;
         if (DataProtector::Protect(secret, shouldFail))
            throw 0;
         if (DataProtector::Unprotect(protectedBlob, shouldFail))
            throw 0;
      }
      if (::chmod(narrowKeyFile.c_str(), 0600) != 0)
         throw 0;
      recovered = "";
      if (!DataProtector::Unprotect(protectedBlob, recovered) || recovered != secret)
         throw 0;

      // A Windows DPAPI blob is recognised as one and refused: it is the value a
      // database moved from Windows holds.
      {
         AnsiString dpapiLike;
         dpapiLike.assign(reinterpret_cast<const char*>(DpapiBlobHeader), sizeof(DpapiBlobHeader));
         dpapiLike.append("the rest of a blob only Windows could open");
         AnsiString shouldFail;
         if (DataProtector::Unprotect(Base64::Encode(dpapiLike.c_str(), dpapiLike.GetLength()), shouldFail))
            throw 0;
      }

#else

      AnsiString protectedBlob;
      if (!DataProtector::Protect(secret, protectedBlob))
         throw 0;

      if (protectedBlob.IsEmpty())
         throw 0;

      // The protected blob must not contain the plaintext.
      if (protectedBlob.Find(secret) != -1)
         throw 0;

      AnsiString recovered;
      if (!DataProtector::Unprotect(protectedBlob, recovered))
         throw 0;

      if (recovered != secret)
         throw 0;

      // A corrupted blob must fail to unprotect rather than return garbage.
      if (protectedBlob.GetLength() > 4)
      {
         std::string t(protectedBlob.c_str(), protectedBlob.GetLength());
         size_t pos = t.size() - 2;
         t[pos] = (t[pos] == 'A') ? 'B' : 'A';
         AnsiString tampered = t.c_str();
         AnsiString shouldFail;
         DataProtector::Unprotect(tampered, shouldFail);
         // We don't assert failure here (DPAPI may tolerate some base64 noise);
         // we only assert it never silently returns the original secret.
         if (shouldFail == secret)
            throw 0;
      }
#endif
   }
}

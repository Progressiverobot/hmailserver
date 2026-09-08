// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "stdafx.h"
#include "Unicode.h"

#ifdef HM_PLATFORM_POSIX
#include <iconv.h>
#include <langinfo.h>
#include <cerrno>
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
      // Windows code page identifier to the name iconv knows the same character
      // set by. Only the code pages this server names are here - the table in
      // CodePages::Initialize is the list, and 0 is CP_ACP, "whatever this machine
      // is set to", which nl_langinfo answers. A code page not in this table is
      // reported as a failed conversion rather than guessed at: a mail server that
      // silently decodes a header in the wrong character set produces mojibake
      // that nobody can trace back to the guess.
      const char *IconvNameForCodePage(unsigned int codePage)
      {
         switch (codePage)
         {
         case 0:     return ::nl_langinfo(CODESET);
         case 950:   return "BIG5";
         case 1250:  return "CP1250";
         case 1251:  return "CP1251";
         case 1252:  return "CP1252";
         case 1253:  return "CP1253";
         case 1254:  return "CP1254";
         case 1255:  return "CP1255";
         case 1256:  return "CP1256";
         case 1257:  return "CP1257";
         case 1258:  return "CP1258";
         case 20127: return "ANSI_X3.4-1968";
         case 50221: return "ISO-2022-JP-2";
         case 65000: return "UTF-7";
         case 65001: return "UTF-8";
         default:    return 0;
         }
      }

      // One conversion, in the two modes the Win32 pair has: measure, or fill.
      //
      // A fresh iconv descriptor per call, deliberately. A descriptor carries
      // shift state for the stateful encodings (ISO-2022-JP is one of the code
      // pages above), so a shared one would carry the tail of somebody else's
      // string into this conversion - and this is called from every connection
      // thread at once.
      //
      // unitSize is the size of one unit of the OUTPUT: iconv counts bytes and the
      // Win32 functions count wide characters in one direction and bytes in the
      // other, so the byte count is divided by it on the way out.
      int ConvertWithIconv(const char *fromName, const char *toName,
                           const char *source, size_t sourceBytes,
                           char *destination, size_t destinationBytes,
                           size_t unitSize)
      {
         if (fromName == 0 || toName == 0 || source == 0)
            return 0;

         iconv_t descriptor = ::iconv_open(toName, fromName);

         if (descriptor == (iconv_t) -1)
            return 0;

         char *inputPosition = (char *) source;
         size_t inputRemaining = sourceBytes;
         int answer = 0;

         if (destination == 0 || destinationBytes == 0)
         {
            // Measure. iconv has no "how long would this be" call, so the text is
            // converted into a scratch buffer that is emptied as it fills and only
            // the total is kept. E2BIG is that buffer filling up, which is the
            // expected outcome here rather than an error; anything else is not.
            char scratch[512];
            size_t total = 0;

            while (inputRemaining > 0)
            {
               char *outputPosition = scratch;
               size_t outputRemaining = sizeof(scratch);

               const size_t result = ::iconv(descriptor, &inputPosition, &inputRemaining, &outputPosition, &outputRemaining);

               total += sizeof(scratch) - outputRemaining;

               if (result == (size_t) -1 && errno != E2BIG)
               {
                  ::iconv_close(descriptor);
                  return 0;
               }
            }

            answer = (int) (total / unitSize);
         }
         else
         {
            char *outputPosition = destination;
            size_t outputRemaining = destinationBytes;

            const size_t result = ::iconv(descriptor, &inputPosition, &inputRemaining, &outputPosition, &outputRemaining);

            if (result == (size_t) -1)
            {
               // Invalid input, an incomplete sequence at the end, or a
               // destination too small. Win32 answers 0 for all three.
               ::iconv_close(descriptor);
               return 0;
            }

            answer = (int) ((destinationBytes - outputRemaining) / unitSize);
         }

         ::iconv_close(descriptor);

         return answer;
      }
   }

   int
   Unicode::FromCodePage(unsigned int codePage, const char *source, int sourceLength, wchar_t *destination, int destinationLength)
   {
      if (source == 0)
         return 0;

      const size_t sourceBytes = sourceLength < 0 ? ::strlen(source) + 1 : (size_t) sourceLength;

      return ConvertWithIconv(IconvNameForCodePage(codePage), "WCHAR_T",
                              source, sourceBytes,
                              (char *) destination, (size_t) destinationLength * sizeof(wchar_t),
                              sizeof(wchar_t));
   }

   int
   Unicode::ToCodePage(unsigned int codePage, const wchar_t *source, int sourceLength, char *destination, int destinationLength)
   {
      if (source == 0)
         return 0;

      const size_t sourceUnits = sourceLength < 0 ? ::wcslen(source) + 1 : (size_t) sourceLength;

      return ConvertWithIconv("WCHAR_T", IconvNameForCodePage(codePage),
                              (const char *) source, sourceUnits * sizeof(wchar_t),
                              destination, (size_t) destinationLength,
                              1);
   }
#endif

   Unicode::Unicode()
   {

   }

   Unicode::~Unicode()
   {

   }

   AnsiString 
   Unicode::ToANSI(const String &sString)
   {
      size_t i;
      size_t nLen = (wcslen(sString) + 1) << 1;
      char *pAnsiString = new char [nLen];
#ifdef HM_PLATFORM_POSIX
      // wcstombs is wcstombs_s with the answer in the return value rather than in
      // an out-parameter, and without the guarantee of termination. It answers
      // (size_t) -1 for a character the locale cannot represent and leaves the
      // buffer unspecified, and it does not terminate a result that exactly fills
      // the buffer - so both are terminated by hand, which is what wcstombs_s
      // does for its caller.
      i = ::wcstombs(pAnsiString, sString, nLen);

      if (i == (size_t) -1)
         pAnsiString[0] = '\0';
      else if (i >= nLen)
         pAnsiString[nLen - 1] = '\0';
#else
      wcstombs_s(&i, pAnsiString, nLen, sString, nLen);
#endif
      AnsiString retval = pAnsiString;
      delete [] pAnsiString;

      return retval;
   }

   bool 
   Unicode::WideToMultiByte(const String &sInput, AnsiString &sOutput)
   {
      int iInputLength = sInput.GetLength();

#ifdef HM_PLATFORM_POSIX
      // 65001 is CP_UTF8. See Unicode::ToCodePage: the same two calls, measure
      // then fill, with the same length rules and the same 0 for a failure.
      int nNeedSize = ToCodePage(65001, sInput, iInputLength, NULL, 0);
#else
      int nNeedSize = WideCharToMultiByte(CP_UTF8, 0, sInput, iInputLength, NULL, 0, NULL, NULL );
#endif

      if (nNeedSize == 0)
      {
         // Either the input was empty or the conversion failed. An empty input
         // is a valid (empty) result; anything else is an error.
         sOutput = "";
         return iInputLength == 0;
      }

#ifdef HM_PLATFORM_POSIX
      int nWritten = ToCodePage(65001, sInput, iInputLength, sOutput.GetBuffer(nNeedSize), nNeedSize);
#else
      int nWritten = WideCharToMultiByte( CP_UTF8, 0, sInput, iInputLength, sOutput.GetBuffer(nNeedSize), nNeedSize, NULL, NULL );
#endif
      if (nWritten == 0)
         return false;

      // GetBuffer resized the string to the requested capacity, so trim it back
      // to the exact number of bytes written. Otherwise the result carries a
      // trailing padding byte that callers measuring GetLength() (for example
      // DPAPI protection of stored secrets) would erroneously include.
      sOutput.ReleaseBuffer(nWritten);

      return true;
   }

   bool 
   Unicode::MultiByteToWide(const AnsiString &sInput, String &sOutput)
   {
      int iInputLength = sInput.GetLength();

      // Empty in, empty out, and answered here rather than below: MultiByteToWideChar
      // returns 0 for cbMultiByte == 0, which is indistinguishable from its error
      // return, so an empty string used to look like a failed conversion - and left
      // sOutput holding whatever it held before.
      if (iInputLength == 0)
      {
         sOutput.Empty();
         return true;
      }

#ifdef HM_PLATFORM_POSIX
      // 65001 is CP_UTF8. See Unicode::FromCodePage.
      int nNeedSize = FromCodePage(65001, sInput, iInputLength, NULL, 0);
#else
      int nNeedSize = MultiByteToWideChar( CP_UTF8, 0, sInput, iInputLength, NULL, 0);
#endif

      if (nNeedSize == 0)
         return false;

      // resize, not GetBuffer alone. CStdStr::GetBuf only ever GROWS -
      // "if (size() < nMinLen) resize(nMinLen)" - and nothing here called
      // ReleaseBuffer to set the final length, so converting a SHORTER value into a
      // String that already held a longer one left the old tail in place.
      //
      // That is not theoretical. SMTPConnection::ProtocolAUTH_ passes the session
      // members username_ and password_ into DecodeSaslPlain, and
      // ResetLoginCredentials_ clears username_ but not password_. So on one
      // connection: AUTH PLAIN with password "LongPassword123" fails, the client
      // retries with "x", and the server validated "xongPassword123" - a credential
      // the client never sent. The user could never authenticate on that connection,
      // and every mangled attempt fed RegisterFailedLogin towards an auto-ban of a
      // legitimate address.
      sOutput.resize(nNeedSize);

#ifdef HM_PLATFORM_POSIX
      if( FromCodePage(65001, sInput, iInputLength, sOutput.GetBuffer(nNeedSize), nNeedSize) == 0 )
#else
      if( MultiByteToWideChar( CP_UTF8, 0, sInput, iInputLength, sOutput.GetBuffer(nNeedSize), nNeedSize ) == 0 )
#endif
         return false;

      return true;
   }

   std::string
   Unicode::ToUtf16Le(const std::wstring &text)
   {
      std::string bytes;
      bytes.reserve(text.size() * 2);

      for (size_t index = 0; index < text.size(); index++)
      {
         unsigned int codePoint = (unsigned int) text[index];

         if (codePoint >= 0x10000 && codePoint <= 0x10FFFF)
         {
            // Only reachable where a wchar_t can hold such a value, which is
            // where it is four bytes. On Windows every character is already a
            // code unit and the loop below writes it as it is - a surrogate half
            // included, so the bytes are exactly the bytes of the String.
            codePoint -= 0x10000;
            const unsigned int high = 0xD800 + (codePoint >> 10);
            const unsigned int low = 0xDC00 + (codePoint & 0x3FF);

            bytes.push_back((char) (high & 0xFF));
            bytes.push_back((char) ((high >> 8) & 0xFF));
            bytes.push_back((char) (low & 0xFF));
            bytes.push_back((char) ((low >> 8) & 0xFF));
         }
         else
         {
            // A value above U+10FFFF is not a character and has no UTF-16 form.
            // No decoder here produces one, so this is a defence against memory
            // that was never a String, and it is written as the replacement
            // character rather than as its low sixteen bits, which would be some
            // other character altogether.
            if (codePoint > 0x10FFFF)
               codePoint = 0xFFFD;

            bytes.push_back((char) (codePoint & 0xFF));
            bytes.push_back((char) ((codePoint >> 8) & 0xFF));
         }
      }

      return bytes;
   }

   std::wstring
   Unicode::FromUtf16Le(const unsigned char *bytes, size_t byteCount)
   {
      std::wstring text;

      if (bytes == 0)
         return text;

      text.reserve(byteCount / 2);

      // Whether a surrogate pair can be joined into one wchar_t, decided by the
      // platform rather than by the input: on Windows a wchar_t is a UTF-16 code
      // unit and the pair IS the character, so the two units are kept as they
      // are and the result is what a copy of the bytes would have been.
      const bool joinsPairs = sizeof(wchar_t) >= 4;

      size_t index = 0;

      // A trailing odd byte is not a code unit and is left out, which is what
      // dividing the byte count by two always did.
      while (index + 1 < byteCount)
      {
         unsigned int unit = (unsigned int) bytes[index] | ((unsigned int) bytes[index + 1] << 8);
         index += 2;

         if (joinsPairs && unit >= 0xD800 && unit <= 0xDBFF && index + 1 < byteCount)
         {
            const unsigned int low = (unsigned int) bytes[index] | ((unsigned int) bytes[index + 1] << 8);

            if (low >= 0xDC00 && low <= 0xDFFF)
            {
               unit = 0x10000 + ((unit - 0xD800) << 10) + (low - 0xDC00);
               index += 2;
            }
         }

         text.push_back((wchar_t) unit);
      }

      return text;
   }

   unsigned char*
   Unicode::CharMoveNext(unsigned char* input, bool utf8)
   {
      if (utf8)
      {
         unsigned char* string = input;
         string++;
         while ((*string & 0xc0) == 0x80)
            string++;

         return string;
      }
      else
      {
         return input + 1;
      }
   }

}
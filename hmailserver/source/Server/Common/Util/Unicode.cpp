// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#include "stdafx.h"
#include "Unicode.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{

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
      wcstombs_s(&i, pAnsiString, nLen, sString, nLen);
      AnsiString retval = pAnsiString;
      delete [] pAnsiString;

      return retval;
   }

   bool 
   Unicode::WideToMultiByte(const String &sInput, AnsiString &sOutput)
   {
      int iInputLength = sInput.GetLength();

      int nNeedSize = WideCharToMultiByte(CP_UTF8, 0, sInput, iInputLength, NULL, 0, NULL, NULL );

      if (nNeedSize == 0)
      {
         // Either the input was empty or the conversion failed. An empty input
         // is a valid (empty) result; anything else is an error.
         sOutput = "";
         return iInputLength == 0;
      }

      int nWritten = WideCharToMultiByte( CP_UTF8, 0, sInput, iInputLength, sOutput.GetBuffer(nNeedSize), nNeedSize, NULL, NULL );
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

      int nNeedSize = MultiByteToWideChar( CP_UTF8, 0, sInput, iInputLength, NULL, 0);

      if( MultiByteToWideChar( CP_UTF8, 0, sInput, iInputLength, sOutput.GetBuffer(nNeedSize), nNeedSize ) == 0 )
         return false;

      return true;
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
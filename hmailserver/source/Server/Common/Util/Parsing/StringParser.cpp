// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "stdafx.h"
#include <iosfwd>

#include "StringParser.h"

#include "../RegularExpression.h"
#include "../../MIME/MimeCode.h"
#include "../Unicode.h"
#include <boost/lexical_cast.hpp>

// NormalizeString (NFKC) for SASLprep lives in Normaliz.lib.
#pragma comment(lib, "Normaliz.lib")

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{

   StringParser::StringParser()
   {

   }

   StringParser::~StringParser()
   {

   }

   String
   StringParser::ExtractDomain(const String &sEMailAddress)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Extracts the domain from an email address string. (the part after @)
   //---------------------------------------------------------------------------()
   {
      int iAtSign = sEMailAddress.ReverseFind(_T("@"));
      String sDomain = sEMailAddress.Mid(iAtSign+1);
      return sDomain;
   }

   String
   StringParser::ExtractAddress(const String &sEMailAddress)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Extracts the adress from an email address string. (the part before @)
   //---------------------------------------------------------------------------()
   {
      int iAtSign = sEMailAddress.ReverseFind(_T("@"));
      String sDomain = sEMailAddress.Mid(0, iAtSign);
      return sDomain;
   }

   bool
   StringParser::IsValidEmailAddress(const String &sEmailAddress)
   {
      // RFC 5321 Section 4.5.3.1.3: the maximum total length of a reverse-path
      // or forward-path is 256 octets, which limits an email address to 254 characters.
      const int maxEmailAddressLength = 254;
      if (sEmailAddress.GetLength() > maxEmailAddressLength)
         return false;

      // Note: RFC 5321 Section 4.5.3.1.1 limits the local part to 64 octets, but we
      // intentionally do not enforce this to maintain backwards compatibility with
      // existing accounts that have longer local parts.
      //
      // Original: ^(("[^<>@\\]+")|(?!\.|.*\.(\.|@))[^<> @\\"]+)@(\[([0-9]{1,3}\.){3}[0-9]{1,3}\]|\[IPv6:(?:[A-Fa-f0-9]{1,4}:){7}[A-Fa-f0-9]{1,4}\]|(?=.{1,255}$)((?!-|\.)[a-zA-Z0-9-]{0,62}[a-zA-Z0-9])(|\.(?!-|\.)[a-zA-Z0-9-]{0,62}[a-zA-Z0-9]){1,126})$
      //
      // Conversion:
      // 1) Replace \ with \\
      // 2) Replace " with \"

      String regularExpression = "^((\"[^<>@\\\\]+\")|(?!\\.|.*\\.(\\.|@))[^<> @\\\\\"]+)@(\\[([0-9]{1,3}\\.){3}[0-9]{1,3}\\]|\\[IPv6:(?:[A-Fa-f0-9]{1,4}:){7}[A-Fa-f0-9]{1,4}\\]|(?=.{1,255}$)((?!-|\\.)[a-zA-Z0-9-]{0,62}[a-zA-Z0-9])(|\\.(?!-|\\.)[a-zA-Z0-9-]{0,62}[a-zA-Z0-9]){1,126})$";

      RegularExpression regexpEvaluator;
      return regexpEvaluator.TestExactMatch(regularExpression, sEmailAddress);
   }

   bool
   StringParser::ContainsNonAscii(const String &sValue)
   {
      const int length = sValue.GetLength();
      for (int i = 0; i < length; i++)
      {
         if (sValue.GetAt(i) > 127 || sValue.GetAt(i) < 0)
            return true;
      }

      return false;
   }

   bool
   StringParser::IsValidEmailAddress(const String &sEmailAddress, bool allowInternationalized)
   {
      // The strict ASCII validator handles everything when internationalization is
      // not requested, or when the address is plain ASCII (so existing behaviour and
      // the ASCII test corpus are untouched).
      if (!allowInternationalized || !ContainsNonAscii(sEmailAddress))
         return IsValidEmailAddress(sEmailAddress);

      // RFC 6531/6532: validate an internationalized (UTF-8) address. The strict
      // regex rejects non-ASCII in the domain, so apply a structural check that
      // permits Unicode letters in both the local-part and the domain while still
      // enforcing the basic shape and length limits.
      const int maxEmailAddressLength = 254;
      if (sEmailAddress.GetLength() > maxEmailAddressLength)
         return false;

      int atPosition = sEmailAddress.ReverseFind('@');
      if (atPosition <= 0 || atPosition == sEmailAddress.GetLength() - 1)
         return false;

      String localPart = sEmailAddress.Left(atPosition);
      String domainPart = sEmailAddress.Mid(atPosition + 1);

      // Local-part: reject control characters and the characters that delimit an
      // SMTP path. A quoted local-part ("...") may contain spaces; otherwise spaces
      // are not allowed.
      bool quotedLocalPart = localPart.GetLength() >= 2 && localPart.StartsWith(_T("\"")) && localPart.EndsWith(_T("\""));
      const int localLength = localPart.GetLength();
      for (int i = 0; i < localLength; i++)
      {
         wchar_t c = localPart.GetAt(i);
         if (c < 0x20)
            return false;
         if (c == '<' || c == '>')
            return false;
         if (c == ' ' && !quotedLocalPart)
            return false;
      }

      // Domain: a sequence of dot-separated labels. Each label must be non-empty,
      // must not start or end with a hyphen, and may contain ASCII letters/digits/
      // hyphen or any non-ASCII (U-label) character. An ASCII-only domain is handed
      // to the strict domain validator.
      if (!ContainsNonAscii(domainPart))
         return IsValidDomainName(domainPart);

      std::vector<String> labels = StringParser::SplitString(domainPart, _T("."));
      if (labels.empty())
         return false;

      for (const String &label : labels)
      {
         const int labelLength = label.GetLength();
         if (labelLength == 0)
            return false;
         if (label.StartsWith(_T("-")) || label.EndsWith(_T("-")))
            return false;

         for (int i = 0; i < labelLength; i++)
         {
            wchar_t c = label.GetAt(i);
            bool isAsciiLetterDigit = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');
            bool isHyphen = (c == '-');
            bool isNonAscii = (c > 127 || c < 0);
            if (!isAsciiLetterDigit && !isHyphen && !isNonAscii)
               return false;
         }
      }

      return true;
   }

   bool
   StringParser::IsValidDomainName(const String &sDomainName)
   {
      // Original: ^(\[([0-9]{1,3}\.){3}[0-9]{1,3}\]|\[IPv6:(?:[A-Fa-f0-9]{1,4}:){7}[A-Fa-f0-9]{1,4}\]|(?=.{1,255}$)((?!-|\.)[a-zA-Z0-9-]{0,62}[a-zA-Z0-9])(|\.(?!-|\.)[a-zA-Z0-9-]{0,62}[a-zA-Z0-9]){1,126})$
      // Conversion:
      // 1) Replace \ with \\
      // 2) Replace " with \"

      String regularExpression = "^(\\[([0-9]{1,3}\\.){3}[0-9]{1,3}\\]|\\[IPv6:(?:[A-Fa-f0-9]{1,4}:){7}[A-Fa-f0-9]{1,4}\\]|(?=.{1,255}$)((?!-|\\.)[a-zA-Z0-9-]{0,62}[a-zA-Z0-9])(|\\.(?!-|\\.)[a-zA-Z0-9-]{0,62}[a-zA-Z0-9]){1,126})$";

      RegularExpression regexpEvaluator;
      return regexpEvaluator.TestExactMatch(regularExpression, sDomainName);
   }

   
   std::vector<String>
   StringParser::SplitString(const String &sInput, const String &sSeperators)
   {
      // Previously, this code used boost::tokenizer to split
      // the contents of string, but I did some tests and it
      // showed that the below code was 50% faster. Not so 
      // unexpected since tokenizer is much more advanced.

      std::vector<String> vecResult;
      int iBeginning = 0;
      int iEnd = sInput.Find(sSeperators);

      if (iEnd == -1)
      {
         // The separator was not found in the string. 
         // We should put the entire string in the result.
         if (!sInput.IsEmpty())
            vecResult.push_back(sInput);

      }

      int iSeperatorLen = sSeperators.GetLength();

      while (iEnd >= 0)
      {
         int iSubStrLength = iEnd - iBeginning;
         
         String sSubString;
         sSubString = sInput.Mid(iBeginning, iSubStrLength);
         
         vecResult.push_back(sSubString);

         // Skip to the position where the next substring
         // can start
         iBeginning = iEnd + iSeperatorLen;
         iEnd = sInput.Find(sSeperators, iBeginning);   
      }

      if (iBeginning > 0)
      {
         String sSubString = sInput.Mid(iBeginning);
         if (!sSubString.IsEmpty())
            vecResult.push_back(sSubString);
      }

      return vecResult;
    
  
   }

   std::vector<AnsiString>
   StringParser::SplitString(const AnsiString &sInput, const AnsiString &sSeperators)
   {
      std::vector<AnsiString> vecResult;
      int iBeginning = 0;
      int iEnd = sInput.Find(sSeperators);

      if (iEnd == -1)
      {
         // The separator was not found in the string. 
         // We should put the entire string in the result.
         if (!sInput.IsEmpty())
            vecResult.push_back(sInput);

      }

      int iSeperatorLen = sSeperators.GetLength();

      while (iEnd >= 0)
      {
         int iSubStrLength = iEnd - iBeginning;

         String sSubString;
         sSubString = sInput.Mid(iBeginning, iSubStrLength);

         vecResult.push_back(sSubString);

         // Skip to the position where the next substring
         // can start
         iBeginning = iEnd + iSeperatorLen;
         iEnd = sInput.Find(sSeperators, iBeginning);   
      }

      if (iBeginning > 0)
      {
         String sSubString = sInput.Mid(iBeginning);
         if (!sSubString.IsEmpty())
            vecResult.push_back(sSubString);
      }

      return vecResult;


   }

   String
   StringParser::JoinVector(const std::vector<String> &sVector, const String &sSeperator)
   {
      std::vector<String>::const_iterator iterVec = sVector.begin();
      std::vector<String>::const_iterator iterEnd = sVector.end();

      String result;

      for (; iterVec != iterEnd; iterVec++)
      {
         result += (*iterVec);

         if (iterVec + 1 != iterEnd)
            result += sSeperator;
      }     
   
      return result;


   }


   std::vector<String>
   StringParser::GetAllButFirst(std::vector<String> sInput)
   {
      std::vector<String> vecResult;
      auto iterCur = sInput.begin();

      if (iterCur == sInput.end())
         return vecResult;

      iterCur++;

      while (iterCur != sInput.end())
      {
         
         vecResult.push_back(*iterCur);   

         iterCur++;
      }

      return vecResult;

   }

   bool
   StringParser::ValidateString(const String &sString, const String &sAllowedChars)
   {
      for (int i = 0; i < sString.GetLength(); i++)
      {
         if (sAllowedChars.Find(sString.GetAt(i)) < 0)
            return false;
      }

      return true;   
   }

   bool
   StringParser::AnyOfCharsExists_(const String &sChars, const String &sLookIn)
   {

      for (int i = 0; i < sChars.GetLength(); i++)
      {
         if (sLookIn.Find(sChars.GetAt(i)) >= 0)
            return true;
      }
      return false;
   }

   int _wildcmp(const wchar_t *wild, const wchar_t *string, int recursions)
   {
      const int maxRecursions = 300;

      recursions++;

      if (recursions > maxRecursions)
         return 0;

      if(*wild == *string)
         return '\0' == *string || _wildcmp(++wild, ++string, recursions);

      if('\0' == *string)
         return '*' == *wild && _wildcmp(++wild, string, recursions);

      switch(*wild)
      {
      case '?':
         return _wildcmp(++wild, ++string, recursions);

      case '*':
         wild++;

         if('\0' == *wild)
            return 1;

         while(*string != '\0')
            if(_wildcmp(wild, string++, recursions))
               return 1;

      default:
         return 0;
      }
   }

   bool
   StringParser::WildcardMatchNoCase(const String &sWildcard, const String &sString)
   {
      String sLowerWild = sWildcard;
      String sLowerStr = sString;

      sLowerWild.ToLower();
      sLowerStr.ToLower();

      return WildcardMatch(sLowerWild, sLowerStr);
   }


   bool
   StringParser::WildcardMatch(const String &pattern, const String &value)
   {
      // Convert the pattern to a regular expression.
      String regularExpression;

      for (int i = 0; i < pattern.GetLength(); i++)
      {
         wchar_t c = pattern[i];

         switch (c)
         {
            case '\\':
            case '|':
            case '.':
            case '^':
            case '$':
            case '+':
            case '(':
            case ')':
            case '[':
            case ']':
            case '{':
            case '}':
               regularExpression.append(_T("\\"));
               regularExpression += c;
               break;
            case '*':
               regularExpression.append(_T(".*"));
               break;
            case '?':
               regularExpression.append(_T("."));
               break;
            default:
               regularExpression += c;
               break;

         }
      }

      RegularExpression regexpEvaluator;
      bool result = regexpEvaluator.TestExactMatch(regularExpression, value);
      return result;
   }

   String 
   StringParser::IntToString(int lTheInt)
   {
      return boost::lexical_cast<std::string>(lTheInt);
   }

   String 
   StringParser::IntToString(unsigned int lTheInt)
   {
      return boost::lexical_cast<std::string>(lTheInt);
   }

   String 
   StringParser::IntToString(__int64 lTheInt)
   {
      String sRetVal;
      sRetVal.Format(_T("%I64d"), lTheInt);
      return sRetVal;
   }

   void
   StringParser::Base64Decode(const String &sInput, String &sOutput)
   {

      if (sInput.GetLength() == 0)
      {
         sOutput.Empty();
         return;
      }

      AnsiString sInputStr = sInput;

      MimeCodeBase64 DeCoder;
      DeCoder.AddLineBreak(false);
      DeCoder.SetInput(sInputStr, sInputStr.GetLength(), false);
      
      AnsiString output;
      DeCoder.GetOutput(output);

      int length = output.GetLength();
      // Since we're going to store the result in
      // a normal StdString, we can't return null
      // characters.
      for (int i = 0; i < length; i++)
      {
         if (output[i] == 0)
            output[i] = '\t';
      }

      sOutput = output;
   }

   void
   StringParser::Base64Encode(const String &sInput, String &sOutput)
   {
      if (sInput.GetLength() == 0)
      {
         sOutput.Empty();
         return;
      }
         
      AnsiString sInputStr = sInput;
      
      MimeCodeBase64 Coder;
      Coder.SetInput(sInputStr, sInputStr.GetLength(), true);
      Coder.AddLineBreak(false);
      
      AnsiString output;
      Coder.GetOutput(output);

      sOutput = output;
   }

   bool
   StringParser::IsNumeric(const String &sInput)
   {

      String sNumbers = "1234567890";
      int l = sInput.GetLength();
      for (int i = 0; i < l; i++)
      {
         if (sNumbers.Find(sInput.GetAt(i)) < 0)
            return false;
      }

      return true;
   }


   bool 
   StringParser::IsValidIPAddress(const String &sAddress)
   {
      IPAddress address;
      return address.TryParse(sAddress, false);
   }

   bool 
   StringParser::TryParseInt(const std::string &str, int &value)
   {
      try
      {
         value = boost::lexical_cast<int>(str);
         return true;
      }
      catch (boost::bad_lexical_cast&)
      {
         return false;
      }
      
   }

   char*
   StringParser::Search(const char *haystack, size_t haystackSize, const char *needle)
   {
      if (haystack == 0 || needle == 0)
         return 0;

      size_t needleSize = strlen(needle);

      for (size_t haystackIndex = 0; haystackIndex < haystackSize; haystackIndex++)
      {
         size_t remainingHaystackSize = haystackSize - haystackIndex;

         // If the string we're searching for is longer than the string
         // we're searching in, there's no point in performing the search.
         if (needleSize > remainingHaystackSize)
            return 0;
         
         const char *currentHaystackPosition = haystack + haystackIndex;

         if (memcmp(currentHaystackPosition, needle, needleSize) == 0)
            return (char*) currentHaystackPosition;
      }

      return 0;
   }

   // Removes all duplicate items from the items collection.
   void 
   StringParser::RemoveDuplicateItems(std::vector<String> &items)
   {

      // Remove duplicate names.
      auto iter = items.begin();
      std::set<String> duplicateCheck;

      while (iter != items.end())
      {
         String name = (*iter);
         if (duplicateCheck.find(name) != duplicateCheck.end())
         {
            // We found a duplicate. Remove it.
            iter = items.erase(iter);
         }
         else
         {
            // This is not a duplicate. Move to next.
            iter++;

            duplicateCheck.insert(name);
         }
      }
   }

   // ---------------------------------------------------------------------------
   // SASLprep (RFC 4013) helper tables / routines.
   //
   // Link the Windows normalisation API used for the NFKC step.
   // ---------------------------------------------------------------------------
   namespace
   {
      struct CpRange { unsigned int lo; unsigned int hi; };

      bool InRanges(unsigned int cp, const CpRange *ranges, size_t count)
      {
         for (size_t i = 0; i < count; i++)
            if (cp >= ranges[i].lo && cp <= ranges[i].hi)
               return true;
         return false;
      }

      // RFC 3454 B.1 - commonly mapped to nothing.
      bool SaslMapToNothing(unsigned int cp)
      {
         static const CpRange ranges[] = {
            {0x00AD, 0x00AD}, {0x034F, 0x034F}, {0x1806, 0x1806},
            {0x180B, 0x180D}, {0x200B, 0x200D}, {0x2060, 0x2060},
            {0xFE00, 0xFE0F}, {0xFEFF, 0xFEFF},
         };
         return InRanges(cp, ranges, sizeof(ranges) / sizeof(ranges[0]));
      }

      // RFC 3454 C.1.2 - non-ASCII space characters mapped to U+0020.
      bool SaslMapToSpace(unsigned int cp)
      {
         static const CpRange ranges[] = {
            {0x00A0, 0x00A0}, {0x1680, 0x1680}, {0x2000, 0x200A},
            {0x202F, 0x202F}, {0x205F, 0x205F}, {0x3000, 0x3000},
         };
         return InRanges(cp, ranges, sizeof(ranges) / sizeof(ranges[0]));
      }

      // RFC 3454 prohibited output: C.2.1, C.2.2, C.3, C.4, C.5, C.6, C.7, C.8, C.9.
      bool SaslProhibited(unsigned int cp)
      {
         static const CpRange ranges[] = {
            // C.2.1 ASCII control characters.
            {0x0000, 0x001F}, {0x007F, 0x007F},
            // C.2.2 non-ASCII control characters.
            {0x0080, 0x009F}, {0x06DD, 0x06DD}, {0x070F, 0x070F},
            {0x180E, 0x180E}, {0x200C, 0x200D}, {0x2028, 0x2029},
            {0x2060, 0x2063}, {0x206A, 0x206F}, {0x1D173, 0x1D17A},
            // C.3 private use.
            {0xE000, 0xF8FF}, {0xF0000, 0xFFFFD}, {0x100000, 0x10FFFD},
            // C.4 non-character code points.
            {0xFDD0, 0xFDEF}, {0xFFFE, 0xFFFF}, {0x1FFFE, 0x1FFFF},
            {0x2FFFE, 0x2FFFF}, {0x3FFFE, 0x3FFFF}, {0x4FFFE, 0x4FFFF},
            {0x5FFFE, 0x5FFFF}, {0x6FFFE, 0x6FFFF}, {0x7FFFE, 0x7FFFF},
            {0x8FFFE, 0x8FFFF}, {0x9FFFE, 0x9FFFF}, {0xAFFFE, 0xAFFFF},
            {0xBFFFE, 0xBFFFF}, {0xCFFFE, 0xCFFFF}, {0xDFFFE, 0xDFFFF},
            {0xEFFFE, 0xEFFFF}, {0xFFFFE, 0xFFFFF}, {0x10FFFE, 0x10FFFF},
            // C.5 surrogate code points.
            {0xD800, 0xDFFF},
            // C.6 inappropriate for plain text.
            {0xFFF9, 0xFFFD},
            // C.7 inappropriate for canonical representation.
            {0x2FF0, 0x2FFB},
            // C.8 change display properties / deprecated.
            {0x0340, 0x0341}, {0x200E, 0x200F}, {0x202A, 0x202E}, {0x206A, 0x206F},
            // C.9 tagging characters.
            {0xE0001, 0xE0001}, {0xE0020, 0xE007F},
         };
         return InRanges(cp, ranges, sizeof(ranges) / sizeof(ranges[0]));
      }

      // RFC 3454 D.1 - characters with bidirectional property R or AL (RandALCat).
      bool SaslRandALCat(unsigned int cp)
      {
         static const CpRange ranges[] = {
            {0x05BE, 0x05BE}, {0x05C0, 0x05C0}, {0x05C3, 0x05C3},
            {0x05D0, 0x05EA}, {0x05F0, 0x05F4}, {0x061B, 0x061B},
            {0x061F, 0x061F}, {0x0621, 0x063A}, {0x0640, 0x064A},
            {0x066D, 0x066F}, {0x0671, 0x06D5}, {0x06DD, 0x06DD},
            {0x06E5, 0x06E6}, {0x06FA, 0x06FE}, {0x0700, 0x070D},
            {0x0710, 0x0710}, {0x0712, 0x072C}, {0x0780, 0x07A5},
            {0x07B1, 0x07B1}, {0x200F, 0x200F}, {0xFB1D, 0xFB1D},
            {0xFB1F, 0xFB28}, {0xFB2A, 0xFB36}, {0xFB38, 0xFB3C},
            {0xFB3E, 0xFB3E}, {0xFB40, 0xFB41}, {0xFB43, 0xFB44},
            {0xFB46, 0xFBB1}, {0xFBD3, 0xFD3D}, {0xFD50, 0xFD8F},
            {0xFD92, 0xFDC7}, {0xFDF0, 0xFDFC}, {0xFE70, 0xFE74},
            {0xFE76, 0xFEFC},
         };
         return InRanges(cp, ranges, sizeof(ranges) / sizeof(ranges[0]));
      }

      // RFC 3454 D.2 - characters with bidirectional property L (LCat). The full
      // table is enormous; we cover the principal strong-left scripts (Latin, Greek,
      // Cyrillic, Armenian, the major Indic blocks, CJK, Kana and Hangul) which is
      // sufficient to detect a mixed L/RandAL string. Missing an exotic L code point
      // only relaxes the check (a lenient accept), never causes a false reject.
      bool SaslLCat(unsigned int cp)
      {
         static const CpRange ranges[] = {
            {0x0041, 0x005A}, {0x0061, 0x007A}, {0x00AA, 0x00AA},
            {0x00B5, 0x00B5}, {0x00BA, 0x00BA}, {0x00C0, 0x00D6},
            {0x00D8, 0x00F6}, {0x00F8, 0x02B8}, {0x02BB, 0x02C1},
            {0x0386, 0x0386}, {0x0388, 0x038A}, {0x038C, 0x038C},
            {0x038E, 0x03A1}, {0x03A3, 0x03CE}, {0x03D0, 0x03F5},
            {0x0400, 0x0482}, {0x048A, 0x04CE}, {0x04D0, 0x04F9},
            {0x0531, 0x0556}, {0x0561, 0x0587}, {0x0905, 0x0939},
            {0x0958, 0x0961}, {0x0985, 0x09B9}, {0x0A05, 0x0A39},
            {0x0A85, 0x0AB9}, {0x0B05, 0x0B39}, {0x0B85, 0x0BB9},
            {0x0C05, 0x0C39}, {0x0C85, 0x0CB9}, {0x0D05, 0x0D39},
            {0x0E01, 0x0E30}, {0x0E40, 0x0E45}, {0x10A0, 0x10C5},
            {0x10D0, 0x10F6}, {0x1100, 0x1159}, {0x1E00, 0x1FBC},
            {0x3041, 0x3096}, {0x309D, 0x309F}, {0x30A1, 0x30FA},
            {0x30FC, 0x30FF}, {0x3105, 0x312C}, {0x3131, 0x318E},
            {0x3400, 0x4DB5}, {0x4E00, 0x9FA5}, {0xA000, 0xA48C},
            {0xAC00, 0xD7A3}, {0xF900, 0xFA2D}, {0xFF21, 0xFF3A},
            {0xFF41, 0xFF5A}, {0xFF66, 0xFFDC},
         };
         return InRanges(cp, ranges, sizeof(ranges) / sizeof(ranges[0]));
      }

      // Decode the UTF-16 code unit(s) at index i into a Unicode scalar value and
      // advance i past the consumed unit(s).
      unsigned int SaslNextCodePoint(const wchar_t *s, int len, int &i)
      {
         wchar_t c = s[i];
         if (c >= 0xD800 && c <= 0xDBFF && i + 1 < len)
         {
            wchar_t c2 = s[i + 1];
            if (c2 >= 0xDC00 && c2 <= 0xDFFF)
            {
               i += 2;
               return 0x10000u + (((unsigned int)(c - 0xD800)) << 10) + (unsigned int)(c2 - 0xDC00);
            }
         }
         i += 1;
         return (unsigned int)c;
      }

      void SaslAppendCodePoint(std::wstring &out, unsigned int cp)
      {
         if (cp <= 0xFFFF)
         {
            out.push_back((wchar_t)cp);
         }
         else
         {
            cp -= 0x10000;
            out.push_back((wchar_t)(0xD800 + (cp >> 10)));
            out.push_back((wchar_t)(0xDC00 + (cp & 0x3FF)));
         }
      }

      // NFKC (Normalization Form KC) via the Win32 normalisation API. Returns false
      // if the input is not valid Unicode (e.g. an unpaired surrogate), which for a
      // credential is treated as prohibited.
      bool SaslNormalizeKC(const std::wstring &input, std::wstring &output)
      {
         if (input.empty())
         {
            output.clear();
            return true;
         }

         int needed = NormalizeString(NormalizationKC, input.c_str(), (int)input.size(), NULL, 0);
         if (needed <= 0)
            return false; // ERROR_NO_UNICODE_TRANSLATION or other invalid input.

         std::wstring buffer;
         buffer.resize((size_t)needed);
         int written = NormalizeString(NormalizationKC, input.c_str(), (int)input.size(), &buffer[0], needed);
         if (written < 0)
         {
            // The estimate was too small; retry with the corrected size.
            buffer.resize((size_t)(-written));
            written = NormalizeString(NormalizationKC, input.c_str(), (int)input.size(), &buffer[0], (int)buffer.size());
         }
         if (written <= 0)
            return false;

         buffer.resize((size_t)written);
         output.swap(buffer);
         return true;
      }
   }

   bool
   StringParser::SaslPrep(const String &sInput, String &sOutput)
   {
      // RFC 4013 SASLprep. Four steps:
      //   1. Map (RFC 3454 B.1 -> nothing, C.1.2 non-ASCII spaces -> U+0020).
      //   2. Normalise with Unicode NFKC.
      //   3. Prohibit output characters (tables C.2.1, C.2.2, C.3-C.9).
      //   4. Check bidirectional text (RFC 3454 section 6).
      // ASCII text without controls is unchanged by every step (NFKC is a no-op on
      // ASCII), so existing credentials are unaffected. The A.1 (unassigned) and the
      // exhaustive D.2 (LCat) tables require the full Unicode Character Database and
      // are out of scope; StringPrep is superseded by PRECIS/RFC 7613.

      // Step 1 - mapping.
      std::wstring mapped;
      mapped.reserve((size_t)sInput.GetLength());
      const wchar_t *in = (LPCWSTR)sInput;
      int inLen = sInput.GetLength();
      for (int i = 0; i < inLen; )
      {
         unsigned int cp = SaslNextCodePoint(in, inLen, i);
         if (SaslMapToNothing(cp))
            continue;
         if (SaslMapToSpace(cp))
         {
            mapped.push_back(L' ');
            continue;
         }
         SaslAppendCodePoint(mapped, cp);
      }

      // Step 2 - NFKC normalisation.
      std::wstring normalized;
      if (!SaslNormalizeKC(mapped, normalized))
         return false;

      // Steps 3 and 4 - prohibition and bidi.
      bool hasRandAL = false;
      bool hasL = false;
      unsigned int firstCp = 0;
      unsigned int lastCp = 0;
      bool firstSet = false;
      int normLen = (int)normalized.size();
      const wchar_t *norm = normalized.c_str();
      for (int i = 0; i < normLen; )
      {
         unsigned int cp = SaslNextCodePoint(norm, normLen, i);

         if (SaslProhibited(cp))
            return false;

         if (!firstSet)
         {
            firstCp = cp;
            firstSet = true;
         }
         lastCp = cp;

         if (SaslRandALCat(cp))
            hasRandAL = true;
         else if (SaslLCat(cp))
            hasL = true;
      }

      // RFC 3454 section 6: if a string contains any RandALCat character it must not
      // contain any LCat character, and the first and last characters must both be
      // RandALCat.
      if (hasRandAL)
      {
         if (hasL)
            return false;
         if (!SaslRandALCat(firstCp) || !SaslRandALCat(lastCp))
            return false;
      }

      sOutput = normalized.c_str();
      return true;
   }

   bool
   StringParser::DecodeSaslPlain(const String &sBase64, String &sAuthzid, String &sAuthcid, String &sPassword)
   {
      // SASL PLAIN (RFC 4616): authzid NUL authcid NUL passwd, UTF-8 encoded.
      // Decode the base64 to raw bytes so embedded NULs and UTF-8 sequences are
      // preserved (a system-codepage conversion would mangle non-ASCII credentials).
      AnsiString sEncoded = sBase64;
      MimeCodeBase64 decoder;
      decoder.AddLineBreak(false);
      decoder.SetInput(sEncoded, sEncoded.GetLength(), false);
      AnsiString sDecoded;
      decoder.GetOutput(sDecoded);

      // Split the raw bytes on NUL into the three SASL PLAIN fields.
      std::vector<AnsiString> raw_parts;
      const char *pBuffer = sDecoded;
      int iLength = sDecoded.GetLength();
      int iStart = 0;
      for (int i = 0; i <= iLength; i++)
      {
         if (i == iLength || pBuffer[i] == 0)
         {
            AnsiString part;
            if (i > iStart)
               part.append(pBuffer + iStart, i - iStart);
            raw_parts.push_back(part);
            iStart = i + 1;
         }
      }

      if (raw_parts.size() != 3)
         return false;

      // RFC 4616: the fields are UTF-8. Decode to wide strings.
      Unicode::MultiByteToWide(raw_parts[0], sAuthzid);
      Unicode::MultiByteToWide(raw_parts[1], sAuthcid);
      Unicode::MultiByteToWide(raw_parts[2], sPassword);

      return true;
   }

   void Assert(bool result)
   {
      if (result == false)
         throw;
   }

   void StringParserTester::Test()
   {  
      String s = "AAAA";
      if (s.CompareNoCase(_T("aaaa")) != 0) throw;

      s = "AAAA";
      if (s.CompareNoCase(_T("bbbb")) == 0) throw;

      s = "AAAA";
      if (s.Equals(_T("aaaa"), true)) throw;

      s = "AAAA";
      if (!s.Equals(_T("aaaa"), false)) throw;

      s = "ZZZZZ";
      if (!s.Equals(_T("ZZZZZ"), false)) throw;

      s = "";
      if (!s.Equals(_T(""), false)) throw;

      // Test a few String functions
      String sTest = "TEST";
      sTest.Replace(_T("TEST"), _T("test"));
      if (sTest != _T("test")) throw;

      sTest = "test";
      sTest.Replace(_T("TEST"), _T("dummy"));
      if (sTest != _T("test")) throw;

      sTest = "test";
      sTest.ReplaceNoCase(_T("TEST"), _T("TEST"));
      if (sTest != _T("TEST")) throw;

      sTest = "TeSt";
      sTest.ReplaceNoCase(_T("TEST"), _T("TEST"));
      if (sTest != _T("TEST")) throw;

      sTest = "TestDummy";
      sTest.ReplaceNoCase(_T("testdummy"), _T("TestDummyReplaced"));
      if (sTest != _T("TestDummyReplaced")) throw;

      sTest = "TestDummy";
      sTest.ReplaceNoCase(_T("test"), _T("TestA"));
      if (sTest != _T("TestADummy")) throw;

      sTest = "Test Test Test Test";
      sTest.ReplaceNoCase(_T("Test"), _T("TEST"));
      if (sTest != _T("TEST TEST TEST TEST")) throw;
      
      sTest = "Test TestA Test Test";
      sTest.ReplaceNoCase(_T("TestA"), _T("TESTB"));
      if (sTest != _T("Test TESTB Test Test")) throw;
 
      // Check email addresses

      if (StringParser::IsValidEmailAddress("@")) throw;
      if (StringParser::IsValidEmailAddress("a")) throw;      
      if (StringParser::IsValidEmailAddress("test@")) throw;
      if (StringParser::IsValidEmailAddress("@.com")) throw;
      if (StringParser::IsValidEmailAddress("\"va@ff\"@test.co.uk")) throw;
      if (StringParser::IsValidEmailAddress("some one@test.co.uk")) throw;
      if (StringParser::IsValidEmailAddress("<someone@test.co.uk>")) throw;
      if (StringParser::IsValidEmailAddress("va ff@test.co.uk")) throw;
      if (!StringParser::IsValidEmailAddress("test@example.test")) throw;
      if (!StringParser::IsValidEmailAddress("test@hmailserver.com")) throw;
      if (!StringParser::IsValidEmailAddress("test_test@hmailserver.com")) throw;
      if (!StringParser::IsValidEmailAddress("bill@microsoft.com")) throw;
      if (!StringParser::IsValidEmailAddress("martin@hmailserver.com")) throw;
      if (!StringParser::IsValidEmailAddress("vaff@test.co.uk")) throw;
      if (!StringParser::IsValidEmailAddress("va'ff@test.co.uk")) throw;
      if (!StringParser::IsValidEmailAddress("\"va ff\"@test.co.uk")) throw;

      // Dot validation in unquoted local part
      if (StringParser::IsValidEmailAddress(".user@example.test")) throw;          // leading dot rejected
      if (StringParser::IsValidEmailAddress("user.@example.test")) throw;          // trailing dot rejected
      if (StringParser::IsValidEmailAddress("us..er@example.test")) throw;         // consecutive dots rejected
      if (StringParser::IsValidEmailAddress("...@example.test")) throw;            // only dots rejected
      if (!StringParser::IsValidEmailAddress("us.er@example.test")) throw;         // valid single dot
      if (!StringParser::IsValidEmailAddress("a.b.c.d@example.test")) throw;       // multiple valid dots
      if (!StringParser::IsValidEmailAddress("\"us..er\"@example.test")) throw;    // consecutive dots in quoted string allowed
      if (!StringParser::IsValidEmailAddress("\".user\"@example.test")) throw;     // leading dot in quoted string allowed
      if (!StringParser::IsValidEmailAddress("\"user.\"@example.test")) throw;     // trailing dot in quoted string allowed

      // Local-part length: RFC 5321 limits to 64 chars, but we allow longer for
      // backwards compatibility with existing accounts.
      if (!StringParser::IsValidEmailAddress("a@example.test")) throw;             // 1-char local part accepted
      if (!StringParser::IsValidEmailAddress("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa@example.test")) throw;  // 65-char local part allowed

      // Overall email length limit (RFC 5321: max 254 chars)
      // Build a 255-char email: 64-char local + @ + 190-char domain = 255
      if (StringParser::IsValidEmailAddress("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa@aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")) throw;

      // IPv6 address literal support
      if (!StringParser::IsValidEmailAddress("user@[IPv6:2001:0db8:85a3:0000:0000:8a2e:0370:7334]")) throw;
      if (StringParser::IsValidEmailAddress("user@[IPv6:invalid]")) throw;         // invalid IPv6 rejected
      if (StringParser::IsValidEmailAddress("user@[IPv6:]")) throw;                // empty IPv6 rejected
      if (StringParser::IsValidEmailAddress("user@[IPv6:2001:0db8:85a3]")) throw;  // too few groups rejected
      if (StringParser::IsValidEmailAddress("user@[IPv6:ZZZZ:0db8:85a3:0000:0000:8a2e:0370:7334]")) throw; // non-hex rejected

      // IPv4 literal
      if (!StringParser::IsValidEmailAddress("user@[192.168.1.1]")) throw;
      if (StringParser::IsValidEmailAddress("user@[1.2.3]")) throw;                // too few octets rejected
      if (StringParser::IsValidEmailAddress("user@[]")) throw;                     // empty brackets rejected

      // Domain validation edge cases
      if (StringParser::IsValidEmailAddress("user@-example.test")) throw;          // domain label starting with hyphen
      if (StringParser::IsValidEmailAddress("user@example-.test")) throw;          // domain label ending with hyphen
      if (StringParser::IsValidEmailAddress("user@.example.test")) throw;          // domain starting with dot
      if (!StringParser::IsValidEmailAddress("user@sub.example.test")) throw;      // subdomain valid
      if (!StringParser::IsValidEmailAddress("user@a.b.c.d.example.test")) throw;  // deep subdomain valid

      // Domain name validation: IPv6 support
      if (!StringParser::IsValidDomainName("[IPv6:2001:0db8:85a3:0000:0000:8a2e:0370:7334]")) throw;
      if (StringParser::IsValidDomainName("[IPv6:invalid]")) throw;
      if (StringParser::IsValidDomainName("[IPv6:]")) throw;                       // empty IPv6 rejected
      if (StringParser::IsValidDomainName("[IPv6:2001:0db8:85a3]")) throw;         // too few groups rejected
      // IPv4 literal still works
      if (!StringParser::IsValidDomainName("[192.168.1.1]")) throw;
      // Domain edge cases
      if (StringParser::IsValidDomainName("-example.test")) throw;                 // leading hyphen rejected
      if (StringParser::IsValidDomainName(".example.test")) throw;                 // leading dot rejected
      if (!StringParser::IsValidDomainName("sub.example.test")) throw;             // subdomain valid

      if (StringParser::ExtractAddress("\"va@ff\"@test.co.uk").Compare(_T("\"va@ff\"")) != 0) throw;
      if (StringParser::ExtractAddress("test@test.co.uk").Compare(_T("test")) != 0) throw;
      if (StringParser::ExtractAddress("t'est@test.co.uk").Compare(_T("t'est")) != 0) throw;
      if (StringParser::ExtractAddress("\"t@es@\"@test.co.uk").Compare(_T("\"t@es@\"")) != 0) throw;
      if (StringParser::ExtractAddress("test@test").Compare(_T("test")) != 0) throw;
      if (StringParser::ExtractAddress("t\"est@example.test").Compare(_T("t\"est")) != 0) throw;

      if (StringParser::ExtractDomain("t\"est@example.test").Compare(_T("example.test")) != 0) throw;
      if (StringParser::ExtractDomain("t'est@test.co.uk").Compare(_T("test.co.uk")) != 0) throw;
      if (StringParser::ExtractDomain("\"t@est\"@test.co.uk").Compare(_T("test.co.uk")) != 0) throw;
      if (StringParser::ExtractDomain("\"t@es@\"@test.co.uk").Compare(_T("test.co.uk")) != 0) throw;
      if (StringParser::ExtractDomain("test@example.test").Compare(_T("example.test")) != 0) throw;

      if (!StringParser::WildcardMatch("Test", "Test")) throw;
      if (!StringParser::WildcardMatch("", "")) throw;
      if (!StringParser::WildcardMatch("Test*", "Testar")) throw;
      if (!StringParser::WildcardMatch("Test*", "Test")) throw;
      if (StringParser::WildcardMatch("Test*", "Te")) throw;

     if (!StringParser::WildcardMatch("*two*", "one-two-three")) throw;
     if (StringParser::WildcardMatch("*two*", "one-three")) throw;
     if (StringParser::WildcardMatch("?two?", "one-two-three")) throw;
     if (!StringParser::WildcardMatch("?two?", "-two-")) throw;

     // Short strings.
     if (!StringParser::WildcardMatch("?", "A")) throw;
     if (StringParser::WildcardMatch("?", "AA")) throw;
     if (!StringParser::WildcardMatch("*", "A")) throw;
     if (!StringParser::WildcardMatch("*", "AA")) throw;
     if (!StringParser::WildcardMatch("*", "")) throw;
     if (StringParser::WildcardMatch("?", "")) throw;

     // Unicode strings
     if (!StringParser::WildcardMatch(_T("??語"), _T("標準語"))) throw;
     if (StringParser::WildcardMatch(_T("?語"), _T("標準語"))) throw;
     if (!StringParser::WildcardMatch(_T("?準?"), _T("標準語"))) throw;
     if (StringParser::WildcardMatch(_T("?準"), _T("標準語"))) throw;
     if (!StringParser::WildcardMatch(_T("標*"), _T("標準語"))) throw;

     // Matching email addresses
     if (!StringParser::WildcardMatch("test@*", "test@example.test")) throw;
     if (!StringParser::WildcardMatch("test@test.co.*", "test@test.co.uk")) throw;
     if (StringParser::WildcardMatch("test@test.co.*", "test@example.test")) throw;
     if (StringParser::WildcardMatch("test@test.co.*", "test@test.co")) throw;

     // Long strings.
     String k10String;
     for (int i = 0; i < 1000; i++)
        k10String  += "AAAAAAAAAA";

     String s310CharString;
     for (int i = 0; i < 31; i++)
        s310CharString  += "AAAAAAAAAA";

     if (!StringParser::WildcardMatch(_T("*"), k10String)) throw;
     if (!StringParser::WildcardMatch(s310CharString, s310CharString)) throw;

     char *p = 0;
     p = StringParser::Search("test", 4, "e");
     if (*p != 'e') throw;
     p = StringParser::Search("test", 4, "es");
     if (*p != 'e') throw;     
     p = StringParser::Search("test", 4, "n");
     if (p != 0) throw;      
     p = StringParser::Search("test", 4, "t");
     if (*p != 't') throw; 
     p = StringParser::Search("test ", 5, " ");
     if (*p != ' ') throw;  
     p = StringParser::Search("lest ", 5, "l");
     if (*p != 'l') throw; 
     p = StringParser::Search("testp", 4, "p");
     if (p != 0) throw;  
     p = StringParser::Search("testp", 5, "");
     if (*p != 't') throw;  
     p = StringParser::Search("", 0, "test");
     if (p != 0) throw;  
     p = StringParser::Search("", 0, "");
     if (p != 0) throw;  
     p = StringParser::Search("test", 4, "p");
     if (p != 0) throw;  
     p = StringParser::Search("test", 4, "feb");
     if (p != 0) throw;

      // RESULT:
      /*
         Strings containing 80% us-ascii characters:
         QP is about 50% faster.

         Strings containing 100% non-usascii
         B64 is about 10% faster.
      */

     String input = _T("A B C");
     std::vector<String> result = StringParser::SplitString(input, _T(" "));
     if (result.size() != 3)
        throw;

     input = "A B";
     result = StringParser::SplitString(input, " ");
     if (result.size() != 2)
        throw;

     // Test Contains and ContainsNoCase
     String s1 = "Test";
     String s2 = "Test";
     Assert(s1.Contains(s2));

     s1 = "Test";
     s2 = "Tes";
     Assert(s1.Contains(s2));

     s1 = "Test";
     s2 = "est";
     Assert(s1.Contains(s2));

     s1 = "Test";
     s2 = "est";
     Assert(s1.Contains(s2));

     s1 = "Te";
     s2 = "Tes";
     Assert(!s1.Contains(s2));

     s1 = "Test";
     s2 = "TEST";
     Assert(!s1.Contains(s2));

     s1 = "Test";
     s2 = "TEST";
     Assert(s1.ContainsNoCase(s2));

     s1 = "ABCDEFGHIJKLMNOPQ";
     s2 = "hijkl";
     Assert(s1.ContainsNoCase(s2));

     s1 = "ABCDEFGHIJKLMNOPQ";
     s2 = "hijkl";
     Assert(!s1.Contains(s2));

     // SaslPrep (RFC 4013: map -> NFKC -> prohibit -> bidi).
     String saslPrepped;
     // ASCII passes through unchanged.
     Assert(StringParser::SaslPrep(_T("user@example.test"), saslPrepped));
     Assert(saslPrepped == _T("user@example.test"));
     // Soft hyphen (U+00AD, table B.1) is removed. Use \u (fixed 4-digit) so the
     // following 'e' is not consumed as part of the escape.
     Assert(StringParser::SaslPrep(L"us\u00ADer", saslPrepped));
     Assert(saslPrepped == _T("user"));
     // Non-ASCII space (U+00A0, table C.1.2) maps to U+0020.
     Assert(StringParser::SaslPrep(L"a\u00A0b", saslPrepped));
     Assert(saslPrepped == _T("a b"));
     // A prohibited control character (U+0001, table C.2.1) is rejected.
     Assert(!StringParser::SaslPrep(L"a\u0001b", saslPrepped));

     // NFKC normalisation: the Latin small ligature fi (U+FB01) decomposes to "fi".
     Assert(StringParser::SaslPrep(L"\uFB01le", saslPrepped));
     Assert(saslPrepped == _T("file"));
     // NFKC: fullwidth Latin A (U+FF21) folds to ASCII "A".
     Assert(StringParser::SaslPrep(L"\uFF21", saslPrepped));
     Assert(saslPrepped == _T("A"));
     // NFKC: superscript two (U+00B2) folds to "2".
     Assert(StringParser::SaslPrep(L"x\u00B2", saslPrepped));
     Assert(saslPrepped == _T("x2"));
     // Mapping then NFKC: U+00AD (soft hyphen) is removed, leaving "IX".
     Assert(StringParser::SaslPrep(L"I\u00ADX", saslPrepped));
     Assert(saslPrepped == _T("IX"));

     // C.3 private-use code point is prohibited.
     Assert(!StringParser::SaslPrep(L"a\uE000b", saslPrepped));
     // C.4 non-character code point is prohibited.
     Assert(!StringParser::SaslPrep(L"a\uFDD0b", saslPrepped));
     // C.6 inappropriate-for-plain-text code point is prohibited.
     Assert(!StringParser::SaslPrep(L"a\uFFFDb", saslPrepped));

     // Bidi (RFC 3454 section 6): an all-RandALCat string (two Hebrew letters) is
     // accepted unchanged.
     Assert(StringParser::SaslPrep(L"\u05D0\u05D1", saslPrepped));
     Assert(saslPrepped == String(L"\u05D0\u05D1"));
     // Bidi: mixing an L character (ASCII 'a') with a RandALCat character is rejected.
     Assert(!StringParser::SaslPrep(L"a\u05D0", saslPrepped));
     // Bidi: a RandALCat string must start and end with RandALCat - a trailing ASCII
     // digit (European Number, not RandALCat) is rejected.
     Assert(!StringParser::SaslPrep(L"\u05D0\u05D11", saslPrepped));
   }
 
   


}
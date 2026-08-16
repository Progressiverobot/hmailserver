// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com

#pragma once

#include <vector>

namespace HM
{
   enum class SieveTokenType
   {
      Identifier,       // command / test / argument identifier
      Tag,              // :name (value holds name without the leading colon)
      Number,           // 1*DIGIT [ "K" / "M" / "G" ] (number holds the resolved value)
      QuotedString,     // "..." (value holds the unescaped content)
      MultiLineString,  // text: ... CRLF . CRLF (value holds the content)
      LeftBrace,        // {
      RightBrace,       // }
      LeftBracket,      // [
      RightBracket,     // ]
      LeftParen,        // (
      RightParen,       // )
      Comma,            // ,
      Semicolon,        // ;
      End               // end of input
   };

   struct SieveToken
   {
      SieveTokenType type = SieveTokenType::End;
      String value;
      __int64 number = 0;
      int line = 0;
   };

   // Tokenizes an RFC 5228 Sieve script into a flat token stream. Handles
   // hash and bracketed comments, quoted strings (with backslash escaping),
   // multi-line "text:" strings (with dot-stuffing), numbers with K/M/G
   // quantifiers, tags and punctuation.
   class SieveLexer
   {
   public:
      explicit SieveLexer(const String &script);

      bool Tokenize(std::vector<SieveToken> &tokens, String &errorMessage);

   private:
      wchar_t Current_() const;
      wchar_t Peek_(int offset) const;
      bool AtEnd_() const;
      void Advance_();

      bool SkipWhitespaceAndComments_(String &errorMessage);
      bool ReadQuotedString_(SieveToken &token, String &errorMessage);
      bool ReadMultiLineString_(SieveToken &token, String &errorMessage);
      void ReadNumber_(SieveToken &token);
      void ReadIdentifier_(SieveToken &token);
      bool ReadTag_(SieveToken &token, String &errorMessage);

      static bool IsIdentifierStart_(wchar_t ch);
      static bool IsIdentifierChar_(wchar_t ch);

      const String script_;
      int length_;
      int pos_;
      int line_;
   };
}

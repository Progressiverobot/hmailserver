// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"

#include "SieveLexer.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   SieveLexer::SieveLexer(const String &script) :
      script_(script),
      length_(script.GetLength()),
      pos_(0),
      line_(1)
   {
   }

   bool
   SieveLexer::AtEnd_() const
   {
      return pos_ >= length_;
   }

   wchar_t
   SieveLexer::Current_() const
   {
      return AtEnd_() ? L'\0' : script_[pos_];
   }

   wchar_t
   SieveLexer::Peek_(int offset) const
   {
      int target = pos_ + offset;
      if (target < 0 || target >= length_)
         return L'\0';

      return script_[target];
   }

   void
   SieveLexer::Advance_()
   {
      if (AtEnd_())
         return;

      if (script_[pos_] == L'\n')
         line_++;

      pos_++;
   }

   bool
   SieveLexer::IsIdentifierStart_(wchar_t ch)
   {
      return (ch >= L'a' && ch <= L'z') || (ch >= L'A' && ch <= L'Z') || ch == L'_';
   }

   bool
   SieveLexer::IsIdentifierChar_(wchar_t ch)
   {
      return IsIdentifierStart_(ch) || (ch >= L'0' && ch <= L'9');
   }

   bool
   SieveLexer::SkipWhitespaceAndComments_(String &errorMessage)
   {
      while (!AtEnd_())
      {
         wchar_t ch = Current_();

         if (ch == L' ' || ch == L'\t' || ch == L'\r' || ch == L'\n')
         {
            Advance_();
            continue;
         }

         if (ch == L'#')
         {
            // Hash comment: skip to end of line.
            while (!AtEnd_() && Current_() != L'\n')
               Advance_();
            continue;
         }

         if (ch == L'/' && Peek_(1) == L'*')
         {
            // Bracketed comment: skip to the closing */.
            int startLine = line_;
            Advance_();
            Advance_();

            bool closed = false;
            while (!AtEnd_())
            {
               if (Current_() == L'*' && Peek_(1) == L'/')
               {
                  Advance_();
                  Advance_();
                  closed = true;
                  break;
               }

               Advance_();
            }

            if (!closed)
            {
               errorMessage.Format(_T("Line %d: unterminated bracketed comment."), startLine);
               return false;
            }

            continue;
         }

         break;
      }

      return true;
   }

   bool
   SieveLexer::ReadQuotedString_(SieveToken &token, String &errorMessage)
   {
      int startLine = line_;
      token.type = SieveTokenType::QuotedString;
      token.line = startLine;
      token.value = _T("");

      // Consume the opening quote.
      Advance_();

      String content;
      while (!AtEnd_())
      {
         wchar_t ch = Current_();

         if (ch == L'\\')
         {
            // Backslash quotes the following character literally.
            Advance_();
            if (AtEnd_())
               break;

            content += Current_();
            Advance_();
            continue;
         }

         if (ch == L'"')
         {
            Advance_();
            token.value = content;
            return true;
         }

         content += ch;
         Advance_();
      }

      errorMessage.Format(_T("Line %d: unterminated quoted string."), startLine);
      return false;
   }

   bool
   SieveLexer::ReadMultiLineString_(SieveToken &token, String &errorMessage)
   {
      // Called positioned immediately after the "text:" introducer.
      int startLine = line_;
      token.type = SieveTokenType::MultiLineString;
      token.line = startLine;

      // The remainder of the introducer line is optional whitespace and an
      // optional hash comment, terminated by a newline.
      while (!AtEnd_() && Current_() != L'\n')
      {
         wchar_t ch = Current_();
         if (ch == L' ' || ch == L'\t' || ch == L'\r')
         {
            Advance_();
            continue;
         }

         if (ch == L'#')
         {
            while (!AtEnd_() && Current_() != L'\n')
               Advance_();
            break;
         }

         errorMessage.Format(_T("Line %d: unexpected character after 'text:' introducer."), line_);
         return false;
      }

      if (AtEnd_())
      {
         errorMessage.Format(_T("Line %d: unterminated multi-line string."), startLine);
         return false;
      }

      // Consume the newline that ends the introducer line.
      Advance_();

      String content;
      while (!AtEnd_())
      {
         // Read a single line.
         String currentLine;
         while (!AtEnd_() && Current_() != L'\n')
         {
            if (Current_() != L'\r')
               currentLine += Current_();

            Advance_();
         }

         // Consume the line-terminating newline (if present).
         if (!AtEnd_())
            Advance_();

         if (currentLine == _T("."))
         {
            token.value = content;
            return true;
         }

         // Remove dot-stuffing: a leading '.' on a content line is doubled.
         if (currentLine.GetLength() > 0 && currentLine[0] == L'.')
            currentLine = currentLine.Mid(1);

         content += currentLine;
         content += _T("\r\n");
      }

      errorMessage.Format(_T("Line %d: unterminated multi-line string."), startLine);
      return false;
   }

   void
   SieveLexer::ReadNumber_(SieveToken &token)
   {
      token.type = SieveTokenType::Number;
      token.line = line_;

      String digits;
      while (!AtEnd_() && Current_() >= L'0' && Current_() <= L'9')
      {
         digits += Current_();
         Advance_();
      }

      __int64 value = _wtoi64(digits);

      wchar_t quantifier = Current_();
      if (quantifier == L'K' || quantifier == L'k')
      {
         value *= 1024;
         Advance_();
      }
      else if (quantifier == L'M' || quantifier == L'm')
      {
         value *= 1024 * 1024;
         Advance_();
      }
      else if (quantifier == L'G' || quantifier == L'g')
      {
         value *= 1024 * 1024 * 1024;
         Advance_();
      }

      token.number = value;
      token.value = digits;
   }

   void
   SieveLexer::ReadIdentifier_(SieveToken &token)
   {
      token.type = SieveTokenType::Identifier;
      token.line = line_;

      String name;
      while (!AtEnd_() && IsIdentifierChar_(Current_()))
      {
         name += Current_();
         Advance_();
      }

      token.value = name;
   }

   bool
   SieveLexer::ReadTag_(SieveToken &token, String &errorMessage)
   {
      int startLine = line_;

      // Consume the leading colon.
      Advance_();

      if (!IsIdentifierStart_(Current_()))
      {
         errorMessage.Format(_T("Line %d: expected a tag name after ':'."), startLine);
         return false;
      }

      String name;
      while (!AtEnd_() && IsIdentifierChar_(Current_()))
      {
         name += Current_();
         Advance_();
      }

      token.type = SieveTokenType::Tag;
      token.line = startLine;
      token.value = name;
      return true;
   }

   bool
   SieveLexer::Tokenize(std::vector<SieveToken> &tokens, String &errorMessage)
   {
      tokens.clear();

      while (true)
      {
         if (!SkipWhitespaceAndComments_(errorMessage))
            return false;

         if (AtEnd_())
            break;

         wchar_t ch = Current_();
         SieveToken token;

         switch (ch)
         {
         case L'{':
            token.type = SieveTokenType::LeftBrace; token.line = line_; Advance_(); break;
         case L'}':
            token.type = SieveTokenType::RightBrace; token.line = line_; Advance_(); break;
         case L'[':
            token.type = SieveTokenType::LeftBracket; token.line = line_; Advance_(); break;
         case L']':
            token.type = SieveTokenType::RightBracket; token.line = line_; Advance_(); break;
         case L'(':
            token.type = SieveTokenType::LeftParen; token.line = line_; Advance_(); break;
         case L')':
            token.type = SieveTokenType::RightParen; token.line = line_; Advance_(); break;
         case L',':
            token.type = SieveTokenType::Comma; token.line = line_; Advance_(); break;
         case L';':
            token.type = SieveTokenType::Semicolon; token.line = line_; Advance_(); break;
         case L'"':
            if (!ReadQuotedString_(token, errorMessage))
               return false;
            break;
         case L':':
            if (!ReadTag_(token, errorMessage))
               return false;
            break;
         default:
            if (ch >= L'0' && ch <= L'9')
            {
               ReadNumber_(token);
            }
            else if (IsIdentifierStart_(ch))
            {
               ReadIdentifier_(token);

               // "text:" introduces a multi-line string.
               if (token.value.CompareNoCase(_T("text")) == 0 && Current_() == L':')
               {
                  Advance_(); // consume ':'
                  if (!ReadMultiLineString_(token, errorMessage))
                     return false;
               }
            }
            else
            {
               errorMessage.Format(_T("Line %d: unexpected character '%c'."), line_, ch);
               return false;
            }
            break;
         }

         tokens.push_back(token);
      }

      SieveToken endToken;
      endToken.type = SieveTokenType::End;
      endToken.line = line_;
      tokens.push_back(endToken);

      return true;
   }
}

// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"
#include "JsonDocument.h"

#include <cstdlib>

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   class JsonParser
   {
   public:
      JsonParser(const std::string &text) :
         text_(text),
         position_(0)
      {
      }

      bool Parse(JsonValue &value, std::string &error)
      {
         SkipWhitespace_();
         if (!ParseValue_(value, 0, error))
            return false;
         SkipWhitespace_();
         if (position_ != text_.size())
         {
            error = Where_("text after the end of the value");
            return false;
         }
         return true;
      }

   private:
      // Deep enough for any document the server reads - a Sigstore bundle is five
      // levels - and shallow enough that a document built to recurse cannot take the
      // thread's stack with it.
      static const int MaxDepth = 64;

      const std::string &text_;
      size_t position_;

      std::string Where_(const char *what) const
      {
         char buffer[64];
         sprintf_s(buffer, sizeof(buffer), " at offset %u", (unsigned) position_);
         return std::string(what) + buffer;
      }

      void SkipWhitespace_()
      {
         while (position_ < text_.size())
         {
            char c = text_[position_];
            if (c != ' ' && c != '\t' && c != '\r' && c != '\n')
               break;
            position_++;
         }
      }

      bool Peek_(char &c) const
      {
         if (position_ >= text_.size())
            return false;
         c = text_[position_];
         return true;
      }

      bool Expect_(const char *literal, std::string &error)
      {
         size_t length = strlen(literal);
         if (text_.compare(position_, length, literal) != 0)
         {
            error = Where_("unexpected token");
            return false;
         }
         position_ += length;
         return true;
      }

      bool ParseValue_(JsonValue &value, int depth, std::string &error)
      {
         if (depth > MaxDepth)
         {
            error = Where_("nesting deeper than the parser accepts");
            return false;
         }

         char c;
         if (!Peek_(c))
         {
            error = Where_("unexpected end of text");
            return false;
         }

         switch (c)
         {
         case '{':
            return ParseObject_(value, depth, error);
         case '[':
            return ParseArray_(value, depth, error);
         case '"':
            value.type_ = JsonValue::TypeString;
            return ParseString_(value.string_, error);
         case 't':
            if (!Expect_("true", error))
               return false;
            value.type_ = JsonValue::TypeBool;
            value.bool_ = true;
            return true;
         case 'f':
            if (!Expect_("false", error))
               return false;
            value.type_ = JsonValue::TypeBool;
            value.bool_ = false;
            return true;
         case 'n':
            if (!Expect_("null", error))
               return false;
            value.type_ = JsonValue::TypeNull;
            return true;
         default:
            if (c == '-' || (c >= '0' && c <= '9'))
               return ParseNumber_(value, error);
            error = Where_("unexpected character");
            return false;
         }
      }

      bool ParseObject_(JsonValue &value, int depth, std::string &error)
      {
         position_++; // {
         value.type_ = JsonValue::TypeObject;

         SkipWhitespace_();
         char c;
         if (Peek_(c) && c == '}')
         {
            position_++;
            return true;
         }

         while (true)
         {
            SkipWhitespace_();
            if (!Peek_(c) || c != '"')
            {
               error = Where_("expected a member name");
               return false;
            }

            std::string key;
            if (!ParseString_(key, error))
               return false;

            SkipWhitespace_();
            if (!Peek_(c) || c != ':')
            {
               error = Where_("expected ':' after a member name");
               return false;
            }
            position_++;

            SkipWhitespace_();
            JsonValue member;
            if (!ParseValue_(member, depth + 1, error))
               return false;

            // The first of a duplicated key wins, as a search by name would find it.
            bool present = false;
            for (size_t i = 0; i < value.members_.size(); i++)
            {
               if (value.members_[i].first == key)
               {
                  present = true;
                  break;
               }
            }
            if (!present)
               value.members_.push_back(std::make_pair(key, member));

            SkipWhitespace_();
            if (!Peek_(c))
            {
               error = Where_("unexpected end of text inside an object");
               return false;
            }
            if (c == ',')
            {
               position_++;
               continue;
            }
            if (c == '}')
            {
               position_++;
               return true;
            }
            error = Where_("expected ',' or '}' in an object");
            return false;
         }
      }

      bool ParseArray_(JsonValue &value, int depth, std::string &error)
      {
         position_++; // [
         value.type_ = JsonValue::TypeArray;

         SkipWhitespace_();
         char c;
         if (Peek_(c) && c == ']')
         {
            position_++;
            return true;
         }

         while (true)
         {
            SkipWhitespace_();
            JsonValue item;
            if (!ParseValue_(item, depth + 1, error))
               return false;
            value.items_.push_back(item);

            SkipWhitespace_();
            if (!Peek_(c))
            {
               error = Where_("unexpected end of text inside an array");
               return false;
            }
            if (c == ',')
            {
               position_++;
               continue;
            }
            if (c == ']')
            {
               position_++;
               return true;
            }
            error = Where_("expected ',' or ']' in an array");
            return false;
         }
      }

      bool ParseHex4_(unsigned &code, std::string &error)
      {
         if (position_ + 4 > text_.size())
         {
            error = Where_("truncated \\u escape");
            return false;
         }
         code = 0;
         for (int i = 0; i < 4; i++)
         {
            char c = text_[position_++];
            unsigned digit;
            if (c >= '0' && c <= '9')
               digit = c - '0';
            else if (c >= 'a' && c <= 'f')
               digit = 10 + c - 'a';
            else if (c >= 'A' && c <= 'F')
               digit = 10 + c - 'A';
            else
            {
               error = Where_("bad hex digit in a \\u escape");
               return false;
            }
            code = code * 16 + digit;
         }
         return true;
      }

      static void AppendUtf8_(std::string &out, unsigned code)
      {
         if (code < 0x80)
            out += (char) code;
         else if (code < 0x800)
         {
            out += (char) (0xC0 | (code >> 6));
            out += (char) (0x80 | (code & 0x3F));
         }
         else if (code < 0x10000)
         {
            out += (char) (0xE0 | (code >> 12));
            out += (char) (0x80 | ((code >> 6) & 0x3F));
            out += (char) (0x80 | (code & 0x3F));
         }
         else
         {
            out += (char) (0xF0 | (code >> 18));
            out += (char) (0x80 | ((code >> 12) & 0x3F));
            out += (char) (0x80 | ((code >> 6) & 0x3F));
            out += (char) (0x80 | (code & 0x3F));
         }
      }

      bool ParseString_(std::string &out, std::string &error)
      {
         position_++; // the opening quote
         out.clear();

         while (true)
         {
            if (position_ >= text_.size())
            {
               error = Where_("unterminated string");
               return false;
            }

            char c = text_[position_++];
            if (c == '"')
               return true;

            if ((unsigned char) c < 0x20)
            {
               error = Where_("control character in a string");
               return false;
            }

            if (c != '\\')
            {
               out += c;
               continue;
            }

            if (position_ >= text_.size())
            {
               error = Where_("unterminated escape");
               return false;
            }

            char e = text_[position_++];
            switch (e)
            {
            case '"': out += '"'; break;
            case '\\': out += '\\'; break;
            case '/': out += '/'; break;
            case 'b': out += '\b'; break;
            case 'f': out += '\f'; break;
            case 'n': out += '\n'; break;
            case 'r': out += '\r'; break;
            case 't': out += '\t'; break;
            case 'u':
               {
                  unsigned code;
                  if (!ParseHex4_(code, error))
                     return false;

                  if (code >= 0xD800 && code <= 0xDBFF)
                  {
                     // A high surrogate must be followed by a low one; together they
                     // are one character outside the basic plane.
                     if (text_.compare(position_, 2, "\\u") != 0)
                     {
                        error = Where_("a high surrogate without its pair");
                        return false;
                     }
                     position_ += 2;
                     unsigned low;
                     if (!ParseHex4_(low, error))
                        return false;
                     if (low < 0xDC00 || low > 0xDFFF)
                     {
                        error = Where_("a high surrogate without its pair");
                        return false;
                     }
                     code = 0x10000 + ((code - 0xD800) << 10) + (low - 0xDC00);
                  }
                  else if (code >= 0xDC00 && code <= 0xDFFF)
                  {
                     error = Where_("a low surrogate without its pair");
                     return false;
                  }

                  AppendUtf8_(out, code);
                  break;
               }
            default:
               error = Where_("unknown escape");
               return false;
            }
         }
      }

      bool ParseNumber_(JsonValue &value, std::string &error)
      {
         size_t start = position_;
         if (text_[position_] == '-')
            position_++;

         size_t digits = 0;
         while (position_ < text_.size() && text_[position_] >= '0' && text_[position_] <= '9')
         {
            position_++;
            digits++;
         }
         if (digits == 0)
         {
            error = Where_("expected a digit");
            return false;
         }

         if (position_ < text_.size() && text_[position_] == '.')
         {
            position_++;
            size_t fraction = 0;
            while (position_ < text_.size() && text_[position_] >= '0' && text_[position_] <= '9')
            {
               position_++;
               fraction++;
            }
            if (fraction == 0)
            {
               error = Where_("expected a digit after the decimal point");
               return false;
            }
         }

         if (position_ < text_.size() && (text_[position_] == 'e' || text_[position_] == 'E'))
         {
            position_++;
            if (position_ < text_.size() && (text_[position_] == '+' || text_[position_] == '-'))
               position_++;
            size_t exponent = 0;
            while (position_ < text_.size() && text_[position_] >= '0' && text_[position_] <= '9')
            {
               position_++;
               exponent++;
            }
            if (exponent == 0)
            {
               error = Where_("expected a digit in the exponent");
               return false;
            }
         }

         value.type_ = JsonValue::TypeNumber;
         value.raw_number_ = text_.substr(start, position_ - start);
         value.number_ = strtod(value.raw_number_.c_str(), nullptr);
         return true;
      }
   };

   JsonValue::JsonValue() :
      type_(TypeNull),
      bool_(false),
      number_(0)
   {
   }

   const JsonValue *
   JsonValue::Get(const std::string &key) const
   {
      if (type_ != TypeObject)
         return nullptr;

      for (size_t i = 0; i < members_.size(); i++)
      {
         if (members_[i].first == key)
            return &members_[i].second;
      }

      return nullptr;
   }

   std::string
   JsonValue::GetString(const std::string &key, const std::string &defaultValue) const
   {
      const JsonValue *member = Get(key);
      if (!member || !member->IsString())
         return defaultValue;
      return member->string_;
   }

   bool
   JsonValue::GetBool(const std::string &key, bool defaultValue) const
   {
      const JsonValue *member = Get(key);
      if (!member || !member->IsBool())
         return defaultValue;
      return member->bool_;
   }

   __int64
   JsonValue::GetInt64(const std::string &key, __int64 defaultValue) const
   {
      const JsonValue *member = Get(key);
      if (!member || !member->IsNumber())
         return defaultValue;
      return member->AsInt64();
   }

   const JsonValue *
   JsonValue::At(size_t index) const
   {
      if (type_ != TypeArray || index >= items_.size())
         return nullptr;
      return &items_[index];
   }

   __int64
   JsonValue::AsInt64() const
   {
      if (type_ != TypeNumber)
         return 0;

      // From the digits rather than the double, so a 64-bit count such as a log
      // index or a file size is exact. A fraction or an exponent goes through the
      // double, which is what such a value means.
      if (raw_number_.find_first_of(".eE") != std::string::npos)
         return (__int64) number_;

#ifdef HM_PLATFORM_POSIX
      // strtoll is the standard C name for _strtoi64: the same conversion, the
      // same three arguments, and a long long which is the same 64 bits that
      // __int64 is here.
      return ::strtoll(raw_number_.c_str(), nullptr, 10);
#else
      return _strtoi64(raw_number_.c_str(), nullptr, 10);
#endif
   }

   bool
   JsonValue::Parse(const std::string &text, JsonValue &value, std::string &error)
   {
      value = JsonValue();
      error.clear();

      JsonParser parser(text);
      return parser.Parse(value, error);
   }
}

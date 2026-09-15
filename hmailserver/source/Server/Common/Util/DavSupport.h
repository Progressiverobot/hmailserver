// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

// What CardDAV (CardDavServer.cpp) and CalDAV (CalDavServer.cpp) share under
// /dav/: the bounded namespace-aware XML reader for PROPFIND and REPORT
// bodies, the string helpers, HTTP Basic as the account through AccountLogon,
// the If-Match / If-None-Match rules, and the response builders with the DAV
// header. Written for CardDAV and moved here unchanged when CalDAV came; a
// bound found in one - the ten-byte entity scan, the name characters an XML
// name refuses - now holds for both.

#pragma once

#include <cstring>
#include <memory>
#include <utility>
#include <vector>

#include "HttpServer.h"

namespace HM
{
   class Account;

   namespace Dav
   {
      // The DAV header of every response under /dav/, and the ceilings of the
      // XML reader.
      extern const char *DavHeader;
      extern const int MaxXmlDepth;
      extern const int MaxXmlElements;

      // Strings.
      AnsiString Utf8(const String &value);
      String Wide(const AnsiString &value);
      AnsiString Trimmed(const AnsiString &value);
      AnsiString Lower(const AnsiString &value);
      AnsiString IntText(int value);
      AnsiString Int64Text(__int64 value);
      AnsiString XmlEscape(const AnsiString &value);
      AnsiString Sha256Hex(const AnsiString &bytes);
      int HexDigit(char c);

      // %XX -> the byte. A '+' stays a '+': this is a path, not a form.
      AnsiString PercentDecode(const AnsiString &value);

      // One path segment for an href: the unreserved characters and '@' (a
      // pchar) as they are, everything else percent-encoded.
      AnsiString PercentEncodeSegment(const AnsiString &value);

      // A username as a log line may carry it: no control characters, and
      // not longer than a name can be.
      String LogSafe(const String &value);

      // A strong ETag: the first 32 hex digits of the SHA-256 of the bytes
      // served, quoted; it changes exactly when the bytes do.
      AnsiString EtagOf(const AnsiString &bytes);

      // The path of the request-target, percent-decoded, without the query,
      // as segments. A trailing slash is remembered by an empty last segment.
      std::vector<AnsiString> PathSegments(const AnsiString &target);

      // Responses: every one carries the DAV header and Cache-Control: no-store.
      HttpResponse Respond(int status, const AnsiString &contentType, const AnsiString &body, const AnsiString &extraHeaders = "");
      HttpResponse Text(int status, const AnsiString &text, const AnsiString &extraHeaders = "");
      HttpResponse Xml(int status, const AnsiString &xml, const AnsiString &extraHeaders = "");
      HttpResponse Unauthorized();
      HttpResponse NotFound();

      // HTTP Basic as the account, through AccountLogon: the same password
      // schemes, directory-linked accounts, app passwords, per-name lockout,
      // auto-ban accounting and last-logon stamp as IMAP. False with no or
      // wrong credentials; disconnect says the auto-ban wants the connection
      // dropped. service names the protocol in the log line.
      bool Authenticate(const HttpRequest &request, const char *service, std::shared_ptr<const Account> &account, bool &disconnect);

      // The If-Match / If-None-Match rules of RFC 7232 for a write. current is
      // the ETag of what is there, or empty when nothing is. False means 412.
      bool PreconditionsHold(const HttpRequest &request, const AnsiString &current);

      //------------------------------------------------------------------------
      // A bounded, namespace-aware XML reader for request bodies
      //------------------------------------------------------------------------

      struct XmlElement
      {
         AnsiString ns;
         AnsiString name;
         AnsiString text;    // the element's own character data, decoded
         std::vector<std::pair<AnsiString, AnsiString>> attributes;   // local name -> decoded value
         std::vector<XmlElement> children;

         bool Is(const char *elementNs, const char *elementName) const
         {
            return ns == elementNs && name == elementName;
         }

         const XmlElement *Child(const char *elementNs, const char *elementName) const
         {
            for (size_t i = 0; i < children.size(); i++)
            {
               if (children[i].Is(elementNs, elementName))
                  return &children[i];
            }
            return nullptr;
         }

         AnsiString Attribute(const char *attributeName) const
         {
            for (size_t i = 0; i < attributes.size(); i++)
            {
               if (attributes[i].first == attributeName)
                  return attributes[i].second;
            }
            return "";
         }
      };

      class XmlReader
      {
      public:
         explicit XmlReader(const AnsiString &text) : text_(text), position_(0), elements_(0) { }

         bool Read(XmlElement &root, AnsiString &problem)
         {
            // A UTF-8 byte order mark is not markup, and some clients write one.
            if (StartsWith_("\xEF\xBB\xBF"))
               position_ += 3;

            if (!SkipMisc_(problem))
               return false;

            if (!At_('<'))
            {
               problem = "no root element";
               return false;
            }

            std::vector<Scope> scopes;
            if (!ReadElement_(root, scopes, 0, problem))
               return false;

            if (!SkipMisc_(problem))
               return false;

            if (position_ < text_.GetLength())
            {
               problem = "content after the root element";
               return false;
            }

            return true;
         }

      private:
         typedef std::vector<std::pair<AnsiString, AnsiString>> Scope;   // prefix -> namespace

         bool At_(char c) const
         {
            return position_ < text_.GetLength() && text_[position_] == c;
         }

         bool StartsWith_(const char *literal) const
         {
            int length = static_cast<int>(strlen(literal));
            if (position_ + length > text_.GetLength())
               return false;
            return memcmp(text_.c_str() + position_, literal, static_cast<size_t>(length)) == 0;
         }

         void SkipWhitespace_()
         {
            while (position_ < text_.GetLength())
            {
               char c = text_[position_];
               if (c != ' ' && c != '\t' && c != '\r' && c != '\n')
                  break;
               position_++;
            }
         }

         bool SkipPast_(const char *terminator, AnsiString &problem)
         {
            int found = text_.Find(terminator, position_);
            if (found < 0)
            {
               problem = "unterminated markup";
               return false;
            }
            position_ = found + static_cast<int>(strlen(terminator));
            return true;
         }

         // Whitespace, comments and processing instructions. A DOCTYPE is
         // refused outright: nothing here needs one, and external entities
         // are how a document reads the server's files.
         bool SkipMisc_(AnsiString &problem)
         {
            while (true)
            {
               SkipWhitespace_();
               if (StartsWith_("<?"))
               {
                  if (!SkipPast_("?>", problem))
                     return false;
                  continue;
               }
               if (StartsWith_("<!--"))
               {
                  if (!SkipPast_("-->", problem))
                     return false;
                  continue;
               }
               if (StartsWith_("<!"))
               {
                  problem = "a DOCTYPE or other declaration is not accepted";
                  return false;
               }
               return true;
            }
         }

         static bool IsNameChar_(char c)
         {
            // Neither an ampersand, a semicolon nor a control byte is part of an
            // XML name; admitted, they were reflected as written into the 207.
            if (c == '&' || c == ';' || (unsigned char) c < 0x20 || c == 0x7f)
               return false;
            return !(c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == '/' || c == '>' || c == '=' || c == '<' || c == '\"' || c == '\'');
         }

         AnsiString ReadName_()
         {
            int start = position_;
            while (position_ < text_.GetLength() && IsNameChar_(text_[position_]))
               position_++;
            return text_.Mid(start, position_ - start);
         }

         static AnsiString DecodeEntities_(const AnsiString &value)
         {
            if (value.Find("&") < 0)
               return value;

            AnsiString result;
            result.reserve(value.GetLength());
            for (int i = 0; i < value.GetLength(); i++)
            {
               char c = value[i];
               if (c != '&')
               {
                  result += c;
                  continue;
               }

               // The semicolon is looked for within ten bytes and no further: a
               // text run of a million ampersands with no semicolon in it made
               // every one of them scan to the end of the run before the limit
               // was applied, tens of seconds on one worker thread (found by
               // the review of 14 September 2026).
               int semicolon = -1;
               for (int k = i + 1; k < value.GetLength() && k <= i + 10; k++)
               {
                  if (value[k] == ';')
                  {
                     semicolon = k;
                     break;
                  }
               }
               if (semicolon < 0)
               {
                  result += c;
                  continue;
               }

               AnsiString entity = value.Mid(i + 1, semicolon - i - 1);
               if (entity == "lt") result += '<';
               else if (entity == "gt") result += '>';
               else if (entity == "amp") result += '&';
               else if (entity == "quot") result += '\"';
               else if (entity == "apos") result += '\'';
               else if (entity.GetLength() > 1 && entity[0] == '#')
               {
                  long code = 0;
                  if (entity[1] == 'x' || entity[1] == 'X')
                     code = strtol(entity.c_str() + 2, nullptr, 16);
                  else
                     code = strtol(entity.c_str() + 1, nullptr, 10);

                  // Not a character: the surrogate range and the two non-characters
                  // at the end of the BMP would be invalid UTF-8 reflected into
                  // the 207, which a strict parser rejects whole.
                  if (code <= 0 || code > 0x10ffff || (code >= 0xd800 && code <= 0xdfff) || code == 0xfffe || code == 0xffff)
                  {
                     result += c;
                     continue;
                  }

                  // UTF-8 encode the code point.
                  if (code < 0x80)
                     result += static_cast<char>(code);
                  else if (code < 0x800)
                  {
                     result += static_cast<char>(0xc0 | (code >> 6));
                     result += static_cast<char>(0x80 | (code & 0x3f));
                  }
                  else if (code < 0x10000)
                  {
                     result += static_cast<char>(0xe0 | (code >> 12));
                     result += static_cast<char>(0x80 | ((code >> 6) & 0x3f));
                     result += static_cast<char>(0x80 | (code & 0x3f));
                  }
                  else
                  {
                     result += static_cast<char>(0xf0 | (code >> 18));
                     result += static_cast<char>(0x80 | ((code >> 12) & 0x3f));
                     result += static_cast<char>(0x80 | ((code >> 6) & 0x3f));
                     result += static_cast<char>(0x80 | (code & 0x3f));
                  }
               }
               else
               {
                  result += c;
                  continue;
               }

               i = semicolon;
            }

            return result;
         }

         static bool Resolve_(const std::vector<Scope> &scopes, const AnsiString &prefix, AnsiString &ns)
         {
            for (size_t i = scopes.size(); i > 0; i--)
            {
               const Scope &scope = scopes[i - 1];
               for (size_t j = 0; j < scope.size(); j++)
               {
                  if (scope[j].first == prefix)
                  {
                     ns = scope[j].second;
                     return true;
                  }
               }
            }

            if (prefix.IsEmpty())
            {
               ns = "";
               return true;
            }

            return false;
         }

         bool ReadElement_(XmlElement &element, std::vector<Scope> &scopes, int depth, AnsiString &problem)
         {
            if (depth > MaxXmlDepth)
            {
               problem = "the document is nested too deeply";
               return false;
            }

            if (++elements_ > MaxXmlElements)
            {
               problem = "the document has too many elements";
               return false;
            }

            position_++;   // '<'
            AnsiString qualifiedName = ReadName_();
            if (qualifiedName.IsEmpty())
            {
               problem = "an element without a name";
               return false;
            }

            Scope scope;
            std::vector<std::pair<AnsiString, AnsiString>> rawAttributes;
            bool selfClosing = false;

            while (true)
            {
               SkipWhitespace_();
               if (position_ >= text_.GetLength())
               {
                  problem = "an unterminated start tag";
                  return false;
               }

               if (At_('/'))
               {
                  position_++;
                  if (!At_('>'))
                  {
                     problem = "a malformed empty-element tag";
                     return false;
                  }
                  position_++;
                  selfClosing = true;
                  break;
               }

               if (At_('>'))
               {
                  position_++;
                  break;
               }

               AnsiString attributeName = ReadName_();
               if (attributeName.IsEmpty())
               {
                  problem = "a malformed attribute";
                  return false;
               }

               SkipWhitespace_();
               if (!At_('='))
               {
                  problem = "an attribute without a value";
                  return false;
               }
               position_++;
               SkipWhitespace_();

               if (!At_('\"') && !At_('\''))
               {
                  problem = "an unquoted attribute value";
                  return false;
               }
               char quote = text_[position_];
               position_++;
               int valueStart = position_;
               int valueEnd = text_.Find(quote, position_);
               if (valueEnd < 0)
               {
                  problem = "an unterminated attribute value";
                  return false;
               }
               AnsiString value = DecodeEntities_(text_.Mid(valueStart, valueEnd - valueStart));
               position_ = valueEnd + 1;

               if (attributeName == "xmlns")
                  scope.push_back(std::make_pair(AnsiString(""), value));
               else if (attributeName.StartsWith("xmlns:"))
                  scope.push_back(std::make_pair(attributeName.Mid(6), value));
               else
               {
                  int colon = attributeName.Find(":");
                  AnsiString localName = colon >= 0 ? attributeName.Mid(colon + 1) : attributeName;
                  rawAttributes.push_back(std::make_pair(localName, value));
               }
            }

            scopes.push_back(scope);

            AnsiString prefix;
            AnsiString localName = qualifiedName;
            int colon = qualifiedName.Find(":");
            if (colon >= 0)
            {
               prefix = qualifiedName.Mid(0, colon);
               localName = qualifiedName.Mid(colon + 1);
            }

            if (!Resolve_(scopes, prefix, element.ns))
            {
               problem = "an undeclared namespace prefix: " + prefix;
               return false;
            }
            element.name = localName;
            element.attributes = rawAttributes;

            if (selfClosing)
            {
               scopes.pop_back();
               return true;
            }

            AnsiString text;
            while (true)
            {
               if (position_ >= text_.GetLength())
               {
                  problem = "an unterminated element: " + qualifiedName;
                  return false;
               }

               if (StartsWith_("</"))
               {
                  position_ += 2;
                  AnsiString endName = ReadName_();
                  SkipWhitespace_();
                  if (endName != qualifiedName || !At_('>'))
                  {
                     problem = "mismatched end tag for " + qualifiedName;
                     return false;
                  }
                  position_++;
                  break;
               }

               if (StartsWith_("<!--"))
               {
                  if (!SkipPast_("-->", problem))
                     return false;
                  continue;
               }

               if (StartsWith_("<![CDATA["))
               {
                  position_ += 9;
                  int end = text_.Find("]]>", position_);
                  if (end < 0)
                  {
                     problem = "an unterminated CDATA section";
                     return false;
                  }
                  text += text_.Mid(position_, end - position_);
                  position_ = end + 3;
                  continue;
               }

               if (StartsWith_("<?"))
               {
                  if (!SkipPast_("?>", problem))
                     return false;
                  continue;
               }

               if (StartsWith_("<!"))
               {
                  problem = "a declaration inside an element";
                  return false;
               }

               if (At_('<'))
               {
                  XmlElement child;
                  if (!ReadElement_(child, scopes, depth + 1, problem))
                     return false;
                  element.children.push_back(child);
                  continue;
               }

               int next = text_.Find("<", position_);
               if (next < 0)
                  next = text_.GetLength();
               text += DecodeEntities_(text_.Mid(position_, next - position_));
               position_ = next;
            }

            element.text = Trimmed(text);
            scopes.pop_back();
            return true;
         }

         const AnsiString &text_;
         int position_;
         int elements_;
      };
   }
}

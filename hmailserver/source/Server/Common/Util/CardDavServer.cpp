// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

// See CardDavServer.h for the shape of the tree. This file is the request
// side of it: a small namespace-aware XML reader for the PROPFIND and REPORT
// bodies (XMLite is neither namespace-aware nor meant for input off the wire),
// the DAV: and CARDDAV: property set of each resource kind, the three reports,
// and PUT/GET/DELETE of one contact as a vCard, on top of ContactStore and
// VCard.
//
// The bits of RFC 4918, 6352 and 6578 that shape the answers:
//
//   - PROPFIND (4918 section 9.1) answers 207 with one D:response per resource
//     in scope, and inside it one D:propstat for the properties found and one,
//     status 404, for those asked for and not there. Depth: 0 is the resource,
//     1 adds its members; infinity is treated as 1, as sabre/dav does, rather
//     than refused, since nothing here is deeper than one level anyway.
//   - The reports (6352 section 8, 6578 section 3): addressbook-multiget takes
//     hrefs and answers each, 404 for one that is not in this address book;
//     addressbook-query takes a filter of prop-filters with text-matches and
//     answers the contacts that match; sync-collection takes a token. The
//     store keeps no change log, so a token is the state of the whole
//     collection: an empty token lists everything, the current token answers
//     "no changes", and any other is refused with DAV:valid-sync-token so the
//     client lists again. That is a valid answer under 6578 section 3.2 and it
//     keeps the cheap poll cheap; it is not an incremental sync.
//   - ETags (6352 section 6.3.2.3) are strong and are the SHA-256 of the card
//     the server serves, so they change exactly when the card does. getctag
//     and the sync-token are the SHA-256 of every member's id and ETag.
//   - PUT with If-None-Match: * creates and is 412 on an existing resource;
//     If-Match must equal the current ETag or the answer is 412 (7232). A card
//     with no EMAIL is 403 with CARDDAV:valid-address-data and a sentence
//     saying why: this address book keeps a name and one e-mail address per
//     contact, and a card without one has nothing to be stored as.

#include "StdAfx.h"

#include <algorithm>
#include <openssl/sha.h>

#include "CardDavServer.h"
#include "ContactStore.h"
#include "VCard.h"
#include "AccountLogon.h"
#include "Unicode.h"
#include "Encoding/Base64.h"
#include "../BO/Account.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   const char *CardDavServer::ContextPath = "/dav/";

   namespace
   {
      const char *NsDav = "DAV:";
      const char *NsCardDav = "urn:ietf:params:xml:ns:carddav";
      const char *NsCalendarServer = "http://calendarserver.org/ns/";

      // Ceilings for a body off the wire. The listener's request size cap
      // bounds the bytes; these bound what a small body can still cost.
      const int MaxXmlDepth = 32;
      const int MaxXmlElements = 20000;
      const size_t MaxMultigetHrefs = 5000;

      // What CARDDAV:max-resource-size advertises, and the cap PUT enforces
      // on a card: the listener's large-request cap, less nothing - a card
      // that arrives has fitted already.
      const int MaxCardBytes = 1024 * 1024;

      const char *DavHeader = "DAV: 1, 3, addressbook\r\n";
      const char *AllowContact = "Allow: OPTIONS, GET, HEAD, PUT, DELETE, PROPFIND, PROPPATCH, REPORT\r\n";
      const char *AllowCollection = "Allow: OPTIONS, PROPFIND, PROPPATCH, REPORT\r\n";

      //------------------------------------------------------------------------
      // Strings
      //------------------------------------------------------------------------

      AnsiString Utf8(const String &value)
      {
         AnsiString utf8;
         Unicode::WideToMultiByte(value, utf8);
         return utf8;
      }

      String Wide(const AnsiString &value)
      {
         String wide;
         if (!Unicode::MultiByteToWide(value, wide))
            wide = String(value);
         return wide;
      }

      AnsiString Trimmed(const AnsiString &value)
      {
         AnsiString text = value;
         text.TrimLeft();
         text.TrimRight();
         return text;
      }

      AnsiString Lower(const AnsiString &value)
      {
         AnsiString text = value;
         text.ToLower();
         return text;
      }

      AnsiString IntText(int value)
      {
         AnsiString text;
         text.Format("%d", value);
         return text;
      }

      AnsiString Int64Text(__int64 value)
      {
         AnsiString text;
         text.Format("%I64d", value);
         return text;
      }

      AnsiString XmlEscape(const AnsiString &value)
      {
         AnsiString result;
         result.reserve(value.GetLength() + 8);

         for (int i = 0; i < value.GetLength(); i++)
         {
            char character = value[i];
            switch (character)
            {
            case '<':  result += "&lt;"; break;
            case '>':  result += "&gt;"; break;
            case '&':  result += "&amp;"; break;
            case '\"': result += "&quot;"; break;
            default:
               // Tabs, CR and LF are legal XML text; other controls are not.
               if (static_cast<unsigned char>(character) >= 32 || character == '\t' || character == '\r' || character == '\n')
                  result += character;
               break;
            }
         }

         return result;
      }

      AnsiString Sha256Hex(const AnsiString &bytes)
      {
         unsigned char digest[SHA256_DIGEST_LENGTH] = {};
         SHA256(reinterpret_cast<const unsigned char *>(bytes.c_str()), static_cast<size_t>(bytes.GetLength()), digest);

         static const char *hex = "0123456789abcdef";
         AnsiString result;
         for (int i = 0; i < SHA256_DIGEST_LENGTH; i++)
         {
            result += hex[digest[i] >> 4];
            result += hex[digest[i] & 0x0f];
         }
         return result;
      }

      int HexDigit(char c)
      {
         if (c >= '0' && c <= '9') return c - '0';
         if (c >= 'a' && c <= 'f') return c - 'a' + 10;
         if (c >= 'A' && c <= 'F') return c - 'A' + 10;
         return -1;
      }

      // %XX -> the byte. A '+' stays a '+': this is a path, not a form.
      AnsiString PercentDecode(const AnsiString &value)
      {
         AnsiString result;
         result.reserve(value.GetLength());

         for (int i = 0; i < value.GetLength(); i++)
         {
            char c = value[i];
            if (c == '%' && i + 2 < value.GetLength())
            {
               int high = HexDigit(value[i + 1]);
               int low = HexDigit(value[i + 2]);
               if (high >= 0 && low >= 0)
               {
                  result += static_cast<char>((high << 4) | low);
                  i += 2;
                  continue;
               }
            }
            result += c;
         }

         return result;
      }

      // One path segment for an href: the unreserved characters and '@' (a
      // pchar) as they are, everything else percent-encoded.
      AnsiString PercentEncodeSegment(const AnsiString &value)
      {
         static const char *hex = "0123456789ABCDEF";
         AnsiString result;
         result.reserve(value.GetLength() + 8);

         for (int i = 0; i < value.GetLength(); i++)
         {
            unsigned char c = static_cast<unsigned char>(value[i]);
            bool keep = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') ||
                        c == '-' || c == '.' || c == '_' || c == '~' || c == '@';
            if (keep)
            {
               result += static_cast<char>(c);
               continue;
            }

            result += '%';
            result += hex[c >> 4];
            result += hex[c & 0x0f];
         }

         return result;
      }

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

               int semicolon = value.Find(";", i);
               if (semicolon < 0 || semicolon - i > 10)
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

      //------------------------------------------------------------------------
      // The resources
      //------------------------------------------------------------------------

      enum ResourceKind
      {
         KindRoot,                // /dav/
         KindPrincipalCollection, // /dav/principals/
         KindPrincipal,           // /dav/principals/<address>/
         KindHomeCollection,      // /dav/addressbooks/
         KindHome,                // /dav/addressbooks/<address>/
         KindAddressBook,         // /dav/addressbooks/<address>/contacts/
         KindContact,             // .../contacts/<id>.vcf, and the contact exists
         KindContactSlot          // .../contacts/<name>, nothing there yet: PUT may create
      };

      struct Resource
      {
         Resource() : kind(KindRoot) { }

         ResourceKind kind;
         AnsiString href;        // the canonical href, percent-encoded
         ContactRecord contact;  // KindContact only
         AnsiString slotName;    // KindContactSlot only: the name, decoded
      };

      // A member of the address book as the responses see it: the row, the
      // card the server serves for it and that card's ETag.
      struct Member
      {
         ContactRecord record;
         AnsiString card;
         AnsiString etag;
         AnsiString href;
         AnsiString uid;
      };

      // Everything one request needs to know, computed once.
      struct Context
      {
         Context(const HttpRequest &httpRequest) : request(httpRequest), listed(false), listFailed(false) { }

         const HttpRequest &request;
         std::shared_ptr<const Account> account;

         String addressWide;         // the account's address, lower-cased
         AnsiString addressUtf8;
         AnsiString principalHref;
         AnsiString homeHref;
         AnsiString bookHref;

         // The address book's members, loaded on first need.
         bool listed;
         bool listFailed;
         std::vector<Member> members;
      };

      // A name-based UUID of a seed, marked as version 8 (RFC 9562 section 5.8)
      // because its bits come from SHA-256: the same seed gives the same UID.
      AnsiString StableUidFromSeed_(const AnsiString &seed);

      // The UID of a contact the webmail made, which has no card and so no UID
      // of its own: derived from the account's address and the row id, so the
      // same row gives the same UID for as long as the account keeps its
      // address. A card a client sent carries its own UID, kept in the row.
      AnsiString StableUid_(const AnsiString &addressUtf8, __int64 id)
      {
         return StableUidFromSeed_("hMailServer.carddav.contact:" + addressUtf8 + ":" + Int64Text(id));
      }

      AnsiString StableUidFromSeed_(const AnsiString &seed)
      {

         unsigned char digest[SHA256_DIGEST_LENGTH] = {};
         SHA256(reinterpret_cast<const unsigned char *>(seed.c_str()), static_cast<size_t>(seed.GetLength()), digest);
         digest[6] = static_cast<unsigned char>((digest[6] & 0x0f) | 0x80);
         digest[8] = static_cast<unsigned char>((digest[8] & 0x3f) | 0x80);

         static const char *hex = "0123456789abcdef";
         AnsiString result;
         for (int i = 0; i < 16; i++)
         {
            if (i == 4 || i == 6 || i == 8 || i == 10)
               result += "-";
            result += hex[digest[i] >> 4];
            result += hex[digest[i] & 0x0f];
         }
         return result;
      }

      // The resource name a client created the contact under, or <id>.vcf for
      // a contact the webmail made.
      AnsiString ContactHref(const Context &context, const ContactRecord &record)
      {
         if (record.uri.IsEmpty())
            return context.bookHref + Int64Text(record.id) + ".vcf";
         return context.bookHref + PercentEncodeSegment(Utf8(record.uri));
      }

      // A username as a log line may carry it: no control characters (a CR
      // or LF in a Base64-decoded credential would start a line of the
      // attacker's choosing), and not longer than a name can be.
      String LogSafe(const String &value)
      {
         String text;
         for (int i = 0; i < value.GetLength() && i < 200; i++)
            text += value[i] < 32 ? _T('?') : value[i];
         return text;
      }

      AnsiString EtagOf(const AnsiString &card)
      {
         return "\"" + Sha256Hex(card).Left(32) + "\"";
      }

      void FillMember(const Context &context, const ContactRecord &record, Member &member)
      {
         member.record = record;
         member.uid = record.uid.IsEmpty() ? StableUid_(context.addressUtf8, record.id) : Utf8(record.uid);

         // A card a client sent is served as it came, byte for byte, so the
         // ETag the client was given for it is the ETag of what it reads back
         // (RFC 9110 section 8.8.3); a row that never came from a client - the
         // webmail's - is served as a card made from its name and address.
         member.card = record.vcard.IsEmpty()
            ? VCard::Generate(member.uid, Utf8(record.name), Utf8(record.address))
            : Utf8(record.vcard);
         member.etag = EtagOf(member.card);
         member.href = ContactHref(context, record);
      }

      bool EnsureListed(Context &context)
      {
         if (context.listed)
            return !context.listFailed;

         context.listed = true;
         std::vector<ContactRecord> records;
         if (!ContactStore::List(context.account->GetID(), records))
         {
            context.listFailed = true;
            return false;
         }

         for (size_t i = 0; i < records.size(); i++)
         {
            Member member;
            FillMember(context, records[i], member);
            context.members.push_back(member);
         }

         return true;
      }

      // The state of the whole collection, from which getctag and the
      // sync-token are made: every member's id and ETag, in id order.
      bool CollectionTag(Context &context, AnsiString &tag)
      {
         if (!EnsureListed(context))
            return false;

         std::vector<AnsiString> lines;
         for (size_t i = 0; i < context.members.size(); i++)
            lines.push_back(Int64Text(context.members[i].record.id) + ":" + context.members[i].etag);
         std::sort(lines.begin(), lines.end());

         AnsiString all;
         for (size_t i = 0; i < lines.size(); i++)
            all += lines[i] + "\n";

         tag = Sha256Hex(all).Left(32);
         return true;
      }

      AnsiString SyncTokenFor(const AnsiString &tag)
      {
         return "urn:x-hmailserver:carddav-sync:" + tag;
      }

      // The path of the request-target, percent-decoded, without the query,
      // as segments. A trailing slash is remembered by an empty last segment.
      std::vector<AnsiString> PathSegments(const AnsiString &target)
      {
         AnsiString path = target;
         int query = path.Find("?");
         if (query >= 0)
            path = path.Mid(0, query);

         std::vector<AnsiString> segments;
         AnsiString current;
         for (int i = 0; i < path.GetLength(); i++)
         {
            char c = path[i];
            if (c == '/')
            {
               if (i > 0)
                  segments.push_back(PercentDecode(current));
               current = "";
               continue;
            }
            current += c;
         }
         segments.push_back(PercentDecode(current));
         return segments;
      }

      bool SameAddress(const Context &context, const AnsiString &segment)
      {
         String wide = Wide(segment);
         wide.ToLower();
         return wide == context.addressWide;
      }

      // The resource a path names, or false: a path outside the tree, or into
      // another account's part of it, is simply not found. Another account's
      // principal is 404 rather than 403 so that the tree of one account says
      // nothing about the existence of another.
      bool Resolve(Context &context, const AnsiString &target, Resource &resource)
      {
         std::vector<AnsiString> segments = PathSegments(target);

         // "/dav" or "/dav/" -> ["dav"] or ["dav", ""]
         if (segments.empty() || segments[0] != "dav")
            return false;

         // Drop the trailing empty segment a trailing slash leaves.
         bool trailingSlash = segments.size() > 1 && segments.back().IsEmpty();
         if (trailingSlash)
            segments.pop_back();

         if (segments.size() == 1)
         {
            resource.kind = KindRoot;
            resource.href = CardDavServer::ContextPath;
            return true;
         }

         if (segments[1] == "principals")
         {
            if (segments.size() == 2)
            {
               resource.kind = KindPrincipalCollection;
               resource.href = AnsiString(CardDavServer::ContextPath) + "principals/";
               return true;
            }
            if (segments.size() == 3 && SameAddress(context, segments[2]))
            {
               resource.kind = KindPrincipal;
               resource.href = context.principalHref;
               return true;
            }
            return false;
         }

         if (segments[1] == "addressbooks")
         {
            if (segments.size() == 2)
            {
               resource.kind = KindHomeCollection;
               resource.href = AnsiString(CardDavServer::ContextPath) + "addressbooks/";
               return true;
            }
            if (!SameAddress(context, segments[2]))
               return false;
            if (segments.size() == 3)
            {
               resource.kind = KindHome;
               resource.href = context.homeHref;
               return true;
            }
            if (segments[3] != "contacts")
               return false;
            if (segments.size() == 4)
            {
               resource.kind = KindAddressBook;
               resource.href = context.bookHref;
               return true;
            }
            if (segments.size() != 5 || trailingSlash || segments[4].IsEmpty())
               return false;

            // The name a client created a contact under names that contact;
            // <id>.vcf names a contact the webmail made, which has no name of
            // its own; any other name is a slot a PUT may fill.
            AnsiString name = segments[4];
            ContactRecord named;
            if (ContactStore::FindByUri(context.account->GetID(), Wide(name), named))
            {
               resource.kind = KindContact;
               resource.contact = named;
               resource.href = ContactHref(context, named);
               return true;
            }

            if (name.EndsWith(".vcf"))
            {
               AnsiString idText = name.Mid(0, name.GetLength() - 4);
               bool numeric = !idText.IsEmpty() && idText.GetLength() < 19;
               for (int i = 0; numeric && i < idText.GetLength(); i++)
                  numeric = idText[i] >= '0' && idText[i] <= '9';

               if (numeric)
               {
                  __int64 id = 0;
                  for (int i = 0; i < idText.GetLength(); i++)
                     id = id * 10 + (idText[i] - '0');

                  ContactRecord record;
                  if (id > 0 && ContactStore::Get(context.account->GetID(), id, record) && record.uri.IsEmpty())
                  {
                     resource.kind = KindContact;
                     resource.contact = record;
                     resource.href = ContactHref(context, record);
                     return true;
                  }
               }
            }

            resource.kind = KindContactSlot;
            resource.href = context.bookHref + PercentEncodeSegment(name);
            resource.slotName = name;
            return true;
         }

         return false;
      }

      //------------------------------------------------------------------------
      // Responses
      //------------------------------------------------------------------------

      HttpResponse Respond(int status, const AnsiString &contentType, const AnsiString &body, const AnsiString &extraHeaders = "")
      {
         HttpResponse response;
         response.status = status;
         response.content_type = contentType;
         response.body = body;
         response.extra_headers = extraHeaders;
         response.extra_headers += DavHeader;
         response.extra_headers += "Cache-Control: no-store\r\n";
         return response;
      }

      HttpResponse Text(int status, const AnsiString &text, const AnsiString &extraHeaders = "")
      {
         return Respond(status, "text/plain; charset=utf-8", text + "\r\n", extraHeaders);
      }

      HttpResponse Xml(int status, const AnsiString &xml, const AnsiString &extraHeaders = "")
      {
         return Respond(status, "application/xml; charset=utf-8", "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" + xml, extraHeaders);
      }

      // A DAV:error body (RFC 4918 section 16) naming the precondition that
      // failed, with a sentence for the person reading the client's log.
      HttpResponse DavError(int status, const AnsiString &preconditionXml, const AnsiString &description)
      {
         AnsiString xml = "<D:error xmlns:D=\"DAV:\" xmlns:C=\"urn:ietf:params:xml:ns:carddav\">" + preconditionXml +
            "<D:responsedescription>" + XmlEscape(description) + "</D:responsedescription></D:error>\r\n";
         return Xml(status, xml);
      }

      HttpResponse Unauthorized()
      {
         return Text(401, "authentication required", "WWW-Authenticate: Basic realm=\"hMailServer\"\r\n");
      }

      HttpResponse NotFound()
      {
         return Text(404, "not found");
      }

      HttpResponse MethodNotAllowed(const Resource &resource)
      {
         bool contact = resource.kind == KindContact || resource.kind == KindContactSlot;
         return Text(405, "method not allowed", contact ? AllowContact : AllowCollection);
      }

      AnsiString MultistatusOpen()
      {
         return "<D:multistatus xmlns:D=\"DAV:\" xmlns:C=\"urn:ietf:params:xml:ns:carddav\" xmlns:CS=\"http://calendarserver.org/ns/\">\r\n";
      }

      //------------------------------------------------------------------------
      // Properties
      //------------------------------------------------------------------------

      struct PropertyName
      {
         PropertyName() { }
         PropertyName(const char *propertyNs, const char *propertyName) : ns(propertyNs), name(propertyName) { }

         AnsiString ns;
         AnsiString name;
      };

      // How the properties are asked for.
      enum PropertyMode
      {
         ModeProp,      // the ones named
         ModeAllProp,   // every one the resource has (but not address-data)
         ModePropName   // the names of every one, no values
      };

      struct PropertyRequest
      {
         PropertyRequest() : mode(ModeAllProp) { }

         PropertyMode mode;
         std::vector<PropertyName> names;

         // address-data's partial-retrieval element, if the request named
         // properties inside it: the card is then reduced to those.
         std::vector<AnsiString> addressDataProperties;
      };

      // The element for a property in a response: prefixed for the three
      // namespaces the document declares, a default-namespace element for any
      // other. Empty inner XML makes an empty element.
      AnsiString PropertyElement(const PropertyName &property, const AnsiString &innerXml)
      {
         AnsiString prefix;
         if (property.ns == NsDav) prefix = "D:";
         else if (property.ns == NsCardDav) prefix = "C:";
         else if (property.ns == NsCalendarServer) prefix = "CS:";

         AnsiString open = "<" + prefix + property.name;
         if (prefix.IsEmpty())
            open += " xmlns=\"" + XmlEscape(property.ns) + "\"";

         if (innerXml.IsEmpty())
            return open + "/>";
         return open + ">" + innerXml + "</" + prefix + property.name + ">";
      }

      AnsiString Href(const AnsiString &href)
      {
         return "<D:href>" + XmlEscape(href) + "</D:href>";
      }

      // The card reduced to the properties a partial address-data asked for.
      AnsiString ReduceCard(const AnsiString &card, const std::vector<AnsiString> &wanted)
      {
         if (wanted.empty())
            return card;

         AnsiString result;
         std::vector<AnsiString> lines = StringParser::SplitString(card, "\r\n");
         for (size_t i = 0; i < lines.size(); i++)
         {
            const AnsiString &line = lines[i];
            if (line.IsEmpty())
               continue;

            int colon = line.Find(":");
            int semicolon = line.Find(";");
            int end = colon;
            if (semicolon >= 0 && semicolon < end)
               end = semicolon;
            AnsiString name = end > 0 ? line.Mid(0, end) : line;
            name.ToUpper();

            bool keep = name == "BEGIN" || name == "END" || name == "VERSION";
            for (size_t j = 0; !keep && j < wanted.size(); j++)
               keep = wanted[j].CompareNoCase(name) == 0;

            if (keep)
               result += line + "\r\n";
         }
         return result;
      }

      // The value of one live property of a resource, or false when the
      // resource has no such property.
      bool PropertyValue(Context &context, const Resource &resource, const Member *member, const PropertyRequest &request,
                         const PropertyName &property, AnsiString &innerXml)
      {
         ResourceKind kind = resource.kind;
         bool isContact = kind == KindContact;
         bool isCollection = !isContact && kind != KindContactSlot;

         if (property.ns == NsDav)
         {
            if (property.name == "resourcetype")
            {
               innerXml = "";
               if (isCollection)
                  innerXml += "<D:collection/>";
               if (kind == KindPrincipal)
                  innerXml += "<D:principal/>";
               if (kind == KindAddressBook)
                  innerXml += "<C:addressbook/>";
               return true;
            }

            if (property.name == "displayname")
            {
               switch (kind)
               {
               case KindRoot: innerXml = "hMailServer"; break;
               case KindPrincipalCollection: innerXml = "Principals"; break;
               case KindPrincipal: innerXml = XmlEscape(context.addressUtf8); break;
               case KindHomeCollection: innerXml = "Address books"; break;
               case KindHome: innerXml = XmlEscape("Address books of " + context.addressUtf8); break;
               case KindAddressBook: innerXml = "Contacts"; break;
               case KindContact:
                  innerXml = XmlEscape(resource.contact.name.IsEmpty() ? Utf8(resource.contact.address) : Utf8(resource.contact.name));
                  break;
               default: return false;
               }
               return true;
            }

            if (property.name == "current-user-principal")
            {
               innerXml = Href(context.principalHref);
               return true;
            }

            if (property.name == "principal-collection-set")
            {
               innerXml = Href(AnsiString(CardDavServer::ContextPath) + "principals/");
               return true;
            }

            if (property.name == "principal-URL" && kind == KindPrincipal)
            {
               innerXml = Href(context.principalHref);
               return true;
            }

            if (property.name == "owner" && (kind == KindHome || kind == KindAddressBook || isContact))
            {
               innerXml = Href(context.principalHref);
               return true;
            }

            if (property.name == "current-user-privilege-set" && (kind == KindPrincipal || kind == KindHome || kind == KindAddressBook || isContact))
            {
               innerXml = "<D:privilege><D:read/></D:privilege><D:privilege><D:write/></D:privilege>"
                          "<D:privilege><D:write-content/></D:privilege><D:privilege><D:bind/></D:privilege>"
                          "<D:privilege><D:unbind/></D:privilege><D:privilege><D:read-current-user-privilege-set/></D:privilege>";
               return true;
            }

            if (property.name == "supported-report-set" && kind == KindAddressBook)
            {
               innerXml = "<D:supported-report><D:report><C:addressbook-multiget/></D:report></D:supported-report>"
                          "<D:supported-report><D:report><C:addressbook-query/></D:report></D:supported-report>"
                          "<D:supported-report><D:report><D:sync-collection/></D:report></D:supported-report>";
               return true;
            }

            if (property.name == "sync-token" && kind == KindAddressBook)
            {
               AnsiString tag;
               if (!CollectionTag(context, tag))
                  return false;
               innerXml = XmlEscape(SyncTokenFor(tag));
               return true;
            }

            if (property.name == "getetag" && isContact && member)
            {
               innerXml = XmlEscape(member->etag);
               return true;
            }

            if (property.name == "getcontenttype" && isContact)
            {
               innerXml = "text/vcard; charset=utf-8";
               return true;
            }

            if (property.name == "getcontentlength" && isContact && member)
            {
               innerXml = IntText(member->card.GetLength());
               return true;
            }

            return false;
         }

         if (property.ns == NsCardDav)
         {
            if (property.name == "addressbook-home-set" && (kind == KindRoot || kind == KindPrincipal))
            {
               innerXml = Href(context.homeHref);
               return true;
            }

            if (kind == KindAddressBook)
            {
               if (property.name == "supported-address-data")
               {
                  innerXml = "<C:address-data-type content-type=\"text/vcard\" version=\"3.0\"/>";
                  return true;
               }
               if (property.name == "addressbook-description")
               {
                  innerXml = XmlEscape("The contacts of " + context.addressUtf8);
                  return true;
               }
               if (property.name == "max-resource-size")
               {
                  innerXml = IntText(MaxCardBytes);
                  return true;
               }
               if (property.name == "supported-collation-set")
               {
                  innerXml = "<C:supported-collation>i;ascii-casemap</C:supported-collation>"
                             "<C:supported-collation>i;octet</C:supported-collation>"
                             "<C:supported-collation>i;unicode-casemap</C:supported-collation>";
                  return true;
               }
            }

            if (property.name == "address-data" && isContact && member)
            {
               innerXml = XmlEscape(ReduceCard(member->card, request.addressDataProperties));
               return true;
            }

            return false;
         }

         if (property.ns == NsCalendarServer)
         {
            if (property.name == "getctag" && kind == KindAddressBook)
            {
               AnsiString tag;
               if (!CollectionTag(context, tag))
                  return false;
               innerXml = XmlEscape(tag);
               return true;
            }
         }

         return false;
      }

      // Every property a resource kind has, for allprop and propname.
      void KnownProperties(ResourceKind kind, bool includeAddressData, std::vector<PropertyName> &names)
      {
         names.push_back(PropertyName(NsDav, "resourcetype"));
         names.push_back(PropertyName(NsDav, "displayname"));
         names.push_back(PropertyName(NsDav, "current-user-principal"));
         names.push_back(PropertyName(NsDav, "principal-collection-set"));

         switch (kind)
         {
         case KindRoot:
            names.push_back(PropertyName(NsCardDav, "addressbook-home-set"));
            break;
         case KindPrincipal:
            names.push_back(PropertyName(NsDav, "principal-URL"));
            names.push_back(PropertyName(NsDav, "current-user-privilege-set"));
            names.push_back(PropertyName(NsCardDav, "addressbook-home-set"));
            break;
         case KindHome:
            names.push_back(PropertyName(NsDav, "owner"));
            names.push_back(PropertyName(NsDav, "current-user-privilege-set"));
            break;
         case KindAddressBook:
            names.push_back(PropertyName(NsDav, "owner"));
            names.push_back(PropertyName(NsDav, "current-user-privilege-set"));
            names.push_back(PropertyName(NsDav, "supported-report-set"));
            names.push_back(PropertyName(NsDav, "sync-token"));
            names.push_back(PropertyName(NsCardDav, "addressbook-description"));
            names.push_back(PropertyName(NsCardDav, "supported-address-data"));
            names.push_back(PropertyName(NsCardDav, "max-resource-size"));
            names.push_back(PropertyName(NsCardDav, "supported-collation-set"));
            names.push_back(PropertyName(NsCalendarServer, "getctag"));
            break;
         case KindContact:
            names.push_back(PropertyName(NsDav, "getetag"));
            names.push_back(PropertyName(NsDav, "getcontenttype"));
            names.push_back(PropertyName(NsDav, "getcontentlength"));
            names.push_back(PropertyName(NsDav, "owner"));
            names.push_back(PropertyName(NsDav, "current-user-privilege-set"));
            if (includeAddressData)
               names.push_back(PropertyName(NsCardDav, "address-data"));
            break;
         default:
            break;
         }
      }

      // One D:response for a resource: the properties found in a 200
      // propstat, the ones asked for and absent in a 404 propstat.
      AnsiString ResponseFor(Context &context, const Resource &resource, const Member *member, const PropertyRequest &request)
      {
         std::vector<PropertyName> names;
         if (request.mode == ModeProp)
            names = request.names;
         else
         {
            KnownProperties(resource.kind, request.mode == ModePropName, names);

            // allprop with D:include: the named ones as well, once each.
            for (size_t i = 0; i < request.names.size(); i++)
            {
               bool known = false;
               for (size_t j = 0; !known && j < names.size(); j++)
                  known = names[j].ns == request.names[i].ns && names[j].name == request.names[i].name;
               if (!known)
                  names.push_back(request.names[i]);
            }
         }

         AnsiString found;
         AnsiString missing;
         for (size_t i = 0; i < names.size(); i++)
         {
            if (request.mode == ModePropName)
            {
               found += PropertyElement(names[i], "");
               continue;
            }

            AnsiString innerXml;
            if (PropertyValue(context, resource, member, request, names[i], innerXml))
               found += PropertyElement(names[i], innerXml);
            else
               missing += PropertyElement(names[i], "");
         }

         AnsiString xml = " <D:response>\r\n  " + Href(resource.href) + "\r\n";
         if (!found.IsEmpty() || missing.IsEmpty())
            xml += "  <D:propstat><D:prop>" + found + "</D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat>\r\n";
         if (!missing.IsEmpty())
            xml += "  <D:propstat><D:prop>" + missing + "</D:prop><D:status>HTTP/1.1 404 Not Found</D:status></D:propstat>\r\n";
         xml += " </D:response>\r\n";
         return xml;
      }

      AnsiString ResponseForMember(Context &context, const Member &member, const PropertyRequest &request)
      {
         Resource resource;
         resource.kind = KindContact;
         resource.contact = member.record;
         resource.href = member.href;
         return ResponseFor(context, resource, &member, request);
      }

      // The D:prop element of a request, into a PropertyRequest.
      void ReadPropertyRequest(const XmlElement *prop, PropertyRequest &request)
      {
         request.mode = ModeProp;
         request.names.clear();
         if (!prop)
            return;

         for (size_t i = 0; i < prop->children.size(); i++)
         {
            const XmlElement &child = prop->children[i];
            PropertyName name;
            name.ns = child.ns;
            name.name = child.name;
            request.names.push_back(name);

            if (child.Is(NsCardDav, "address-data"))
            {
               for (size_t j = 0; j < child.children.size(); j++)
               {
                  if (child.children[j].Is(NsCardDav, "prop"))
                  {
                     AnsiString wanted = child.children[j].Attribute("name");
                     if (!wanted.IsEmpty())
                        request.addressDataProperties.push_back(wanted);
                  }
               }
            }
         }
      }

      //------------------------------------------------------------------------
      // Authentication
      //------------------------------------------------------------------------

      // HTTP Basic as the account, through AccountLogon: the same password
      // schemes, directory-linked accounts, app passwords, per-name lockout,
      // auto-ban accounting and last-logon stamp as IMAP. False with no or
      // wrong credentials; disconnect says the auto-ban wants the connection
      // dropped.
      bool Authenticate(const HttpRequest &request, std::shared_ptr<const Account> &account, bool &disconnect)
      {
         disconnect = false;

         AnsiString header = request.Header("authorization");
         if (header.GetLength() < 6 || header.Mid(0, 6).CompareNoCase("basic ") != 0)
            return false;

         AnsiString encoded = Trimmed(header.Mid(6));
         AnsiString decoded = Base64::Decode(encoded, encoded.GetLength());
         int separator = decoded.Find(":");
         if (separator <= 0)
            return false;

         String username = Wide(decoded.Mid(0, separator));
         String password = Wide(decoded.Mid(separator + 1));

         bool viaAppPassword = false;
         account = AccountLogon().Logon(request.peer, username, password, disconnect, &viaAppPassword);
         if (!account)
         {
            LOG_APPLICATION("CardDAV: account authentication failed for " + LogSafe(username) + " from " + String(request.peer.ToString()) + ".");
            return false;
         }

         if (!account->GetActive())
         {
            LOG_APPLICATION("CardDAV: account " + username + " authenticated but is inactive; refused.");
            account.reset();
            return false;
         }

         return true;
      }

      //------------------------------------------------------------------------
      // The methods
      //------------------------------------------------------------------------

      bool ReadBody(const HttpRequest &request, XmlElement &root, AnsiString &problem)
      {
         XmlReader reader(request.body);
         return reader.Read(root, problem);
      }

      int DepthOf(const HttpRequest &request)
      {
         AnsiString depth = Lower(Trimmed(request.Header("depth")));
         if (depth == "0")
            return 0;
         // Absent, 1 and infinity: one level. See the file comment.
         return 1;
      }

      HttpResponse HandleOptions(const Resource &resource)
      {
         bool contact = resource.kind == KindContact || resource.kind == KindContactSlot;
         return Respond(200, "text/plain", "", contact ? AllowContact : AllowCollection);
      }

      HttpResponse HandlePropfind(Context &context, const Resource &resource)
      {
         if (resource.kind == KindContactSlot)
            return NotFound();

         PropertyRequest request;
         if (!Trimmed(context.request.body).IsEmpty())
         {
            XmlElement root;
            AnsiString problem;
            if (!ReadBody(context.request, root, problem))
               return Text(400, "the PROPFIND body is not well-formed XML: " + problem);
            if (!root.Is(NsDav, "propfind"))
               return Text(400, "the PROPFIND body must be a DAV:propfind element");

            if (root.Child(NsDav, "propname"))
               request.mode = ModePropName;
            else if (const XmlElement *prop = root.Child(NsDav, "prop"))
               ReadPropertyRequest(prop, request);
            else
            {
               request.mode = ModeAllProp;
               // D:include names properties to add to allprop; they are
               // answered by a second pass over the named ones.
               if (const XmlElement *include = root.Child(NsDav, "include"))
               {
                  PropertyRequest included;
                  ReadPropertyRequest(include, included);
                  request.names = included.names;
               }
            }
         }

         int depth = DepthOf(context.request);

         AnsiString xml = MultistatusOpen();

         // The resource itself, and for allprop with D:include the extra ones.
         const Member *self = nullptr;
         Member selfMember;
         if (resource.kind == KindContact)
         {
            FillMember(context, resource.contact, selfMember);
            self = &selfMember;
         }
         xml += ResponseFor(context, resource, self, request);

         if (depth == 1)
         {
            switch (resource.kind)
            {
            case KindRoot:
            {
               Resource principals;
               principals.kind = KindPrincipalCollection;
               principals.href = AnsiString(CardDavServer::ContextPath) + "principals/";
               xml += ResponseFor(context, principals, nullptr, request);

               Resource homes;
               homes.kind = KindHomeCollection;
               homes.href = AnsiString(CardDavServer::ContextPath) + "addressbooks/";
               xml += ResponseFor(context, homes, nullptr, request);
               break;
            }
            case KindPrincipalCollection:
            {
               Resource principal;
               principal.kind = KindPrincipal;
               principal.href = context.principalHref;
               xml += ResponseFor(context, principal, nullptr, request);
               break;
            }
            case KindHomeCollection:
            {
               Resource home;
               home.kind = KindHome;
               home.href = context.homeHref;
               xml += ResponseFor(context, home, nullptr, request);
               break;
            }
            case KindHome:
            {
               Resource book;
               book.kind = KindAddressBook;
               book.href = context.bookHref;
               xml += ResponseFor(context, book, nullptr, request);
               break;
            }
            case KindAddressBook:
            {
               if (!EnsureListed(context))
                  return Text(500, "the contacts could not be read");
               for (size_t i = 0; i < context.members.size(); i++)
                  xml += ResponseForMember(context, context.members[i], request);
               break;
            }
            default:
               break;
            }
         }

         xml += "</D:multistatus>\r\n";
         return Xml(207, xml);
      }

      // Nothing here is writable by PROPPATCH: the one address book has the
      // name it has. Each property asked for is answered 403 in the 207, which
      // is how RFC 4918 section 9.2 says to refuse some and not all - and a
      // client that tries to name its address book learns so per property
      // rather than from a bare failure.
      HttpResponse HandleProppatch(Context &context, const Resource &resource)
      {
         if (resource.kind == KindContactSlot)
            return NotFound();

         XmlElement root;
         AnsiString problem;
         if (!ReadBody(context.request, root, problem))
            return Text(400, "the PROPPATCH body is not well-formed XML: " + problem);
         if (!root.Is(NsDav, "propertyupdate"))
            return Text(400, "the PROPPATCH body must be a DAV:propertyupdate element");

         AnsiString refused;
         for (size_t i = 0; i < root.children.size(); i++)
         {
            const XmlElement &action = root.children[i];
            if (!action.Is(NsDav, "set") && !action.Is(NsDav, "remove"))
               continue;
            const XmlElement *prop = action.Child(NsDav, "prop");
            if (!prop)
               continue;
            for (size_t j = 0; j < prop->children.size(); j++)
            {
               PropertyName name;
               name.ns = prop->children[j].ns;
               name.name = prop->children[j].name;
               refused += PropertyElement(name, "");
            }
         }

         AnsiString xml = MultistatusOpen();
         xml += " <D:response>\r\n  " + Href(resource.href) + "\r\n";
         xml += "  <D:propstat><D:prop>" + refused + "</D:prop><D:status>HTTP/1.1 403 Forbidden</D:status>"
                "<D:responsedescription>The properties of this address book are fixed; none can be set or removed.</D:responsedescription></D:propstat>\r\n";
         xml += " </D:response>\r\n</D:multistatus>\r\n";
         return Xml(207, xml);
      }

      HttpResponse HandleGet(Context &context, const Resource &resource, bool head)
      {
         if (resource.kind == KindRoot)
         {
            AnsiString text = "hMailServer CardDAV. Your principal is " + context.principalHref +
               " and your address book is " + context.bookHref + ".";
            return Respond(200, "text/plain; charset=utf-8", head ? AnsiString("") : text + "\r\n");
         }

         if (resource.kind == KindContactSlot)
            return NotFound();

         if (resource.kind != KindContact)
            return MethodNotAllowed(resource);

         Member member;
         FillMember(context, resource.contact, member);

         AnsiString headers = "ETag: " + member.etag + "\r\n";

         AnsiString ifNoneMatch = Trimmed(context.request.Header("if-none-match"));
         if (!ifNoneMatch.IsEmpty() && ifNoneMatch != "*")
         {
            std::vector<AnsiString> tags = StringParser::SplitString(ifNoneMatch, ",");
            for (size_t i = 0; i < tags.size(); i++)
            {
               AnsiString tag = Trimmed(tags[i]);
               if (tag.StartsWith("W/"))
                  tag = tag.Mid(2);
               if (tag == member.etag)
                  return Respond(304, "text/vcard; charset=utf-8", "", headers);
            }
         }

         return Respond(200, "text/vcard; charset=utf-8", head ? AnsiString("") : member.card, headers);
      }

      // The If-Match / If-None-Match rules of RFC 7232 for a write. current is
      // the ETag of what is there, or empty when nothing is. False means 412.
      bool PreconditionsHold(const HttpRequest &request, const AnsiString &current)
      {
         AnsiString ifMatch = Trimmed(request.Header("if-match"));
         if (!ifMatch.IsEmpty())
         {
            if (current.IsEmpty())
               return false;
            if (ifMatch != "*")
            {
               bool matched = false;
               std::vector<AnsiString> tags = StringParser::SplitString(ifMatch, ",");
               for (size_t i = 0; !matched && i < tags.size(); i++)
                  matched = Trimmed(tags[i]) == current;
               if (!matched)
                  return false;
            }
         }

         AnsiString ifNoneMatch = Trimmed(request.Header("if-none-match"));
         if (!ifNoneMatch.IsEmpty())
         {
            if (ifNoneMatch == "*")
               return current.IsEmpty();

            std::vector<AnsiString> tags = StringParser::SplitString(ifNoneMatch, ",");
            for (size_t i = 0; i < tags.size(); i++)
            {
               AnsiString tag = Trimmed(tags[i]);
               if (tag.StartsWith("W/"))
                  tag = tag.Mid(2);
               if (!current.IsEmpty() && tag == current)
                  return false;
            }
         }

         return true;
      }

      bool IsVCardContentType(const AnsiString &contentType)
      {
         AnsiString type = Lower(Trimmed(contentType));
         int semicolon = type.Find(";");
         if (semicolon >= 0)
            type = Trimmed(type.Mid(0, semicolon));
         return type.IsEmpty() || type == "text/vcard" || type == "text/x-vcard" || type == "text/directory";
      }

      HttpResponse HandlePut(Context &context, const Resource &resource)
      {
         if (resource.kind != KindContact && resource.kind != KindContactSlot)
            return MethodNotAllowed(resource);

         const HttpRequest &request = context.request;

         if (!IsVCardContentType(request.Header("content-type")))
            return DavError(415, "<C:supported-address-data/>", "The body must be a vCard: text/vcard.");

         if (request.body.GetLength() > MaxCardBytes)
            return DavError(403, "<C:max-resource-size/>", "The card is larger than " + IntText(MaxCardBytes) + " bytes.");

         std::vector<VCardProperty> properties;
         AnsiString problem;
         if (!VCard::Parse(request.body, properties, problem))
            return DavError(403, "<C:valid-address-data/>", "The body is not one vCard: " + problem + ".");

         AnsiString legacy;
         if (VCard::UsesLegacyEncoding(properties, legacy))
            return DavError(403, "<C:valid-address-data/>",
               "The card uses a vCard 2.1 encoding this server does not decode (" + legacy +
               "); send vCard 3.0 or 4.0, UTF-8 throughout.");

         AnsiString nameUtf8;
         AnsiString addressUtf8;
         if (!VCard::ExtractNameAndAddress(properties, nameUtf8, addressUtf8))
            return DavError(403, "<C:valid-address-data/>",
               "This address book keeps a name and one e-mail address per contact, and the card has no EMAIL; "
               "there is nothing to store it as. Add an e-mail address to the contact.");

         String name = Wide(nameUtf8);
         name.TrimLeft();
         name.TrimRight();
         if (name.GetLength() > ContactStore::MaximumNameLength)
            name = name.Mid(0, ContactStore::MaximumNameLength);

         String address = Wide(addressUtf8);
         if (!ContactStore::IsValidAddress(address))
            return DavError(403, "<C:valid-address-data/>", "The EMAIL is not one e-mail address: " + addressUtf8);

         __int64 accountId = context.account->GetID();

         // The card's own UID is kept; a card without one gets a stable one -
         // for an existing contact the one it had, for a new one derived from
         // the name it was created under - so the same card always carries the
         // same UID and a client never sees a contact change identity.
         AnsiString uid = VCard::UidOf(properties);
         if (uid.IsEmpty())
         {
            uid = resource.kind == KindContact
               ? (resource.contact.uid.IsEmpty() ? StableUid_(context.addressUtf8, resource.contact.id) : Utf8(resource.contact.uid))
               : StableUidFromSeed_("hMailServer.carddav.resource:" + context.addressUtf8 + ":" + resource.slotName);
         }

         // The card is kept as sent: what a GET returns is what the client
         // wrote, so the ETag answered here is the ETag of the stored
         // representation, as RFC 9110 section 9.3.4 requires of a PUT.
         const AnsiString &card = request.body;

         if (resource.kind == KindContact)
         {
            Member current;
            FillMember(context, resource.contact, current);
            if (!PreconditionsHold(request, current.etag))
               return Text(412, "precondition failed: the contact has changed since it was read, or If-None-Match: * named an existing resource",
                  "ETag: " + current.etag + "\r\n");

            // The address is the row's identity in this store: moving this
            // contact onto another contact's address would be two rows for
            // one address, which the table refuses.
            __int64 other = 0;
            if (ContactStore::FindByAddress(accountId, address, other) && other != resource.contact.id)
            {
               ContactRecord otherRecord;
               AnsiString otherHref = ContactStore::Get(accountId, other, otherRecord) ? ContactHref(context, otherRecord) : AnsiString("");
               return Text(409, "another contact of this address book already has the address " + addressUtf8 +
                  " (" + otherHref + "); a contact is one address here");
            }

            if (!ContactStore::UpdateCard(accountId, resource.contact.id, name, address, Wide(uid), Wide(card)))
               return Text(500, "the contact could not be saved");

            ContactRecord updated = resource.contact;
            updated.name = name;
            updated.address = address;
            updated.uid = Wide(uid);
            updated.vcard = Wide(card);
            Member written;
            FillMember(context, updated, written);
            LOG_DEBUG("CardDAV: " + String(context.addressUtf8) + " updated contact " + StringParser::IntToString(updated.id) + ".");
            return Respond(204, "text/plain", "", "ETag: " + written.etag + "\r\n");
         }

         // A new resource, under the client's name. Nothing is there, so an
         // If-Match cannot hold; If-None-Match: * does.
         if (!PreconditionsHold(request, ""))
            return Text(412, "precondition failed: nothing exists at this URL for If-Match to match");

         // A contact is one address here. A card for an address the book
         // already holds - a contact the webmail collected, or one a client
         // created before under another name - is not a second contact and is
         // not this one either: the client asked to create, and rewriting the
         // other contact under its name would leave the client with two copies
         // of one row. 409 names the contact that has the address.
         __int64 existing = 0;
         ContactRecord record;
         if (ContactStore::FindByAddress(accountId, address, existing))
         {
            ContactRecord existingRecord;
            AnsiString existingHref = ContactStore::Get(accountId, existing, existingRecord) ? ContactHref(context, existingRecord) : AnsiString("");
            return Text(409, "this address book already has a contact with the address " + addressUtf8 +
               " (" + existingHref + "); a contact is one address here - update that one, or delete it first");
         }

         if (!ContactStore::InsertCard(accountId, name, address, Wide(resource.slotName), Wide(uid), Wide(card), record))
         {
            // Two clients creating the same address at once: the second insert
            // is refused by the table's unique index, and the answer is the same
            // 409 as if the first had come a moment earlier.
            if (ContactStore::FindByAddress(accountId, address, existing))
            {
               ContactRecord existingRecord;
               AnsiString existingHref = ContactStore::Get(accountId, existing, existingRecord) ? ContactHref(context, existingRecord) : AnsiString("");
               return Text(409, "this address book already has a contact with the address " + addressUtf8 + " (" + existingHref + ")");
            }
            return Text(500, "the contact could not be saved");
         }

         Member created;
         FillMember(context, record, created);
         LOG_DEBUG("CardDAV: " + String(context.addressUtf8) + " created contact " + StringParser::IntToString(record.id) + " at " + String(created.href) + ".");

         // The contact lives where the client put it: the Location is the
         // request's own URL, canonically encoded.
         return Respond(201, "text/plain", "", "ETag: " + created.etag + "\r\nLocation: " + created.href + "\r\n");
      }

      HttpResponse HandleDelete(Context &context, const Resource &resource)
      {
         if (resource.kind == KindContactSlot)
            return NotFound();
         if (resource.kind != KindContact)
            return Text(403, "the address book and the collections above it cannot be deleted");

         Member current;
         FillMember(context, resource.contact, current);
         if (!PreconditionsHold(context.request, current.etag))
            return Text(412, "precondition failed: the contact has changed since it was read", "ETag: " + current.etag + "\r\n");

         if (!ContactStore::Delete(context.account->GetID(), resource.contact.id))
            return NotFound();

         LOG_DEBUG("CardDAV: " + String(context.addressUtf8) + " deleted contact " + StringParser::IntToString(resource.contact.id) + ".");
         return Respond(204, "text/plain", "");
      }

      //------------------------------------------------------------------------
      // The reports
      //------------------------------------------------------------------------

      HttpResponse HandleMultiget(Context &context, const XmlElement &report)
      {
         PropertyRequest request;
         if (const XmlElement *prop = report.Child(NsDav, "prop"))
            ReadPropertyRequest(prop, request);
         else
         {
            request.mode = ModeProp;
            request.names.push_back(PropertyName(NsDav, "getetag"));
            request.names.push_back(PropertyName(NsCardDav, "address-data"));
         }

         if (!EnsureListed(context))
            return Text(500, "the contacts could not be read");

         AnsiString xml = MultistatusOpen();
         size_t hrefs = 0;
         for (size_t i = 0; i < report.children.size(); i++)
         {
            if (!report.children[i].Is(NsDav, "href"))
               continue;

            if (++hrefs > MaxMultigetHrefs)
               return Text(400, "too many hrefs in one multiget");

            // An href may be absolute; only its path is compared, decoded, so
            // that a client's own encoding of the address still matches.
            AnsiString href = Trimmed(report.children[i].text);
            int scheme = href.Find("://");
            if (scheme >= 0)
            {
               int slash = href.Find("/", scheme + 3);
               href = slash >= 0 ? href.Mid(slash) : AnsiString("/");
            }

            // Matched against the members already in memory, decoded on both
            // sides so a client's own encoding of the address still matches;
            // no lookup in the store per href.
            AnsiString wanted = PercentDecode(href);
            const Member *found = nullptr;
            for (size_t j = 0; !found && j < context.members.size(); j++)
            {
               if (PercentDecode(context.members[j].href) == wanted)
                  found = &context.members[j];
            }

            if (found)
               xml += ResponseForMember(context, *found, request);
            else
               xml += " <D:response>\r\n  " + Href(href) + "\r\n  <D:status>HTTP/1.1 404 Not Found</D:status>\r\n </D:response>\r\n";
         }

         xml += "</D:multistatus>\r\n";
         return Xml(207, xml);
      }

      struct TextMatch
      {
         TextMatch() : negate(false) { }

         AnsiString text;        // UTF-8
         AnsiString collation;   // i;unicode-casemap by default
         AnsiString matchType;   // contains by default
         bool negate;
      };

      struct PropFilter
      {
         PropFilter() : isNotDefined(false), allOf(false) { }

         AnsiString name;        // upper-cased vCard property name
         bool isNotDefined;
         bool allOf;             // test="allof"; anyof by default
         std::vector<TextMatch> textMatches;
      };

      struct Filter
      {
         Filter() : allOf(false) { }

         bool allOf;
         std::vector<PropFilter> propFilters;
      };

      void ReadFilter(const XmlElement &filter, Filter &result)
      {
         result.allOf = Lower(filter.Attribute("test")) == "allof";

         for (size_t i = 0; i < filter.children.size(); i++)
         {
            const XmlElement &element = filter.children[i];
            if (!element.Is(NsCardDav, "prop-filter"))
               continue;

            PropFilter propFilter;
            propFilter.name = element.Attribute("name");
            propFilter.name.ToUpper();
            propFilter.allOf = Lower(element.Attribute("test")) == "allof";

            for (size_t j = 0; j < element.children.size(); j++)
            {
               const XmlElement &condition = element.children[j];
               if (condition.Is(NsCardDav, "is-not-defined"))
                  propFilter.isNotDefined = true;
               else if (condition.Is(NsCardDav, "text-match"))
               {
                  TextMatch match;
                  match.text = condition.text;
                  match.collation = Lower(condition.Attribute("collation"));
                  match.matchType = Lower(condition.Attribute("match-type"));
                  match.negate = Lower(condition.Attribute("negate-condition")) == "yes";
                  propFilter.textMatches.push_back(match);
               }
               // param-filter is not evaluated: the cards here carry no
               // parameter a client filters on, and a filter that cannot be
               // applied does not narrow the answer.
            }

            result.propFilters.push_back(propFilter);
         }
      }

      // The values a contact has for a vCard property, as the card would
      // carry them.
      void PropertyValues(const Member &member, const AnsiString &name, std::vector<AnsiString> &values)
      {
         AnsiString nameUtf8 = Utf8(member.record.name);
         AnsiString addressUtf8 = Utf8(member.record.address);

         if (name == "FN")
            values.push_back(nameUtf8.IsEmpty() ? addressUtf8 : nameUtf8);
         else if (name == "N")
         {
            AnsiString family;
            AnsiString given;
            VCard::SplitName(nameUtf8, family, given);
            if (!family.IsEmpty())
               values.push_back(family);
            if (!given.IsEmpty())
               values.push_back(given);
            if (values.empty())
               values.push_back("");
         }
         else if (name == "EMAIL")
            values.push_back(addressUtf8);
         else if (name == "UID")
            values.push_back(member.uid);
      }

      bool TextMatches(const TextMatch &match, const AnsiString &value)
      {
         bool caseSensitive = match.collation == "i;octet";
         AnsiString haystack = caseSensitive ? value : Lower(value);
         AnsiString needle = caseSensitive ? match.text : Lower(match.text);

         bool result;
         if (match.matchType == "equals")
            result = haystack == needle;
         else if (match.matchType == "starts-with")
            result = haystack.StartsWith(needle);
         else if (match.matchType == "ends-with")
            result = haystack.EndsWith(needle);
         else
            result = haystack.Find(needle) >= 0;

         return match.negate ? !result : result;
      }

      bool PropFilterMatches(const PropFilter &filter, const Member &member)
      {
         std::vector<AnsiString> values;
         PropertyValues(member, filter.name, values);

         if (filter.isNotDefined)
            return values.empty();

         if (filter.textMatches.empty())
            return !values.empty();

         if (values.empty())
            return false;

         bool anyMatched = false;
         bool allMatched = true;
         for (size_t i = 0; i < filter.textMatches.size(); i++)
         {
            bool matched = false;
            for (size_t j = 0; !matched && j < values.size(); j++)
               matched = TextMatches(filter.textMatches[i], values[j]);
            anyMatched = anyMatched || matched;
            allMatched = allMatched && matched;
         }

         return filter.allOf ? allMatched : anyMatched;
      }

      bool FilterMatches(const Filter &filter, const Member &member)
      {
         if (filter.propFilters.empty())
            return true;

         bool anyMatched = false;
         bool allMatched = true;
         for (size_t i = 0; i < filter.propFilters.size(); i++)
         {
            bool matched = PropFilterMatches(filter.propFilters[i], member);
            anyMatched = anyMatched || matched;
            allMatched = allMatched && matched;
         }

         return filter.allOf ? allMatched : anyMatched;
      }

      HttpResponse HandleQuery(Context &context, const XmlElement &report)
      {
         PropertyRequest request;
         if (const XmlElement *prop = report.Child(NsDav, "prop"))
            ReadPropertyRequest(prop, request);
         else
         {
            request.mode = ModeProp;
            request.names.push_back(PropertyName(NsDav, "getetag"));
            request.names.push_back(PropertyName(NsCardDav, "address-data"));
         }

         Filter filter;
         if (const XmlElement *filterElement = report.Child(NsCardDav, "filter"))
            ReadFilter(*filterElement, filter);

         int limit = -1;
         if (const XmlElement *limitElement = report.Child(NsCardDav, "limit"))
         {
            if (const XmlElement *results = limitElement->Child(NsCardDav, "nresults"))
            {
               limit = atoi(results->text.c_str());
               if (limit < 0)
                  limit = 0;
            }
         }

         if (!EnsureListed(context))
            return Text(500, "the contacts could not be read");

         AnsiString xml = MultistatusOpen();
         int answered = 0;
         for (size_t i = 0; i < context.members.size(); i++)
         {
            if (limit >= 0 && answered >= limit)
               break;
            if (!FilterMatches(filter, context.members[i]))
               continue;
            xml += ResponseForMember(context, context.members[i], request);
            answered++;
         }

         xml += "</D:multistatus>\r\n";
         return Xml(207, xml);
      }

      HttpResponse HandleSyncCollection(Context &context, const XmlElement &report)
      {
         PropertyRequest request;
         if (const XmlElement *prop = report.Child(NsDav, "prop"))
            ReadPropertyRequest(prop, request);
         else
         {
            request.mode = ModeProp;
            request.names.push_back(PropertyName(NsDav, "getetag"));
         }

         AnsiString presented;
         if (const XmlElement *token = report.Child(NsDav, "sync-token"))
            presented = Trimmed(token->text);

         AnsiString tag;
         if (!CollectionTag(context, tag))
            return Text(500, "the contacts could not be read");
         AnsiString current = SyncTokenFor(tag);

         AnsiString xml = MultistatusOpen();

         if (presented.IsEmpty())
         {
            // The initial sync: everything.
            for (size_t i = 0; i < context.members.size(); i++)
               xml += ResponseForMember(context, context.members[i], request);
         }
         else if (presented != current)
         {
            // The store keeps no change log, so a token that is not the
            // present state cannot be turned into the changes since it. The
            // client is told so and lists again (RFC 6578 section 3.2).
            return DavError(403, "<D:valid-sync-token/>",
               "The sync token is not the address book's current state, and this server keeps no change log to answer "
               "what changed since; ask again without a token for the whole address book.");
         }

         xml += " <D:sync-token>" + XmlEscape(current) + "</D:sync-token>\r\n";
         xml += "</D:multistatus>\r\n";
         return Xml(207, xml);
      }

      HttpResponse HandleReport(Context &context, const Resource &resource)
      {
         if (resource.kind == KindContactSlot)
            return NotFound();

         XmlElement root;
         AnsiString problem;
         if (!ReadBody(context.request, root, problem))
            return Text(400, "the REPORT body is not well-formed XML: " + problem);

         bool multiget = root.Is(NsCardDav, "addressbook-multiget");
         bool query = root.Is(NsCardDav, "addressbook-query");
         bool sync = root.Is(NsDav, "sync-collection");

         if (!multiget && !query && !sync)
            return DavError(403, "<D:supported-report/>", "The reports here are addressbook-multiget, addressbook-query and sync-collection.");

         // multiget is allowed at the address book and at a contact; the
         // other two are questions about the collection.
         bool onBook = resource.kind == KindAddressBook;
         bool onContact = resource.kind == KindContact;
         if (!onBook && !(multiget && onContact))
            return DavError(403, "<D:supported-report/>", "This report applies to the address book, " + context.bookHref + ".");

         if (multiget)
            return HandleMultiget(context, root);
         if (query)
            return HandleQuery(context, root);
         return HandleSyncCollection(context, root);
      }
   }

   bool
   CardDavServer::IsDavTarget(const AnsiString &target)
   {
      AnsiString path = target;
      int query = path.Find("?");
      if (query >= 0)
         path = path.Mid(0, query);

      return path == "/dav" || path.StartsWith(ContextPath);
   }

   bool
   CardDavServer::IsLargeRequest(const AnsiString &method, const AnsiString &target)
   {
      if (!IsDavTarget(target))
         return false;

      return method == "PUT" || method == "REPORT";
   }

   HttpResponse
   CardDavServer::Handle(const HttpRequest &request, bool over_https)
   {
      // Basic puts the account's password on the wire, so this surface is
      // HTTPS only - the same rule as the Apple configuration profile on this
      // listener, for the same reason. Refused before the credential is
      // looked at, so a password sent in clear is at least not also checked.
      if (!over_https)
      {
         return Text(403,
            "CardDAV is served over HTTPS only: HTTP Basic authentication would send the account's password in clear. "
            "Use the https:// address of this server (WebServicesHttpsPort), or put a TLS-terminating proxy in front "
            "of this listener that sets X-Forwarded-Proto: https.");
      }

      const AnsiString &method = request.method;

      Context context(request);
      bool disconnect = false;
      if (!Authenticate(request, context.account, disconnect))
      {
         HttpResponse refusal = Unauthorized();
         // The connection closes with the refusal, whatever the lockout said:
         // IMAP delays a wrong password before answering (the tarpit), and this
         // listener has no delay, so a guess costs a new connection instead of
         // running at wire speed down one kept-alive socket.
         refusal.close = true;
         return refusal;
      }

      context.addressWide = context.account->GetAddress();
      context.addressWide.ToLower();
      context.addressUtf8 = Utf8(context.addressWide);
      AnsiString segment = PercentEncodeSegment(context.addressUtf8);
      context.principalHref = AnsiString(ContextPath) + "principals/" + segment + "/";
      context.homeHref = AnsiString(ContextPath) + "addressbooks/" + segment + "/";
      context.bookHref = context.homeHref + "contacts/";

      Resource resource;
      if (!Resolve(context, request.target, resource))
         return NotFound();

      HttpResponse response;
      try
      {
         if (method == "OPTIONS")
            response = HandleOptions(resource);
         else if (method == "PROPFIND")
            response = HandlePropfind(context, resource);
         else if (method == "PROPPATCH")
            response = HandleProppatch(context, resource);
         else if (method == "REPORT")
            response = HandleReport(context, resource);
         else if (method == "GET")
            response = HandleGet(context, resource, false);
         else if (method == "HEAD")
            response = HandleGet(context, resource, true);
         else if (method == "PUT")
            response = HandlePut(context, resource);
         else if (method == "DELETE")
            response = HandleDelete(context, resource);
         else if (method == "MKCOL" || method == "MKCALENDAR")
            response = Text(403, "this server keeps one address book per account, Contacts; no collection can be made");
         else
            response = MethodNotAllowed(resource);
      }
      catch (...)
      {
         response = Text(500, "internal error");
      }

      response.close = response.close || disconnect;

      LOG_DEBUG("CardDAV: " + String(context.addressUtf8) + " " + String(method) + " " + String(request.target) + " -> " +
         StringParser::IntToString(response.status) + ".");

      return response;
   }
}

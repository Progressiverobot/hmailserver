// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"

#include "SieveMessage.h"

#include "../Mime/Mime.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   SieveMessage::SieveMessage(const String &rawMessage) :
      size_(0),
      raw_(rawMessage),
      bodyOffset_(-1)
   {
      Parse_(rawMessage);
   }

   void
   SieveMessage::Parse_(const String &rawMessage)
   {
      size_ = rawMessage.GetLength();

      // Find the header/body separator (a blank line). Support CRLF and LF.
      int boundary = rawMessage.Find(_T("\r\n\r\n"));
      int separatorLength = 4;
      if (boundary < 0)
      {
         boundary = rawMessage.Find(_T("\n\n"));
         separatorLength = 2;
      }

      String headerBlock = boundary >= 0 ? rawMessage.Mid(0, boundary) : rawMessage;

      // Where the body begins, for the "body" test. -1 when the message has no
      // blank line at all, which means it has headers and no body - and that is
      // NOT the same as an empty body: a test against a message with no body must
      // find nothing to compare.
      bodyOffset_ = boundary >= 0 ? boundary + separatorLength : -1;

      // Normalise line endings and split into logical (unfolded) header lines.
      headerBlock.Replace(_T("\r\n"), _T("\n"));

      std::vector<String> lines;
      int start = 0;
      while (start <= headerBlock.GetLength())
      {
         int newline = headerBlock.Find(_T("\n"), start);
         if (newline < 0)
         {
            lines.push_back(headerBlock.Mid(start));
            break;
         }

         lines.push_back(headerBlock.Mid(start, newline - start));
         start = newline + 1;
      }

      String currentName;
      String currentValue;
      bool haveCurrent = false;

      for (const String &line : lines)
      {
         if (line.GetLength() > 0 && (line[0] == L' ' || line[0] == L'\t'))
         {
            // Folded continuation of the previous header.
            if (haveCurrent)
            {
               String continuation = line;
               continuation.TrimLeft();
               currentValue += _T(" ");
               currentValue += continuation;
            }
            continue;
         }

         // Commit the previous header.
         if (haveCurrent)
         {
            Header header;
            header.name = currentName;
            header.name.ToLower();
            header.value = currentValue;
            headers_.push_back(header);
            haveCurrent = false;
         }

         int colon = line.Find(_T(":"));
         if (colon <= 0)
            continue;

         currentName = line.Mid(0, colon);
         currentName.TrimRight();
         currentValue = line.Mid(colon + 1);
         currentValue.TrimLeft();
         haveCurrent = true;
      }

      if (haveCurrent)
      {
         Header header;
         header.name = currentName;
         header.name.ToLower();
         header.value = currentValue;
         headers_.push_back(header);
      }
   }

   std::vector<String>
   SieveMessage::GetHeaderValues(const String &name) const
   {
      String lowerName = name;
      lowerName.ToLower();

      std::vector<String> values;
      for (const Header &header : headers_)
      {
         if (header.name == lowerName)
            values.push_back(header.value);
      }

      return values;
   }

   bool
   SieveMessage::HasHeader(const String &name) const
   {
      String lowerName = name;
      lowerName.ToLower();

      for (const Header &header : headers_)
      {
         if (header.name == lowerName)
            return true;
      }

      return false;
   }

   bool
   SieveMessage::ContentTypeMatches_(const String &partType, const std::vector<String> &contentTypes)
   {
      for (const String &wanted : contentTypes)
      {
         // RFC 5173 5: an empty string matches every part, a bare top-level type
         // ("image") matches every subtype of it, and a full "type/subtype"
         // matches exactly.
         if (wanted.IsEmpty())
            return true;

         if (wanted.Find(_T("/")) >= 0)
         {
            if (partType.CompareNoCase(wanted) == 0)
               return true;

            continue;
         }

         String prefix = wanted;
         prefix += _T("/");

         if (partType.GetLength() >= prefix.GetLength() &&
             partType.Mid(0, prefix.GetLength()).CompareNoCase(prefix) == 0)
            return true;
      }

      return false;
   }

   void
   SieveMessage::CollectMatchingParts_(std::shared_ptr<MimeBody> part,
                                       const String &transform,
                                       const std::vector<String> &contentTypes,
                                       int depth,
                                       std::vector<String> &values)
   {
      if (!part)
         return;

      // A malformed or hostile message can nest parts deeply, and this walk is
      // recursive. Sieve runs on the delivery thread, so a stack overflow here is
      // a lost message at best. 20 levels is far past anything a real client
      // produces.
      if (depth > 20)
         return;

      if (part->IsMultiPart())
      {
         for (std::shared_ptr<MimeBody> child = part->FindFirstPart(); child; child = part->FindNextPart())
            CollectMatchingParts_(child, transform, contentTypes, depth + 1, values);

         return;
      }

      String partType = part->GetCleanContentType();

      // A part with no Content-Type is text/plain (RFC 2045 5.2).
      if (partType.IsEmpty())
         partType = _T("text/plain");

      const bool wanted = transform == _T("content")
         ? ContentTypeMatches_(partType, contentTypes)
         : partType.GetLength() >= 5 && partType.Mid(0, 5).CompareNoCase(_T("text/")) == 0;

      if (!wanted)
         return;

      // GetUnicodeText decodes the transfer encoding and the charset, which is
      // what both ":text" and ":content" are defined to compare against - a
      // script looking for a word must not have to know whether the sender chose
      // base64.
      values.push_back(part->GetUnicodeText());
   }

   std::vector<String>
   SieveMessage::GetBodyValues(const String &transform, const std::vector<String> &contentTypes) const
   {
      std::vector<String> values;

      // No blank line: headers only, so there is no body to compare against.
      if (bodyOffset_ < 0 || bodyOffset_ >= raw_.GetLength())
         return values;

      if (transform.CompareNoCase(_T("raw")) == 0)
      {
         // Undecoded and unstructured, exactly as it arrived.
         values.push_back(raw_.Mid(bodyOffset_));
         return values;
      }

      // ":text" and ":content" need the MIME structure, so the parser is given
      // the WHOLE message - the boundary it needs is declared in the headers.
      AnsiString rawMessage = raw_;

      MimeBody message;
      size_t index = 0;
      bool partLoaded = false;

      message.Load(rawMessage.c_str(), rawMessage.size(), index, partLoaded);

      if (!partLoaded)
         return values;

      // Not a multipart message: the message itself is the single part, and its
      // own Content-Type decides whether it counts.
      CollectMatchingParts_(std::shared_ptr<MimeBody>(&message, [](MimeBody *) {}),
                            transform.CompareNoCase(_T("content")) == 0 ? _T("content") : _T("text"),
                            contentTypes, 0, values);

      return values;
   }

   std::vector<String>
   SieveMessage::ExtractAddresses(const String &headerValue, const String &addressPart)
   {
      std::vector<String> result;

      // Split the header value into comma-separated address entries.
      std::vector<String> entries;
      int start = 0;
      int length = headerValue.GetLength();
      for (int i = 0; i <= length; i++)
      {
         if (i == length || headerValue[i] == L',')
         {
            entries.push_back(headerValue.Mid(start, i - start));
            start = i + 1;
         }
      }

      for (String entry : entries)
      {
         entry.Trim();
         if (entry.IsEmpty())
            continue;

         // Prefer the address inside angle brackets when present.
         String address = entry;
         int lt = entry.Find(_T("<"));
         if (lt >= 0)
         {
            int gt = entry.Find(_T(">"), lt + 1);
            if (gt > lt)
               address = entry.Mid(lt + 1, gt - lt - 1);
         }

         address.Trim();
         if (address.IsEmpty())
            continue;

         if (addressPart.CompareNoCase(_T("localpart")) == 0)
         {
            int at = address.Find(_T("@"));
            result.push_back(at >= 0 ? address.Mid(0, at) : address);
         }
         else if (addressPart.CompareNoCase(_T("domain")) == 0)
         {
            int at = address.Find(_T("@"));
            result.push_back(at >= 0 ? address.Mid(at + 1) : _T(""));
         }
         else
         {
            // ":all" (the default).
            result.push_back(address);
         }
      }

      return result;
   }
}

// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// http://www.hmailserver.com

#include "StdAfx.h"

#include "SieveMessage.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   SieveMessage::SieveMessage(const String &rawMessage) :
      size_(0)
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
      (void)separatorLength;

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

// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#include "StdAfx.h"

#include "Canonicalization.h"
#include "../../MIME/MimeCode.h"
#include "../../MIME/Mime.h"
#include "..\../Util\Parsing\StringParser.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   AnsiString
   Canonicalization::GetDKIMWithoutSignature_(AnsiString value)
   {
      // RFC 6376 3.7: when the DKIM-Signature field itself is hashed, the value
      // of its b= tag is treated as the empty string. Everything else - the tag
      // name, the semicolons, and any folding whitespace under "simple" - has to
      // survive byte for byte, because that is what the signer hashed.
      //
      // This used to locate the tag with value.Find("b="), which is not a tag
      // boundary. The bh= value is base64 and therefore ends with '=' padding, so
      // any body hash whose last base64 character is 'b' contains the sequence
      // "b=" - one signed message in 64. The search then matched inside bh=,
      // nothing was stripped (the two edits cancelled out), the real signature
      // stayed in the hash input, and the signature could not verify: a valid
      // signature reported as a permanent failure, and with it the DKIM failure
      // score and the loss of DMARC alignment for that message.
      //
      // Walk the tag list on its ';' boundaries instead.
      int position = 0;
      const int length = value.GetLength();

      while (position < length)
      {
         const int semicolonPosition = value.Find(";", position);
         const int tagEnd = semicolonPosition < 0 ? length : semicolonPosition;

         AnsiString tag = value.Mid(position, tagEnd - position);

         const int equalsPosition = tag.Find("=");
         if (equalsPosition > 0)
         {
            AnsiString tagName = tag.Mid(0, equalsPosition);

            // Trim: under "simple" the value is still folded, so a tag can be
            // preceded by CRLF and WSP. Tag names are case sensitive (RFC 6376
            // 3.2), so only a lower case "b" is the signature.
            tagName.Trim();

            if (tagName == "b")
            {
               // Keep everything up to and including this tag's '=', then
               // resume at the ';' - the remaining tags are unchanged.
               AnsiString result = value.Mid(0, position + equalsPosition + 1);

               if (semicolonPosition >= 0)
                  result += value.Mid(semicolonPosition);

               return result;
            }
         }

         if (semicolonPosition < 0)
            break;

         position = semicolonPosition + 1;
      }

      // No signature tag at all. Unchanged from the original behaviour: the
      // caller gets an empty value, and such a signature is refused by
      // DKIM::ValidateHeaderContents_ before it reaches verification anyway.
      return "";
   }


   AnsiString
   RelaxedCanonicalization::CanonicalizeBody(AnsiString value)
   {
      /*
         Ignores all whitespace at the end of lines.  Implementations MUST
         NOT remove the CRLF at the end of the line.

         Reduces all sequences of WSP within a line to a single SP
         character.

         Ignores all empty lines at the end of the message body.  "Empty
         line" is defined in Section 3.4.3.
      */
      
      std::vector<AnsiString> lines = StringParser::SplitString(value, "\r\n");

      // Everything here stays in AnsiString. The body is the input to a hash, so
      // it has to keep its bytes: the previous version collected the lines into
      // std::vector<String> and joined them, which took every byte of every
      // message body through a narrow -> wide -> narrow conversion. It happens to
      // round-trip today only because nothing in the process ever sets a locale,
      // which is a very long way from a guarantee for the one buffer in the
      // server that must be byte-exact.
      std::vector<AnsiString> output;

      // Empty lines are held back until a non-empty line proves they are not the
      // trailing ones, which RFC 6376 3.4.4 says to ignore.
      std::vector<AnsiString> pendingEmptyLines;

      for (AnsiString line : lines)
      {
         line.Replace("\t", " ");

         //   o  Reduces all sequences of WSP within a line to a single SP character.
         while (line.Find("  ") >= 0)
            line.Replace("  ", " ");

         line.TrimRight();

         pendingEmptyLines.push_back(line);

         if (!line.IsEmpty())
         {
            for (const AnsiString &pendingLine : pendingEmptyLines)
            {
               output.push_back(pendingLine);
            }
            pendingEmptyLines.clear();
         }
      }

      AnsiString result;
      for (const AnsiString &outputLine : output)
      {
         // Every retained line ends with a CRLF, including the last one.
         result += outputLine;
         result += "\r\n";
      }

      return result;
   }

   AnsiString 
   RelaxedCanonicalization::CanonicalizeHeader(AnsiString header, const std::pair<AnsiString, AnsiString> &signatureField, const std::vector<AnsiString> &fieldsToInclude, AnsiString &fieldList)
   {
      MimeHeader mimeHeader;
      mimeHeader.Load(header, header.GetLength(), true);

      std::vector<MimeField> fields = mimeHeader.Fields();

      String result;
      for(AnsiString headerField : fieldsToInclude)
      {  
         headerField.Trim();

         // locate this field, starting from the end.
         String value;
         for (size_t i = fields.size(); i > 0; i--)
         {
            size_t fieldIndex = i - 1;
            MimeField field = fields[fieldIndex];

            if (headerField.CompareNoCase(field.GetName()) == 0)
            {
               // found
               fields.erase(fields.begin() + fieldIndex);

               value = field.GetValue();
               // Fix for DKIM Header verification failing on empty header value, for example: subject header
               if (value.GetLength() == 0)
               {
                  value += "\r\n";
               }
               break;
            }
         }

         if (value.GetLength() > 0)
         {
            /* 
            Convert all header field names (not the header field values) to
            lowercase.  For example, convert "SUBJect: AbC" to "subject: AbC".
            Delete any WSP characters remaining before and after the colon
            separating the header field name from the header field value.  The
            colon separator MUST be retained.
            */

            String relaxedFieldValue;
            String relaxedHeaderName = CanonicalizeHeaderName(headerField);
            relaxedFieldValue = CanonicalizeHeaderValue(value);

            result += relaxedHeaderName + ":" + relaxedFieldValue + "\r\n";

            if (!fieldList.IsEmpty())
               fieldList += ":";

            fieldList += headerField;
         }
      }

      if (!signatureField.first.IsEmpty())
      {
         // Don't pick the value from the actual header, use the header we're verifying instead
         // If there are more than one DKIM-signature fields in the header, this will be important.
         AnsiString  relaxedHeaderName = CanonicalizeHeaderName(signatureField.first);
         AnsiString relaxedFieldValue = CanonicalizeHeaderValue(signatureField.second);

         //and without a trailing CRLF.
         result += relaxedHeaderName + ":" + GetDKIMWithoutSignature_(relaxedFieldValue);
      }

      return result;
   }     


   AnsiString 
   RelaxedCanonicalization::CanonicalizeHeaderName(AnsiString name)
   {
      /*
      Unfold all header field continuation lines as described in
      [RFC2822]; in particular, lines with terminators embedded in
      continued header field values (that is, CRLF sequences followed by
      WSP) MUST be interpreted without the CRLF.  Implementations MUST
      NOT remove the CRLF at the end of the header field value.
      */

      name.ToLower();
      name.Trim();

      return name;
   }


   AnsiString 
   RelaxedCanonicalization::CanonicalizeHeaderValue(AnsiString value)
   {
      /*
      Unfold all header field continuation lines as described in
      [RFC2822]; in particular, lines with terminators embedded in
      continued header field values (that is, CRLF sequences followed by
      WSP) MUST be interpreted without the CRLF.  Implementations MUST
      NOT remove the CRLF at the end of the header field value.
      */

      value.Replace("\r\n ", " ");
      value.Replace("\r\n\t", " ");

      /*
      Convert all sequences of one or more WSP characters to a single SP
      character.  WSP characters here include those before and after a
      line folding boundary.
      */

      value.Replace("\t ", " ");
      value.Replace(" \t", " ");
      
      while (value.Find("  ") >= 0)
         value.Replace("  ", " ");

      /*
      Delete all WSP characters at the end of each unfolded header field value.
      */

      while (value.EndsWith(" ") || value.EndsWith("\t"))
         value = value.Mid(0, value.GetLength() -1);

      /* Delete any WSP characters remaining before and after the colon
      separating the header field name from the header field value.  The
      colon separator MUST be retained.
      */
      value.Trim();


      return value;
   }


   AnsiString 
   SimpleCanonicalization::CanonicalizeBody(AnsiString value)
   {
      // remove all empty lines.
      while (value.EndsWith("\r\n"))
         value = value.Mid(0, value.GetLength()-2);
      
      value += "\r\n";
      
      return value;
   }

   AnsiString 
   SimpleCanonicalization::CanonicalizeHeader(AnsiString header, const std::pair<AnsiString, AnsiString> &signatureField, const std::vector<AnsiString> &fieldsToInclude, AnsiString &fieldList)
   {
      // first build a formatted list of header lines.
      std::vector<AnsiString> formattedHeaderLines;

      AnsiString result;
      std::vector<AnsiString> headerLines = StringParser::SplitString(header, "\r\n");

      AnsiString foldedLines;
      for (size_t i = headerLines.size(); i > 0; i--)
      {
         AnsiString line = headerLines[i-1];

         if (line.StartsWith(" ") || line.StartsWith("\t"))
         {
            // line is folded. append to next.
            foldedLines = line + "\r\n" + foldedLines;
         }
         else
         {
            // we have a line!
            int colonPos = line.Find(":");
            if (colonPos < 0)
            {
               assert(0); // broken header.
               continue;
            }

            AnsiString entireHeaderField = line + "\r\n" + foldedLines;

            formattedHeaderLines.push_back(entireHeaderField);

            foldedLines = "";
         }
      }

      for(AnsiString fieldToInclude : fieldsToInclude)
      {
         fieldToInclude.Trim();

         // locate the header line.
         auto iter = formattedHeaderLines.begin();
         auto iterEnd = formattedHeaderLines.end();
         
         for (; iter != iterEnd; iter++)
         {
            AnsiString headerLine = (*iter);

            int colonPos = headerLine.Find(":");
            AnsiString headerName = headerLine.Mid(0, colonPos);

            if (headerName.CompareNoCase(fieldToInclude) == 0) 
            {
               result += headerLine;

               if (!fieldList.IsEmpty())
                  fieldList += ":";

               fieldList += headerName;

               formattedHeaderLines.erase(iter);
               break;
            }
         }

      }

      if (!signatureField.first.IsEmpty())
      {
         // Don't pick the value from the actual header, use the header we're verifying instead
         // If there are more than one DKIM-signature fields in the header, this will be important.
         AnsiString headerName = signatureField.first;

         AnsiString headerLine = headerName + ": " + GetDKIMWithoutSignature_(signatureField.second);

         if (headerLine.EndsWith("\r\n"))
            headerLine = headerLine.Mid(0, headerLine.GetLength()-2);

         result += headerLine;
      }


      return result;
   }

   void
   CanonicalizationTester::Test()
   {
      const std::vector<AnsiString> noHeaderFields;
      const AnsiString messageHeader = "From: sender@example.test\r\nSubject: test\r\n";

      // -- The b= tag is removed from the DKIM-Signature field, and nothing else.
      {
         // The body hash below ends with "...b=", i.e. its last base64 character
         // is 'b' followed by the padding. That is one signed message in 64, and
         // it is what the old Find("b=") matched instead of the signature.
         const AnsiString signatureValue =
            "v=1; a=rsa-sha256; c=simple/simple; d=example.test; s=sel; h=from:subject; "
            "bh=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAb=; b=SIGNATUREVALUE";

         std::pair<AnsiString, AnsiString> signatureField("DKIM-Signature", signatureValue);

         AnsiString fieldList;
         SimpleCanonicalization simple;
         AnsiString result = simple.CanonicalizeHeader(messageHeader, signatureField, noHeaderFields, fieldList);

         // The signature value must not be part of what is hashed...
         if (result.Find("SIGNATUREVALUE") >= 0)
            throw 0;

         // ...the body hash must be, in full, padding included...
         if (result.Find("bh=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAb=;") < 0)
            throw 0;

         // ...and the tag itself stays, with an empty value.
         if (!result.EndsWith("; b="))
            throw 0;
      }

      // -- The same, for a body hash that does not end in 'b' (the other 63/64).
      {
         const AnsiString signatureValue =
            "v=1; a=rsa-sha256; c=simple/simple; d=example.test; s=sel; h=from:subject; "
            "bh=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=; b=SIGNATUREVALUE";

         std::pair<AnsiString, AnsiString> signatureField("DKIM-Signature", signatureValue);

         AnsiString fieldList;
         SimpleCanonicalization simple;
         AnsiString result = simple.CanonicalizeHeader(messageHeader, signatureField, noHeaderFields, fieldList);

         if (result.Find("SIGNATUREVALUE") >= 0)
            throw 0;
         if (!result.EndsWith("; b="))
            throw 0;
      }

      // -- b= is not required to be the last tag. Tags after it are kept.
      {
         const AnsiString signatureValue =
            "v=1; a=rsa-sha256; d=example.test; s=sel; h=from; b=SIGNATUREVALUE; bh=AAAb=";

         std::pair<AnsiString, AnsiString> signatureField("DKIM-Signature", signatureValue);

         AnsiString fieldList;
         RelaxedCanonicalization relaxed;
         AnsiString result = relaxed.CanonicalizeHeader(messageHeader, signatureField, noHeaderFields, fieldList);

         if (result.Find("SIGNATUREVALUE") >= 0)
            throw 0;
         if (result.Find("b=; bh=AAAb=") < 0)
            throw 0;

         // Relaxed lower-cases the field name and drops the space after the colon.
         if (result.Find("dkim-signature:v=1;") < 0)
            throw 0;
      }

      // -- Body canonicalization (RFC 6376 3.4.3 and 3.4.4).
      {
         RelaxedCanonicalization relaxed;
         SimpleCanonicalization simple;

         // An empty body is the one case where the two differ: relaxed hashes
         // nothing at all, simple hashes a single CRLF.
         if (relaxed.CanonicalizeBody("") != "")
            throw 0;
         if (simple.CanonicalizeBody("") != "\r\n")
            throw 0;

         // A body that does not end with a CRLF is given exactly one.
         if (relaxed.CanonicalizeBody("hello") != "hello\r\n")
            throw 0;
         if (simple.CanonicalizeBody("hello") != "hello\r\n")
            throw 0;

         // Trailing empty lines are ignored; interior ones are kept.
         if (relaxed.CanonicalizeBody("a\r\n\r\nb\r\n\r\n\r\n") != "a\r\n\r\nb\r\n")
            throw 0;
         if (simple.CanonicalizeBody("a\r\n\r\nb\r\n\r\n\r\n") != "a\r\n\r\nb\r\n")
            throw 0;

         // Relaxed collapses runs of WSP and drops trailing WSP; simple must not
         // touch either.
         if (relaxed.CanonicalizeBody("a  b\t c \r\n") != "a b c\r\n")
            throw 0;
         if (simple.CanonicalizeBody("a  b\t c \r\n") != "a  b\t c \r\n")
            throw 0;

         // Bytes above 0x7F are hash input like any other and must pass through
         // untouched - an ISO-8859-1 body is the common case here.
         const AnsiString eightBit = "caf\xE9 au lait\r\n";

         if (relaxed.CanonicalizeBody(eightBit) != eightBit)
            throw 0;
         if (simple.CanonicalizeBody(eightBit) != eightBit)
            throw 0;
      }
   }

}
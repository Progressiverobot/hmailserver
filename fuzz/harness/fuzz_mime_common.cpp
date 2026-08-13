// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
//
// Implementation of the shared traversal. See fuzz_mime_common.h for the
// reasoning behind the buffer contract, the volatile sink and the traversal
// limits.

#include "stdafx.h"

#include "fuzz_mime_common.h"

namespace hm_fuzz
{
   volatile size_t sink = 0;

   namespace
   {
      // The field names below are the ones MimeEnvironment registers a field
      // coder for, plus Content-Type/Content-Disposition which get the
      // parameter coder. Anything else lands on the default coder and adds no
      // coverage, so the list is deliberately short: every entry here costs one
      // decode per part per execution, and execution rate is the fuzzer's only
      // real currency.
      const char *const kDecodedFieldNames[] =
      {
         "Subject",
         "From",
         "To",
         "Cc",
         "Reply-To",
         "Content-Type",
         "Content-Disposition",
         "Content-Description"
      };

      const char *const kContentTypeParameters[] =
      {
         "charset",
         "boundary",
         "name"
      };
   }

   void ExerciseHeader(HM::MimeHeader &header)
   {
      Consume(static_cast<int>(header.GetMediaType()));
      Consume(header.GetContentType());
      Consume(header.GetMainType());
      Consume(header.GetSubType());
      Consume(header.GetCharset());
      Consume(header.GetName());
      Consume(header.GetBoundary());
      Consume(header.GetTransferEncoding());
      Consume(header.GetDisposition());
      Consume(header.GetDescription());

      // The filename accessors are the ones that decide what an attachment is
      // called on disk, so they are the ones an attacker most wants to control.
      // Both go through the parameter scanner and then the RFC 2047 decoder.
      Consume(header.GetRawFilename());
      Consume(header.GetUnicodeFilename());

      for (size_t index = 0; index < sizeof(kContentTypeParameters) / sizeof(kContentTypeParameters[0]); index++)
         Consume(header.GetParameter(HM::CMimeConst::ContentType(), kContentTypeParameters[index]));

      Consume(header.GetParameter(HM::CMimeConst::ContentDisposition(), HM::CMimeConst::Filename()));

      for (size_t index = 0; index < sizeof(kDecodedFieldNames) / sizeof(kDecodedFieldNames[0]); index++)
      {
         Consume(header.GetUnicodeFieldValue(kDecodedFieldNames[index]));
         Consume(header.FieldExists(kDecodedFieldNames[index]));
      }

      // Walk the fields by index as well as by name. The bound is
      // GetFieldCount() and not a fixed number for a specific reason:
      // MimeHeader::GetField(unsigned int) tests iIndex <= fields_.size() - 1,
      // which underflows to SIZE_MAX on an empty header and returns
      // &fields_[0] on an empty vector. An input of nothing but CRLF parses
      // successfully with zero fields, so calling GetField(0) unconditionally
      // would report a null dereference on that one input, forever, and hide
      // everything else.
      //
      // That underflow is a real defect - InterfaceMessageHeaders::get_Item
      // passes a COM caller's index straight into it with no bound check - but
      // it belongs in Common\Mime, not in the harness, and a fuzz target must
      // not be built around a known bug it cannot fix. It is written up in the
      // fuzzing doc as the first thing to fix so this loop can lose its guard.
      const int field_count = header.GetFieldCount();
      for (int index = 0; index < field_count; index++)
      {
         HM::MimeField *field = header.GetField(static_cast<unsigned int>(index));
         if (field == nullptr)
            continue;

         Consume(field->GetName());
         Consume(field->GetValue());
         Consume(field->GetCharset());
         Consume(field->GetLength());

         HM::AnsiString parameter_value;
         Consume(field->GetParameter(HM::CMimeConst::Charset(), parameter_value));
         Consume(parameter_value);
         Consume(field->GetParameter(HM::CMimeConst::Boundary(), parameter_value));
         Consume(parameter_value);
         Consume(field->GetParameter(HM::CMimeConst::Filename(), parameter_value));
         Consume(parameter_value);

         HM::AnsiString stored_field;
         field->Store(stored_field);
         Consume(stored_field);
      }

      HM::AnsiString raw_header = header.GetHeaderContents();
      Consume(raw_header);

      HM::String unicode_header = header.GetUnicodeHeaderContents();
      Consume(unicode_header);
   }

   size_t ExerciseBody(const std::shared_ptr<HM::MimeBody> &body, int depth, size_t visited_so_far)
   {
      if (!body)
         return visited_so_far;

      if (depth > kMaxTraversalDepth || visited_so_far >= kMaxTraversalParts)
         return visited_so_far;

      size_t visited = visited_so_far + 1;

      ExerciseHeader(*body);

      // MimeBody::GetContentEncodedLength is declared in Mime.h and defined
      // nowhere, so it is deliberately not called here - it would be an
      // unresolved external at link time. Reported separately; a declaration
      // with no definition is a trap for the next person who needs it.
      Consume(body->GetContentLength());
      Consume(body->GetContent());
      Consume(body->IsText());
      Consume(body->IsMessage());
      Consume(body->IsMultiPart());
      Consume(body->IsAttachment());
      Consume(body->GetCleanContentType());
      Consume(body->GetNumberOfParts());
      Consume(body->GetPartCount());

      // The decoders. GetRawText hands back the stored bytes; GetUnicodeText
      // runs them through the Content-Transfer-Encoding coder (base64 or
      // quoted-printable) and then a code-page conversion, which is the longest
      // attacker-controlled path in the whole file: charset comes from the
      // header, so the input picks both the decoder and the code page.
      Consume(body->GetRawText());
      Consume(body->GetUnicodeText());

      // A message/rfc822 attachment is re-parsed in memory by
      // LoadEncapsulatedMessage - a nested, unbounded run of the same parser on
      // the same untrusted bytes. GetUnicodeFilename already reaches it for the
      // Subject, but only the Subject; going through it explicitly gets the
      // whole nested tree under test.
      if (body->IsEncapsulatedRFC822Message())
      {
         std::shared_ptr<HM::MimeBody> encapsulated = body->LoadEncapsulatedMessage();
         visited = ExerciseBody(encapsulated, depth + 1, visited);
      }

      // GetAttachmentList needs a shared_ptr to the part it is called on
      // (the comment in Mime.cpp calls this "really ugly but should work
      // fine"), which is why the harness holds every part by shared_ptr.
      HM::MimeBody::BodyList attachments;
      Consume(body->GetAttachmentList(body, attachments));
      for (const auto &attachment : attachments)
      {
         if (attachment)
         {
            Consume(attachment->GetRawFilename());
            Consume(attachment->GetUnicodeFilename());
         }
      }

      // Child parts, using the same FindFirstPart/FindNextPart idiom the server
      // uses. Each part keeps its own iterator, so recursing through children
      // does not disturb the parent's position.
      std::shared_ptr<HM::MimeBody> part = body->FindFirstPart();
      while (part)
      {
         if (visited >= kMaxTraversalParts)
            break;

         visited = ExerciseBody(part, depth + 1, visited);
         part = body->FindNextPart();
      }

      return visited;
   }
}

// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// libFuzzer target: the codecs, driven directly.
//
// MimeCode.cpp is reachable through the other two targets, but only at the end
// of a chain that first has to produce a syntactically plausible header. Driving
// the codecs directly spends every execution inside them instead, which matters
// because they are where the byte-level pointer arithmetic is:
//
//   - MimeCodeBase64::Decode / MimeCodeQP::Decode / MimeCodeQ::Decode: the
//     transfer-encoding decoders that run over every attachment body.
//   - FieldCodeBase::Decode -> MimeEncodedWord::Decode: the RFC 2047
//     encoded-word decoder. This one already looks fragile on a careful read -
//     it does strchr() from inside the value and then indexes [1] and [2] off
//     the result *before* checking the result against the end of the buffer.
//   - MimeParameterRFC2184Decoder::Decode: RFC 2231/2184 parameter
//     continuations and %-hex decoding, i.e. the thing that decides an
//     attachment's filename.
//   - MIMEUnicodeEncoder::EncodeValue: the encode direction. Less obviously
//     attacker-controlled, but a rule that rewrites a Subject, a vacation
//     auto-reply and the DKIM header writer all re-encode values that came from
//     the message, and MimeEncodedWord::BEncode walks the input with
//     Unicode::CharMoveNext doing its own multi-byte arithmetic.
//
// Input layout: the first byte selects the Content-Transfer-Encoding name to
// decode with, the rest is the payload. The selector is part of the artifact
// file, so a crash reproduces by replaying the file with no extra ceremony -
// which is the only property of an input layout that really matters.

#include "stdafx.h"

#include "fuzz_mime_common.h"

#include "Util/Charset.h"

namespace
{
   // The two registered coders, the three unregistered ones that appear in real
   // mail (they fall through to the pass-through default coder, which is itself
   // worth covering because it is what an unknown encoding gets), and the empty
   // name, which CreateCoder maps to 7bit.
   const char *const kCoderNames[] =
   {
      "base64",
      "quoted-printable",
      "7bit",
      "8bit",
      "binary",
      "x-uuencode",
      ""
   };

   const char *const kFieldCoderNames[] =
   {
      "Subject",              // FieldCodeText
      "From",                 // FieldCodeAddress
      "Content-Type",         // FieldCodeParameter
      "X-Not-Registered"      // default FieldCodeBase
   };

   const size_t kCoderCount = sizeof(kCoderNames) / sizeof(kCoderNames[0]);
   const size_t kFieldCoderCount = sizeof(kFieldCoderNames) / sizeof(kFieldCoderNames[0]);
}

extern "C" int LLVMFuzzerTestOneInput(const uint8_t *data, size_t size)
{
   if (size < 2)
      return 0;

   hm_fuzz::Run([data, size]()
   {
      const size_t coder_index = data[0] % kCoderCount;
      const size_t field_coder_index = data[0] % kFieldCoderCount;

      // NUL terminated for the same reason as the other targets, and here it is
      // not merely defensive: MimeEncodedWord::Decode calls strchr() on the
      // input, so a payload without a terminator would run off the end on every
      // execution. In production the input is always a std::string's buffer.
      hm_fuzz::ParseBuffer payload(data + 1, size - 1);

      // Transfer-encoding decode, exactly as MimeBody::GetUnicodeText does it.
      {
         HM::MimeCodeBase *coder = HM::MimeEnvironment::CreateCoder(kCoderNames[coder_index]);

         HM::AnsiString decoded;
         coder->SetInput(payload.Data(), payload.Size(), false);
         coder->GetOutput(decoded);
         delete coder;

         hm_fuzz::Consume(decoded);
      }

      // Header-field decode, exactly as MimeHeader::GetUnicodeFieldValue does
      // it: field coder, then a code-page conversion using the charset the
      // encoded word itself declared.
      {
         HM::FieldCodeBase *field_coder = HM::MimeEnvironment::CreateFieldCoder(kFieldCoderNames[field_coder_index]);

         HM::AnsiString decoded;
         field_coder->SetInput(payload.Data(), payload.Size(), false);
         field_coder->GetOutput(decoded);

         HM::AnsiString charset = field_coder->GetCharset();
         delete field_coder;

         hm_fuzz::Consume(decoded);
         hm_fuzz::Consume(charset);

         HM::String wide = HM::Charset::ToWideChar(decoded, charset);
         hm_fuzz::Consume(wide);

         // Encode direction, on the decoded text. This is the round trip a
         // rewritten header goes through, and it is the only way to reach
         // MimeEncodedWord::BEncode/QEncode with content the input controls.
         //
         // The charset passed in is the one the *input* declared, not a
         // hard-coded "utf-8", and that is deliberate. MimeHeader::
         // SetUnicodeFieldValue falls back to the message's own Content-Type
         // charset parameter, so the charset is attacker-controlled and
         // arbitrarily long - and MimeEncodedWord::BEncode computes
         //     nMaxBlockSize = (75 - charsetLen - 7) / 4 * 3
         // which goes to zero (and negative) once the charset name is long
         // enough, at which point the encode loop makes no progress per
         // iteration. Hard-coding "utf-8" here would put a floor of 45 under
         // that arithmetic and hide the whole class of bug. If the fuzzer hangs
         // or runs out of memory in BEncode rather than crashing, that is the
         // finding - see docs/Fuzzing.md on triaging timeouts and OOMs.
         HM::AnsiString encode_charset = charset.IsEmpty() ? HM::AnsiString("utf-8") : charset;
         HM::AnsiString re_encoded = HM::MIMEUnicodeEncoder::EncodeValue(encode_charset, wide);
         hm_fuzz::Consume(re_encoded);
      }

      // RFC 2231/2184 parameter value, e.g.
      //    filename*0*=iso-8859-1''%41%42; filename*1*=%43
      {
         HM::MimeParameterRFC2184Decoder decoder;
         HM::AnsiString parameter(payload.Data(), payload.Size());
         hm_fuzz::Consume(decoder.Decode(parameter));
      }
   });

   return 0;
}

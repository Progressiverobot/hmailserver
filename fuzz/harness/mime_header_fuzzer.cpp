// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
//
// libFuzzer target: the header parser on its own, in all three shapes
// production calls it.
//
// A separate target from mime_message_fuzzer, for a reason worth stating:
// MimeHeader::Load runs on far more messages than MimeBody::Load does. IMAP
// FETCH ENVELOPE, IMAP BODY[HEADER], the rule engine and the DKIM signer all
// load headers alone, without ever parsing a body - so header-only input is a
// bigger attack surface than whole messages, and it is a much smaller input
// space. Small inputs mean a far higher execution rate and a far deeper walk
// into MimeField::Load's folding, parameter and encoded-word handling, which is
// where the fiddly pointer arithmetic lives:
//
//     pszEnd = FindString(pszEnd, "\r\n", pszData+nDataSize);
//     if (!pszEnd) return 0;
//     pszEnd += 2;
//   } while (*pszEnd == '\t' || *pszEnd == ' ');   // reads at pszData+nDataSize
//
// The seed corpus for this target is the header block of each real test message
// (make-corpus.ps1 copies the bytes up to and including the first blank line -
// verbatim, no rewriting), so the seeds are already valid folded headers.

#include "stdafx.h"

#include "fuzz_mime_common.h"

#include "Util/Parsing/StringParser.h"

extern "C" int LLVMFuzzerTestOneInput(const uint8_t *data, size_t size)
{
   hm_fuzz::Run([data, size]()
   {
      hm_fuzz::ParseBuffer buffer(data, size);

      // Shape 1: unfolded, which is what MimeBody::Load itself passes and what
      // Utilities::GetMimeHeader passes. Folded continuation lines are collapsed
      // into single spaces by MimeField::UnfoldField.
      {
         HM::MimeHeader header;
         hm_fuzz::Consume(header.Load(buffer.Data(), buffer.Size(), true));
         hm_fuzz::ExerciseHeader(header);
      }

      // Shape 2: unfold = false. Only one caller does this and it is the one
      // that matters most for correctness: DKIMSigner::... loads the header with
      // folding preserved, because the signature covers the bytes as they
      // appear on the wire. A parse difference between the two modes is how a
      // signature gets computed over something other than what was sent.
      {
         HM::MimeHeader header;
         hm_fuzz::Consume(header.Load(buffer.Data(), buffer.Size(), false));
         hm_fuzz::ExerciseHeader(header);
      }

      // Shape 3: Utilities::GetMimeHeader's call, reproduced exactly.
      //
      // That function finds "\r\n\r\n" and then passes a *size that stops two
      // bytes into the terminator*, with the rest of the message still present
      // in the buffer behind it. So unlike every other caller, the byte at
      // pszData[nDataSize] is not a NUL - it is '\r'. The parser's habit of
      // reading one past the counted length therefore reads real message bytes
      // here rather than a terminator, and any bug that depends on what that
      // byte is only reproduces in this shape. Reproducing the call faithfully
      // costs three lines, so there is no excuse for approximating it.
      {
         const char *header_end = HM::StringParser::Search(buffer.Data(), buffer.Size(), "\r\n\r\n");
         if (header_end != nullptr)
         {
            const size_t header_size = static_cast<size_t>(header_end - buffer.Data()) + 2;

            HM::MimeHeader header;
            hm_fuzz::Consume(header.Load(buffer.Data(), header_size, true));
            hm_fuzz::ExerciseHeader(header);
         }
      }
   });

   return 0;
}

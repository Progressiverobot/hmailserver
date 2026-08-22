// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// libFuzzer target: a whole message through MimeBody::Load, then the accessors
// the server runs on it, then a store/re-parse round trip.
//
// This is the target that matters most, because MimeBody::Load is what every
// message that arrives over SMTP eventually goes through - MessageData loads the
// spooled file with MimeBody::LoadFromFile, which is Load plus a File read - and
// because it is the only entry point that recurses. A crash here is not a bad
// request; it is the process that holds every mailbox on the machine going down
// mid-transaction, which is a mail outage plus whatever state the write path had
// half-finished.
//
// What this target does NOT cover, so nobody reads more into a clean run than is
// there:
//   - LoadFromFile's byte-offset bookkeeping (body_byte_offset_/body_byte_end_
//     and the trailing-CRLF trim). That arithmetic decides which bytes
//     SaveAllToFile copies verbatim, so getting it wrong invalidates a DKIM body
//     hash rather than crashing. It needs File to serve bytes from memory; see
//     fuzz_environment.cpp.
//   - Anything above the parser: IMAP BODYSTRUCTURE generation, rule matching,
//     the DKIM canonicalisers. Those are separate targets when someone wants
//     them.

#include "stdafx.h"

#include "fuzz_mime_common.h"

namespace
{
   // Above this, the round trip stops being worth its cost: Store() on a message
   // with a large body copies the body, and doing it on every execution buys
   // less coverage per second than simply running more inputs. 256 KB is far
   // above any structure in the seed corpus.
   const size_t kMaxRoundTripBytes = 256 * 1024;
}

extern "C" int LLVMFuzzerTestOneInput(const uint8_t *data, size_t size)
{
   hm_fuzz::Run([data, size]()
   {
      hm_fuzz::ParseBuffer buffer(data, size);

      // Held by shared_ptr because GetAttachmentList needs one, and because
      // that is how the server holds a parsed message (MessageData keeps a
      // std::shared_ptr<MimeBody>).
      std::shared_ptr<HM::MimeBody> message = std::shared_ptr<HM::MimeBody>(new HM::MimeBody());

      size_t index = 0;
      bool part_loaded = false;
      size_t consumed = message->Load(buffer.Data(), buffer.Size(), index, part_loaded);

      hm_fuzz::Consume(consumed);
      hm_fuzz::Consume(part_loaded);

      // Load returning 0 means "no header found"; the server treats that as a
      // message it cannot parse and keeps the object anyway, so the accessors
      // still run over an empty body. Do the same rather than returning early -
      // the accessors on an empty header are exactly the state that found
      // MimeHeader::GetField's size_t underflow.
      hm_fuzz::ExerciseBody(message, 0, 0);

      // Round trip. Store() is what SaveAllToFile calls when anything in the
      // message was modified, so it runs on attacker-controlled structure every
      // time a rule rewrites a header or the DKIM signer adds one. Re-parsing
      // its output tests the pair for agreement: a serialiser that emits
      // something its own parser mishandles is how a message becomes
      // unreadable-but-stored, and that has been a real class of bug here.
      HM::AnsiString stored;
      message->Store(stored, true);
      hm_fuzz::Consume(stored);

      if (stored.GetLength() > 0 && static_cast<size_t>(stored.GetLength()) <= kMaxRoundTripBytes)
      {
         std::shared_ptr<HM::MimeBody> reparsed = std::shared_ptr<HM::MimeBody>(new HM::MimeBody());

         size_t reparse_index = 0;
         bool reparse_part_loaded = false;

         // stored is a std::string underneath, so c_str() is NUL terminated and
         // GetLength() excludes the terminator - the same contract ParseBuffer
         // constructs for the raw input.
         hm_fuzz::Consume(reparsed->Load(stored.c_str(), static_cast<size_t>(stored.GetLength()),
                                         reparse_index, reparse_part_loaded));
         hm_fuzz::Consume(reparse_part_loaded);

         // One level of traversal on the re-parsed tree. Not the full traversal:
         // the point is to reach the parser and the header accessors on
         // serialiser output, and the full walk has already run on this content
         // once.
         hm_fuzz::ExerciseHeader(*reparsed);
      }
   });

   return 0;
}

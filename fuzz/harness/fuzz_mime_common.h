// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
//
// Shared machinery for the MIME fuzz targets: the input-buffer contract, the
// exception policy, and the traversal that drags a parsed message past the
// accessors an untrusted message actually reaches in the server.
//
// Read the three "WHY" blocks below before changing any of it. Each one encodes
// a decision that, if got wrong, turns the fuzzer from a bug finder into a
// generator of false positives - and a fuzzer that cries wolf gets switched off
// within a week, which is the normal way this kind of work dies.

#pragma once

#include "stdafx.h"

#include <stdint.h>
#include <string.h>

#include "Mime/Mime.h"
#include "Mime/MimeCode.h"

namespace hm_fuzz
{
   // ---------------------------------------------------------------------
   // WHY (1): the buffer handed to the parser must be NUL terminated
   // ---------------------------------------------------------------------
   // MimeBody::Load's contract is stated at its only production call site,
   // MimeBody::LoadFromFile:
   //
   //     // Read the file as a text file. This will cause a null
   //     // to be added by File, which is required by Load() below.
   //     ...
   //     Load(pFileContents->GetCharBuffer(), pFileContents->GetSize() - 1, ...)
   //
   // File::ReadTextFile appends a NUL that is counted in GetSize(), and the size
   // passed to Load excludes it. The parser relies on that: MimeField::Load
   // walks with unbounded reads such as
   //
   //     while (*pszStart == ' ' || *pszStart == '\t') pszStart++;
   //     ... while (*pszEnd == '\t' || *pszEnd == ' ');
   //
   // where pszEnd can legitimately sit one past the last counted byte. Handing
   // libFuzzer's raw data pointer straight to Load would make ASAN report a
   // heap-buffer-overflow on almost every input - all of them false, because in
   // production that byte exists and is zero.
   //
   // So the harness allocates size + 1, copies, and writes the terminator. That
   // reproduces the production contract exactly: reads of the terminator are
   // in-bounds, and a read past *it* is a genuine out-of-bounds read that ASAN
   // will catch. The one-byte allocation growth is also why we copy rather than
   // fuzz in place - ASAN's redzone then sits immediately after the terminator,
   // which is the tightest possible detector for "walked off the end".
   class ParseBuffer
   {
   public:
      ParseBuffer(const uint8_t *data, size_t size) :
         storage_(size + 1)
      {
         if (size > 0)
            memcpy(storage_.data(), data, size);

         storage_[size] = '\0';
         size_ = size;
      }

      const char *Data() const { return storage_.data(); }
      size_t Size() const { return size_; }

   private:
      std::vector<char> storage_;
      size_t size_ = 0;
   };

   // ---------------------------------------------------------------------
   // WHY (2): results are written to a volatile sink
   // ---------------------------------------------------------------------
   // Several accessors are inline in Mime.h and do nothing but forward. With
   // optimisation on and the result unused, the compiler is free to delete the
   // call - and deleting a call means the coverage feedback for the code behind
   // it never appears, so libFuzzer stops steering towards it and the run
   // quietly explores nothing. Writing a byte of every result to a volatile
   // makes the calls observable. It costs one store.
   extern volatile size_t sink;

   inline void Consume(const char *value)
   {
      if (value != nullptr)
         sink += static_cast<size_t>(static_cast<unsigned char>(value[0]));
      else
         sink += 1;
   }

   inline void Consume(const std::string &value)
   {
      sink += value.size();
      if (!value.empty())
         sink += static_cast<size_t>(static_cast<unsigned char>(value[0]));
   }

   inline void Consume(const HM::AnsiString &value)
   {
      Consume(static_cast<const std::string &>(value));
   }

   inline void Consume(const HM::String &value)
   {
      sink += value.size();
      if (!value.empty())
         sink += static_cast<size_t>(value[0]);
   }

   inline void Consume(size_t value)
   {
      sink += value;
   }

   // int and bool have their own overloads on purpose. Without the int one, an
   // int argument is an equally good conversion to size_t and to bool and the
   // call is ambiguous; enum results (MediaType) still have to be cast at the
   // call site for the same reason.
   inline void Consume(int value)
   {
      sink += static_cast<size_t>(value);
   }

   inline void Consume(bool value)
   {
      sink += value ? 1u : 0u;
   }

   // ---------------------------------------------------------------------
   // Traversal limits
   // ---------------------------------------------------------------------
   // These bound the *harness*, not the parser.
   //
   // MimeBody::Load recurses once per nesting level of a multipart message and
   // has no depth limit of its own, so a small input can build a very deep tree.
   // That recursion is exactly what we want to test and it happens inside Load,
   // before the traversal starts. But if the traversal then recursed to the same
   // depth with its own String temporaries in every frame, it would exhaust the
   // stack in the harness on a tree the parser itself survived - a false
   // positive that looks identical to a real one in the report.
   //
   // So the traversal stops at a depth and a part count far beyond anything real
   // mail contains (the deepest structure in the seed corpus is three levels)
   // and leaves stack-depth testing to Load. A stack overflow that IS a real
   // finding shows MimeBody::Load frames repeated all the way down the ASAN
   // stack trace; one caused by the traversal shows ExerciseBody frames. Check
   // which before filing anything.
   const int kMaxTraversalDepth = 8;
   const size_t kMaxTraversalParts = 256;

   // Exercises every read-only accessor on a header that untrusted input can
   // reach. Each of these is a small parser in its own right, operating on text
   // the header parser has already accepted:
   //
   //   GetParameter        -> MimeField::FindParameter, the ';'-separated
   //                          parameter scanner, including RFC 2231/2184
   //                          continuations and %-decoding
   //   GetUnicodeFieldValue-> FieldCodeBase::Decode, i.e. the RFC 2047
   //                          encoded-word decoder plus base64/Q decoding, then
   //                          a code-page conversion through Charset
   //   GetRawFilename      -> the filename/name parameter path that decides what
   //                          an attachment is called on disk
   //   GetMediaType        -> the _memicmp table walk over Content-Type
   //
   // The field names passed to GetUnicodeFieldValue are the ones with registered
   // field coders (Subject/From/To/Cc get the address or text coder,
   // Content-Type and Content-Disposition get the parameter coder); using an
   // unregistered name would only ever exercise the default coder.
   void ExerciseHeader(HM::MimeHeader &header);

   // Walks a parsed message: the header of every part, the decoded body of every
   // part, the attachment list, and the encapsulated-message path (a
   // message/rfc822 attachment is re-parsed in memory by
   // MimeBody::LoadEncapsulatedMessage, which is a second, nested run of the
   // parser on attacker-controlled bytes).
   //
   // Returns the number of parts visited so callers can respect
   // kMaxTraversalParts across siblings.
   size_t ExerciseBody(const std::shared_ptr<HM::MimeBody> &body, int depth, size_t visited_so_far);

   // ---------------------------------------------------------------------
   // WHY (3): the harness swallows exactly the exceptions production swallows
   // ---------------------------------------------------------------------
   // MimeBody::LoadFromFile wraps the whole parse in catch (...), copies the
   // message to "Problematic messages" and carries on. ByteBuffer::Empty throws
   // std::logic_error, and any allocation in the parser can throw
   // std::bad_alloc. If those escaped LLVMFuzzerTestOneInput, the C++ runtime
   // would call abort() and libFuzzer would report a crash - for a condition the
   // server survives by design. Every such report would be a false positive, and
   // there would be a lot of them.
   //
   // So Run() catches what production catches, and nothing else: a memory-safety
   // error is not an exception, it is a trap, and ASAN reports it before any
   // handler runs. Note the one real divergence, documented in docs/Fuzzing.md:
   // the server is built /EHa, where catch (...) also swallows access
   // violations; clang-cl builds /EHsc, where it does not. That makes the fuzz
   // build strictly better at surfacing memory errors than the shipped one is at
   // surviving them, which is the right direction for a bug hunt - and it is
   // precisely why the crash oracle had to exist first for the *server* side.
   template <typename TBody>
   void Run(TBody body)
   {
      try
      {
         body();
      }
      catch (const std::exception &)
      {
         // Matches MimeBody::LoadFromFile's catch (...). Not a finding.
      }
      catch (...)
      {
         // Includes the bare `throw;` statements the older utility code uses as
         // an error signal.
      }
   }
}

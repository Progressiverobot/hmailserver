// Copyright (c) 2005 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// Created 2005-10-05
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"
#include ".\transparenttransmissionbuffer.h"

#include "ByteBuffer.h"
#include "../Application/IniFileSettings.h"
#include "../Application/Configuration.h"
#include "../../SMTP/SMTPConfiguration.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   TransparentTransmissionBuffer::TransparentTransmissionBuffer(bool bSending) : 
      is_sending_(bSending),
      binary_mode_(false),
      transmission_ended_(false),
      last_send_ended_with_newline_(false),
      previous_chunk_ended_with_carriage_return_(false),
      previous_chunk_ended_with_newline_(true),
      ended_on_non_standard_marker_(false),
      data_sent_(0),
      max_size_kb_(0),
      cancel_transmission_(false),
      write_failed_(false),
      writes_completed_(0)
   {
      buffer_ = std::shared_ptr<ByteBuffer>(new ByteBuffer);

      flushed_tail_[0] = '\r';
      flushed_tail_[1] = '\n';
   }

   TransparentTransmissionBuffer::~TransparentTransmissionBuffer(void)
   {

   }

   // POP3Connection and SMTPClientConnection hold one of these for the life of the
   // session and re-Initialize it per message, so anything carried between chunks
   // has to be reset here or it leaks from one transfer into the next: a carry
   // left saying "mid-line" would make the first byte of the NEXT message look
   // like a continuation, and a line-leading dot there would go out unstuffed.
   void
   TransparentTransmissionBuffer::ResetPerTransferState_()
   {
      data_sent_ = 0;
      transmission_ended_ = false;
      ended_on_non_standard_marker_ = false;
      surplus_after_terminator_.reset();
      previous_chunk_ended_with_newline_ = true;
      previous_chunk_ended_with_carriage_return_ = false;
      last_send_ended_with_newline_ = false;
      flushed_tail_[0] = '\r';
      flushed_tail_[1] = '\n';
   }

   bool
   TransparentTransmissionBuffer::Initialize(std::weak_ptr<TCPConnection> pTCPConnection)
   {
      tcp_connection_ = pTCPConnection;

      ResetPerTransferState_();

      return true;
   }

   bool 
   TransparentTransmissionBuffer::Initialize(const String &sFilename)
   {
      if (!file_.Open(sFilename, File::OTAppend))
      {
         // This is not good. We failed to get a handle to the file.
         // Log to event log and notify the sender of this error.

         String sErrorMessage;
         sErrorMessage.Format(_T("Failed to write to the file %s. Data from sender rejected."), sFilename.c_str());

         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5075, "TransparentTransmissionBuffer::SaveToFile_", sErrorMessage);

         return false;
      }

      ResetPerTransferState_();

      return true;
   }

   void 
   TransparentTransmissionBuffer::SetMaxSizeKB(size_t maxSize)
   {
      max_size_kb_ = maxSize;
   }

   void 
   TransparentTransmissionBuffer::Append(const BYTE *pBuffer, size_t iBufferSize)
   {
      if (iBufferSize == 0)
      {
         // Nothing to add.
         return;
      }   

      if (pBuffer == 0)
      {
         ErrorManager::Instance()->ReportError(ErrorManager::High, 5411, "TransparentTransmissionBuffer::Append", "pBuffer is NULL");
         throw;
      }

      if (buffer_ == 0)
      {
         ErrorManager::Instance()->ReportError(ErrorManager::High, 5412, "TransparentTransmissionBuffer::Append", "buffer_ is NULL");
         throw;
      }

      data_sent_+= iBufferSize;

      // Add the new data to the buffer.
      buffer_->Add(pBuffer, iBufferSize);

      // Check if we have received the entire message.
      if (buffer_->GetSize() >= 2 && !is_sending_ && !binary_mode_)
      {
         // If receiving, we should check for end-of-data.
         //
         // The marker is SEARCHED FOR rather than checked at the end of the buffer,
         // and the difference is a hang that shipped twice. The first version
         // required \r\n.\r\n literally at the buffer's end, so a message whose
         // final line ended with a bare LF was never seen to end (the 6.2.18 fix
         // widened the spellings). But the widened checks still only looked at the
         // END of the buffer - and a pipelining client puts bytes BEHIND the
         // marker in the same segment. Postfix sends "...\r\n.\r\nQUIT\r\n" as one
         // write whenever it has nothing further for the connection, and the next
         // MAIL FROM instead when it has more mail queued. Whenever those bytes
         // arrived in the same read as the marker, the marker was mid-buffer, no
         // tail check could ever match, and the session hung: all data received,
         // no reply ever sent, "timed out while sending end of data" on the peer.
         //
         // Measured against a real Postfix 3.10 on 2026-08-15, which is also what
         // exposed why the original reporter's PIPELINING workaround "worked":
         // without pipelining the client waits for the 250 before QUIT, so the
         // marker IS the tail. Whether the hang struck was pure TCP segmentation
         // luck - which is why a same-host relay hit it every time and internet
         // senders almost never did.
         size_t iSize = buffer_->GetSize();
         const char *pCharBuffer = buffer_->GetCharBuffer();

         // Whether a bare LF is accepted where the standard requires CRLF - the
         // "Allow incorrect line endings" setting. Gated, because RFC 5321 is
         // specific that the terminator is <CRLF>.<CRLF>, and a server should not
         // invent tolerances it was not asked for.
         const bool allowBareLineFeed =
            Configuration::Instance()->GetSMTPConfiguration()->GetAllowIncorrectLineEndings();

         // Only bytes appended by THIS call can complete a marker, but a marker can
         // straddle the boundary: the longest spelling is five bytes, so start four
         // bytes before the new data.
         const size_t previousSize = iSize - iBufferSize;
         const size_t scanFrom = previousSize > 4 ? previousSize - 4 : 0;

         size_t dotPosition = 0;
         size_t terminatorLength = 0;   // the dot plus the newline that follows it
         bool nonStandardMarker = false;

         // The byte `offset` positions before `index`, reaching into the tail of
         // what Flush already removed when the index runs off the front of the
         // buffer. Without this the first two positions had to be guessed, and
         // guessing "CRLF" turned a bare-LF marker whose newline had just been
         // flushed away into a standard one - accepted where policy forbids it,
         // and with its trailing bytes handed to the command parser.
         auto byteBefore = [&](size_t index, size_t offset) -> char
         {
            if (index >= offset)
               return pCharBuffer[index - offset];

            return (offset - index == 1) ? flushed_tail_[1] : flushed_tail_[0];
         };

         for (size_t i = scanFrom; i < iSize && terminatorLength == 0; i++)
         {
            if (pCharBuffer[i] != '.')
               continue;

            // Left context: the dot must start a line - follow a newline, either
            // CRLF or (under the setting) a bare LF.
            if (byteBefore(i, 1) != '\n')
               continue;

            const bool leftIsBare = byteBefore(i, 2) != '\r';

            // Right context: CRLF, or bare LF under the setting.
            const bool rightIsCrlf = i + 2 < iSize &&
               pCharBuffer[i + 1] == '\r' && pCharBuffer[i + 2] == '\n';
            const bool rightIsBareLf = !rightIsCrlf &&
               i + 1 < iSize && pCharBuffer[i + 1] == '\n';

            if (!rightIsCrlf && !rightIsBareLf)
               continue;

            const bool isBare = leftIsBare || rightIsBareLf;
            if (isBare && !allowBareLineFeed)
               continue;

            // A bare-LF spelling counts only at the very END of everything received
            // so far - never mid-buffer. This is deliberate and is pinned by the
            // smuggling test: in the CVE-2023-51764 shape, an upstream relay did NOT
            // treat "\n.\r\n" as end-of-data and forwarded everything as one message
            // body, so a server that honours it mid-stream delivers a silently
            // TRUNCATED message (or worse, executes what follows). Mid-buffer, a
            // bare marker is body text. The STANDARD marker carries no such
            // ambiguity - every conformant relay agrees where the message ends - so
            // it alone is recognised anywhere, which is what un-hangs a pipelining
            // client whose QUIT rides behind the dot.
            const size_t candidateLength = rightIsCrlf ? (size_t) 3 : (size_t) 2;
            if (isBare && i + candidateLength != iSize)
               continue;

            dotPosition = i;
            terminatorLength = candidateLength;
            nonStandardMarker = isBare;
         }

         if (terminatorLength > 0)
         {
            // Whatever follows the marker is the client's next pipelined input -
            // legitimate SMTP, because only the STANDARD marker is ever recognised
            // mid-buffer and every conformant relay agrees where a standard-marked
            // message ends. Preserved for the caller to feed back through command
            // parsing. (A bare marker is tail-only, so it can have no surplus.)
            const size_t surplusStart = dotPosition + terminatorLength;
            const size_t surplusLength = iSize - surplusStart;

            if (surplusLength > 0)
            {
               surplus_after_terminator_ = std::shared_ptr<ByteBuffer>(new ByteBuffer);
               surplus_after_terminator_->Add((const BYTE *) pCharBuffer + surplusStart, surplusLength);
            }

            // Trim to the message content alone: the marker and everything behind
            // it go; the body keeps its own final line ending, which sits BEFORE
            // dotPosition.
            buffer_->DecreaseSize(iSize - dotPosition);

            // GetSize() answers "how big is this message", and the size limits are
            // enforced from it. The marker and any pipelined commands behind it are
            // not message content - a relay that pipelines its next transaction
            // behind the dot would otherwise push a message that fits over the
            // configured maximum and earn it a 552.
            const size_t notMessageContent = terminatorLength + surplusLength;
            data_sent_ = data_sent_ > notMessageContent ? data_sent_ - notMessageContent : 0;

            transmission_ended_ = true;
            ended_on_non_standard_marker_ = nonStandardMarker;
         }
      }
   }

   bool 
   TransparentTransmissionBuffer::GetRequiresFlush()
   {
      if (buffer_->GetSize() > 40000 || transmission_ended_)
         return true;
      else
         return false;
   }

   size_t
   TransparentTransmissionBuffer::GetSize()
   {  
      return data_sent_;
   }

   bool
   TransparentTransmissionBuffer::Flush(bool bForce)
   {
      bool dataProcessed = false;

      if (!GetRequiresFlush() && !bForce)
         return dataProcessed;

      if (binary_mode_)
      {
         // RFC 3030 BDAT: the chunk payload is byte-transparent. Write whatever has
         // been buffered verbatim - no line-boundary search, no dot-unstuffing and
         // no end-of-data sequence handling. The caller bounds memory by flushing
         // as the buffer grows and marks the end via MarkTransmissionEnded().
         size_t bufferSize = buffer_->GetSize();
         if (bufferSize > 0)
         {
            std::shared_ptr<ByteBuffer> pOutBuffer = std::shared_ptr<ByteBuffer>(new ByteBuffer);
            pOutBuffer->Add(buffer_->GetBuffer(), bufferSize);
            buffer_->Empty();

            SaveToFile_(pOutBuffer);
            dataProcessed = true;
         }

         if (transmission_ended_ && file_.IsOpen())
         {
            // Checked, for the reason the setting exists: it is bought specifically to
            // make the spool file durable before the sender is told 250, so a failed
            // fsync has to refuse the message rather than acknowledge it.
            if (IniFileSettings::Instance()->GetMessageStoreFsync() && !file_.FlushToDisk())
               ReportFlushFailure_();

            file_.Close();
         }

         return dataProcessed;
      }

      if (buffer_->GetSize() > MAX_LINE_LENGTH)
      {
         // Something fishy is going on. We've received over MAX_LINE_LENGTH
         // characters on a single line with no new line character. This should
         // never happen in normal email communication so let's assume someone
         // is trying to attack us.
         cancel_transmission_ = true;
         cancel_message_ = "Too long line was received. Transmission aborted.";
         bForce = true;
      }

      // Locate last \n
      const char *pBuffer = buffer_->GetCharBuffer();
      size_t bufferSize = buffer_->GetSize();
      
      /*
         RFC rfc2821
         text line
            The maximum total length of a text line including the <CRLF> is
            1000 characters (not counting the leading dot duplicated for
            transparency).  This number may be increased by the use of SMTP
            Service Extensions.
      */

      size_t maxLineLength = MAX_LINE_LENGTH;

      // Start in the end and move 'back' MAX_LINE_LENGTH characters.
      size_t searchEndPos = 0;

      // An empty buffer used to return here, which skipped the end-of-transmission
      // block at the bottom of this function - so the spool file was left OPEN,
      // with up to a CRT buffer of the message tail unwritten, while the accept
      // stage read it, rewrote it and recorded its size. The file is opened in
      // append mode, so those bytes then landed after the rewritten content at
      // close. The search loop below is already a no-op on an empty buffer
      // (current_position starts at 0), so falling through costs nothing and the
      // close and its fsync always run.
      if (bufferSize > maxLineLength)
         searchEndPos = bufferSize - maxLineLength;
      else
         searchEndPos = 0;

      for (size_t current_position = bufferSize; current_position > searchEndPos; current_position--)
      {
         char s = pBuffer[current_position-1];

         // If we found a newline, send anything up until that.
         // If we're forcing a send, send all we got
         // If we found no newline in the stream, the message is malformed according to RFC2821 (max 1000 chars per line). 
         //    Send all we got anyway. 

         if (s == '\n' || bForce)
         {
            last_send_ended_with_newline_ = s == '\n';

            // Copy the data up including this position
            size_t bytes_to_copy = current_position;

            std::shared_ptr<ByteBuffer> pOutBuffer = std::shared_ptr<ByteBuffer>(new ByteBuffer);
            pOutBuffer->Add(buffer_->GetBuffer(), bytes_to_copy);

            // Remember what the bytes leaving the buffer ended with, so the next
            // end-of-data scan can ask what precedes the new index 0 instead of
            // assuming it was a CRLF. Order matters: read before Empty() shifts
            // the buffer.
            if (bytes_to_copy >= 2)
            {
               flushed_tail_[0] = pBuffer[bytes_to_copy - 2];
               flushed_tail_[1] = pBuffer[bytes_to_copy - 1];
            }
            else if (bytes_to_copy == 1)
            {
               flushed_tail_[0] = flushed_tail_[1];
               flushed_tail_[1] = pBuffer[0];
            }

            // Remove it from the old buffer
            size_t remaining_bytes = buffer_->GetSize() - bytes_to_copy;
            buffer_->Empty(remaining_bytes);

            // Parse this buffer and add it to file/socket
            if (is_sending_)
               InsertTransmissionPeriod_(pOutBuffer);
            else
               RemoveTransmissionPeriod_(pOutBuffer);

            // The parsed buffer can now be sent.
            if (is_sending_)
            {
               if (std::shared_ptr<TCPConnection> connection = tcp_connection_.lock())
               {
                  connection->EnqueueWrite(pOutBuffer);
               }

            }
            else
            {
               SaveToFile_(pOutBuffer);
            }

            dataProcessed = true;

            break;
         }
      }

      if (transmission_ended_ && file_.IsOpen())
      {
         // Durability: when configured, force the fully-received message to physical
         // disk before we close it. This is the SMTP accept point, so the spool file
         // is durable before the server acknowledges the message to the sender - and
         // that promise is the whole reason the setting is turned on, so the result is
         // checked. An unchecked fsync buys nothing but the appearance of durability.
         if (!is_sending_ && IniFileSettings::Instance()->GetMessageStoreFsync() && !file_.FlushToDisk())
            ReportFlushFailure_();

         file_.Close();
      }

      return dataProcessed;
   }

   void
   TransparentTransmissionBuffer::ReportFlushFailure_()
   //---------------------------------------------------------------------------//
   // A configured fsync of the received message failed. Treated exactly like a
   // failed write: the message is not on disk in the way the operator asked for, so
   // it is refused with a transient code rather than acknowledged.
   //---------------------------------------------------------------------------//
   {
      if (!write_failed_)
      {
         ErrorManager::Instance()->ReportError(ErrorManager::High, 5863, "TransparentTransmissionBuffer::ReportFlushFailure_",
            "Failed to flush the received message to disk, with MessageStoreFsync enabled. The message is refused rather than acknowledged as durable.");
      }

      write_failed_ = true;
   }

   bool
   TransparentTransmissionBuffer::SaveToFile_(std::shared_ptr<ByteBuffer> pBuffer)
   {
      if (max_size_kb_ > 0 && (data_sent_ / 1024) > max_size_kb_)
      {
         // we've reached the max size. don't save more data.
         return false;
      }

      if (!cancel_transmission_)
      {
         size_t noOfBytesWritten = 0;

         // This used to be `bool bResult = file_.Write(pBuffer, noOfBytesWritten);`
         // with bResult never read, and the function returned true unconditionally
         // below. So a spool write that failed - a full disk, a quota, an I/O error -
         // was invisible twice over: the result was dropped here, and every caller was
         // told the data had been saved. On the receiving side that means the bytes
         // are missing from the message and the sender is still given a 250.
         //
         // A short write counts as a failure for the same reason: the message on disk
         // is not the message that was sent.
         //
         // MSVC does not warn about the unused variable at /W3 (C4189 is /W4), which
         // is why it survived here for as long as it did.

         // Fault injection, off unless [Settings] SimulateSpoolWriteFailure is set. Mode
         // 1 fails every write and leaves the spool file empty; mode 2 lets the first
         // write through and fails the rest, which is both the shape a disk filling up
         // mid-message actually has and the only shape that used to be accepted, since a
         // truncated file has content and a non-zero size and so passes everything
         // downstream. Short-circuits the write rather than writing first, because the
         // point is content that never reached the disk.
         const int simulationMode = is_sending_ ? 0 : IniFileSettings::Instance()->GetSimulateSpoolWriteFailure();
         const bool simulateFailure = simulationMode == 1 || (simulationMode == 2 && writes_completed_ > 0);

         if (simulateFailure || !file_.Write(pBuffer, noOfBytesWritten) || noOfBytesWritten != pBuffer->GetSize())
         {
            if (!write_failed_)
            {
               // Once per transmission: a full disk fails every subsequent flush of
               // the same message too, and one report per message is enough to
               // diagnose it without turning a disk-full into a log flood.
               String message;
               message.Format(_T("Failed to write %Iu bytes of received message data to the spool file. Wrote %Iu. The message is refused rather than accepted incomplete."),
                  pBuffer->GetSize(), noOfBytesWritten);

               ErrorManager::Instance()->ReportError(ErrorManager::High, 5862, "TransparentTransmissionBuffer::SaveToFile_", message);
            }

            write_failed_ = true;

            return false;
         }

         writes_completed_++;
      }

      return true;
   }

   void 
   TransparentTransmissionBuffer::InsertTransmissionPeriod_(std::shared_ptr<ByteBuffer> pBuffer)
   {
      // All . which are placed as the first character on a new
      // line should be replaced with ..
      
      // Allocate maximum required length for the out buffer.
      char *pInBuffer = (char*) pBuffer->GetCharBuffer();

      char *pOutBuffer = new char[pBuffer->GetSize() * 2];
      char *pOutBufferStart = pOutBuffer;

      size_t iInBufferSize = pBuffer->GetSize();
     
      for (size_t i = 0; i < iInBufferSize; i++)
      {
         char c = pInBuffer[i];

         // A dot is doubled when it STARTS a line, and only then. At index 0 the
         // preceding byte is in the previous chunk, so the answer is carried state
         // rather than assumed.
         //
         // The two conditions this replaces were wrong in both directions. "i > 2 &&
         // previous == '\n'" silently skipped a line-leading dot at index 1 or 2,
         // which happens whenever a chunk boundary lands just before an empty line
         // (Flush cuts at the last newline, so a chunk can begin "\r\n.", leaving the
         // dot at index 2). The receiver then un-stuffs a dot that was never stuffed:
         // ".X" arrives as "X", and a line that is a lone "." becomes <CRLF>.<CRLF> -
         // the end-of-DATA marker - truncating the message there and handing whatever
         // follows to the receiver's command parser. And the unconditional "i == 0"
         // stuffed a dot that did NOT start a line, after a forced mid-line split.
         if (c == '.')
         {
            const bool atLineStart = (i == 0)
               ? previous_chunk_ended_with_newline_
               : pInBuffer[i - 1] == '\n';

            if (atLineStart)
            {
               *pOutBuffer = '.';
               pOutBuffer++;
            }
         }

         // Add the character
         *pOutBuffer = c;
         pOutBuffer++;
      }

      previous_chunk_ended_with_newline_ = iInBufferSize > 0 && pInBuffer[iInBufferSize - 1] == '\n';

      // Clear the buffer and insert the new data
      size_t iOutBufferLen = pOutBuffer - pOutBufferStart;
      pBuffer->Empty();
      pBuffer->Add((BYTE*) pOutBufferStart, iOutBufferLen);

      // Free memory for the old buffer
      delete [] pOutBufferStart;
   }

   void
   TransparentTransmissionBuffer::RemoveTransmissionPeriod_(std::shared_ptr<ByteBuffer> pBuffer)
   {
      // Allocate maximum required length for the out buffer.
      char *pInBuffer = (char*) pBuffer->GetCharBuffer();

      size_t iInBufferSize = pBuffer->GetSize();

      // Whether bare line feeds are repaired on the way to the spool file rather than
      // stored as they arrived. This is the same "Allow incorrect line endings" setting
      // that decides whether such a message is accepted at all - with it off, the message
      // is refused with 554 and never reaches here.
      //
      // Repairing rather than merely tolerating is the point. Accepting a message and
      // storing it with bare LFs produces a message the server cannot correctly serve
      // afterwards: POP3 RETR and IMAP FETCH both terminate their payload with CRLF.CRLF,
      // so a stored body whose last line ends with a bare LF is sent as "...\n.\r\n" and a
      // strict client cannot find the end of it - the retrieval hangs in the same shape as
      // the reception hang this setting was blocking. Proven, not theorised: the first
      // build that accepted these messages then hung the suite's own POP3 client on
      // exactly that, after the SMTP side had logged "250 Queued".
      //
      // So the rule is: accept the sender's sloppiness at the door and normalise it once,
      // here, where every byte is already being walked for dot-unstuffing and the extra
      // work is a comparison per character.
      const bool repairBareLineFeeds = !is_sending_ &&
         Configuration::Instance()->GetSMTPConfiguration()->GetAllowIncorrectLineEndings();

      // Twice the input, because repairing grows the data: every bare LF becomes CRLF. The
      // old allocation was exactly the input size, which was correct while this loop could
      // only ever remove bytes.
      char *pOutBuffer = new char[iInBufferSize * 2 + 2];
      char *pOutBufferStart = pOutBuffer;

      for (size_t i = 0; i < iInBufferSize; i++)
      {
         char c = pInBuffer[i];

         // The mirror of the stuffing rule above, and it carried the same two
         // defects: a line-leading dot at index 1 or 2 was left stuffed (so a sender's
         // "..line" was stored as "..line" instead of ".line"), and after a forced
         // mid-line split a dot at index 0 was stripped although it did not start a
         // line - deleting a character out of the message body.
         if (c == '.')
         {
            const bool atLineStart = (i == 0)
               ? previous_chunk_ended_with_newline_
               : pInBuffer[i - 1] == '\n';

            if (atLineStart)
               continue;
         }

         // A line feed with no carriage return in front of it. The preceding byte may be
         // in the PREVIOUS chunk - Flush hands this function whole lines, but a forced
         // flush (an over-long line, or a cancelled transmission) can split anywhere,
         // including between a CR and its LF. Hence the carried state: without it, a split
         // there would produce "\r\r\n" and corrupt the line the split fell inside.
         if (repairBareLineFeeds && c == '\n')
         {
            const bool precededByCarriageReturn = i > 0
               ? pInBuffer[i - 1] == '\r'
               : previous_chunk_ended_with_carriage_return_;

            if (!precededByCarriageReturn)
            {
               *pOutBuffer = '\r';
               pOutBuffer++;
            }
         }

         // Add the character to the out buffer
         *pOutBuffer = c;
         pOutBuffer++;

      }

      previous_chunk_ended_with_carriage_return_ = iInBufferSize > 0 && pInBuffer[iInBufferSize - 1] == '\r';
      previous_chunk_ended_with_newline_ = iInBufferSize > 0 && pInBuffer[iInBufferSize - 1] == '\n';

      // Clear the buffer and insert the new data
      size_t iOutBufferLen = pOutBuffer - pOutBufferStart;
      pBuffer->Empty();
      pBuffer->Add((BYTE*) pOutBufferStart, iOutBufferLen);

      // Free memory for the old buffer
      delete [] pOutBufferStart;
   }

   // ---- Tests ----------------------------------------------------------------

   std::string
   TransparentTransmissionBufferTester::Stuff_(TransparentTransmissionBuffer &buffer, const std::string &chunk)
   {
      auto pBuffer = std::shared_ptr<ByteBuffer>(new ByteBuffer);
      if (!chunk.empty())
         pBuffer->Add((const BYTE *) chunk.data(), chunk.size());

      buffer.InsertTransmissionPeriod_(pBuffer);

      return std::string((const char *) pBuffer->GetCharBuffer(), pBuffer->GetSize());
   }

   std::string
   TransparentTransmissionBufferTester::Unstuff_(TransparentTransmissionBuffer &buffer, const std::string &chunk)
   {
      auto pBuffer = std::shared_ptr<ByteBuffer>(new ByteBuffer);
      if (!chunk.empty())
         pBuffer->Add((const BYTE *) chunk.data(), chunk.size());

      buffer.RemoveTransmissionPeriod_(pBuffer);

      return std::string((const char *) pBuffer->GetCharBuffer(), pBuffer->GetSize());
   }

   void
   TransparentTransmissionBufferTester::Test()
   {
      // SEND side: a dot that starts a line is doubled; a dot mid-line is left alone.
      {
         TransparentTransmissionBuffer sender(true);

         // Whole message in one chunk: first-line dot (index 0) and a later
         // line-leading dot are both stuffed; the mid-line dot in "a.b" is not.
         if (Stuff_(sender, ".hello\r\na.b\r\n.world\r\n") != "..hello\r\na.b\r\n..world\r\n")
            throw 0;
      }
      {
         // The defect the fix targets: a line-leading dot arriving at chunk index 1
         // and index 2. A tail-only / "i > 2" test missed both. Each chunk here ends
         // on a newline, so the following chunk begins at a line start.
         TransparentTransmissionBuffer sender(true);
         if (Stuff_(sender, "x\r\n") != "x\r\n")                 // sets "ended on newline"
            throw 0;
         if (Stuff_(sender, ".y\r\n") != "..y\r\n")              // dot at index 0 of a line start
            throw 0;

         TransparentTransmissionBuffer sender2(true);
         if (Stuff_(sender2, "a\n") != "a\n")
            throw 0;
         if (Stuff_(sender2, "a\n.z\n") != "a\n..z\n")           // dot at index 2, preceded by \n
            throw 0;
      }
      {
         // Carry across the boundary: the previous chunk did NOT end on a newline,
         // so a dot at index 0 of the next chunk is mid-line and must NOT be stuffed.
         // This is the forced-mid-line-split case the old "i == 0" stuffed wrongly.
         TransparentTransmissionBuffer sender(true);
         if (Stuff_(sender, "partial line with no newline") != "partial line with no newline")
            throw 0;
         if (Stuff_(sender, ".still the same line\r\n") != ".still the same line\r\n")
            throw 0;
      }

      // RECEIVE side: the exact inverse, so a stuffed stream round-trips.
      {
         TransparentTransmissionBuffer receiver(false);
         if (Unstuff_(receiver, "..hello\r\na.b\r\n..world\r\n") != ".hello\r\na.b\r\n.world\r\n")
            throw 0;
      }
      {
         // Index-1 and index-2 line-leading stuffed dots across chunks: both must be
         // un-stuffed, or a "..x" stays doubled in the stored message.
         TransparentTransmissionBuffer receiver(false);
         if (Unstuff_(receiver, "x\r\n") != "x\r\n")
            throw 0;
         if (Unstuff_(receiver, "..y\r\n") != ".y\r\n")
            throw 0;

         TransparentTransmissionBuffer receiver2(false);
         if (Unstuff_(receiver2, "a\n") != "a\n")
            throw 0;
         if (Unstuff_(receiver2, "a\n..z\n") != "a\n.z\n")
            throw 0;
      }
      {
         // A mid-line dot at chunk index 0 after a mid-line split must NOT be stripped
         // (the old "i == 0" continue deleted a body character here).
         TransparentTransmissionBuffer receiver(false);
         if (Unstuff_(receiver, "partial line with no newline") != "partial line with no newline")
            throw 0;
         if (Unstuff_(receiver, ".still the same line\r\n") != ".still the same line\r\n")
            throw 0;
      }
      {
         // The security case in miniature: a sender that correctly stuffs a lone-dot
         // line produces "..", and a receiver that fails to un-stuff a boundary-split
         // ".." would turn a doubled dot back into a bare ".", which downstream is the
         // DATA terminator. Prove the round trip holds when the marker-ish line lands
         // at a chunk start.
         TransparentTransmissionBuffer receiver(false);
         if (Unstuff_(receiver, "body\r\n") != "body\r\n")
            throw 0;
         if (Unstuff_(receiver, "..\r\nmore\r\n") != ".\r\nmore\r\n")
            throw 0;
      }
   }

}
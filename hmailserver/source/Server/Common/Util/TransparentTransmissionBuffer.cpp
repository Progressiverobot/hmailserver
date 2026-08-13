// Copyright (c) 2005 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// Created 2005-10-05

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
      ended_on_non_standard_marker_(false),
      data_sent_(0),
      max_size_kb_(0),
      cancel_transmission_(false),
      write_failed_(false),
      writes_completed_(0)
   {
      buffer_ = std::shared_ptr<ByteBuffer>(new ByteBuffer);
   }

   TransparentTransmissionBuffer::~TransparentTransmissionBuffer(void)
   {

   }

   bool
   TransparentTransmissionBuffer::Initialize(std::weak_ptr<TCPConnection> pTCPConnection)
   {
      tcp_connection_ = pTCPConnection;

      data_sent_ = 0;

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

      data_sent_ = 0;

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

      // Check if we have received the entire buffer.
      if (buffer_->GetSize() >= 3 && !is_sending_ && !binary_mode_)
      {
         // If receiving, we should check for end-of-data
         size_t iSize = buffer_->GetSize();
         const char *pCharBuffer = buffer_->GetCharBuffer();

         // Whether a bare LF is accepted where the standard requires CRLF. This is the
         // existing "Allow incorrect line endings" setting, and until now it was honoured
         // everywhere EXCEPT the one place where getting it wrong hangs the session.
         //
         // The old test for end-of-data required, literally, \r\n.\r\n at the end of the
         // buffer. So a message whose final body line ended with a bare LF arrived as
         // ...text\n.\r\n - and the byte five back is 't', not '\r', so end-of-data was
         // never recognised. The other test only fires when the dot is the first byte in
         // the buffer, which happens only when the preceding flush ended exactly on a
         // newline, so it does not cover this either. The result is not a rejection but a
         // HANG: every byte of the message has arrived, the server goes on waiting for a
         // terminator that has already been and gone, the sending MTA waits for a reply
         // that will never come, and the spool file is left at zero bytes. That is
         // discussion #18's exact signature - "no external receiving is possible", with
         // the log stopping dead after 354 - and it is why the reporter's workaround of
         // disabling PIPELINING appeared to help: it changed how the data was segmented.
         //
         // Gated on the setting rather than always allowed, because RFC 5321 is specific
         // that the terminator is <CRLF>.<CRLF>, and a server should not invent
         // tolerances it was not asked for. A server that WAS asked for them should not
         // then refuse in the one case that matters.
         const bool allowBareLineFeed =
            Configuration::Instance()->GetSMTPConfiguration()->GetAllowIncorrectLineEndings();

         // How many bytes the terminator occupies, so the right number are removed. The
         // old code always removed three, which is correct for ".\r\n" and one byte too
         // many for ".\n" - and one byte too many means the last character of the message
         // body is silently eaten.
         size_t terminatorLength = 0;

         // A dot alone on a line, at the start of the buffer: the preceding flush ended
         // on the line boundary, so the newline before the dot is no longer here.
         if (pCharBuffer[0] == '.' && pCharBuffer[1] == '\r' && pCharBuffer[2] == '\n')
            terminatorLength = 3;
         else if (allowBareLineFeed && iSize >= 2 && pCharBuffer[0] == '.' && pCharBuffer[1] == '\n')
         {
            terminatorLength = 2;
            ended_on_non_standard_marker_ = true;
         }

         if (terminatorLength == 0 && iSize >= 5 &&
             pCharBuffer[iSize - 5] == '\r' && pCharBuffer[iSize - 4] == '\n' &&
             pCharBuffer[iSize - 3] == '.' &&
             pCharBuffer[iSize - 2] == '\r' && pCharBuffer[iSize - 1] == '\n')
         {
            terminatorLength = 3;
         }

         // Everything matched up to this point is the standard marker. Anything matched
         // below it is not, and the distinction is carried out to the caller rather than
         // being forgotten here: a non-standard marker means anything the peer has already
         // pipelined behind it has to be thrown away instead of parsed as SMTP commands.
         // Honouring those bytes is the CVE-2023-51764 smuggling primitive - a relay
         // upstream that does not recognise the marker forwards one message, and a server
         // that recognises it AND executes what follows has been made to accept a second
         // message nobody authorised.
         if (terminatorLength == 0 && allowBareLineFeed)
         {
            // The four spellings a sender with bare-LF line endings can produce. Each is
            // checked against the end of the buffer, and each removes only the dot and
            // the newline that follows it, leaving the body's own line ending in place.
            const bool crlfDotLf = iSize >= 4 &&
               pCharBuffer[iSize - 4] == '\r' && pCharBuffer[iSize - 3] == '\n' &&
               pCharBuffer[iSize - 2] == '.'  && pCharBuffer[iSize - 1] == '\n';

            const bool lfDotCrlf = iSize >= 4 &&
               pCharBuffer[iSize - 4] == '\n' && pCharBuffer[iSize - 3] == '.' &&
               pCharBuffer[iSize - 2] == '\r' && pCharBuffer[iSize - 1] == '\n';

            const bool lfDotLf = iSize >= 3 &&
               pCharBuffer[iSize - 3] == '\n' && pCharBuffer[iSize - 2] == '.' &&
               pCharBuffer[iSize - 1] == '\n';

            if (lfDotCrlf)
               terminatorLength = 3;
            else if (crlfDotLf || lfDotLf)
               terminatorLength = 2;

            if (terminatorLength > 0)
               ended_on_non_standard_marker_ = true;
         }

         if (terminatorLength > 0)
         {
            // Remove the transmission-end characters, leaving the message body and its
            // own final line ending.
            buffer_->DecreaseSize(terminatorLength);

            transmission_ended_ = true;
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
      
      if (bufferSize == 0)
         return dataProcessed;

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
         if (c == '.')
         {
            if (i == 0)
            {
               *pOutBuffer = '.';
               pOutBuffer++;
            }
            else if (i > 2 && pInBuffer[i-1] == '\n')
            {
               *pOutBuffer = '.';
               pOutBuffer++;
            }
         }

         // Add the character
         *pOutBuffer = c;
         pOutBuffer++;
      }

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
         if (c == '.')
         {
            if (i == 0)
               continue;
            else if (i > 2 && pInBuffer[i-1] == '\n')
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

      // Clear the buffer and insert the new data
      size_t iOutBufferLen = pOutBuffer - pOutBufferStart;
      pBuffer->Empty();
      pBuffer->Add((BYTE*) pOutBufferStart, iOutBufferLen);

      // Free memory for the old buffer
      delete [] pOutBufferStart;
   }

}
// Copyright (c) 2005 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// Created 2005-10-05

// Purpose:
//
// To act as a layer between the SMTP/POP3 components
// and the receiving socket/FILE. This class replaces
// the . with .. in the TCP/IP communication in the 
// SMTP/POP3 protocol. It also checks for the \r\n.\r\n
// sequence in the data to detect end-of-message.
// 

#pragma once

#include "File.h"

#include <string>
#include <boost/function.hpp>
#include "../TCPIP/TCPConnection.h"

namespace HM
{
   class ByteBuffer;
   class SocketConnection;
   class IProtocolParser;

   class TransparentTransmissionBufferTester;

   class TransparentTransmissionBuffer
   {
      // Drives InsertTransmissionPeriod_/RemoveTransmissionPeriod_ directly with
      // exact chunk boundaries. The line-start defect they carried only shows when
      // a chunk begins one or two bytes before a line-leading dot, which cannot be
      // provoked deterministically through a socket - the receive path only splits
      // above 40000 bytes and both paths split where TCP reads happen to land.
      friend class TransparentTransmissionBufferTester;

   public:
      TransparentTransmissionBuffer(bool bSending);
      ~TransparentTransmissionBuffer(void);

      void Append(const BYTE *pBuffer, size_t iBufferSize);
      bool Flush(bool bForce = false);
      
      bool Initialize(std::weak_ptr<TCPConnection> pTcpConnection);
      bool Initialize(const String &sFilename);

      void SetMaxSizeKB(size_t maxSize);

      // RFC 3030 CHUNKING (BDAT): byte-transparent receive mode. In binary mode the
      // buffer performs no SMTP dot-unstuffing and no \r\n.\r\n end-of-data detection;
      // the caller controls when the message ends (via the BDAT octet counts). The
      // default (false) preserves the classic DATA/POP3 transparent-transmission
      // behaviour unchanged.
      void SetBinaryMode(bool binary) { binary_mode_ = binary; }

      // BDAT: the caller marks end-of-message once the final (LAST) chunk has been
      // fully received, so the next Flush closes (and optionally fsyncs) the file.
      void MarkTransmissionEnded() { transmission_ended_ = true; }

      // Whether the end-of-data marker that ended this transmission was the standard
      // CRLF.CRLF or one of the bare-LF spellings accepted only under "allow incorrect
      // line endings". The caller needs to know because anything pipelined behind a
      // non-standard marker must be discarded rather than parsed as commands - that is
      // the CVE-2023-51764 smuggling primitive.
      bool GetEndedOnNonStandardMarker() const { return ended_on_non_standard_marker_; }

      // Bytes that arrived in the same reads as the message but BEHIND the standard
      // end-of-data marker: a pipelining client's next commands. Postfix sends
      // "...\r\n.\r\nQUIT\r\n" in one segment as a matter of course, and with several
      // queued messages the next MAIL FROM rides there instead. Null when there were
      // none - and always null after a NON-standard marker, because those bytes are
      // discarded inside Append (the CVE-2023-51764 rule above).
      std::shared_ptr<ByteBuffer> GetSurplusAfterTerminator() const { return surplus_after_terminator_; }
      
      std::shared_ptr<ByteBuffer> GetBuffer() 
      {
         return buffer_; 
      }

      bool GetTransmissionEnded()
      {
         return transmission_ended_;
      }

      bool GetRequiresFlush();
      // There's enough data in the buffer to motivate
      // a flush to file/socket.

      bool GetLastSendEndedWithNewline()
      {
         return last_send_ended_with_newline_;
      }

      bool SaveToFile_(std::shared_ptr<ByteBuffer> pBuffer);
      // Flushes the supplied buffer to file.

      size_t GetSize();

      bool GetCancelTransmission() {return cancel_transmission_;}
      String GetCancelMessage() {return cancel_message_;}

      // True if any write of received data to the spool file failed, or if a
      // configured fsync of it failed.
      //
      // Deliberately separate from cancel_transmission_, which produces a permanent
      // 554: a full disk or an I/O error is transient, and a sender told 554 discards
      // a message that would have been deliverable ten minutes later. The receiving
      // side turns this into 451 4.3.0 through the handler that already exists for a
      // spool file that could not be written at all.
      bool GetWriteFailed() const {return write_failed_;}

   private:

      void InsertTransmissionPeriod_(std::shared_ptr<ByteBuffer> pIn);
      void RemoveTransmissionPeriod_(std::shared_ptr<ByteBuffer> pIn);

      void ReportFlushFailure_();

      std::shared_ptr<ByteBuffer> buffer_;
      // The buffer containing the data to send/receive.
      
      bool transmission_ended_;
      // Have we found the end-of-transsmision sequence?
      
      bool is_sending_;
      // Are we sending data, or are we receiving data?

      bool binary_mode_;
      // RFC 3030 BDAT byte-transparent receive mode (no dot-unstuffing / no
      // end-of-data sequence detection). Default false = classic DATA behaviour.

      bool last_send_ended_with_newline_;

      // Whether the previous chunk handed to RemoveTransmissionPeriod_ ended on a
      // carriage return. Only needed for the bare-LF repair, and only ever true when a
      // forced flush split the data between a CR and its LF - without it, the LF at
      // the start of the next chunk looks bare and gets a second carriage return put
      // in front of it, corrupting the line the split fell inside.
      bool previous_chunk_ended_with_carriage_return_;

      // Whether the previous chunk ended on a line feed, i.e. whether the NEXT chunk
      // begins at the start of a line. Both dot-stuffing (send) and dot-unstuffing
      // (receive) act only on a dot that starts a line, and Flush hands this class
      // one chunk at a time - so "is index 0 a line start?" cannot be answered from
      // the chunk alone and must be carried across the boundary. Initialised true: a
      // message begins at the start of its first line. Without this a line-leading
      // dot landing at chunk index 1 or 2 was left un-stuffed on send, which a
      // receiver un-stuffs into a shorter line - or, for a lone ".", into a premature
      // <CRLF>.<CRLF> end-of-data (truncation, and an injection primitive); and a
      // forced mid-line split put a non-line-start dot at index 0, which was stuffed
      // (send) or stripped (receive) as though it began a line.
      bool previous_chunk_ended_with_newline_;

      bool ended_on_non_standard_marker_;

      // See GetSurplusAfterTerminator().
      std::shared_ptr<ByteBuffer> surplus_after_terminator_;

      // Output types
      File file_;
      std::weak_ptr<TCPConnection> tcp_connection_;

      size_t data_sent_;
      size_t max_size_kb_;

      bool cancel_transmission_;
      String cancel_message_;

      bool write_failed_;

      // Successful writes of received data so far. Used only by the fault injection in
      // SaveToFile_, to tell "nothing reached the disk" from "some of it did" - the two
      // shapes behave differently and only the second one ever lost mail.
      int writes_completed_;


      enum Limits
      {
         MAX_LINE_LENGTH = 100000
      };
   };

   // Deterministic tests for SMTP dot transparency across chunk boundaries. Wired
   // into ClassTester::DoTests, reachable via hMailServer.exe /Test.
   class TransparentTransmissionBufferTester
   {
   public:
      void Test();

   private:
      // Runs one chunk through the private transform and returns the result as a
      // std::string so exact bytes (including any embedded NUL) can be compared.
      static std::string Stuff_(TransparentTransmissionBuffer &buffer, const std::string &chunk);
      static std::string Unstuff_(TransparentTransmissionBuffer &buffer, const std::string &chunk);
   };
}

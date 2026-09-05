// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"
#include ".\SpamAssassinClient.h"

#include "../../Util/ByteBuffer.h"
#include "../../Util/File.h"
#include "../../Util/FileUtilities.h"

#include "../../Application/TimeoutCalculator.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   SpamAssassinClient::SpamAssassinClient(const String &sFile,
                                          boost::asio::io_context& io_context, 
                                          boost::asio::ssl::context& context,
                                          std::shared_ptr<Event> disconnected,
                                          std::shared_ptr<bool> testCompleted) :
               TCPConnection(CSNone, io_context, context, disconnected, ""),
               test_completed_(testCompleted),
               total_result_bytes_written_(0)
   {
      TimeoutCalculator calculator;
      SetTimeout(calculator.Calculate(IniFileSettings::Instance()->GetSAMinTimeout(), IniFileSettings::Instance()->GetSAMaxTimeout()));

      // The timeout above is an idle deadline, re-armed on every byte received, so
      // a spamd that trickles resets it indefinitely. The caller blocks a pooled
      // thread - the one that acknowledges the message - until this connection is
      // destroyed, so the session also needs an absolute ceiling. This is the
      // relayed-mail stall in discussion #18.
      SetSessionCeiling(IniFileSettings::Instance()->GetSAMaxTimeout() + 30);
            
      message_file_ = sFile;
	   spam_dsize_ = -1;
	   message_size_ = -1;

      *test_completed_ = false;
   }


   SpamAssassinClient::~SpamAssassinClient(void)
   {
      try
      {
         Cleanup_();
      }
      catch (...)
      {
         
      }
   }

   void
   SpamAssassinClient::OnConnected()
   {
      // We'll handle all incoming data as binary.
      SetReceiveBinary(true);
      message_size_ = FileUtilities::FileSize(message_file_);
      EnqueueWrite("PROCESS SPAMC/1.2\r\n");
	  //LOG_DEBUG("SENT: PROCESS SPAMC/1.2");
	  String sConLen;
	  sConLen.Format(_T("Content-length: %I64d\r\n"), message_size_);
	  EnqueueWrite(sConLen);

     // RFC-less but documented in spamd's protocol notes: "User: <name>" selects the
     // preferences the scan runs under. Only sent when configured, so a spamd that was
     // never told about users keeps seeing the request it always saw.
     if (!user_.IsEmpty())
        EnqueueWrite("User: " + user_ + "\r\n");

	  EnqueueWrite("\r\n");
     SendFileContents_(message_file_);
   }

   AnsiString 
   SpamAssassinClient::GetCommandSeparator() const
   {
      return "\r\n";
   }

   void 
   SpamAssassinClient::OnCouldNotConnect(const AnsiString &sErrorDescription)
   {
      String logMessage;
      logMessage.Format(_T("Failed to connect to SpamAssassin. Session %d"), GetSessionID());
      LOG_DEBUG(logMessage);
   }

   bool
   SpamAssassinClient::SendFileContents_(const String &sFilename)
   {
      String logMessage;
      logMessage.Format(_T("Sending message to SpamAssassin. Session %d, File: %s"), GetSessionID(), sFilename.c_str());
      LOG_DEBUG(logMessage);

      File oFile;
      if (!oFile.Open(sFilename, File::OTReadOnly))
      {
         String sErrorMsg;
         sErrorMsg.Format(_T("Could not send file %s via socket since it does not exist."), sFilename.c_str());

         ErrorManager::Instance()->ReportError(ErrorManager::High, 5019, "SMTPClientConnection::SendFileContents_", sErrorMsg);

         return false;
      }

      const int maxIterations = 100000;
      for (int i = 0; i < maxIterations; i++)
      {
         std::shared_ptr<ByteBuffer> pBuf = oFile.ReadChunk(20000);

         if (pBuf->GetSize() == 0)
            break;

         BYTE *pSendBuffer = (BYTE*) pBuf->GetBuffer();
         size_t iSendBufferSize = pBuf->GetSize();

         EnqueueWrite(pBuf);
      }

      EnqueueShutdownSend();

      // Request the response...
      EnqueueRead("");
      
      return true;
   }

   void
   SpamAssassinClient::OnConnectionTimeout()
   {
      // do nothing
   }

   void
   SpamAssassinClient::OnExcessiveDataReceived()
   {
      // do nothing
   }

   void
   SpamAssassinClient::ParseData(const AnsiString &sData)
   {

   }

   void
   SpamAssassinClient::ParseData(std::shared_ptr<ByteBuffer> pBuf)
   {
      // Captured before ParseFirstBuffer_ strips the header: a completed read that
      // delivered no bytes means spamd closed the connection (EOF).
      size_t incoming_bytes = pBuf ? pBuf->GetSize() : 0;

      if (!result_)
      {
         String logMessage;
         logMessage.Format(_T("Parsing response from SpamAssassin. Session %d"), GetSessionID());
         LOG_DEBUG(logMessage);

         result_ = std::shared_ptr<File>(new File);
         if (!result_->Open(FileUtilities::GetTempFileName(), File::OTAppend))
         {
            LOG_DEBUG("SA: could not open a temp file for the SpamAssassin response; keeping the original message.");
            AbortResponse_();
            return;
         }

         spam_dsize_ = ParseFirstBuffer_(pBuf);

         if (spam_dsize_ < 0)
         {
            // Malformed or incomplete response header. Abort without writing:
            // the original message is preserved and the failure is reported by
            // the caller. (A negative length used to wrap to a huge size_t,
            // writing the raw SPAMD header into the message and looping forever.)
            LOG_DEBUG("SA: invalid SpamAssassin response header; keeping the original message.");
            AbortResponse_();
            return;
         }
      }

      // Append output to the file.
      //
      // The result was discarded, and this file becomes the message: a failed or short
      // write left total_result_bytes_written_ counting bytes that are not on disk, so
      // the length check below could be satisfied by a file that is truncated, and
      // SpamAssassin's rewritten message would replace the original minus whatever did
      // not get written. Aborting keeps the original message, which is what the two
      // other failure paths in this function already do.
      size_t written_bytes = 0;

      if (!result_->Write(pBuf, written_bytes))
      {
         LOG_DEBUG("SA: the response could not be written to disk; keeping the original message.");
         AbortResponse_();
         return;
      }

      total_result_bytes_written_ += written_bytes;

      if (total_result_bytes_written_ >= spam_dsize_)
      {
         FinishTesting_();
         return;
      }

      // More of the body is expected. If this read delivered nothing, spamd closed
      // the connection early: stop rather than spinning on a dead socket (the old
      // code re-armed the read forever, pinning a core and never timing out).
      if (incoming_bytes == 0)
      {
         LOG_DEBUG("SA: connection closed before the full response arrived; keeping the original message.");
         AbortResponse_();
         return;
      }

      EnqueueRead("");
   }

   void
   SpamAssassinClient::AbortResponse_()
   {
      // Discard the partial response (leaving the original message file untouched)
      // and do not set test_completed_, so SpamTestSpamAssassin::RunTest logs the
      // failure. Not enqueuing another read lets the connection wind down.
      Cleanup_();
   }

   void
   SpamAssassinClient::FinishTesting_()
   {
      if (!result_)
         return;

      result_->Close();

      // Copy message if test has been run
      bool bTestsRun = true;

      String sTempFile = result_->GetName();

      // new way: check the result from spamd. Require a positive length so an
      // empty response (Content-length: 0, or a lookup that returned 0) can never
      // overwrite the live message with a zero-byte file.
      if (bTestsRun && spam_dsize_ > 0 && (FileUtilities::FileSize(sTempFile) == spam_dsize_))
      {
         // Both results are checked. The message has already been accepted, so the
         // sender has been given a 250 and there is nobody left to tell - a failure
         // here that went unreported would be a silently lost or unmodified message.
         // Failing to apply the SpamAssassin result is recoverable: the original
         // message is intact and gets delivered without the added headers, which is
         // exactly what happens when spamd is unreachable. Losing the message is not
         // recoverable, which is why FileUtilities::Move no longer deletes the
         // destination before renaming over it.
         if (IniFileSettings::Instance()->GetSAMoveVsCopy())
         {
            // Move temp file overwriting message file
            if (FileUtilities::Move(sTempFile, message_file_))
            {
               LOG_DEBUG("SA - Move used");
            }
            else
            {
               ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5860, "SpamAssassinClient::FinishTesting_",
                  "Could not move the SpamAssassin result over the message file. The message is unchanged and is delivered without the SpamAssassin headers. FileUtilities::Move has already reported the underlying reason.");
            }
         }
         else
         {
            // Copy temp file to message file
            if (FileUtilities::Copy(sTempFile, message_file_, false))
            {
               LOG_DEBUG("SA - Copy+Delete used");
            }
            else
            {
               ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5861, "SpamAssassinClient::FinishTesting_",
                  "Could not copy the SpamAssassin result over the message file. The message is unchanged and is delivered without the SpamAssassin headers.");
            }
         }
	  } 
     else 
     {
		 String logMessage;
		 logMessage.Format(_T("SA: Temp file size did not match what Spamd reported! (temp: %d, spamd: %I64d). Reverting to original message file."),FileUtilities::FileSize(sTempFile),spam_dsize_);
         LOG_DEBUG(logMessage);
	  }
     
     *test_completed_ = true;
   }

   int
   SpamAssassinClient::ParseFirstBuffer_(std::shared_ptr<ByteBuffer> pBuffer) const
   {
      // Don't send first line, since it's the Result header.
      char *pHeaderEndPosition = StringParser::Search(pBuffer->GetCharBuffer(), pBuffer->GetSize(), "\r\n\r\n");
      if (!pHeaderEndPosition)
      {
         LOG_DEBUG("The response from SpamAssasin was not valid. Aborting. Expected a header.\r\n");
         return -1;
      }
            
      size_t headerLength = pHeaderEndPosition - pBuffer->GetCharBuffer();
      AnsiString spamAssassinHeader(pBuffer->GetCharBuffer(), headerLength);

      std::vector<AnsiString> headerLines = StringParser::SplitString(spamAssassinHeader, "\r\n");

      // An error reply (e.g. "SPAMD/1.1 76 Bad header") is a single line with no
      // Content-length, so guard both indices before reading them.
      if (headerLines.size() < 2)
      {
         LOG_DEBUG(Formatter::Format("The response from SpamAssasin was not valid. Aborting. Incomplete header: {0}\r\n", spamAssassinHeader));
         return -1;
      }

      AnsiString firstLine = headerLines[0];
      AnsiString secondLine = headerLines[1];

      // Accept any SPAMD protocol version, not just 1.1, as long as it is EX_OK.
      if (!firstLine.StartsWith("SPAMD/") || firstLine.FindNoCase("EX_OK") < 0)
      {
         LOG_DEBUG(Formatter::Format("The response from SpamAssasin was not valid. Aborting. Expected: SPAMD/<version> 0 EX_OK, Got: {0}\r\n", firstLine));
         return -1;
      }

      if (!secondLine.StartsWith("Content-length:"))
      {
         // We should never get here, since we should always have
         // a header in the result
         LOG_DEBUG(Formatter::Format("The response from SpamAssasin was not valid. Aborting. Expected: Content-Length:<value>, Got: {0}\r\n", secondLine));
         return -1;
      }

      // Extract the second line from the first buffer. This buffer
      // contains the result of the operation (success / failure).
      std::vector<AnsiString> contentLengthHeader = StringParser::SplitString(secondLine, ":");
      if (contentLengthHeader.size() != 2)
      {
         LOG_DEBUG(Formatter::Format("The response from SpamAssasin was not valid. Aborting. Content-Length header not properly formatted. Expected: Content-Length:<value>, Got: {0}\r\n", secondLine));
         return -1;
      }

      int contentLength;
      std::string sConSize = contentLengthHeader[1].Trim();
      if (!StringParser::TryParseInt(sConSize, contentLength))
      {
        LOG_DEBUG(Formatter::Format("The response from SpamAssasin was not valid. Aborting. Content-Length header not properly formatted. Expected: Content-Length:<value>, Got: {0}\r\n", secondLine));
	     return -1;
      }

      if (contentLength < 0)
      {
         // A negative length would wrap when driving the read loop / size check.
         LOG_DEBUG(Formatter::Format("The response from SpamAssasin was not valid. Aborting. Negative Content-Length: {0}\r\n", secondLine));
         return -1;
      }

      // Remove the SA header lines from the result.
      size_t iEndingBytesSize = pBuffer->GetSize() - headerLength - 4; // 4 due to header ending with \r\n\r\n.
      pBuffer->Empty(iEndingBytesSize);

      return contentLength;
   }

   /*
      Handles the situation where SpamAssasin does not send the entire
      response to hMailServer. In this case, an error will be logged and
      SA won't have any effect.

   */
   void 
   SpamAssassinClient::OnReadError(int errorCode)
   {
      String errorMessage;
      errorMessage.Format(_T("There was a communication error with SpamAssassin. ")
                          _T("hMailServer tried to retrieve data from SpamAssassin but the connection ")
                          _T("to SpamAssassin was lost. The WinSock error code is %d. Enable debug ")
                          _T("logging to retrieve more information regarding this problem. ")
                          _T("The problem could be that SpamAssassin is malfunctioning."), errorCode);

      ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5157, "SpamAssassinClient::OnReadError", errorMessage);

   }

   void 
   SpamAssassinClient::Cleanup_()
   {
      if (result_ != nullptr)
      {
         result_->Close();
         FileUtilities::DeleteFile(result_->GetName());
         result_ = nullptr;
      }
   }
}

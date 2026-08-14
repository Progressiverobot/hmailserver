// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// http://www.hmailserver.com

#include "StdAfx.h"

#include "AuthenticationResultsWriter.h"
#include "AuthenticationResults.h"

#include "../BO/Message.h"
#include "../Persistence/PersistentMessage.h"
#include "../Util/File.h"
#include "../Util/FileUtilities.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   bool
   AuthenticationResultsWriter::Write(const String &messageFileName,
                                      std::shared_ptr<Message> message,
                                      std::shared_ptr<AuthenticationResults> results)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Builds the new header fields, drops any Authentication-Results field already in
   // the message that carries our own authserv-id, and replaces the accepted message
   // with the result in a single atomic rename.
   //
   // The accepted message is only ever opened read-only and only ever replaced whole.
   // Every failure path returns with that file exactly as it was found and still
   // deliverable, because the sender has already been told 250 by the time this runs:
   // a message that ends up missing, truncated, or under the wrong name is permanently
   // lost mail the sender believes was delivered. A message without an
   // Authentication-Results header is not. DistributionListSender::PrependHeaderLines_
   // is the reference implementation of this shape and its comment explains each of the
   // three failure windows this avoids; the same reasoning applies here unchanged.
   //---------------------------------------------------------------------------()
   {
      if (!results || results->IsEmpty())
         return false;

      AnsiString newFields;

      if (IniFileSettings::Instance()->GetAuthenticationResultsEnabled())
      {
         AnsiString value = results->BuildAuthenticationResultsValue();

         if (!value.IsEmpty())
            newFields += "Authentication-Results: " + value + "\r\n";
      }

      if (IniFileSettings::Instance()->GetReceivedSpfHeaderEnabled() && results->HasSpfResult())
      {
         AnsiString value = results->BuildReceivedSpfValue();

         if (!value.IsEmpty())
            newFields += "Received-SPF: " + value + "\r\n";
      }

      if (newFields.IsEmpty())
         return false;

      const AnsiString authservId = AuthenticationResults::GetAuthservId();

      const AnsiString header = PersistentMessage::LoadHeader(messageFileName);

      if (header.IsEmpty())
      {
         // No header could be read, so the message file could not be opened or has no
         // header terminator. LoadHeader has already reported the I/O case. Adding
         // nothing is right: a rewrite built on a header we could not read would be
         // guesswork on an accepted message.
         return false;
      }

      // RFC 8601 section 5. Anything here bearing our own authserv-id was not written
      // by us - this runs before we add ours - so on mail whose source we have not
      // authenticated it is a forgery, and a downstream filter that trusts our name
      // would act on a "dkim=pass" the sender wrote for themselves.
      int removedCount = 0;
      const AnsiString strippedHeader = AuthenticationResults::StripResultsForAuthservId(header, authservId, removedCount);

      const long originalSize = FileUtilities::FileSize(messageFileName);
      const String tempFileName = messageFileName + ".authresults.tmp";

      // Two shapes, because the common one is much simpler and its size arithmetic is
      // exact. With nothing to strip this is a pure prepend and the result is the new
      // fields followed by every original byte. With something to strip, the header
      // block is replaced and the body is copied from just past where the old header
      // ended - LoadHeader returns the header up to and including the CRLF that closes
      // its last field, so that offset is exactly its length.
      const bool stripping = removedCount > 0;

      const long expectedSize = stripping
         ? (originalSize - header.GetLength() + strippedHeader.GetLength() + newFields.GetLength())
         : (originalSize + newFields.GetLength());

      bool written = false;

      try
      {
         File temporaryFile;

         if (!temporaryFile.Open(tempFileName, File::OTCreate))
         {
            // Nothing has been written and the accepted message has not been opened.
            ErrorManager::Instance()->ReportError(ErrorManager::Medium, 6124, "AuthenticationResultsWriter::Write",
               Formatter::Format("Could not create '{0}' to add the Authentication-Results header. The message is delivered unchanged.", tempFileName));
            return false;
         }

         File messageFile;

         if (stripping)
         {
            written = temporaryFile.Write(newFields) &&
                      temporaryFile.Write(strippedHeader) &&
                      messageFile.Open(messageFileName, File::OTReadOnly) &&
                      messageFile.SetPosition(header.GetLength()) &&
                      temporaryFile.Write(messageFile) &&
                      temporaryFile.FlushToDisk();
         }
         else
         {
            written = temporaryFile.Write(newFields) &&
                      messageFile.Open(messageFileName, File::OTReadOnly) &&
                      temporaryFile.Write(messageFile) &&
                      temporaryFile.FlushToDisk();
         }

         temporaryFile.Close();
         messageFile.Close();
      }
      catch (...)
      {
         // File::Write(File&) copies through ReadChunk, which reports and rethrows on a
         // read error rather than returning false, and also throws if the chunk buffer
         // cannot be allocated. Letting that escape would carry a failure out of the
         // accept path for a message sitting on disk intact, and leave the temporary
         // file behind. It is what it is: the rewrite did not happen.
         written = false;
      }

      const long newSize = FileUtilities::FileSize(tempFileName);

      // The exact figure, not merely "it grew". File::Write(File&) returns true when a
      // chunk comes back short at end of file, so a body truncated by less than the
      // length of the fields just added would still leave a larger file.
      if (!written || newSize != expectedSize)
      {
         FileUtilities::DeleteFile(tempFileName);

         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 6125, "AuthenticationResultsWriter::Write",
            Formatter::Format("Rewrite of '{0}' to add the Authentication-Results header produced {1} bytes where {2} were expected (original {3}), so it was discarded and the accepted message left untouched and deliverable.",
               messageFileName, (int) newSize, (int) expectedSize, (int) originalSize));
         return false;
      }

      // The one and only modification to the accepted message: replaced whole, or not
      // at all.
      if (!FileUtilities::Move(tempFileName, messageFileName))
      {
         FileUtilities::DeleteFile(tempFileName);

         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 6126, "AuthenticationResultsWriter::Write",
            Formatter::Format("Could not put the rewritten '{0}' in place, so the accepted message is still there unchanged and is delivered without an Authentication-Results header.", messageFileName));
         return false;
      }

      if (message)
         message->SetSize((int) expectedSize);

      if (removedCount > 0)
      {
         // Worth saying out loud rather than only in a debug line: a message arriving
         // with our own authserv-id on it is either a misconfigured relay in front of
         // this server or somebody trying to have their own verdict believed.
         LOG_APPLICATION(Formatter::Format("AuthenticationResultsWriter - Removed {0} forged Authentication-Results header(s) claiming the identity '{1}' from an inbound message.",
            removedCount, String(authservId)));
      }

      return true;
   }
}

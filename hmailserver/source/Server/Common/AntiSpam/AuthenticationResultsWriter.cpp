// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// http://www.hmailserver.com

#include "StdAfx.h"

#include "AuthenticationResultsWriter.h"
#include "AuthenticationResults.h"

#include "../BO/Message.h"
#include "../Persistence/PersistentMessage.h"
#include "../Util/ByteBuffer.h"
#include "../Util/File.h"
#include "../Util/FileUtilities.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   namespace
   {
      // As many bytes as a header block may occupy before this gives up on finding the
      // terminator. Generous - a header with a long DKIM signature, a long Received
      // chain and an ARC set is still far under it - and bounded so a message with no
      // terminator at all cannot make this read the whole file.
      const int MaxRawHeaderBytes = 262144;

      // The header block's RAW bytes, and false when the terminator was not found
      // inside the cap.
      //
      // This does NOT use PersistentMessage::LoadHeader, and that is the whole point.
      // LoadHeader accumulates the file's bytes into a *wide* String and returns it as
      // an AnsiString, so what comes back has been through a CP_ACP narrow -> wide ->
      // narrow round trip: its GetLength() is a converted character count, equal to the
      // raw byte count only for pure 7-bit ASCII. Using it as a file offset - which is
      // what this writer did - seeks to the wrong place the moment a header carries a
      // non-ASCII byte on a host whose ANSI code page is multibyte or UTF-8, and the
      // rewrite then splices the last header line onto the body or duplicates header
      // bytes ahead of it. Re-emitting the round-tripped text also silently rewrites
      // those bytes, which breaks any DKIM signature covering them - the exact promise
      // the strip is supposed to keep.
      //
      // Worse, the size check could never catch it: expectedSize was computed from the
      // same length used to do the seek, so the two agreed algebraically whatever that
      // length was. Reading the bytes here is what makes the offset and the size honest.
      bool ReadRawHeader_(const String &fileName, AnsiString &header)
      {
         File file;

         if (!file.Open(fileName, File::OTReadOnly))
            return false;

         std::shared_ptr<ByteBuffer> buffer = file.ReadChunk(MaxRawHeaderBytes);
         file.Close();

         if (!buffer || buffer->GetSize() == 0)
            return false;

         AnsiString raw((const char*) buffer->GetBuffer(), buffer->GetSize());

         int terminator = raw.Find("\r\n\r\n");

         if (terminator < 0)
            return false;

         // Up to and including the CRLF that closes the last field, so the blank line
         // begins the remainder - the same boundary LoadHeader used, but measured in
         // bytes.
         header = raw.Mid(0, terminator + 2);
         return true;
      }
   }
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

      // A header we could not delimit is one we must not rewrite by offset. Stripping is
      // skipped rather than attempted: with no terminator the "header" would be the whole
      // file, and a dropped field's continuation state would then swallow any body line
      // that happens to begin with a space. The fields we add are still prepended, which
      // needs no offset at all.
      AnsiString header;
      const bool haveDelimitedHeader = ReadRawHeader_(messageFileName, header);

      int removedCount = 0;
      AnsiString strippedHeader;

      if (haveDelimitedHeader)
      {
         // RFC 8601 section 5. Anything here bearing our own authserv-id was not written
         // by us - this runs before we add ours - so on mail whose source we have not
         // authenticated it is a forgery, and a downstream filter that trusts our name
         // would act on a "dkim=pass" the sender wrote for themselves.
         strippedHeader = AuthenticationResults::StripResultsForAuthservId(header, authservId, removedCount);
      }

      unsigned __int64 originalSize = 0;
      if (!FileUtilities::FileSize64(messageFileName, originalSize))
      {
         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 6127, "AuthenticationResultsWriter::Write",
            Formatter::Format("Could not measure '{0}', so no Authentication-Results header was added. The message is delivered unchanged.", messageFileName));
         return false;
      }

      const String tempFileName = messageFileName + ".authresults.tmp";

      // Two shapes, because the common one is much simpler and its size arithmetic is
      // exact. With nothing to strip this is a pure prepend and the result is the new
      // fields followed by every original byte. With something to strip, the header
      // block is replaced and the body is copied from just past where the old header
      // ended - ReadRawHeader_ returns the header up to and including the CRLF that
      // closes its last field, measured in bytes, so that offset is exactly its length.
      const bool stripping = removedCount > 0;

      // Unsigned 64-bit throughout. FileUtilities::FileSize returns a long that
      // saturates at LONG_MAX for a file of 2 GiB or more, and adding to a saturated
      // value is signed overflow - undefined behaviour that happened to fail in the safe
      // direction rather than by design.
      const unsigned __int64 expectedSize = stripping
         ? (originalSize - (unsigned __int64) header.GetLength()
                         + (unsigned __int64) strippedHeader.GetLength()
                         + (unsigned __int64) newFields.GetLength())
         : (originalSize + (unsigned __int64) newFields.GetLength());

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

      unsigned __int64 newSize = 0;
      const bool measured = FileUtilities::FileSize64(tempFileName, newSize);

      // The exact figure, not merely "it grew". File::Write(File&) returns true when a
      // chunk comes back short at end of file, so a body truncated by less than the
      // length of the fields just added would still leave a larger file.
      //
      // The header length that feeds expectedSize is now the RAW byte length, and it is
      // the same number the seek used - so unlike the previous version, where both sides
      // derived from a converted character count and therefore always agreed, a wrong
      // offset now shows up here as a size mismatch.
      if (!written || !measured || newSize != expectedSize)
      {
         FileUtilities::DeleteFile(tempFileName);

         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 6125, "AuthenticationResultsWriter::Write",
            Formatter::Format("Rewrite of '{0}' to add the Authentication-Results header produced {1} bytes where {2} were expected (original {3}), so it was discarded and the accepted message left untouched and deliverable.",
               messageFileName, (__int64) newSize, (__int64) expectedSize, (__int64) originalSize));
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

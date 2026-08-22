// Copyright (c) 2008 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// Created 2008-12-23
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"
#include "TraceHeaderWriter.h"
#include "../BO/Message.h"
#include "../Persistence/PersistentMessage.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   TraceHeaderWriter::TraceHeaderWriter()
   {

   }

   TraceHeaderWriter::~TraceHeaderWriter(void)
   {

   }

   bool 
   TraceHeaderWriter::Write(const String &messageFileName, std::shared_ptr<Message> message, const std::vector<std::pair<AnsiString, AnsiString> > &headerFields)
   {
      if (headerFields.size() == 0)
         return true;

      // Add a return-path header. 
      String tempFile = messageFileName + ".tmp";

      File temporaryFile;
      if (!temporaryFile.Open(tempFile, File::OTCreate))
         return false;

      typedef std::pair<AnsiString, AnsiString> headerField;

      AnsiString prependString;
      for(headerField field : headerFields)
      {
         prependString += field.first + ": " + field.second + "\r\n";
      }
      
      bool written = temporaryFile.Write(prependString);

      if (written)
      {
         File messageFile;

         written = messageFile.Open(messageFileName, File::OTReadOnly) &&
                   temporaryFile.Write(messageFile);
      }

      temporaryFile.Close();

      // Every failure here leaves a rewritten copy of the message on disk that
      // nothing refers to. This runs for every DKIM signature, ARC seal, local
      // delivery and SpamAssassin result, so a full disk or a message file locked
      // by a scanner would otherwise drop one orphan per message into the
      // account's folder, where the consistency check would never notice it (it
      // looks for database rows with no file, not files with no row).
      if (!written || !FileUtilities::Move(tempFile, messageFileName))
      {
         FileUtilities::DeleteFile(tempFile);
         return false;
      }

      return true;
   }

}
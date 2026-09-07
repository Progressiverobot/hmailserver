// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "stdafx.h"
#include "IMAPCommandCompress.h"
#include "IMAPConnection.h"
#include "../Common/Application/IniFileSettings.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   IMAPCommandCompress::IMAPCommandCompress()
   {
   }

   IMAPCommandCompress::~IMAPCommandCompress()
   {
   }

   IMAPResult
   IMAPCommandCompress::ExecuteCommand(std::shared_ptr<IMAPConnection> pConnection, std::shared_ptr<IMAPCommandArgument> pArgument)
   {
      // Not advertised when switched off, and a command that is not advertised is
      // not understood: BAD, as for any unknown command.
      if (!IniFileSettings::Instance()->GetImapCompressionEnabled())
         return IMAPResult(IMAPResult::ResultBad, "Unknown or NULL command");

      // "COMPRESS DEFLATE": the one algorithm the RFC defines.
      String sLine = pArgument->Command();
      int iSpace = sLine.Find(_T(" "));
      String sAlgorithm = iSpace >= 0 ? sLine.Mid(iSpace + 1) : String();
      sAlgorithm.TrimLeft();
      sAlgorithm.TrimRight();
      if (sAlgorithm.CompareNoCase(_T("DEFLATE")) != 0)
         return IMAPResult(IMAPResult::ResultBad, "Unsupported compression algorithm; this server offers DEFLATE");

      // RFC 4978 section 3: a second COMPRESS on the same connection is refused
      // with the COMPRESSIONACTIVE response code, and so is one under a TLS layer
      // that already compresses - which this server's never does.
      if (pConnection->IsCompressed())
         return IMAPResult(IMAPResult::ResultNo, "[COMPRESSIONACTIVE] DEFLATE active via COMPRESS");

      // The OK is the last plain line; the connection deflates everything after it
      // and inflates everything the client sends from now. ResultOK, so that the
      // read for the next - compressed - command is posted as usual.
      if (!pConnection->EnableCompression(pArgument->Tag() + " OK DEFLATE active\r\n"))
         return IMAPResult(IMAPResult::ResultNo, "Compression could not be started");

      String sMessage;
      sMessage.Format(_T("IMAP: COMPRESS=DEFLATE active for session %d"), pConnection->GetSessionID());
      LOG_DEBUG(sMessage);

      return IMAPResult(IMAPResult::ResultOK, "");
   }
}

// Copyright (c) 2010 Martin Knafve / hMailServer.com.
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

using boost::asio::ip::tcp;

#include "VirusScanningResult.h"

namespace HM
{
   class ClamAVVirusScanner
   {
   public:
      ClamAVVirusScanner(void);
      ~ClamAVVirusScanner(void);

      static VirusScanningResult Scan(const String &sFilename);
      static VirusScanningResult Scan(const String &hostName, int primaryPort, const String &sFilename);

      // Scans bytes held in memory over the same INSTREAM exchange a file goes
      // through. The connection test uses it so that its EICAR sample never touches
      // the disk: written to the data directory, the host's own antivirus removes
      // it before clamd is asked about it, and the test then reported a missing file
      // rather than anything about clamd.
      static VirusScanningResult ScanData(const String &hostName, int port, const AnsiString &data);

      // clamd's own liveness and identity commands: PING, which a healthy daemon
      // answers with PONG, and VERSION, whose reply names the engine and the
      // signature database. Used by the connection test before it scans anything, so
      // "clamd is not there" and "clamd is there and misjudges EICAR" are told apart,
      // and the administrator sees which clamd they are talking to. Both are
      // non-session commands, so each gets a connection of its own.
      static bool Ping(const String &hostName, int port, String &version, String &error);
   };

}

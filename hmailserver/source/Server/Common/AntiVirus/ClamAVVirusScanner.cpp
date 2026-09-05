// Copyright (c) 2010 Martin Knafve / hMailServer.com.
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"

#include "ClamAVVirusScanner.h"
#include "AntiVirusConfiguration.h"

#include "../TCPIP/SynchronousConnection.h"
#include "../Util/ByteBuffer.h"
#include "../Util/File.h"

#include "../Application/TimeoutCalculator.h"

#include <Boost/Regex.hpp>
#include <functional>
using namespace boost;


#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   namespace
   {
      // The INSTREAM chunk prefix: the chunk's length as four octets in network order,
      // and a zero length ends the stream.
      bool WriteChunkLength_(SynchronousConnection &connection, uint32_t size)
      {
         const uint32_t networkOrder = htonl(size);

         ByteBuffer prefix;
         prefix.Add(reinterpret_cast<const BYTE*>(&networkOrder), sizeof(networkOrder));
         return connection.Write(prefix);
      }

      // One INSTREAM exchange: nINSTREAM, length-prefixed chunks from the source until
      // it yields an empty one, the terminator, and clamd's one-line verdict.
      VirusScanningResult StreamToClamd_(const String &hostName, int port,
                                         const std::function<std::shared_ptr<ByteBuffer>()> &nextChunk)
      {
         TimeoutCalculator calculator;

         LOG_DEBUG("Connecting to ClamAV virus scanner...");
         SynchronousConnection commandConnection(calculator.Calculate(IniFileSettings::Instance()->GetClamMinTimeout(), IniFileSettings::Instance()->GetClamMaxTimeout()));
         if (!commandConnection.Connect(hostName, port))
         {
            return VirusScanningResult(_T("ClamAVVirusScanner::Scan"),
               Formatter::Format("Unable to connect to ClamAV server at {0}:{1}.", hostName, port));
         }

         if (!commandConnection.Write("nINSTREAM\n"))
            return VirusScanningResult("ClamAVVirusScanner::Scan", "Unable to write INSTREAM command.");

         const int maxIterations = 100000;
         for (int i = 0; i < maxIterations; i++)
         {
            std::shared_ptr<ByteBuffer> pBuf = nextChunk();

            if (!pBuf || pBuf->GetSize() == 0)
               break;

            if (!WriteChunkLength_(commandConnection, static_cast<uint32_t>(pBuf->GetSize())))
               return VirusScanningResult("ClamAVVirusScanner::Scan", "Unable to write packet size to stream port.");

            if (!commandConnection.Write(*pBuf))
               return VirusScanningResult("ClamAVVirusScanner::Scan", "Unable to write packet data to stream port.");
         }

         if (!WriteChunkLength_(commandConnection, 0))
            return VirusScanningResult("ClamAVVirusScanner::Scan", "Unable to write end of stream.");

         AnsiString readData;
         if (!commandConnection.ReadUntil("\n", readData))
            return VirusScanningResult("ClamAVVirusScanner::Scan", "Unable to read response (after streaming).");

         commandConnection.Close();

         readData.TrimRight("\n");

         // Parse the response and see if a virus was reported.
         try
         {
            const regex expression("^stream.*: (.*) FOUND$");
            cmatch what;
            if (regex_match(readData.c_str(), what, expression))
            {
               LOG_DEBUG("Virus detected: " + what[1]);
               return VirusScanningResult(VirusScanningResult::VirusFound, String(what[1]));
            }
            else
            {
               LOG_DEBUG("No virus detected: " + readData);
               return VirusScanningResult(VirusScanningResult::NoVirusFound, Formatter::Format("Result: {0}", readData));
            }
         }
         catch (std::runtime_error&) // regex_match will throw runtime_error if regexp is too complex.
         {
            return VirusScanningResult("ClamAVVirusScanner::Scan", "Unable to parse regular expression.");
         }
      }
   }

   ClamAVVirusScanner::ClamAVVirusScanner(void)
   {
   }

   ClamAVVirusScanner::~ClamAVVirusScanner(void)
   {

   }

   bool
   ClamAVVirusScanner::Ping(const String &hostName, int port, String &version, String &error)
   {
      version.Empty();
      error.Empty();

      TimeoutCalculator calculator;
      const int timeout = calculator.Calculate(IniFileSettings::Instance()->GetClamMinTimeout(), IniFileSettings::Instance()->GetClamMaxTimeout());

      {
         SynchronousConnection connection(timeout);
         if (!connection.Connect(hostName, port))
         {
            error = Formatter::Format("Unable to connect to ClamAV server at {0}:{1}.", hostName, port);
            return false;
         }

         AnsiString reply;
         if (!connection.Write("nPING\n") || !connection.ReadUntil("\n", reply))
         {
            error = Formatter::Format("clamd at {0}:{1} did not answer PING.", hostName, port);
            return false;
         }
         connection.Close();

         reply.TrimRight("\n");
         if (reply != "PONG")
         {
            error = Formatter::Format("clamd at {0}:{1} answered PING with '{2}' rather than PONG.", hostName, port, String(reply));
            return false;
         }
      }

      // The version is a courtesy: a daemon that answers PING but not VERSION is
      // still a daemon, so a failure here is reported in the text, not as a failure.
      SynchronousConnection connection(timeout);
      if (connection.Connect(hostName, port))
      {
         AnsiString reply;
         if (connection.Write("nVERSION\n") && connection.ReadUntil("\n", reply))
         {
            reply.TrimRight("\n");
            version = reply;
         }
         connection.Close();
      }

      return true;
   }

   VirusScanningResult
   ClamAVVirusScanner::Scan(const String &sFilename)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   //---------------------------------------------------------------------------()
   {
      AntiVirusConfiguration& config = Configuration::Instance()->GetAntiVirusConfiguration();

      String hostName = config.GetClamAVHost();
      int primaryPort = config.GetClamAVPort();

      return Scan(hostName, primaryPort, sFilename);
   }

   VirusScanningResult
   ClamAVVirusScanner::Scan(const String &hostName, int primaryPort, const String &sFilename)
   {
      File oFile;
      if (!oFile.Open(sFilename, File::OTReadOnly))
      {
         String sErrorMsg = Formatter::Format("Could not send file {0} via socket since it does not exist.", sFilename);
         return VirusScanningResult("ClamAVVirusScanner::Scan", sErrorMsg);
      }

      const int STREAM_BLOCK_SIZE = 4096;
      return StreamToClamd_(hostName, primaryPort, [&oFile, STREAM_BLOCK_SIZE]()
         {
            return oFile.ReadChunk(STREAM_BLOCK_SIZE);
         });
   }

   VirusScanningResult
   ClamAVVirusScanner::ScanData(const String &hostName, int port, const AnsiString &data)
   {
      bool sent = false;
      return StreamToClamd_(hostName, port, [&data, &sent]()
         {
            std::shared_ptr<ByteBuffer> chunk = std::shared_ptr<ByteBuffer>(new ByteBuffer());

            if (!sent && data.GetLength() > 0)
            {
               chunk->Add(reinterpret_cast<const BYTE*>(data.c_str()), static_cast<size_t>(data.GetLength()));
               sent = true;
            }

            return chunk;
         });
   }
}

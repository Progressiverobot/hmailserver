// Copyright (c) 2010 Martin Knafve / hMailServer.com.
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"

#include "VirusScannerTester.h"
#include "CustomVirusScanner.h"
#include "ClamWinVirusScanner.h"
#include "ClamAVVirusScanner.h"
#include "VirusScanningResult.h"

#include "../Util/GUIDCreator.h"

#include <algorithm>

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   bool
   VirusScannerTester::TestClamAVConnect(const String &hostName, int port, String &message)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   //---------------------------------------------------------------------------()
   {
      // PING and VERSION first: a daemon that is not there, or is something other
      // than clamd on that port, is told apart from one that is there and misjudges
      // the two samples, and the administrator learns which clamd answered.
      String version, pingError;
      if (!ClamAVVirusScanner::Ping(hostName, port, version, pingError))
      {
         message = pingError;
         return false;
      }

      const String identity = version.IsEmpty() ? String(_T("clamd answered PING")) : version;

      // Both samples are streamed from memory rather than written to the data
      // directory. A host antivirus watching that directory removes an EICAR file
      // the moment it is written, and this test then reported a missing file - on
      // every Windows installation with real-time protection on, which is the
      // default - rather than anything about clamd.
      VirusScanningResult result = ClamAVVirusScanner::ScanData(hostName, port, PlainSample_());

      if (result.GetVirusFound())
      {
         message = identity + ". False positive: " + result.GetDetails();
         return false;
      }

      result = ClamAVVirusScanner::ScanData(hostName, port, EicarSample_());
      message = identity + ". " + result.GetDetails();

      return result.GetVirusFound();
   }

   bool
   VirusScannerTester::TestCustomVirusScanner(const String &executable, int returnValue, String &message)
   {
      CustomVirusScanner scanner;

      String testFile = GeneratePlainTestFile_();
      VirusScanningResult result = scanner.Scan(executable, returnValue, testFile);
      FileUtilities::DeleteFile(testFile);

      if (result.GetVirusFound())
      {
         message = "False positive: " + result.GetDetails();
         return false;
      }

      testFile = GenerateVirusTestFile_();
      if (SampleRemovedBeforeScan_(testFile, message))
         return false;

      result = scanner.Scan(executable, returnValue, testFile);
      FileUtilities::DeleteFile(testFile);
      message = result.GetDetails();
      return result.GetVirusFound();
   }

   bool
   VirusScannerTester::TestClamWinVirusScanner(const String &executable, const String &databasePath, String &message)
   {
      ClamWinVirusScanner scanner;

      String testFile = GeneratePlainTestFile_();
      VirusScanningResult result = scanner.Scan(executable, databasePath, testFile);
      FileUtilities::DeleteFile(testFile);

      if (result.GetVirusFound())
      {
         message = "False positive: " + result.GetDetails();
         return false;
      }

      testFile = GenerateVirusTestFile_();
      if (SampleRemovedBeforeScan_(testFile, message))
         return false;

      result = scanner.Scan(executable, databasePath, testFile);
      FileUtilities::DeleteFile(testFile);
      message = result.GetDetails();
      return result.GetVirusFound();
   }

   AnsiString
   VirusScannerTester::PlainSample_()
   {
      return "Test";
   }

   AnsiString
   VirusScannerTester::EicarSample_()
   {
      // Held reversed so the binary itself does not carry the test string, and
      // reversed back only when it is about to be scanned.
      AnsiString reversed = " *H+H$!ELIF-TSET-SURIVITNA-DRADNATS-RACIE$}7)CC7)^P(45XZP\\4[PA@%P!O5X";
      reversed.MakeReverse();
      return reversed;
   }

   bool
   VirusScannerTester::SampleRemovedBeforeScan_(const String &testFile, String &message)
   {
      if (FileUtilities::Exists(testFile))
         return false;

      message = Formatter::Format("The EICAR sample written to {0} was removed before the scanner could read it, which is what a host antivirus watching the data directory does. Exclude the mail store from real-time scanning, which the scanner configured here replaces for mail, and run the test again.", testFile);
      return true;
   }

   String
   VirusScannerTester::GenerateVirusTestFile_()
   {
      // Write the test virus to the data directory to simulate email.
      String dataDir = IniFileSettings::Instance()->GetDataDirectory();

      String messageFileName = GUIDCreator::GetGUID() + ".eml";
      String fullPathToMessage = FileUtilities::Combine(dataDir, messageFileName);

      FileUtilities::WriteToFile(fullPathToMessage, EicarSample_());

      return fullPathToMessage;
   }

   String
   VirusScannerTester::GeneratePlainTestFile_()
   {
      // Write the plain sample to the data directory to simulate email.
      String dataDir = IniFileSettings::Instance()->GetDataDirectory();

      String messageFileName = GUIDCreator::GetGUID() + ".eml";
      String fullPathToMessage = FileUtilities::Combine(dataDir, messageFileName);

      FileUtilities::WriteToFile(fullPathToMessage, PlainSample_());

      return fullPathToMessage;
   }
}

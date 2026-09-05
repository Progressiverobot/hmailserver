// Copyright (c) 2010 Martin Knafve / hMailServer.com.
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

namespace HM
{
   class VirusScannerTester
   {
   public:
      bool TestClamAVConnect(const String &hostName, int port, String &message);
      bool TestCustomVirusScanner(const String &executable, int returnValue, String &message);
      bool TestClamWinVirusScanner(const String &executable, const String &databasePath, String &message);

   private:
      // The two samples every test uses: a few harmless bytes that must not alarm,
      // and the EICAR test string that must.
      static AnsiString PlainSample_();
      static AnsiString EicarSample_();

      String GenerateVirusTestFile_();
      String GeneratePlainTestFile_();

      // The command-line scanners read a file, and a host antivirus watching the data
      // directory removes an EICAR file as soon as it is written. When the sample is
      // gone before the scanner is run, that is what the administrator is told.
      static bool SampleRemovedBeforeScan_(const String &testFile, String &message);
   };

}

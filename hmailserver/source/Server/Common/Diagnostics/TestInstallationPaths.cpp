// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "stdafx.h"

#include "TestInstallationPaths.h"
#include "../Util/Registry.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   TestInstallationPaths::TestInstallationPaths()
   {

   }

   TestInstallationPaths::~TestInstallationPaths()
   {

   }

   void
   TestInstallationPaths::AppendPath_(String &report, bool &all_present, const String &label, const String &path, bool is_directory)
   {
      String state;

      if (path.IsEmpty())
      {
         state = _T("not set");
      }
      else
      {
         const bool present = is_directory ? FileUtilities::DirectoryExists(path) : FileUtilities::Exists(path);

         state = present ? _T("exists") : _T("MISSING");

         if (!present)
            all_present = false;
      }

      report.append(Formatter::Format(_T("  {0}: {1}   [{2}]\r\n"), label, path.IsEmpty() ? String(_T("-")) : path, state));
   }

   DiagnosticResult
   TestInstallationPaths::PerformTest()
   {
      DiagnosticResult diagResult;
      diagResult.SetName("Installation paths");
      diagResult.SetDescription("Lists every directory and file the server was configured with, where each was read from, and whether it exists.");

      IniFileSettings *ini = IniFileSettings::Instance();

      String report;
      bool all_present = true;

      // Where the configuration itself came from. Everything below was read out
      // of this file, so a wrong answer here explains every wrong answer after it.
      report.append(_T("Configuration\r\n"));
      AppendPath_(report, all_present, _T("hMailServer.ini"), IniFileSettings::GetInitializationFile(), false);

#ifdef HM_PLATFORM_POSIX
      report.append(_T("  Registry: none on this platform; the executable's own directory is the anchor\r\n"));
#else
      // The value the server actually anchors on. It is read from the 32-bit
      // registry view (Registry::GetStringValue asks for KEY_WOW64_32KEY), and
      // that is the view an administrator has to write when repointing it -
      // writing the 64-bit view changes nothing and looks like it worked.
      String install_location;
      Registry registry;

      if (registry.GetStringValue(HKEY_LOCAL_MACHINE, "SOFTWARE\\hMailServer", "InstallLocation", install_location))
         AppendPath_(report, all_present, _T("HKLM\\SOFTWARE\\hMailServer\\InstallLocation (32-bit view)"), install_location, true);
      else
         report.append(_T("  HKLM\\SOFTWARE\\hMailServer\\InstallLocation (32-bit view): not present; the executable's own directory is the anchor\r\n"));
#endif

      report.append(_T("[Directories]\r\n"));
      AppendPath_(report, all_present, _T("ProgramFolder"), ini->GetProgramDirectory(), true);
      AppendPath_(report, all_present, _T("DataFolder"), ini->GetDataDirectory(), true);
      AppendPath_(report, all_present, _T("LogFolder"), ini->GetLogDirectory(), true);
      AppendPath_(report, all_present, _T("TempFolder"), ini->GetTempDirectory(), true);
      AppendPath_(report, all_present, _T("EventFolder"), ini->GetEventDirectory(), true);
      AppendPath_(report, all_present, _T("DatabaseFolder"), ini->GetDatabaseDirectory(), true);

#ifndef HM_PLATFORM_POSIX
      // The failure issue #158 describes: the INI's ProgramFolder was edited and
      // the registry anchor was not, or the other way round, and the server ran
      // from one tree with the configuration of the other. Both are printed
      // above; this line says outright when they disagree.
      if (!install_location.IsEmpty())
      {
         String anchored = install_location;
         if (anchored.Right(1) != FileUtilities::PathSeparator)
            anchored += FileUtilities::PathSeparator;

         if (anchored.CompareNoCase(ini->GetProgramDirectory()) != 0)
         {
            report.append(Formatter::Format(_T("  WARNING: ProgramFolder ({0}) and the registry InstallLocation ({1}) name different directories. ")
                                            _T("The server finds its configuration through the registry value and its language files, database scripts and 7-Zip through ProgramFolder; ")
                                            _T("after a move both must be repointed (docs/RelocatingAnInstallation.md).\r\n"),
                                            ini->GetProgramDirectory(), install_location));
            all_present = false;
         }
      }
#endif

      report.append(_T("Derived from ProgramFolder\r\n"));
      AppendPath_(report, all_present, _T("Bin"), ini->GetBinDirectory(), true);
      AppendPath_(report, all_present, _T("Languages"), ini->GetLanguageDirectory(), true);
      AppendPath_(report, all_present, _T("DBScripts"), ini->GetDBScriptDirectory(), true);

      // Every [Settings] and [Database] value that names a file or a directory.
      // Each is optional, so an empty one is "not set" rather than a failure;
      // a set one that is not there is the failure this report exists to show.
      report.append(_T("[Settings]\r\n"));
      AppendPath_(report, all_present, _T("ArchiveDir"), ini->GetArchiveDir(), true);
      AppendPath_(report, all_present, _T("AcmeCertificateDirectory"), ini->GetAcmeCertificateDirectory(), true);
      AppendPath_(report, all_present, _T("OAuth2PublicKeyFile"), ini->GetOAuth2RsaPublicKeyFile(), false);
      AppendPath_(report, all_present, _T("UpdateTrustRootsFile"), ini->GetUpdateTrustRootsFile(), false);
      AppendPath_(report, all_present, _T("UpdateLogPublicKeyFile"), ini->GetUpdateLogPublicKeyFile(), false);
      AppendPath_(report, all_present, _T("RestApiCertificateFile"), ini->GetRestApiCertificateFile(), false);
      AppendPath_(report, all_present, _T("RestApiPrivateKeyFile"), ini->GetRestApiPrivateKeyFile(), false);
      AppendPath_(report, all_present, _T("MetricsServerCertificateFile"), ini->GetMetricsServerCertificateFile(), false);
      AppendPath_(report, all_present, _T("MetricsServerPrivateKeyFile"), ini->GetMetricsServerPrivateKeyFile(), false);
      AppendPath_(report, all_present, _T("WebServicesCertificateFile"), ini->GetWebServicesCertificateFile(), false);
      AppendPath_(report, all_present, _T("WebServicesPrivateKeyFile"), ini->GetWebServicesPrivateKeyFile(), false);

      report.append(_T("[Database]\r\n"));
      AppendPath_(report, all_present, _T("PostgreSQLSslRootCert"), ini->GetDatabasePostgreSQLSslRootCert(), false);

      report.append(_T("Paths held in the database (SSL certificates, DKIM keys, client-certificate CA bundles, the backup destination, ")
                    _T("virus-scanner executables, rule actions, the archive index and the quarantine) are not listed here; ")
                    _T("each is on its own page, and docs/RelocatingAnInstallation.md names every column.\r\n"));

      diagResult.SetSuccess(all_present);
      diagResult.SetDetails(report);

      return diagResult;
   }
}

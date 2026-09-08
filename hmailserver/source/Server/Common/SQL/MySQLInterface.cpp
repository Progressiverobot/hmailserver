// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "stdafx.h"

#include "MySQLInterface.h"

#ifdef HM_PLATFORM_POSIX
// The dynamic loader. It is what LoadLibrary, GetProcAddress and FreeLibrary
// are here, and it is the only Windows subsystem this file uses.
#include <dlfcn.h>
#endif

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{

   MySQLInterface::MySQLInterface() :
      library_instance_(0),
      p_mysql_real_connect(0),
      p_mysql_close(0),
      p_mysql_init(0),
      p_mysql_options(0),
      p_mysql_error(0),
      p_mysql_query(0),
      p_mysql_store_result(0),
      p_mysql_free_result(0),
      p_mysql_insert_id(0),
      p_mysql_errno(0),
      p_mysql_num_rows(0),
      p_mysql_fetch_row(0),
      p_mysql_num_fields(0),
      p_mysql_fetch_lengths(0),
      p_mysql_fetch_field_direct(0),
      p_mysql_get_server_version(0)
   {

   }

   MySQLInterface::~MySQLInterface()
   {
      try
      {
         if (library_instance_)
         {
#ifdef HM_PLATFORM_POSIX
            // dlclose is FreeLibrary: it drops this reference to the object and
            // unmaps it when the last one goes. The handle can only have come
            // from the dlopen in Load, which is the same promise the Windows arm
            // makes about LoadLibrary.
            ::dlclose(library_instance_);
#else
            FreeLibrary(library_instance_);
#endif
         }
      }
      catch (...)
      {

      }
   }

#ifdef HM_PLATFORM_POSIX
   // The client libraries this build asks the dynamic linker for, in the order it
   // asks. MariaDB Connector/C first, because that is the client this program
   // bundles on Windows and the one most distributions install; then the Oracle
   // client under its packaged soname and under the development symlink a
   // -dev/-devel package leaves behind.
   //
   // There is no "the DLL beside the executable" here. A shared object is found
   // along the linker's own search path - ld.so.conf, LD_LIBRARY_PATH, the RPATH
   // of the program - and the client is a packaged library rather than a file the
   // administrator copies into a Bin directory, so what is named below is a
   // soname and not a path.
   static const char *MYSQL_CLIENT_CANDIDATES[] =
   {
      "libmariadb.so.3",
      "libmysqlclient.so.21",
      "libmysqlclient.so",
      "libmariadb.so"
   };
#endif

   String 
   MySQLInterface::GetLibraryFileName_()
   {
#ifdef HM_PLATFORM_POSIX
      // The first candidate, which is what the error message names when none of
      // them loads. Load tries the whole list.
      return String(MYSQL_CLIENT_CANDIDATES[0]);
#else
      // Characters, not bytes: the old alloca(2048) reserved 2048 bytes while
      // GetModuleFileName was told it could write 2048 characters, which is twice
      // that in a Unicode build.
      TCHAR szPath[1024];
      const DWORD characterCount = sizeof(szPath) / sizeof(TCHAR);

      DWORD dwPathLength = GetModuleFileName(NULL, szPath, characterCount);

      if (dwPathLength >= characterCount)
         dwPathLength = characterCount - 1;

      szPath[dwPathLength] = 0;

      String sPath(szPath);

      int iLastSlash = std::max(sPath.ReverseFind(_T("\\")), sPath.ReverseFind(_T("/")));
      String sRetVal = sPath.Mid(0, iLastSlash);
      sRetVal += "\\libmysql.dll";

      return sRetVal;
#endif
   }

   String
   MySQLInterface::GetLibraryDirectory()
   {
#ifdef HM_PLATFORM_POSIX
      // The directory the client library was actually loaded from. There is no
      // module handle to ask here, but dladdr answers the same question about any
      // address inside a loaded object, and one of the entry points resolved by
      // Load is such an address.
      //
      // Before Load has run there is no library and so no directory, and the
      // answer is empty - which is what the one caller does the right thing with:
      // it stops asking the client to look for its authentication plugins
      // somewhere, and a packaged client already knows where its own plugins are.
      if (p_mysql_init == 0)
         return String();

      Dl_info info;

      if (::dladdr((void *) p_mysql_init, &info) == 0 || info.dli_fname == 0)
         return String();

      String sPath(info.dli_fname);

      const int iLastSlash = sPath.ReverseFind(_T("/"));

      if (iLastSlash <= 0)
         return String();

      return sPath.Mid(0, iLastSlash);
#else
      // Characters, not bytes: the old alloca(2048) reserved 2048 bytes while
      // GetModuleFileName was told it could write 2048 characters, which is twice
      // that in a Unicode build.
      TCHAR szPath[1024];
      const DWORD characterCount = sizeof(szPath) / sizeof(TCHAR);

      DWORD dwPathLength = GetModuleFileName(NULL, szPath, characterCount);

      if (dwPathLength >= characterCount)
         dwPathLength = characterCount - 1;

      szPath[dwPathLength] = 0;

      String sPath(szPath);

      int iLastSlash = std::max(sPath.ReverseFind(_T("\\")), sPath.ReverseFind(_T("/")));
      return sPath.Mid(0, iLastSlash);
#endif
   }

   bool
   MySQLInterface::Load(String &sErrorMessage)
   {
#ifdef HM_PLATFORM_POSIX
      // RTLD_NOW resolves every symbol at load time, so a client built against a
      // different libc is an error here rather than a crash at the first query.
      // RTLD_LOCAL keeps its symbols out of the global namespace, so nothing else
      // in the process binds to them by accident - the same isolation LoadLibrary
      // gives on Windows.
      String sLibrary;

      for (const char *candidate : MYSQL_CLIENT_CANDIDATES)
      {
         library_instance_ = ::dlopen(candidate, RTLD_NOW | RTLD_LOCAL);

         if (library_instance_ != 0)
            break;

         if (!sLibrary.IsEmpty())
            sLibrary += _T(", ");

         sLibrary += String(candidate);
      }

      if (!library_instance_)
      {
         // dlerror is a one-shot: the second read of it is empty, so the reason
         // the last candidate failed is taken once and kept.
         const char *lastError = ::dlerror();

         sErrorMessage = Formatter::Format("Error:\r\n"
               "The MySQL client library could not be loaded.\r\n"
               "hMailServer needs it to be able to connect to MySQL.\r\n"
               "Install the MariaDB Connector/C or MySQL client package and make sure the dynamic linker can find it.\r\n"
               "Tried: {0}\r\n"
               "Last error: {1}", sLibrary, String(lastError == 0 ? "none reported" : lastError));

         ErrorManager::Instance()->ReportError(ErrorManager::Critical, 5094, "MySQLInterface::Load", sErrorMessage);

         return false;
      }

      // dlsym is GetProcAddress. A name that is not in the object answers null,
      // which is exactly what the Windows arm gets and what every caller of these
      // pointers already tests for.
      p_mysql_real_connect = (hm_mysql_real_connect*) ::dlsym(library_instance_, "mysql_real_connect");
      p_mysql_close = (hm_mysql_close*) ::dlsym(library_instance_, "mysql_close");
      p_mysql_init = (hm_mysql_init*) ::dlsym(library_instance_, "mysql_init");
      p_mysql_options = (hm_mysql_options*) ::dlsym(library_instance_, "mysql_options");
      p_mysql_error = (hm_mysql_error*) ::dlsym(library_instance_, "mysql_error");
      p_mysql_query = (hm_mysql_query*) ::dlsym(library_instance_, "mysql_query");
      p_mysql_store_result = (hm_mysql_store_result*) ::dlsym(library_instance_, "mysql_store_result");
      p_mysql_free_result = (hm_mysql_free_result*) ::dlsym(library_instance_, "mysql_free_result");
      p_mysql_insert_id = (hm_mysql_insert_id*) ::dlsym(library_instance_, "mysql_insert_id");
      p_mysql_errno = (hm_mysql_errno*) ::dlsym(library_instance_, "mysql_errno");
      p_mysql_num_rows = (hm_mysql_num_rows*) ::dlsym(library_instance_, "mysql_num_rows");
      p_mysql_fetch_row = (hm_mysql_fetch_row*) ::dlsym(library_instance_, "mysql_fetch_row");
      p_mysql_num_fields = (hm_mysql_num_fields*) ::dlsym(library_instance_, "mysql_num_fields");
      p_mysql_fetch_lengths = (hm_mysql_fetch_lengths*) ::dlsym(library_instance_, "mysql_fetch_lengths");
      p_mysql_fetch_field_direct = (hm_mysql_fetch_field_direct*) ::dlsym(library_instance_, "mysql_fetch_field_direct");
      p_mysql_get_server_version = (hm_mysql_get_server_version*) ::dlsym(library_instance_, "mysql_get_server_version");

      return true;
#else
      String sLibrary = GetLibraryFileName_();
      library_instance_ = LoadLibrary(sLibrary);

      if (!library_instance_)
      {
         String versionArchitecture = Application::Instance()->GetVersionArchitecture();

         sErrorMessage = Formatter::Format("Error:\r\n"
               "The MySQL client (libmysql.dll, {0}) could not be loaded.\r\n"
               "hMailServer needs this file to be able to connect to MySQL.\r\n"
               "The MySQL client needs to be manually copied to the hMailServer Bin directory. The file is not included in the hMailServer installation.\r\n"
               "It can be obtained from https://dev.mysql.com/downloads/connector/c/.\r\n"
               "Path: {1}", versionArchitecture, sLibrary);

         ErrorManager::Instance()->ReportError(ErrorManager::Critical, 5094, "MySQLInterface::Load", sErrorMessage);

         return false;
      }

      p_mysql_real_connect = (hm_mysql_real_connect*)GetProcAddress( (HMODULE)library_instance_, "mysql_real_connect" );
      p_mysql_close = (hm_mysql_close*) GetProcAddress( (HMODULE)library_instance_, "mysql_close" );
      p_mysql_init = (hm_mysql_init*) GetProcAddress( (HMODULE)library_instance_, "mysql_init" );
      p_mysql_options = (hm_mysql_options*) GetProcAddress( (HMODULE)library_instance_, "mysql_options" );
      p_mysql_error = (hm_mysql_error*) GetProcAddress( (HMODULE)library_instance_, "mysql_error" );
      p_mysql_query = (hm_mysql_query*) GetProcAddress( (HMODULE)library_instance_, "mysql_query" );
      p_mysql_store_result = (hm_mysql_store_result*) GetProcAddress( (HMODULE)library_instance_, "mysql_store_result" );
      p_mysql_free_result = (hm_mysql_free_result*) GetProcAddress( (HMODULE)library_instance_, "mysql_free_result" );
      p_mysql_insert_id = (hm_mysql_insert_id*) GetProcAddress( (HMODULE)library_instance_, "mysql_insert_id" );
      p_mysql_errno = (hm_mysql_errno*) GetProcAddress( (HMODULE)library_instance_, "mysql_errno" );
      p_mysql_num_rows = (hm_mysql_num_rows*) GetProcAddress( (HMODULE)library_instance_, "mysql_num_rows" );
      p_mysql_fetch_row = (hm_mysql_fetch_row*) GetProcAddress( (HMODULE)library_instance_, "mysql_fetch_row" );
      p_mysql_num_fields = (hm_mysql_num_fields*) GetProcAddress( (HMODULE)library_instance_, "mysql_num_fields" );
      p_mysql_fetch_lengths = (hm_mysql_fetch_lengths*) GetProcAddress( (HMODULE)library_instance_, "mysql_fetch_lengths" );
      p_mysql_fetch_field_direct = (hm_mysql_fetch_field_direct*) GetProcAddress( (HMODULE)library_instance_, "mysql_fetch_field_direct" );
      p_mysql_get_server_version = (hm_mysql_get_server_version*) GetProcAddress( (HMODULE)library_instance_, "mysql_get_server_version" );

      return true;
#endif
   }

   bool
   MySQLInterface::IsLoaded()
   {
      // library_instance_ is a handle, which is a pointer. "> 0" was an ORDERED
      // comparison of one against a null pointer constant: MSVC accepts it, a
      // conforming compiler rejects it, and "!= 0" is the same test - a loaded
      // library has a handle, an unloaded one has none - spelt in a way both
      // accept and both answer identically for every value this can hold.
      if (library_instance_ != 0)
         return true;
      else
         return false;

   }
}

// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// The precompiled-header substitute for the POSIX build of the server core.
//
// Every core translation unit opens with #include "StdAfx.h" (or "stdafx.h" -
// both spellings are in the tree). The CMake build puts platform/pch first on
// the include path, so on Linux that include resolves to a two-line file which
// includes this one, and the real Server/hMailServer/stdafx.h is never read.
//
// It cannot be: that header #imports the ADO type library, which is an MSVC
// feature with no clang equivalent, and pulls in ATL. Those two facts are why
// there has never been a portable build. What the core actually needs from a
// precompiled header is the C++ standard library, Boost, the string class and
// the assertion macro - all of which are portable, and all of which are here.
//
// The preprocessor state below is not cosmetic. The Windows build compiles with
// UNICODE and _UNICODE, so _T("x") is a wide literal and HM::String is
// CStdStr<wchar_t>. Building without them would still compile and would be a
// DIFFERENT PROGRAM, because half the string handling would change width.
#pragma once

#include "../platform/hm_platform.h"

// SS_ANSI makes CStdString use the C++ locale facets instead of
// MultiByteToWideChar/WideCharToMultiByte. It is the switch its author put there
// for exactly this, and it is what makes the string class - which is in the
// signature of nearly every function in the server - compile here at all.
#ifndef SS_ANSI
#define SS_ANSI
#endif

#ifndef UNICODE
#define UNICODE
#endif
#ifndef _UNICODE
#define _UNICODE
#endif

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <functional>
#include <list>
#include <map>
#include <memory>
#include <mutex>
#include <queue>
#include <set>
#include <sstream>
#include <string>
#include <thread>
#include <vector>

#include <boost/bind/bind.hpp>
#include <boost/thread.hpp>
#include <boost/chrono.hpp>
#include <boost/asio.hpp>
#include <boost/asio/ssl.hpp>
#include <boost/signals2/signal.hpp>

namespace HM
{
   void AssertionFailed(const char *expression, const char *file, int line);
}

// The same three-way definition the Windows precompiled header gives: kept in an
// assertion build, an assert() in a debug build, gone in a release build.
#if defined(HM_KEEP_ASSERTIONS)
   #define HM_ASSERT(expression) ((expression) ? (void) 0 : HM::AssertionFailed(#expression, __FILE__, __LINE__))
#elif defined(_DEBUG)
   #include <cassert>
   #define HM_ASSERT(expression) assert(expression)
#else
   #define HM_ASSERT(expression) ((void) 0)
#endif

#ifndef ASSERT
   #define ASSERT(expression) HM_ASSERT(expression)
#endif

// The same common headers the Windows precompiled header ends with, in the same
// order, because every translation unit is written expecting them: String and
// AnsiString above all, then the singletons, the configuration, the SQL layer
// and the formatter. Two of that list are NOT here - COM/COMAuthentication.h and
// COM/COMAuthenticator.h - because Server/COM is the ATL administration API and
// is not part of this build; a source that needs them is a source this build
// does not compile, and the census says which.
//
// The Windows header writes these with backslashes, which a POSIX compiler reads
// as part of the file name rather than as separators.
#include "../Common/Util/StdString.h"

#include "../Common/Util/XMLite.h"
#include "../Common/Util/Singleton.h"
#include "../Common/Application/Constants.h"
#include "../Common/Application/PropertySet.h"
#include "../Common/Application/Configuration.h"
#include "../Common/Application/IniFileSettings.h"
#include "../Common/Application/Application.h"
#include "../Common/Application/Logger.h"
#include "../Common/Application/ErrorManager.h"
#include "../Common/SQL/SQLParameter.h"
#include "../Common/SQL/SQLStatement.h"
#include "../Common/SQL/DatabaseConnectionManager.h"
#include "../Common/SQL/DALRecordset.h"
#include "../Common/SQL/DALRecordsetFactory.h"
#include "../Common/SQL/SQLCommand.h"
#include "../Common/Util/Parsing/StringParser.h"
#include "../Common/Util/FileUtilities.h"
#include "../Common/Util/HeapChecker.h"

#include "../Common/BO/BusinessObject.h"
#include "../IMAP/IMAPResult.h"
#include "../Common/TCPIP/IPAddress.h"
#include "../Common/Util/Strings/FormatArgument.h"
#include "../Common/Util/Strings/Formatter.h"

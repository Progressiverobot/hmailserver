// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
//
// Precompiled-header substitute for the clang-cl / libFuzzer build of the MIME
// parser. Every translation unit the fuzz build compiles out of Server\Common
// opens with #include "stdafx.h", and build-fuzz.ps1 puts THIS directory first
// on the include path so that include resolves here instead of to the server's
// own Server\hMailServer\stdafx.h.
//
// WHY THE REAL stdafx.h CANNOT BE USED
// -----------------------------------
// This is the whole reason fuzzing the parsers was never started. The server's
// stdafx.h is not merely heavy, it is unusable under clang:
//
//   1. It contains "#import "..\..\..\..\libraries\msado28\msado28-x64.tlb"".
//      #import of a type library is an MSVC-only feature that runs the MIDL
//      importer and synthesises a .tlh header. clang-cl does not implement it
//      (it is not a preprocessor feature; it needs the COM type-library reader),
//      so the first translation unit fails before it reaches any of our code.
//      There is no flag that turns this off and no way to stub it from outside
//      the file.
//   2. It pulls ATL (atlbase.h/atlcom.h), boost::thread, boost::asio and
//      boost::asio::ssl. Those bring in link dependencies (boost_thread,
//      boost_filesystem, libssl/libcrypto) that a fuzz target has no business
//      carrying, and each one is a chance for the sanitizer runtime to
//      interpose on something unrelated to MIME.
//   3. It includes resource.h from the server project, which is generated
//      alongside the .rc file.
//
// So the fuzz build gets its own minimal world: the real StdString.h (String
// and AnsiString are woven through every signature in the parser and cannot be
// faked), the real Formatter/FormatArgument declarations, and small
// stand-ins for the four environment singletons the parser touches on its
// error paths. Nothing here fakes parser logic - every byte of MIME handling in
// the fuzz binary is the same source the server ships.
//
// A NOTE ON WHAT MUST STAY IN STEP WITH THE REAL BUILD
// ---------------------------------------------------
// The preprocessor state below is not cosmetic. hMailServer.vcxproj compiles
// with UNICODE;_UNICODE (plus _MBCS from CharacterSet=MultiByte), so _T("x")
// is a wide literal and HM::String is CStdStr<wchar_t>. Building the fuzzer
// without those defines would still compile - and would fuzz a *different
// program* than the one that ships, because half the string handling would
// change width. build-fuzz.ps1 passes them on the command line; they are
// asserted here so a hand-run clang-cl cannot silently get it wrong.

#pragma once

#if !defined(UNICODE) || !defined(_UNICODE)
   #error "The fuzz build must define UNICODE and _UNICODE, matching hMailServer.vcxproj. Without them HM::String changes width and you are not fuzzing the shipped parser. Use build-fuzz.ps1."
#endif

// Keep the Windows API surface identical to the server's (Windows 10 1607).
#define WINVER 0x0A00
#define _WIN32_WINNT 0x0A00
#define NTDDI_VERSION 0x0A000002 // NTDDI_WIN10_RS1 (1607 / 14393)

#define STRICT
#define NOMINMAX
#define _WINSOCK_DEPRECATED_NO_WARNINGS

// Deliberately NOT defined here, although the server's stdafx.h defines them:
// VC_EXTRALEAN and WIN32_LEAN_AND_MEAN. StdString.h includes <comdef.h>
// unconditionally on MSVC, and comdef.h needs the OLE declarations that those
// two macros strip out of Windows.h. The macros only affect which Windows
// headers are visible, not the behaviour of any parser code, so widening the
// header set here does not change what is being fuzzed.
#include <WinSock2.h>
#include <Windows.h>

// <algorithm> is load-bearing, not tidiness: NOMINMAX above removes the Windows
// min/max macros, and MimeCode.cpp calls bare min() relying on the
// "using namespace std" that MimeCode.h leaves at global scope. The real
// stdafx.h also defines NOMINMAX, so this is the same resolution the shipped
// build gets - just with the include made explicit instead of arriving through
// Boost.
#include <algorithm>
#include <functional>
#include <list>
#include <map>
#include <memory>
#include <mutex>
#include <queue>
#include <set>
#include <stdexcept>
#include <string>
#include <vector>

// <assert.h> for the lowercase assert() in MimeEncodedWord::BEncode. NDEBUG is
// defined by build-fuzz.ps1 (matching Release), so it expands to nothing - which
// is what we want: that particular assertion is comparing two values that are
// equal by construction, and in Release it does not exist. In the real build
// this header arrives through StdString.h's ASSERT fallback; here that fallback
// never runs, because ASSERT is defined a few lines further down - before
// StdString.h is included.
#include <assert.h>
#include <string.h>

// ASSERT: a no-op, exactly as in the shipped Release build.
//
// This is the single most important line in the shim. Mime.cpp and MimeCode.cpp
// are littered with ASSERT() on conditions that untrusted input controls -
// MimeBody::Load does ASSERT(strBoundary.size() > 0) on a multipart part whose
// boundary parameter is missing, which is a one-line message away. In the
// shipped Release build ASSERT expands to ((void)0) and execution continues; in
// a debug build it aborts.
//
// If the fuzz build let ASSERT abort, libFuzzer would report a "crash" on the
// very first malformed input, that crash would be a faithful report of a
// condition production ignores, and the genuinely interesting findings
// (out-of-bounds reads, use-after-free, stack exhaustion) would be buried under
// it forever. We want the Release semantics because the Release binary is what
// takes mail from the internet.
//
// The rejected alternative was to leave asserts live and triage them: that puts
// a human in the loop on every one of thousands of executions per second, which
// is precisely the thing a fuzzer is supposed to remove. build-fuzz.ps1
// -Asserts flips this for a deliberate, separate hunt for logic-invariant
// violations; that variant is not the one to leave running.
#if defined(ASSERT)
   #undef ASSERT
#endif
#if defined(HM_FUZZ_ASSERTS)
   #include <assert.h>
   #define ASSERT(exp) assert(exp)
#else
   #define ASSERT(exp) ((void)0)
#endif

// HM::String / HM::AnsiString and the TCHAR plumbing. Real header, unmodified.
#include "Util/StdString.h"

// Real header, and it has to be here rather than in the translation units that
// use it: Mime.cpp, MimeCode.cpp and Charset.cpp all call StringParser
// (::Search for the "\r\n\r\n" and "?=" scans, ::Base64Encode for RFC 2047
// encoding) without including it, because the server's stdafx.h includes it for
// them. StringParser.cpp itself is compiled into the fuzz targets, so these are
// the real implementations, not stubs.
#include "Util/Parsing/StringParser.h"

// Boost-free replacement for Common\Util\Singleton.h.
//
// CodePages.h derives from Singleton<CodePages> and Charset::ToWideChar calls
// CodePages::Instance(), so the fuzz build needs the template. The real one
// guards creation with boost::recursive_mutex, which would drag boost_thread
// into the link for no benefit: a libFuzzer target is single-threaded by
// construction (libFuzzer's -workers forks processes, it does not thread inside
// one). std::call_once gives the same guarantee with no library dependency.
//
// This intentionally does NOT include the real Singleton.h. If a future
// translation unit added to the fuzz build includes it directly, the two
// definitions of ::Singleton<T> will collide and the compiler will say so
// loudly - which is the outcome we want, rather than two subtly different
// singletons in one binary.
template <class T>
class Singleton
{
public:
   virtual ~Singleton() {}

   // Heap-allocated and never freed, matching the real Singleton. A
   // function-local static object would be destroyed during exit processing,
   // and the point of the real design is that these outlive everything that
   // might still call them on the way down.
   static T *Instance()
   {
      static T *instance = nullptr;
      static std::once_flag once;
      std::call_once(once, []() { instance = new T(); });
      return instance;
   }
};

namespace HM
{
   // Stand-in for Common\Application\ErrorManager.
   //
   // The real ErrorManager writes an entry to the ERROR log, which needs
   // Logger, Configuration, the INI settings and a writable log directory - an
   // entire server. The MIME code reaches it from exactly two places, both on
   // malformed-input paths: MimeBody::LoadFromFile's catch(...) and
   // MimeParameterRFC2184Decoder::Decode.
   //
   // Reports are counted rather than ignored so a harness can assert on them if
   // it ever wants to; they are deliberately NOT treated as findings. An
   // ErrorManager report on a deliberately corrupted message is the parser
   // doing its job, and turning it into a libFuzzer crash would make every
   // malformed RFC 2231 parameter a "bug" and stop the run. The real reason
   // that distinction matters is spelled out in docs/Fuzzing.md.
   class ErrorManager : public Singleton<ErrorManager>
   {
   public:
      enum eSeverity
      {
         Critical = 1,
         High = 2,
         Medium = 3,
         Low = 4
      };

      void ReportError(eSeverity, int, const String &, const String &)
      {
         ++report_count_;
      }

      size_t GetReportCount() const { return report_count_; }
      void ResetReportCount() { report_count_ = 0; }

   private:
      size_t report_count_ = 0;
   };

   // Stand-in for Common\Application\IniFileSettings. MimeBody::LoadFromFile
   // asks for the log directory so it can copy a message it failed to parse
   // into "Problematic messages". The fuzz build never opens that path (File
   // below refuses every Open), but the call has to compile and link.
   class IniFileSettings : public Singleton<IniFileSettings>
   {
   public:
      String GetLogDirectory() const { return _T("."); }
   };

   // Stand-in for Common\Util\FileUtilities.
   //
   // The real one is 600 lines over boost::filesystem, GUIDCreator, Dictionary
   // and Languages. The parser only uses three of its functions and only on
   // paths that write files, which a fuzz target must never do: a target that
   // touches the disk is neither deterministic nor safe to run 100k times a
   // second, and a corpus entry that happens to write a file would make the
   // next execution depend on the last.
   //
   // Note that this is a *declaration* the fuzz build owns, not the real header
   // being included - so it does not have to track changes to
   // FileUtilities.h. Only the three signatures the MIME code calls exist here;
   // adding a fourth call site in Common\Mime will fail to compile until it is
   // added, which is the correct amount of friction.
   class FileUtilities
   {
   public:
      // Behaviourally identical to the real one - last backslash, whole string if
      // there is none - but it deliberately does NOT call String::ReverseFind.
      //
      // CStdStr::ReverseFind contains
      //
      //    this->rfind(0 == szFind ? MYTYPE() : szFind, pos)
      //
      // and clang rejects that ternary outright: "conditional expression is
      // ambiguous; 'CStdStr<wchar_t>' can be converted to 'const wchar_t *' and
      // vice versa". MSVC accepts it, so the server builds, and the template is
      // only instantiated for a given method if something calls it - which meant
      // this one line in the harness was enough to fail the whole fuzz build on
      // StdString.h line 3434.
      //
      // Fixing StdString.h itself would be the tidier-looking change and is the
      // wrong one to make here: it is shared production code on the hot path of
      // every protocol, the fuzz build is not a reason to touch it, and the
      // ambiguity is latent rather than a live defect under MSVC. So the harness
      // avoids the method instead. std::wstring::rfind has no such problem.
      static String GetFileNameFromFullPath(const String &full_path)
      {
         const std::wstring::size_type last_separator = full_path.rfind(L'\\');

         if (last_separator == std::wstring::npos)
            return full_path;

         return full_path.Mid(static_cast<int>(last_separator) + 1);
      }

      static bool Copy(const String &, const String &, bool = false) { return false; }
      static bool WriteToFile(const String &, const String &, bool) { return false; }
      static bool WriteToFile(const String &, const AnsiString &) { return false; }
   };

   // Only StringParser::IsValidIPAddress needs this, and nothing the MIME parser
   // does reaches that function - but StringParser.cpp is compiled whole, so the
   // type has to exist for the file to compile.
   //
   // Declared rather than included: the real IPAddress pulls in the Boost.Asio
   // networking headers and Winsock, which is a large amount of machinery for a
   // harness with no sockets, and every byte of it would be one more thing whose
   // behaviour differs between the fuzz build and the server. Refusing every
   // address is safe here precisely because the answer is never consulted; if a
   // future MIME change ever does call IsValidIPAddress, this fake stops being
   // adequate and the harness will be lying rather than failing - which is why
   // this comment says so instead of leaving it to be discovered.
   class IPAddress
   {
   public:
      bool TryParse(const AnsiString &) { return false; }
      bool TryParse(const AnsiString &, bool) { return false; }
   };
}

// Real declarations for the formatter. Definitions live in
// fuzz_environment.cpp; using the real headers means the fuzz build cannot
// drift out of signature agreement with the server the way a hand-written fake
// would.
#include "Util/Strings/FormatArgument.h"
#include "Util/Strings/Formatter.h"

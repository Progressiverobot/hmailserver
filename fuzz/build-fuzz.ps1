# Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
# SPDX-License-Identifier: AGPL-3.0-or-later

# Builds the libFuzzer targets for the MIME parser with clang-cl.
#
# WHY THIS IS A SEPARATE BUILD AND NOT A PROJECT IN hMailServer.sln
# ----------------------------------------------------------------
# Three reasons, in order of how much trouble each one causes:
#
#   1. It cannot be MSVC. libFuzzer and AddressSanitizer's fuzzer integration
#      (-fsanitize=fuzzer) exist only in clang. MSVC has /fsanitize=address but
#      no coverage-guided fuzzing driver, so the whole item requires a second
#      toolchain alongside v145. That second toolchain is the reason this was
#      the last of the structural prerequisites to be started.
#   2. It cannot use the server's stdafx.h. That header #imports the ADO type
#      library, which clang-cl does not implement, so the fuzz build gets a
#      shim precompiled header (harness\shim\stdafx.h - read the comment at the
#      top of it before changing anything here).
#   3. It must never disturb the regression suite. The suite runs against a live
#      single-instance Windows service, and anything that rebuilds
#      hMailServer.vcxproj runs its pre/post-build events and stops that
#      service. This script writes only into fuzz\build and fuzz\bin, compiles
#      the Server\Common sources into its own object files, and touches neither
#      the solution nor the service. It is safe to run while the suite is
#      running (it will just compete for CPU).
#
# WHAT IS COMPILED
# ----------------
# The real parser, and nothing but stubs around it: Mime.cpp, MimeCode.cpp,
# MimeChar.cpp, MimeType.cpp, CodePages.cpp and the four utility translation
# units they genuinely need (Charset, ByteBuffer, Unicode, StringParser +
# RegularExpression, which StringParser needs). Everything else the parser
# touches - File, FileUtilities, ErrorManager, IniFileSettings, Formatter - is
# environment, and is stubbed in harness\fuzz_environment.cpp and the shim. If
# the parser's own logic were stubbed, the findings would be worthless.
#
# The compiler flags mirror hMailServer.vcxproj's Release configuration
# (/MD, NDEBUG, UNICODE/_UNICODE/_MBCS) on purpose. A fuzz build with different
# preprocessor state is a fuzz build of a different program: _UNICODE alone
# decides whether HM::String is narrow or wide.
#
# Usage:
#   .\fuzz\build-fuzz.ps1                    # build all targets + seed corpus
#   .\fuzz\build-fuzz.ps1 -Clean             # discard objects first
#   .\fuzz\build-fuzz.ps1 -Target mime_header_fuzzer
#   .\fuzz\build-fuzz.ps1 -Asserts           # ASSERT() live (see below)
#
# -Asserts builds a variant where ASSERT() aborts instead of expanding to
# ((void)0). Do not use it for a normal run: the shipped Release build ignores
# those assertions, several of them are one malformed message away, and libFuzzer
# would report the first one as a crash and stop exploring. It is only useful as
# a deliberate, separate hunt for violated internal invariants.

Param(
    [switch]$Clean,
    [switch]$Asserts,
    [switch]$SkipCorpus,
    [string]$Target = '',
    [string]$BoostInclude = '',

    # CRT linkage. MT is the default because MD does not link, measured on
    # LLVM 22.1.8:
    #
    #   lld-link: error: /failifmismatch: mismatch detected for 'RuntimeLibrary'
    #
    # The prebuilt clang_rt.fuzzer-x86_64.lib in an LLVM release is built against
    # the STATIC CRT, and the STL stamps a /failifmismatch directive recording
    # which CRT every object chose, so /MD objects cannot be linked with it. The
    # alternatives were to rebuild compiler-rt from source to match, or to accept
    # static linkage - and the divergence from the shipped build is small and does
    # not touch anything the MIME parser does. Note this DOES mean the allocator
    # under test is the static CRT's rather than the DLL's.
    #
    # -RuntimeLibrary MD is kept because a future LLVM may ship a /MD runtime, and
    # because it is worth being able to try it in one command rather than editing
    # this file.
    [ValidateSet('MD', 'MT')]
    [string]$RuntimeLibrary = 'MT'
)

# Deliberately NOT $ErrorActionPreference = 'Stop': the failure paths below use
# Write-Error followed by a specific exit code, and under 'Stop' the Write-Error
# terminates the script first and every failure would exit 1. The exit codes are
# how a wrapper (or a future CI job) tells "toolchain missing" from "compile
# failed", so they are worth keeping distinct.

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Definition
$repoRoot = (Get-Item $scriptRoot).Parent.FullName

$objDir = Join-Path $scriptRoot 'build\obj'
$binDir = Join-Path $scriptRoot 'bin'
$harnessDir = Join-Path $scriptRoot 'harness'
$shimDir = Join-Path $harnessDir 'shim'
$commonDir = Join-Path $repoRoot 'hmailserver\source\Server\Common'

# ---------------------------------------------------------------------------
# Sources
# ---------------------------------------------------------------------------
# Repo-relative on purpose: these paths are also read by the regression test
# RegressionTests\MIME\FuzzHarness.cs, which fails if any of them stops
# existing. Moving Mime.cpp without updating this list would otherwise leave a
# fuzz build that silently stops covering the parser, and the failure would only
# show up as "no new coverage" months later.
$serverSources = @(
    'hmailserver\source\Server\Common\Mime\Mime.cpp',
    'hmailserver\source\Server\Common\Mime\MimeChar.cpp',
    'hmailserver\source\Server\Common\Mime\MimeCode.cpp',
    'hmailserver\source\Server\Common\Mime\MimeType.cpp',
    'hmailserver\source\Server\Common\Mime\CodePages.cpp',
    'hmailserver\source\Server\Common\Util\Charset.cpp',
    'hmailserver\source\Server\Common\Util\ByteBuffer.cpp',
    'hmailserver\source\Server\Common\Util\Unicode.cpp',
    'hmailserver\source\Server\Common\Util\RegularExpression.cpp',
    'hmailserver\source\Server\Common\Util\Parsing\StringParser.cpp'
)

$harnessSources = @(
    'fuzz\harness\fuzz_environment.cpp',
    'fuzz\harness\fuzz_mime_common.cpp'
)

# One executable per entry. Each defines LLVMFuzzerTestOneInput; libFuzzer
# supplies main().
$targetSources = @(
    'fuzz\harness\mime_message_fuzzer.cpp',
    'fuzz\harness\mime_header_fuzzer.cpp',
    'fuzz\harness\mime_decode_fuzzer.cpp'
)

# ---------------------------------------------------------------------------
# Toolchain
# ---------------------------------------------------------------------------
. (Join-Path $scriptRoot 'Find-ClangCl.ps1')

if (-not (Import-VsDevEnvironment)) {
    Write-Error 'Could not establish the MSVC build environment. clang-cl links with link.exe and against the MSVC CRT; install the "Desktop development with C++" workload.'
    exit 2
}

$clang = Find-ClangCl
if (-not $clang) {
    Write-Error @'
clang-cl not found.

Install it through the Visual Studio Installer: Individual components ->
"C++ Clang tools for Windows" (component id Microsoft.VisualStudio.Component.VC.Llvm.Clang).
That component also provides the AddressSanitizer and libFuzzer runtime
libraries, so a standalone llvm.org build is a fallback, not the preferred
route.
'@
    exit 2
}

Write-Host "clang-cl: $clang"
& $clang --version | Select-Object -First 1 | ForEach-Object { Write-Host "  $_" }

# A clang-cl on PATH is not necessarily a clang-cl with the sanitizer runtimes.
# On a machine with a Swift toolchain, an Android NDK or a Chocolatey LLVM, the
# fallback lookup finds one of those, everything compiles, and the link fails
# with LNK1181 on clang_rt.fuzzer-x86_64.lib twenty minutes later. Check up
# front and say what is actually wrong.
if (-not (Test-ClangSanitizerRuntime -ClangClPath $clang)) {
    Write-Error @"
The clang-cl found at
    $clang
does not ship the x64 AddressSanitizer and libFuzzer runtime libraries
(clang_rt.asan-x86_64.lib / clang_rt.fuzzer-x86_64.lib under
lib\clang\<version>\lib\windows). It is almost certainly a clang that came with
something else - a Swift toolchain, an NDK, a bare LLVM install.

Install Visual Studio's own Clang, which does ship them:

  Visual Studio Installer -> Modify -> Individual components ->
      "C++ Clang tools for Windows"
  (component id Microsoft.VisualStudio.Component.VC.Llvm.Clang)

Or from the command line, against the Build Tools installation:

  "C:\Program Files (x86)\Microsoft Visual Studio\Installer\setup.exe" modify ``
      --installPath "<your VS or BuildTools install path>" ``
      --add Microsoft.VisualStudio.Component.VC.Llvm.Clang --passive

Then run this script again; it prefers the Visual Studio toolchain over PATH.
"@
    exit 2
}

# Boost is needed for two translation units only: RegularExpression.cpp
# (boost::regex, header-only since Boost 1.75) and StringParser.cpp
# (boost::tokenizer, boost::lexical_cast - both header-only). No Boost .lib is
# linked, and BOOST_ALL_NO_LIB below makes sure the MSVC auto-link pragmas do
# not try.
if (-not $BoostInclude) {
    if ($env:hMailServerLibs) { $BoostInclude = Join-Path $env:hMailServerLibs 'boost_1_91_0' }
    elseif ($env:BOOST_INCLUDE_PATH) { $BoostInclude = $env:BOOST_INCLUDE_PATH }
}

if (-not $BoostInclude -or -not (Test-Path (Join-Path $BoostInclude 'boost\regex.hpp'))) {
    Write-Error "Boost headers not found. Pass -BoostInclude <dir> (the directory containing boost\), or set hMailServerLibs as the main build does. Tried: '$BoostInclude'"
    exit 2
}

Write-Host "Boost includes: $BoostInclude"

# ---------------------------------------------------------------------------
# Verify every source exists before doing any work
# ---------------------------------------------------------------------------
$allSources = $serverSources + $harnessSources + $targetSources
$missing = @()
foreach ($relative in $allSources) {
    if (-not (Test-Path (Join-Path $repoRoot $relative))) { $missing += $relative }
}

if ($missing.Count -gt 0) {
    Write-Error ("Source files listed in this script do not exist:`n  {0}`nIf a file moved, fix the list at the top of build-fuzz.ps1 (and RegressionTests\MIME\FuzzHarness.cs will start passing again)." -f ($missing -join "`n  "))
    exit 3
}

if ($Clean) {
    Write-Host 'Cleaning...'
    foreach ($directory in @($objDir, $binDir)) {
        if (Test-Path $directory) { Remove-Item -Recurse -Force $directory }
    }
}

foreach ($directory in @($objDir, $binDir)) {
    if (-not (Test-Path $directory)) { New-Item -ItemType Directory -Force -Path $directory | Out-Null }
}

# ---------------------------------------------------------------------------
# Flags
# ---------------------------------------------------------------------------
# /Z7 rather than /Zi: debug information goes into the object files, so several
# sources can be compiled in one invocation without contending for a PDB, and
# the linker still produces a PDB for the executable. ASan's stack traces are
# useless without it - a crash artifact you cannot read is a crash you cannot
# fix.
#
# /O1 rather than /O2 or /Od: optimised, because the shipped build is optimised
# and optimisation changes stack layout and inlining (which is exactly what a
# stack-overflow or use-after-free finding depends on), but not so aggressively
# that traces become unreadable.
#
# /W0: warnings off. The MSVC /W3 /WX gate on hMailServer.vcxproj is the place
# to argue about warnings; clang has a different warning set, this build
# compiles 15-year-old third-party MIME code with it, and a wall of noise here
# would only train people to ignore the output that matters.
#
# /std:c++14 is REQUIRED, not conservatism. StdString.h - which defines
# HM::String and HM::AnsiString and is therefore not optional - derives from
# std::binary_function and std::unary_function. Both were removed from the
# standard in C++17, and the MSVC STL stops declaring them in C++17 mode
# (_HAS_AUTO_PTR_ETC goes to 0). Raising this flag to /std:c++17 produces a wall
# of errors in a header nobody wants to modernise. The server itself builds at
# the v145 default, which is also C++14.
#
# /EHsc rather than the server's /EHa: clang-cl does not implement asynchronous
# exception handling. The practical difference is that catch (...) here does NOT
# swallow access violations the way it does in the shipped build - which is
# better for a bug hunt, and is explained at length in hmailserver\docs\Fuzzing.md
# because it is also the reason the crash oracle had to exist first.
$defines = @(
    '/DWIN32', '/D_WINDOWS', '/DNDEBUG',
    '/DUNICODE', '/D_UNICODE', '/D_MBCS',
    '/D_SCL_SECURE_NO_WARNINGS', '/D_CRT_SECURE_NO_WARNINGS',
    '/DBOOST_ALL_NO_LIB', '/DBOOST_DATE_TIME_NO_LIB',
    '/DBOOST_USE_WINAPI_VERSION=0x0A00',

    # These two are mandatory, and their absence is a link error rather than a
    # compile error, which is why they are easy to leave out. Measured on
    # LLVM 22.1.8 with a one-line fuzz target:
    #
    #   lld-link: error: /failifmismatch: mismatch detected for 'annotate_string':
    #   >>> clang_rt.fuzzer-x86_64.lib(FuzzerCrossOver.cpp.obj) has value 0
    #   >>> <our>.obj has value 1
    #
    # The prebuilt compiler-rt libraries in an LLVM release are built with MSVC
    # STL's ASan container annotations turned OFF, and the STL stamps a
    # /failifmismatch directive into every object recording which way it was
    # compiled. Any translation unit that enables them therefore refuses to link
    # against libFuzzer. Turning them off costs the container-overflow checks on
    # std::string and std::vector - a real loss, but the alternative is no
    # libFuzzer at all unless compiler-rt is rebuilt from source to match.
    '/D_DISABLE_STRING_ANNOTATION=1', '/D_DISABLE_VECTOR_ANNOTATION=1'
)

if ($Asserts) {
    $defines += '/DHM_FUZZ_ASSERTS'
    Write-Host 'ASSERT() is LIVE in this build (-Asserts). Expect assertion aborts on malformed input; do not use this variant for a normal run.' -ForegroundColor Yellow
}

$includes = @(
    "/I$shimDir",        # must be first: this is where "stdafx.h" resolves
    "/I$harnessDir",
    "/I$commonDir",
    "/I$BoostInclude"
)

$commonFlags = @('/nologo', '/EHsc', "/$RuntimeLibrary", '/O1', '/Z7', '/W0', '/std:c++14') + $defines + $includes

# -fsanitize=fuzzer-no-link for the library objects: they get the coverage
# instrumentation libFuzzer steers on, but not the driver's main(). Only the
# target translation unit links -fsanitize=fuzzer, which is what pulls in main.
$librarySanitizer = '-fsanitize=fuzzer-no-link,address'
$targetSanitizer = '-fsanitize=fuzzer,address'

$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()

# ---------------------------------------------------------------------------
# Compile the shared objects
# ---------------------------------------------------------------------------
# One clang-cl invocation per source, with an explicit /Fo<file>.obj, rather than
# one invocation for all of them with /Fo<directory>\. Two reasons, both learned
# the hard way on Windows:
#
#   - An argument that both contains a space and ends in a backslash gets
#     mangled on its way to a native process (the trailing backslash escapes the
#     quote the shell added). /Fo"C:\some path\obj\" is exactly that shape, so it
#     breaks on any machine where the repository lives under a path with a space.
#   - When ten sources go through one invocation, a compile error names the file
#     but the progress output does not, and the first run of a new fuzz build is
#     precisely when you want to know which translation unit died.
#
# The cost is re-parsing the shim and the STL once per source. That is seconds,
# once, and this is not on anybody's inner loop.
$objects = @()
$sharedCount = ($serverSources + $harnessSources).Count
$sharedIndex = 0

Write-Host ''
Write-Host ("Compiling {0} shared translation units..." -f $sharedCount) -ForegroundColor Cyan

foreach ($relative in ($serverSources + $harnessSources)) {
    $sharedIndex++
    $source = Join-Path $repoRoot $relative
    $objectName = [System.IO.Path]::GetFileNameWithoutExtension($relative) + '.obj'
    $objectPath = Join-Path $objDir $objectName

    Write-Host ("  [{0}/{1}] {2}" -f $sharedIndex, $sharedCount, $relative)

    $compileArgs = @('/c', $librarySanitizer) + $commonFlags + @("/Fo$objectPath", $source)
    & $clang @compileArgs
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Compilation of $relative failed with exit code $LASTEXITCODE."
        exit 4
    }

    if (-not (Test-Path $objectPath)) {
        Write-Error "clang-cl reported success but $objectPath does not exist."
        exit 4
    }

    $objects += $objectPath
}

# ---------------------------------------------------------------------------
# Link one executable per target
# ---------------------------------------------------------------------------
# $objects is built from the shared source list above, never by globbing the
# object directory. Globbing would pick up the previous run's target objects -
# each of which defines LLVMFuzzerTestOneInput - and the second target would
# fail to link with a duplicate symbol on any incremental build.
$built = @()
foreach ($relative in $targetSources) {
    $name = [System.IO.Path]::GetFileNameWithoutExtension($relative)
    if ($Target -and $Target -ne $name) { continue }

    $source = Join-Path $repoRoot $relative
    $objectPath = Join-Path $objDir "$name.obj"
    $output = Join-Path $binDir "$name.exe"

    # Compile, then link, in two invocations. One combined command - the target
    # source plus the already-built library objects plus /Fo - fails with
    #
    #   clang-cl: error: cannot specify '/Fo...obj' when compiling multiple
    #   source files
    #
    # because clang-cl counts every .obj input as another source file and then
    # refuses a single named output for them. Splitting the steps also means a
    # failure says which half it was.
    Write-Host ("Compiling {0}..." -f $name) -ForegroundColor Cyan

    $compileArgs = @($targetSanitizer) + $commonFlags + @('/c', "/Fo$objectPath", $source)
    & $clang @compileArgs
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Compilation of $name failed with exit code $LASTEXITCODE."
        exit 5
    }

    Write-Host ("Linking {0}..." -f $name) -ForegroundColor Cyan

    # The sanitizer flag is repeated here because it is what pulls in libFuzzer's
    # main() and the ASan runtime at link time; the compile flags are not, because
    # clang-cl has nothing to compile in this step.
    $linkArgs = @($targetSanitizer, "/$RuntimeLibrary", $objectPath) + $objects + @("/Fe$output")
    & $clang @linkArgs
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Linking $name failed with exit code $LASTEXITCODE."
        exit 5
    }

    if (-not (Test-Path $output)) {
        Write-Error "clang-cl reported success but $output does not exist."
        exit 5
    }

    $built += $output
}

if ($Target -and $built.Count -eq 0) {
    Write-Error ("No target named '{0}'. Known targets: {1}" -f $Target, (($targetSources | ForEach-Object { [System.IO.Path]::GetFileNameWithoutExtension($_) }) -join ', '))
    exit 3
}

# ---------------------------------------------------------------------------
# AddressSanitizer runtime
# ---------------------------------------------------------------------------
# The DLL has to be findable at process start or every target dies with
# 0xc0000135 (STATUS_DLL_NOT_FOUND) before it runs a single input, and copying it
# next to the executables is more robust than telling people to fix their PATH.
#
# Copied for BOTH CRT choices, deliberately. This step used to run only for /MD,
# on the reasoning that a static CRT means a static ASan - and that is wrong.
# Measured on LLVM 22.1.8: a target built with -RuntimeLibrary MT still imports
# clang_rt.asan_dynamic-x86_64.dll and still fails to start with 0xc0000135
# without it. Since /MT is now the default (because /MD cannot link against the
# shipped libFuzzer at all), skipping the copy here would mean every fresh clone
# gets a build that reports success and produces three executables that cannot
# run.
if ($true) {
    $runtimeDir = Find-ClangRuntimeDirectory -ClangClPath $clang
    if ($runtimeDir) {
        $asanDlls = @(Get-ChildItem -Path $runtimeDir -Filter 'clang_rt.asan*dynamic*x86_64.dll' -ErrorAction SilentlyContinue)
        foreach ($dll in $asanDlls) {
            Copy-Item -LiteralPath $dll.FullName -Destination $binDir -Force
            Write-Host ("Copied {0} next to the targets." -f $dll.Name)
        }
        if ($asanDlls.Count -eq 0) {
            Write-Host "No dynamic ASan runtime found in $runtimeDir - assuming clang linked it statically." -ForegroundColor DarkGray
        }
    } else {
        Write-Warning "Could not locate the clang runtime directory. If a target fails to start with a missing-DLL error (0xc0000135), add <toolchain>\lib\clang\<version>\lib\windows to PATH, or rebuild with -RuntimeLibrary MT."
    }
}

$stopwatch.Stop()
Write-Host ''
Write-Host ("Build completed in {0:F1} seconds." -f $stopwatch.Elapsed.TotalSeconds)
foreach ($executable in $built) { Write-Host ("  {0}" -f $executable) }

# ---------------------------------------------------------------------------
# Seed corpus
# ---------------------------------------------------------------------------
if (-not $SkipCorpus) {
    Write-Host ''
    & (Join-Path $scriptRoot 'make-corpus.ps1')
    if ($LASTEXITCODE -ne 0) {
        Write-Error "make-corpus.ps1 failed with exit code $LASTEXITCODE."
        exit $LASTEXITCODE
    }
}

Write-Host ''
Write-Host 'Next:' -ForegroundColor Green
Write-Host '  .\fuzz\run-fuzz.ps1 -Target mime_message_fuzzer -Minutes 5     # smoke test'
Write-Host '  .\fuzz\run-fuzz.ps1 -Target mime_header_fuzzer -Replay         # corpus replay only, exits 0 or reports'
Write-Host '  See hmailserver\docs\Fuzzing.md before leaving one running for hours.'

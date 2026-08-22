# Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
# https://www.progressiverobot.com
#
# Regenerates hmailserver\source\Server\Common\AntiSpam\DMARC\PublicSuffixListData.h
# from the Public Suffix List (https://publicsuffix.org/).
#
# WHY A GENERATOR AND A COMMITTED HEADER, RATHER THAN A DATA FILE THE SERVER LOADS:
# DMARC::GetOrganizationalDomain is security logic - a wrong answer either lets one
# registrant forge another (suffix too short) or rejects correctly-signed mail
# (suffix too long). A data file in the program or data directory can be missing,
# stale, truncated by a failed install, or edited, and every one of those failure
# modes happens on the operator's machine where nobody is watching. Compiling the
# list in means the data provably matches what was tested, and the only way to
# change it is a rebuild - the same bar as changing the algorithm itself.
#
# USAGE:
#   .\generate-public-suffix-list.ps1                          # downloads the current list
#   .\generate-public-suffix-list.ps1 -ListFile psl.dat        # uses a local copy (reproducible)
#
# The script is idempotent: run against the same input file it produces a
# byte-identical header (no timestamps are embedded - the list's own VERSION and
# COMMIT lines identify the input instead). It validates hard and throws rather
# than emitting a partial header, because a truncated table IS the vulnerability
# this data exists to close: every suffix that fails to make it into the header is
# a suffix whose registrants can forge each other under relaxed alignment.
# SPDX-License-Identifier: AGPL-3.0-or-later

[CmdletBinding()]
param(
   # Path to an already-downloaded public_suffix_list.dat. When omitted the list
   # is fetched from the canonical URL (the PSL maintainers ask that it be pulled
   # from there and nowhere else).
   [string] $ListFile,

   # Where to write the generated header. Defaults to the location the server
   # project compiles it from.
   [string] $OutFile
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutFile)
{
   $OutFile = Join-Path $repoRoot 'hmailserver\source\Server\Common\AntiSpam\DMARC\PublicSuffixListData.h'
}

$listUrl = 'https://publicsuffix.org/list/public_suffix_list.dat'

if (-not $ListFile)
{
   $ListFile = Join-Path ([System.IO.Path]::GetTempPath()) 'public_suffix_list.dat'
   Write-Host "Downloading $listUrl ..."
   Invoke-WebRequest -Uri $listUrl -OutFile $ListFile -UseBasicParsing | Out-Null
}

if (-not (Test-Path $ListFile))
{
   throw "List file not found: $ListFile"
}

$lines = [System.IO.File]::ReadAllLines($ListFile, [System.Text.Encoding]::UTF8)

# ---------------------------------------------------------------------------
# Parse.
#
# The file format (https://github.com/publicsuffix/list/wiki/Format): one rule
# per line; '//' lines are comments; a leading '!' marks an exception rule; a
# leading '*.' is a wildcard whose '*' matches exactly one label. The format
# guarantees '*' only ever appears as the entire leftmost label, and the lookup
# code depends on that, so it is asserted here rather than assumed.
# ---------------------------------------------------------------------------

$icannBegin   = '// ===BEGIN ICANN DOMAINS==='
$icannEnd     = '// ===END ICANN DOMAINS==='
$privateBegin = '// ===BEGIN PRIVATE DOMAINS==='
$privateEnd   = '// ===END PRIVATE DOMAINS==='

$versionLine = ''
$commitLine = ''
$section = ''
$icannRuleCount = 0
$privateRuleCount = 0

# The three rule categories, stored the way the lookup consumes them:
# wildcards WITHOUT their '*.' prefix, exceptions WITHOUT their '!'.
$normalRules = [System.Collections.Generic.HashSet[string]]::new()
$wildcardRules = [System.Collections.Generic.HashSet[string]]::new()
$exceptionRules = [System.Collections.Generic.HashSet[string]]::new()

# The list carries IDN rules in Unicode (e.g. xn--p1ai's rules are written as
# Cyrillic). Mail almost always carries domains in their ASCII/ACE (xn--) form,
# because that is what DNS transports - but EAI mail can carry raw UTF-8. Both
# forms of every IDN rule are therefore emitted, so a lookup matches whichever
# form the message used, and the server never needs an IDN conversion at runtime.
$idn = [System.Globalization.IdnMapping]::new()
$idn.AllowUnassigned = $true

function Test-NonAscii([string] $value)
{
   return $value -cmatch '[^\x00-\x7F]'
}

function Convert-RuleToAce([string] $rule)
{
   $labels = $rule.Split('.')
   for ($i = 0; $i -lt $labels.Length; $i++)
   {
      if (Test-NonAscii $labels[$i])
      {
         # If an IDN label cannot be converted, GetAscii throws and the
         # generation fails rather than silently emitting only one form: a rule
         # that exists but cannot match the form mail actually carries is a
         # hole in the table.
         $labels[$i] = $idn.GetAscii($labels[$i])
      }
   }
   return ($labels -join '.')
}

function Add-Rule([System.Collections.Generic.HashSet[string]] $set, [string] $rule)
{
   [void]$set.Add($rule)

   if (Test-NonAscii $rule)
   {
      [void]$set.Add((Convert-RuleToAce $rule))
   }
}

foreach ($rawLine in $lines)
{
   $line = $rawLine.Trim()

   if ($line -eq '') { continue }

   if ($line.StartsWith('//'))
   {
      if ($line -like '// VERSION:*') { $versionLine = $line.Substring(3).Trim() }
      if ($line -like '// COMMIT:*')  { $commitLine = $line.Substring(3).Trim() }
      if ($line -eq $icannBegin)   { $section = 'icann' }
      if ($line -eq $icannEnd)     { $section = '' }
      if ($line -eq $privateBegin) { $section = 'private' }
      if ($line -eq $privateEnd)   { $section = '' }
      continue
   }

   # A rule outside the marked sections means the file is not the format this
   # script understands; refuse rather than guess.
   if ($section -eq '') { throw "Rule found outside any section marker: '$line'" }

   if ($line -cmatch '\s') { throw "Rule contains whitespace: '$line'" }

   $rule = $line.ToLowerInvariant()

   if ($section -eq 'icann') { $icannRuleCount++ } else { $privateRuleCount++ }

   if ($rule.StartsWith('!'))
   {
      $rule = $rule.Substring(1)
      if ($rule.Contains('*') -or $rule.Contains('!')) { throw "Malformed exception rule: '$line'" }
      # The lookup returns "labels in exception minus one" as the suffix length
      # and reserves 0 for "no data at all", so a single-label exception (an
      # exception to the implicit '*' rule) would be ambiguous. None has ever
      # existed in the list; if one appears, the lookup needs thought, not a
      # silently emitted table.
      if (($rule -split '\.').Count -lt 2) { throw "Single-label exception rule is not supported: '$line'" }
      Add-Rule $exceptionRules $rule
   }
   elseif ($rule.StartsWith('*.'))
   {
      $rule = $rule.Substring(2)
      if ($rule.Contains('*') -or $rule.Contains('!')) { throw "Wildcard in a non-leftmost position is not supported: '$line'" }
      Add-Rule $wildcardRules $rule
   }
   else
   {
      if ($rule.Contains('*') -or $rule.Contains('!')) { throw "Wildcard in a non-leftmost position is not supported: '$line'" }
      Add-Rule $normalRules $rule
   }

   if ($rule.StartsWith('.') -or $rule.EndsWith('.') -or $rule.Contains('..'))
   {
      throw "Rule has an empty label: '$line'"
   }
}

# Truncation guard. A partial download that still parses line-by-line would
# otherwise produce a plausible-looking header missing thousands of suffixes -
# and every missing suffix is a forgeable one. The current list has ~7,000
# ICANN rules and ~3,000 private ones; these floors only catch gross damage.
if ($icannRuleCount -lt 1000) { throw "Only $icannRuleCount ICANN rules parsed - the input looks truncated." }
if ($privateRuleCount -lt 100) { throw "Only $privateRuleCount PRIVATE rules parsed - the input looks truncated." }
if ($versionLine -eq '') { throw 'No VERSION line found - the input is not the canonical list.' }

# ---------------------------------------------------------------------------
# Sort. The C++ lookup binary-searches these arrays with wcscmp, which compares
# UTF-16 code units on Windows - exactly what StringComparer.Ordinal compares -
# so this sort and that search agree by construction. Any other collation here
# would make binary_search miss entries at random, which fails towards the
# forgery direction.
# ---------------------------------------------------------------------------

function Get-SortedArray([System.Collections.Generic.HashSet[string]] $set)
{
   $arr = [string[]]::new($set.Count)
   $set.CopyTo($arr)
   [Array]::Sort($arr, [System.StringComparer]::Ordinal)
   return ,$arr
}

$sortedNormal = Get-SortedArray $normalRules
$sortedWildcard = Get-SortedArray $wildcardRules
$sortedException = Get-SortedArray $exceptionRules

# The longest candidate the lookup ever needs to consider: a wildcard rule
# '*.X' matches one label more than X itself has.
$maxRuleLabels = 0
foreach ($r in $sortedNormal)    { $n = ($r -split '\.').Count;     if ($n -gt $maxRuleLabels) { $maxRuleLabels = $n } }
foreach ($r in $sortedException) { $n = ($r -split '\.').Count;     if ($n -gt $maxRuleLabels) { $maxRuleLabels = $n } }
foreach ($r in $sortedWildcard)  { $n = ($r -split '\.').Count + 1; if ($n -gt $maxRuleLabels) { $maxRuleLabels = $n } }

# ---------------------------------------------------------------------------
# Emit. Non-ASCII characters are written as universal-character-names (\uXXXX /
# \UXXXXXXXX, which take a fixed number of hex digits) rather than \x escapes
# (which greedily consume any hex digit that follows and would corrupt a rule
# whose escaped character is followed by 'a'-'f') or raw UTF-8 (whose
# interpretation depends on the compiler's source-charset setting). The header
# is pure ASCII and means the same thing under any /source-charset.
# ---------------------------------------------------------------------------

function ConvertTo-CppWideLiteral([string] $value)
{
   $sb = [System.Text.StringBuilder]::new()
   for ($i = 0; $i -lt $value.Length; $i++)
   {
      $c = $value[$i]
      if ([string]$c -cmatch '^[a-z0-9._\-]$')
      {
         [void]$sb.Append($c)
      }
      elseif ([char]::IsHighSurrogate($c) -and ($i + 1) -lt $value.Length -and [char]::IsLowSurrogate($value[$i + 1]))
      {
         [void]$sb.AppendFormat('\U{0:X8}', [char]::ConvertToUtf32($c, $value[$i + 1]))
         $i++
      }
      elseif ([char]::IsSurrogate($c))
      {
         throw "Unpaired surrogate in rule '$value' - the input file is corrupt."
      }
      else
      {
         [void]$sb.AppendFormat('\u{0:X4}', [int]$c)
      }
   }
   return $sb.ToString()
}

function Emit-RuleArray([System.Text.StringBuilder] $out, [string] $name, [string[]] $rules)
{
   if ($rules.Length -eq 0)
   {
      # A zero-length array is ill-formed C++; keep a null placeholder and an
      # explicit zero count so the lookup's bounds never touch it.
      [void]$out.AppendLine("   const wchar_t* const $name[] = { nullptr };")
      [void]$out.AppendLine("   const size_t ${name}Count = 0;")
      return
   }

   [void]$out.AppendLine("   const wchar_t* const $name[] =")
   [void]$out.AppendLine('   {')
   foreach ($rule in $rules)
   {
      [void]$out.AppendLine(('      L"{0}",' -f (ConvertTo-CppWideLiteral $rule)))
   }
   [void]$out.AppendLine('   };')
   [void]$out.AppendLine("   const size_t ${name}Count = sizeof($name) / sizeof($name[0]);")
}

$out = [System.Text.StringBuilder]::new()
[void]$out.AppendLine('// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd')
[void]$out.AppendLine('// https://www.progressiverobot.com')
[void]$out.AppendLine('//')
[void]$out.AppendLine('// GENERATED FILE - DO NOT EDIT BY HAND.')
[void]$out.AppendLine('// Regenerate with build\generate-public-suffix-list.ps1, which also documents')
[void]$out.AppendLine('// why the data is compiled in rather than loaded from disk.')
[void]$out.AppendLine('//')
[void]$out.AppendLine('// Derived from the Public Suffix List, https://publicsuffix.org/list/public_suffix_list.dat')
[void]$out.AppendLine("// $versionLine")
if ($commitLine -ne '') { [void]$out.AppendLine("// $commitLine") }
[void]$out.AppendLine('//')
[void]$out.AppendLine('// The Public Suffix List is subject to the terms of the Mozilla Public')
[void]$out.AppendLine('// License, v. 2.0. If a copy of the MPL was not distributed with this file,')
[void]$out.AppendLine('// you can obtain one at https://mozilla.org/MPL/2.0/.')
[void]$out.AppendLine('//')
[void]$out.AppendLine('// Rules are stored the way PublicSuffixList.cpp consumes them: lowercase,')
[void]$out.AppendLine("// wildcards without their leading '*.', exceptions without their leading '!',")
[void]$out.AppendLine('// each array sorted by UTF-16 code unit (wcscmp order) for binary search, and')
[void]$out.AppendLine('// IDN rules present in both their Unicode and their ASCII (xn--) forms so that')
[void]$out.AppendLine('// no IDN conversion is ever needed at runtime. Both the ICANN and the PRIVATE')
[void]$out.AppendLine('// sections are included: PRIVATE suffixes (shared-hosting domains such as')
[void]$out.AppendLine('// github.io) separate registrants exactly the way ccTLD registries do, and')
[void]$out.AppendLine('// treating them as one organization would let any tenant DMARC-align as any')
[void]$out.AppendLine('// other.')
[void]$out.AppendLine('')
[void]$out.AppendLine('#pragma once')
[void]$out.AppendLine('')
[void]$out.AppendLine('namespace HM')
[void]$out.AppendLine('{')
[void]$out.AppendLine('namespace PublicSuffixListData')
[void]$out.AppendLine('{')
Emit-RuleArray $out 'NormalRules' $sortedNormal
[void]$out.AppendLine('')
Emit-RuleArray $out 'WildcardRules' $sortedWildcard
[void]$out.AppendLine('')
Emit-RuleArray $out 'ExceptionRules' $sortedException
[void]$out.AppendLine('')
[void]$out.AppendLine('   // The largest number of labels any rule can match; lookups never need to')
[void]$out.AppendLine('   // consider a longer candidate than this.')
[void]$out.AppendLine("   const size_t MaxRuleLabels = $maxRuleLabels;")
[void]$out.AppendLine('}')
[void]$out.AppendLine('}')

[System.IO.File]::WriteAllText($OutFile, $out.ToString(), [System.Text.UTF8Encoding]::new($false))

Write-Host "Wrote $OutFile"
Write-Host ("  {0} ({1} ICANN + {2} PRIVATE rules in the input)" -f $versionLine, $icannRuleCount, $privateRuleCount)
Write-Host ("  Normal: {0}  Wildcard: {1}  Exception: {2}  MaxRuleLabels: {3}" -f $sortedNormal.Length, $sortedWildcard.Length, $sortedException.Length, $maxRuleLabels)

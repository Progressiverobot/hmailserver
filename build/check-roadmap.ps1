# Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
# SPDX-License-Identifier: AGPL-3.0-or-later

<#
   Checks Roadmap.md against itself.

   The roadmap is a 750-item status document whose whole value is that the counts
   can be trusted: the contents table at the top says how many items in each
   section are shipped, underway, not started or deferred, each section repeats
   its own counts under its heading, and the tick box on every row is the actual
   fact. Three places to say the same number, edited by hand, months apart.

   They drift. On 12 August 2026 four sections had a body that was ahead of the
   contents table - items had been ticked off as they shipped without the summary
   being touched - and the numbers in the contents table did not add up to its own
   Total row. Nothing in the build noticed, because nothing was looking.

   This is what looks. The tick boxes are treated as the truth, because they are
   the thing an author edits when the work actually happens; the summaries have to
   match them. Exits non-zero with a list of what disagrees.

   Run it after editing Roadmap.md, and before tagging a release - RELEASE.md
   lists it in the recipe.
#>

[CmdletBinding()]
param(
   [string] $Path = (Join-Path $PSScriptRoot '..\Roadmap.md')
)

$ErrorActionPreference = 'Stop'

# Written as code points rather than literals so that the script behaves the same
# whichever encoding the host decides the file has. The deferred mark is followed
# by U+FE0F (variation selector 16), which is part of the string in the document
# and has to be part of it here.
$shipped    = [char]::ConvertFromUtf32(0x2705)
$underway   = [char]::ConvertFromUtf32(0x1F504)
$notStarted = [char]::ConvertFromUtf32(0x2B1C)
$deferred   = [char]::ConvertFromUtf32(0x23F8) + [char]::ConvertFromUtf32(0xFE0F)
$enDash     = [char]::ConvertFromUtf32(0x2013)

$marks = @($shipped, $underway, $notStarted, $deferred)
$names = @('shipped', 'underway', 'not started', 'deferred')

function Get-Anchor
{
   # GitHub's heading anchors: lower case, punctuation dropped, spaces to hyphens.
   # An em dash is dropped and the spaces around it are not, which is why
   # "Dated items - the forcing functions" anchors with a double hyphen.
   param([string] $Text)

   $anchor = $Text.ToLowerInvariant()
   $anchor = $anchor -replace '[^a-z0-9 \-]', ''

   return ($anchor -replace ' ', '-')
}

function Get-Counts
{
   # The four count cells of a table row, with the en dash read as zero.
   param([string] $Cells)

   $values = @()

   foreach ($cell in ($Cells -split '\|'))
   {
      $text = $cell.Trim().Trim('*')

      if ($text -eq $enDash -or $text -eq '')
      {
         $values += 0
      }
      elseif ($text -match '^\d+$')
      {
         $values += [int] $text
      }
      else
      {
         return $null
      }
   }

   if ($values.Count -ne 4)
   {
      return $null
    }

   return , $values
}

if (-not (Test-Path -LiteralPath $Path))
{
   Write-Host "Roadmap not found: $Path" -ForegroundColor Red
   exit 1
}

$lines = @(Get-Content -LiteralPath $Path -Encoding UTF8)

# ---------------------------------------------------------------------------
# What the contents table claims, and what the Total row claims.
# ---------------------------------------------------------------------------
$declared = [ordered] @{}
$titles = @{}
$totalRow = $null

foreach ($line in $lines)
{
   if ($line -match '^\|\s*\[([^\]]+)\]\(#([^)]+)\)\s*\|(.+)\|\s*$')
   {
      $counts = Get-Counts $Matches[3]

      if ($null -ne $counts)
      {
         $declared[$Matches[2]] = $counts
         $titles[$Matches[2]] = $Matches[1]
      }
   }
   elseif ($line -match '^\|\s*\*\*Total\*\*\s*\|(.+)\|\s*$')
   {
      $totalRow = Get-Counts $Matches[1]
   }
}

# ---------------------------------------------------------------------------
# What the tick boxes actually say, attributed to the heading above them.
# Headings come in both flavours here: ATX (## Title) and Setext (Title with
# ---- under it). Counting only ATX would file every Setext section's rows
# under whichever ATX heading happened to come first.
# ---------------------------------------------------------------------------
$actual = @{}
$sectionSummaries = @{}
$current = $null

for ($i = 0; $i -lt $lines.Count; $i++)
{
   $line = $lines[$i]

   if ($line -match '^#{2,3}\s+(.+?)\s*$')
   {
      $current = Get-Anchor $Matches[1]
      continue
   }

   if ($i + 1 -lt $lines.Count -and $lines[$i + 1] -match '^[-=]{4,}\s*$' -and
       $line.Trim() -ne '' -and -not $line.StartsWith('|'))
   {
      $current = Get-Anchor $line.Trim()
      $i++
      continue
   }

   if ($null -eq $current)
   {
      continue
   }

   # "26 shipped - 0 underway - 12 not started - 1 deferred", the per-section
   # restatement that sits under the heading.
   if ($line -match '^(\d+) shipped\D+(\d+) underway\D+(\d+) not started\D+(\d+) deferred\s*$')
   {
      $sectionSummaries[$current] = @([int] $Matches[1], [int] $Matches[2], [int] $Matches[3], [int] $Matches[4])
      continue
   }

   if ($line -match '^\|\s*(\S+)\s*\|')
   {
      $index = $marks.IndexOf($Matches[1])

      if ($index -ge 0)
      {
         if (-not $actual.ContainsKey($current))
         {
            $actual[$current] = @(0, 0, 0, 0)
         }

         $counts = $actual[$current]
         $counts[$index] = $counts[$index] + 1
         $actual[$current] = $counts
      }
   }
}

# ---------------------------------------------------------------------------
# Report.
# ---------------------------------------------------------------------------
$problems = @()

Write-Host 'Roadmap.md consistency'

$sums = @(0, 0, 0, 0)

foreach ($anchor in $declared.Keys)
{
   for ($c = 0; $c -lt 4; $c++)
   {
      $sums[$c] += $declared[$anchor][$c]
   }
}

if ($null -eq $totalRow)
{
   $problems += 'The contents table has no **Total** row.'
}
else
{
   for ($c = 0; $c -lt 4; $c++)
   {
      if ($sums[$c] -ne $totalRow[$c])
      {
         $problems += ('Total row says {0} {1}; the section rows above it add up to {2}.' -f $totalRow[$c], $names[$c], $sums[$c])
      }
   }
}

foreach ($anchor in $declared.Keys)
{
   $title = $titles[$anchor]

   if (-not $actual.ContainsKey($anchor))
   {
      $problems += ("Contents lists '{0}' (#{1}) but no section with that anchor has any tick boxes." -f $title, $anchor)
      continue
   }

   for ($c = 0; $c -lt 4; $c++)
   {
      if ($declared[$anchor][$c] -ne $actual[$anchor][$c])
      {
         $problems += ("{0}: contents says {1} {2}, the rows in the section say {3}." -f $title, $declared[$anchor][$c], $names[$c], $actual[$anchor][$c])
      }
   }

   if ($sectionSummaries.ContainsKey($anchor))
   {
      for ($c = 0; $c -lt 4; $c++)
      {
         if ($sectionSummaries[$anchor][$c] -ne $actual[$anchor][$c])
         {
            $problems += ("{0}: the line under the heading says {1} {2}, the rows say {3}." -f $title, $sectionSummaries[$anchor][$c], $names[$c], $actual[$anchor][$c])
         }
      }
   }
}

# The item count in the prose above the table.
$totalItems = ($sums | Measure-Object -Sum).Sum
$claimed = $lines | Select-String -Pattern '^(\d+) items\.' | Select-Object -First 1

if ($claimed -and [int] $claimed.Matches[0].Groups[1].Value -ne $totalItems)
{
   $problems += ('The text says {0} items; the counts add up to {1}.' -f $claimed.Matches[0].Groups[1].Value, $totalItems)
}

Write-Host ('  {0} sections, {1} items ({2} shipped, {3} underway, {4} not started, {5} deferred)' -f
   $declared.Count, $totalItems, $sums[0], $sums[1], $sums[2], $sums[3])

if ($problems.Count -eq 0)
{
   Write-Host '  OK    every count matches the tick boxes it summarises' -ForegroundColor Green
   exit 0
}

foreach ($problem in $problems)
{
   Write-Host ('  FAIL  ' + $problem) -ForegroundColor Red
}

Write-Host ''
Write-Host ("{0} problem(s). The tick boxes are the truth - fix the summaries to match them." -f $problems.Count) -ForegroundColor Red

exit 1

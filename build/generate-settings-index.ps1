# Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
# SPDX-License-Identifier: AGPL-3.0-or-later

# Regenerates the Control Panel's settings search index.
#
# The Ctrl+K palette searches page names AND individual settings, so an
# administrator can type "log level" or "LogDeleteDays" and be taken to the page
# that owns it, wherever that page happens to live.
#
# The generated file is committed rather than produced at build time, so that
# changes to the index show up in review diffs. Nothing regenerates it
# automatically, so two things keep it honest:
#   - the CI "Verify the committed settings index is up to date" step re-runs
#     this script and fails if the working tree moves;
#   - ControlPanel.Tests\Services\SettingsSearchIndexTests.cs fails if a setting
#     or a whole settings page is missing from the committed index.
# Neither replaces running this script yourself after changing a setting.
#
# Three kinds of page are scanned: the two table-driven settings views, and the
# hand-written pages listed in $handWrittenPages, whose settings are plain XAML
# controls wired up in the code-behind.
#
# Usage:  ./build/generate-settings-index.ps1
#
# Run it after adding, removing or relabelling any setting.

$ErrorActionPreference = 'Stop'

# The page sources are UTF-8 and several setting labels contain non-ASCII text
# ("Password pepper - WARNING", "Deliver local -> local" with a real em dash and
# arrow). Windows PowerShell 5.1 reads files as the ANSI code page unless told
# otherwise, which turns those labels into mojibake and makes them unsearchable,
# so every read below goes through this instead of a bare Get-Content.
function Read-SourceText($path) {
   return Get-Content -LiteralPath $path -Raw -Encoding UTF8
}

$repoRoot = (Get-Item $PSScriptRoot).Parent.FullName
$cpRoot = Join-Path $repoRoot 'hmailserver\source\Tools\ControlPanel'
$featurePath = Join-Path $cpRoot 'Views\FeatureSettingsView.xaml.cs'
$serverPath = Join-Path $cpRoot 'Views\ServerSettingsView.xaml.cs'
$outputPath = Join-Path $cpRoot 'Services\SettingsSearchIndex.g.cs'

# Section -> nav page key, matching MainWindow's page factories.
$featurePages = @{ 'Security' = 'security'; 'Automation' = 'acme'; 'Integration' = 'api'; 'Hardening' = 'hardening';
                   'Authentication' = 'authentication'; 'Dns' = 'dns'; 'WebServices' = 'webservices' }
$serverPages = @{ 'Protocols' = 'protocols'; 'Delivery' = 'delivery'; 'AntiSpam' = 'antispam'; 'AntiVirus' = 'antivirus';
                  'Tls' = 'tls'; 'Logging' = 'logging'; 'Performance' = 'performance'; 'Advanced' = 'advanced';
                  'AdminAccess' = 'adminaccess'; 'AutoBan' = 'autoban' }

# Hand-written pages carry settings too - they just declare them as ordinary
# XAML controls instead of a table, so the two scans above cannot see them.
# View file (without extension) -> nav page key. Pages that only display data
# (status, queue, logs, dashboard) are deliberately absent: they own no setting,
# and scanning them would only turn read-outs into search noise.
$handWrittenPages = @{ 'BackupView' = 'backup'; 'IPRangesView' = 'ipranges'; 'TcpIpPortsView' = 'ports';
                       'RoutesView' = 'routes'; 'DomainsView' = 'domains'; 'RulesView' = 'rules';
                       'SslCertificatesView' = 'certs' }

function Add-Entry($list, $label, $key, $page) {
   # $label must already be escaped the way a C# string literal escapes it.
   $label = $label.Trim()
   if ([string]::IsNullOrWhiteSpace($label)) { return }
   $list.Add([PSCustomObject]@{ Label = $label; Key = $key; Page = $page }) | Out-Null
}

# On a hand-written page the on-screen wording lives in the XAML and the setting
# it writes lives in the code-behind; the x:Name of the control is what ties the
# two together, so that is what these two helpers key on.
function Get-XamlLabels($xamlText) {
   $labels = @{}
   $pending = $null

   foreach ($el in [regex]::Matches($xamlText, '<([A-Za-z_][\w:.\-]*)([^<>]*)>')) {
      $tag = $el.Groups[1].Value
      $attrs = $el.Groups[2].Value

      $nameMatch = [regex]::Match($attrs, 'x:Name="([^"]+)"')
      $contentMatch = [regex]::Match($attrs, '\bContent="([^"]*)"')
      $textMatch = [regex]::Match($attrs, '\bText="([^"]*)"')

      # A text box is labelled by the TextBlock above it rather than by an
      # attribute of its own, so remember the last short caption seen. Long
      # blocks are explanatory notes and bindings are not captions.
      if ($tag -like '*TextBlock') {
         if ($textMatch.Success) {
            $caption = [System.Net.WebUtility]::HtmlDecode($textMatch.Groups[1].Value).Trim()
            if ($caption.Length -gt 0 -and $caption.Length -le 60 -and -not $caption.StartsWith('{')) {
               $pending = $caption
            }
         }
         continue
      }

      if (-not $nameMatch.Success) { continue }
      $name = $nameMatch.Groups[1].Value

      if ($contentMatch.Success -and -not $contentMatch.Groups[1].Value.StartsWith('{')) {
         $labels[$name] = [System.Net.WebUtility]::HtmlDecode($contentMatch.Groups[1].Value).Trim()
      }
      elseif ($pending -and $tag -match '(TextBox|PasswordBox|ComboBox|NumberBox)$' -and $attrs -notmatch 'PlaceholderText=') {
         # A placeholder means the box is a "create a new one" field on a list
         # page, not a setting; those boxes describe themselves and the caption
         # above them is the heading of the whole add row.
         $labels[$name] = $pending
         $pending = $null
      }
   }

   return $labels
}

function Get-CodeKeys($codeText) {
   $keys = @{}

   # COM properties are reached through dynamic locals; requiring the object to
   # be one keeps control-to-control assignments out of the results.
   $dynamicLocals = @{}
   foreach ($m in [regex]::Matches($codeText, '\bdynamic\s+(\w+)\s*=')) {
      $dynamicLocals[$m.Groups[1].Value] = $true
   }

   foreach ($m in [regex]::Matches($codeText, '(\w+)\.(?:IsChecked|Text)\s*=\s*\w+\.Read(?:Bool|String|Int|Number)\(\s*"([^"]+)"')) {
      $keys[$m.Groups[1].Value] = $m.Groups[2].Value
   }
   foreach ($m in [regex]::Matches($codeText, '\w+\.Write(?:Bool|String|Int|Number)\(\s*"([^"]+)"\s*,\s*(\w+)\.(?:IsChecked|Text)')) {
      $keys[$m.Groups[2].Value] = $m.Groups[1].Value
   }
   foreach ($m in [regex]::Matches($codeText, '(\w+)\.(?:IsChecked|Text)\s*=\s*\((?:bool|string|int)\)\s*(\w+)\.(\w+)')) {
      if ($dynamicLocals.ContainsKey($m.Groups[2].Value) -and -not $keys.ContainsKey($m.Groups[1].Value)) {
         $keys[$m.Groups[1].Value] = $m.Groups[3].Value
      }
   }
   foreach ($m in [regex]::Matches($codeText, '(\w+)\.(\w+)\s*=\s*(\w+)\.(?:IsChecked\s*==\s*true|Text\b)')) {
      if ($dynamicLocals.ContainsKey($m.Groups[1].Value) -and -not $keys.ContainsKey($m.Groups[3].Value)) {
         $keys[$m.Groups[3].Value] = $m.Groups[2].Value
      }
   }

   return $keys
}

# XAML text is not a C# literal, so it has to be escaped before it is written
# into one - unlike the labels lifted straight out of the two sources above.
function ConvertTo-CSharpLiteral($text) {
   return $text.Replace('\', '\\').Replace('"', '\"')
}

# The reverse, for labels lifted OUT of C# source. A label may legitimately
# contain an escaped quote - says \"reject\" - and a capture that keeps the
# escapes would be double-escaped on emit, showing literal backslashes in the
# palette. This bit for real on FilterHookRejectScore's label: the naive
# [^"]* capture stopped at the escaped quote, emitted a string ending in a
# lone backslash, and the generated file did not compile.
function ConvertFrom-CSharpLiteral($text) {
   return $text.Replace('\"', '"').Replace('\\', '\')
}

$entries = New-Object System.Collections.ArrayList

# --- INI-backed pages (FeatureSettingsView): "case Section.X:" blocks ---
$featureText = Read-SourceText $featurePath
$featureParts = [regex]::Split($featureText, 'case Section\.(\w+):')
for ($i = 1; $i -lt $featureParts.Count; $i += 2) {
   $section = $featureParts[$i]
   $body = $featureParts[$i + 1]
   $page = $featurePages[$section]
   if (-not $page) { continue }

   foreach ($m in [regex]::Matches($body, 'Key = "([^"]+)"[^}]*?Label = "((?:[^"\\]|\\.)*)"')) {
      Add-Entry $entries (ConvertFrom-CSharpLiteral $m.Groups[2].Value) $m.Groups[1].Value $page
   }
}

# --- COM-backed pages (ServerSettingsView): "private void Build<Section>()" ---
$serverText = Read-SourceText $serverPath
foreach ($m in [regex]::Matches($serverText, '(?s)private void Build(\w+)\(\)\s*\{(.*?)\n      \}')) {
   $section = $m.Groups[1].Value
   $body = $m.Groups[2].Value
   $page = $serverPages[$section]
   if (-not $page) { continue }

   foreach ($s in [regex]::Matches($body, 'Path = "([^"]+)",\s*Label = "((?:[^"\\]|\\.)*)"')) {
      Add-Entry $entries (ConvertFrom-CSharpLiteral $s.Groups[2].Value) $s.Groups[1].Value $page
   }
   foreach ($s in [regex]::Matches($body, 'Label = "((?:[^"\\]|\\.)*)",\s*Path = "([^"]+)"')) {
      Add-Entry $entries (ConvertFrom-CSharpLiteral $s.Groups[1].Value) $s.Groups[2].Value $page
   }
}

# --- Hand-written pages: XAML for the wording, code-behind for the setting ---
foreach ($view in ($handWrittenPages.Keys | Sort-Object)) {
   $page = $handWrittenPages[$view]
   $xamlPath = Join-Path $cpRoot "Views\$view.xaml"
   $codePath = Join-Path $cpRoot "Views\$view.xaml.cs"

   if (-not (Test-Path $xamlPath) -or -not (Test-Path $codePath)) {
      throw "$view is listed in `$handWrittenPages but $xamlPath or $codePath is missing."
   }

   $labels = Get-XamlLabels (Read-SourceText $xamlPath)
   $keys = Get-CodeKeys (Read-SourceText $codePath)

   # A control the page reads or writes but never labels cannot be searched for
   # by name, so there is nothing useful to index.
   foreach ($control in ($keys.Keys | Sort-Object)) {
      if (-not $labels.ContainsKey($control)) { continue }
      Add-Entry $entries $labels[$control] $keys[$control] $page
   }
}

# Stable order so regeneration produces no spurious diffs.
$sorted = $entries | Sort-Object Page, Label, Key -Unique

$sb = New-Object System.Text.StringBuilder
# The licence header goes in here, not onto the generated file afterwards:
# this script rewrites the whole file, so anything stamped on it by
# build/add-license-headers.py lasts exactly until the next regeneration -
# which is how the SPDX check went red on the first push after both landed.
[void]$sb.AppendLine('// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors')
[void]$sb.AppendLine('// SPDX-License-Identifier: AGPL-3.0-or-later')
[void]$sb.AppendLine('// <auto-generated>')
[void]$sb.AppendLine('//     Generated by build/generate-settings-index.ps1 from the settings page')
[void]$sb.AppendLine('//     definitions. Do not edit by hand - re-run the script instead.')
[void]$sb.AppendLine('// </auto-generated>')
[void]$sb.AppendLine()
[void]$sb.AppendLine('namespace hMailServer.ControlPanel.Services')
[void]$sb.AppendLine('{')
[void]$sb.AppendLine('   public static partial class SettingsSearchIndex')
[void]$sb.AppendLine('   {')
[void]$sb.AppendLine('      /// <summary>Every setting on the settings pages: label, INI key or COM path, and the nav page that owns it.</summary>')
[void]$sb.AppendLine('      public static readonly SettingEntry[] Entries =')
[void]$sb.AppendLine('      {')

foreach ($e in $sorted) {
   # The captured text comes straight out of a C# string literal, so it is
   # already escaped; re-escaping it here would double the backslashes.
   # One escape, at the one place text becomes a C# literal - wherever the
   # label came from, it is plain text by the time it reaches here.
   [void]$sb.AppendLine("         new SettingEntry(`"$(ConvertTo-CSharpLiteral $e.Label)`", `"$($e.Key)`", `"$($e.Page)`"),")
}

[void]$sb.AppendLine('      };')
[void]$sb.AppendLine('   }')
[void]$sb.AppendLine('}')

New-Item -ItemType Directory -Force -Path (Split-Path $outputPath) | Out-Null

# UTF-8 with no BOM, written verbatim. Set-Content -Encoding UTF8 would do this
# under PowerShell 7 but emit a BOM under Windows PowerShell 5.1, so which shell
# happened to run the script would decide whether the CI up-to-date check passes.
# $outputPath is absolute, so WriteAllText's own working directory does not matter.
[System.IO.File]::WriteAllText($outputPath, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))

Write-Host "Wrote $($sorted.Count) settings to $outputPath"

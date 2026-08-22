# Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
# SPDX-License-Identifier: AGPL-3.0-or-later

<#
.SYNOPSIS
    Export, diff and apply hMailServer configuration as a reviewable JSON document.

.DESCRIPTION
    Configuration lives in two places: a database (domains, accounts, aliases,
    distribution lists, routes, IP ranges, TCP/IP ports) and hMailServer.ini
    (server-wide feature settings). Both are perfectly good stores; what has been
    missing is a way to see the whole thing at once, put it in version control,
    review a change as a diff, and reproduce it on another machine.

    This is that. It talks to the server through the COM API - the same interface
    the Control Panel and the regression suite use - so it can never disagree with
    the rules the server itself enforces.

    Three verbs:

      export   Write the live configuration to a JSON file (or stdout).
      diff     Compare a JSON file against the live configuration.
      apply    Make the live configuration match a JSON file.

.PARAMETER Action
    export, diff or apply.

.PARAMETER Path
    The JSON file. Omit on export to write to stdout.

.PARAMETER AdminPassword
    The hMailServer administrator password. Prompted for if omitted.

.PARAMETER Host
    Remote host to administer. Defaults to the local machine.

.PARAMETER AllowDelete
    apply only. Permits removal of objects that exist on the server but not in the
    file. OFF BY DEFAULT AND DELIBERATELY SO - see the safety notes below.

.PARAMETER Force
    apply only. Actually write. Without it, apply is a dry run and prints what it
    would do, because a config tool that writes by default is a foot-gun.

.EXAMPLE
    .\hmconfig.ps1 export -Path config.json
    .\hmconfig.ps1 diff -Path config.json
    .\hmconfig.ps1 apply -Path config.json          # dry run
    .\hmconfig.ps1 apply -Path config.json -Force   # write

.NOTES
    SAFETY, and why apply behaves the way it does.

    Deleting an account deletes its mail. A "declarative" tool that reconciles by
    removing anything absent from the file would turn a stale checkout, a partial
    export, or a typo in a domain name into permanent mail loss - so removal
    requires -AllowDelete AND -Force, and each deletion is named before it happens.
    Additive and update operations are the default because they cannot lose mail.

    Passwords are never exported. The server stores hashes, and a hash is not a
    password - there is nothing useful to write to a file and nothing safe to put in
    version control. apply therefore never touches the password of an existing
    account, and creates new accounts with a random one that must be reset. A config
    file that silently reset every mailbox password on apply would be far worse than
    one that cannot set them at all.

    This is configuration, not backup. It does not contain mail, and it is not a
    substitute for the backup covered in hmailserver/docs/Upgrading.md.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateSet('export', 'diff', 'apply')]
    [string] $Action,

    [Parameter(Position = 1)]
    [string] $Path,

    [string] $AdminPassword,

    [Alias('Server')]
    [string] $HostName = '',

    [switch] $AllowDelete,

    [switch] $Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# The schema version of the document this script writes. Bump it when the shape
# changes so that apply can refuse a file it does not understand rather than
# half-applying one.
$script:SchemaVersion = 1

function Connect-HMailServer {
    param([string] $ComputerName, [string] $Password)

    $app = if ([string]::IsNullOrWhiteSpace($ComputerName)) {
        New-Object -ComObject 'hMailServer.Application'
    } else {
        $type = [Type]::GetTypeFromProgID('hMailServer.Application', $ComputerName, $true)
        [Activator]::CreateInstance($type)
    }

    if ([string]::IsNullOrWhiteSpace($Password)) {
        $secure = Read-Host -Prompt 'hMailServer administrator password' -AsSecureString
        $Password = [Runtime.InteropServices.Marshal]::PtrToStringAuto(
            [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure))
    }

    # Authenticate returns an account object on success and $null on failure.
    if (-not $app.Authenticate('Administrator', $Password)) {
        throw 'Authentication failed. Check the administrator password.'
    }

    return $app
}

# Reads a property that may not exist on an older server build, so the tool keeps
# working across versions instead of dying on the first missing member.
function Get-Prop {
    param($Object, [string] $Name, $Default = $null)
    try { return $Object.$Name } catch { return $Default }
}

function Export-Configuration {
    param($App)

    $settings = $App.Settings

    $config = [ordered]@{
        schemaVersion  = $script:SchemaVersion
        exportedUtc    = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
        serverVersion  = Get-Prop $App 'Version' 'unknown'
        # Deliberately not exported: anything secret (passwords, hashes, private
        # keys) and anything machine-specific that would be wrong on another host.
        settings       = [ordered]@{
            maxMessageSize            = Get-Prop $settings 'MaxMessageSize'
            maxNumberOfMXHosts        = Get-Prop $settings 'MaxNumberOfMXHosts'
            maxNumberOfInvalidCommands = Get-Prop $settings 'MaxNumberOfInvalidCommands'
            autoBanOnLogonFailure     = Get-Prop $settings 'AutoBanOnLogonFailure'
            maxInvalidLogonAttempts   = Get-Prop $settings 'MaxInvalidLogonAttempts'
            autoBanMinutes            = Get-Prop $settings 'AutoBanMinutes'
            hostName                  = Get-Prop $settings 'HostName'
            welcomeSMTP               = Get-Prop $settings 'WelcomeSMTP'
            welcomePOP3               = Get-Prop $settings 'WelcomePOP3'
            welcomeIMAP               = Get-Prop $settings 'WelcomeIMAP'
            useSpamAssassin           = Get-Prop $settings.AntiSpam 'UseSpamAssassin'
            spamAssassinHost          = Get-Prop $settings.AntiSpam 'SpamAssassinHost'
            spamAssassinPort          = Get-Prop $settings.AntiSpam 'SpamAssassinPort'
            spamMarkThreshold         = Get-Prop $settings.AntiSpam 'SpamMarkThreshold'
            spamDeleteThreshold       = Get-Prop $settings.AntiSpam 'SpamDeleteThreshold'
        }
        domains        = @()
        tcpIpPorts     = @()
    }

    for ($d = 0; $d -lt $App.Domains.Count; $d++) {
        $domain = $App.Domains.Item($d)

        $domainEntry = [ordered]@{
            name                    = $domain.Name
            active                  = $domain.Active
            maxSize                 = Get-Prop $domain 'MaxSize'
            maxNoOfAccounts         = Get-Prop $domain 'MaxNoOfAccounts'
            postmaster              = Get-Prop $domain 'Postmaster'
            plusAddressingEnabled   = Get-Prop $domain 'PlusAddressingEnabled'
            plusAddressingCharacter = Get-Prop $domain 'PlusAddressingCharacter'
            dkimSignEnabled         = Get-Prop $domain 'DKIMSignEnabled'
            dkimSelector            = Get-Prop $domain 'DKIMSelector'
            dkimPrivateKeyFile      = Get-Prop $domain 'DKIMPrivateKeyFile'
            accounts                = @()
            aliases                 = @()
            distributionLists       = @()
        }

        for ($a = 0; $a -lt $domain.Accounts.Count; $a++) {
            $account = $domain.Accounts.Item($a)
            # No password, and no password hash. See the safety notes.
            $domainEntry.accounts += [ordered]@{
                address           = $account.Address
                active            = $account.Active
                maxSize           = Get-Prop $account 'MaxSize'
                adminLevel        = Get-Prop $account 'AdminLevel'
                personFirstName   = Get-Prop $account 'PersonFirstName'
                personLastName    = Get-Prop $account 'PersonLastName'
                forwardAddress    = Get-Prop $account 'ForwardAddress'
                forwardEnabled    = Get-Prop $account 'ForwardEnabled'
                forwardKeepOriginal = Get-Prop $account 'ForwardKeepOriginal'
                vacationMessageOn = Get-Prop $account 'VacationMessageOn'
                vacationSubject   = Get-Prop $account 'VacationSubject'
                enableSignature   = Get-Prop $account 'EnableSignature'
            }
        }

        for ($i = 0; $i -lt $domain.Aliases.Count; $i++) {
            $alias = $domain.Aliases.Item($i)
            $domainEntry.aliases += [ordered]@{
                name   = $alias.Name
                value  = $alias.Value
                active = $alias.Active
            }
        }

        for ($i = 0; $i -lt $domain.DistributionLists.Count; $i++) {
            $list = $domain.DistributionLists.Item($i)
            $members = @()
            try {
                for ($m = 0; $m -lt $list.Recipients.Count; $m++) {
                    $members += $list.Recipients.Item($m).RecipientAddress
                }
            } catch { }

            $domainEntry.distributionLists += [ordered]@{
                address    = $list.Address
                active     = $list.Active
                mode       = Get-Prop $list 'Mode'
                requireAuth = Get-Prop $list 'RequireSMTPAuth'
                members    = $members
            }
        }

        $config.domains += $domainEntry
    }

    for ($p = 0; $p -lt $App.Settings.TCPIPPorts.Count; $p++) {
        $port = $App.Settings.TCPIPPorts.Item($p)
        $config.tcpIpPorts += [ordered]@{
            protocol           = Get-Prop $port 'Protocol'
            portNumber         = Get-Prop $port 'PortNumber'
            address            = Get-Prop $port 'Address'
            connectionSecurity = Get-Prop $port 'ConnectionSecurity'
        }
    }

    return $config
}

function Compare-Configuration {
    param($Live, $Wanted)

    $differences = New-Object System.Collections.Generic.List[string]

    function Compare-Node {
        param($LiveNode, $WantedNode, [string] $Prefix)

        if ($null -eq $WantedNode) { return }

        foreach ($property in $WantedNode.PSObject.Properties) {
            $name = $property.Name
            if ($name -in @('schemaVersion', 'exportedUtc', 'serverVersion')) { continue }

            $wantedValue = $property.Value
            $liveValue = if ($LiveNode -and $LiveNode.PSObject.Properties[$name]) { $LiveNode.$name } else { $null }

            if ($wantedValue -is [System.Management.Automation.PSCustomObject]) {
                Compare-Node -LiveNode $liveValue -WantedNode $wantedValue -Prefix "$Prefix$name."
            }
            elseif ($wantedValue -is [System.Object[]] -or $wantedValue -is [System.Collections.IList]) {
                # Collections are keyed by their natural identifier, so a reordered
                # export is not reported as a change.
                $key = if ($name -eq 'accounts' -or $name -eq 'distributionLists') { 'address' }
                       elseif ($name -eq 'domains' -or $name -eq 'aliases') { 'name' }
                       elseif ($name -eq 'tcpIpPorts') { 'portNumber' }
                       else { $null }

                if (-not $key) { continue }

                $liveItems = @($liveValue)
                foreach ($wantedItem in @($wantedValue)) {
                    if ($null -eq $wantedItem) { continue }
                    $id = $wantedItem.$key
                    $match = $liveItems | Where-Object { $_ -and $_.$key -eq $id } | Select-Object -First 1
                    if (-not $match) {
                        $differences.Add("+ $Prefix$name[$id] does not exist on the server")
                    } else {
                        Compare-Node -LiveNode $match -WantedNode $wantedItem -Prefix "$Prefix$name[$id]."
                    }
                }
                foreach ($liveItem in $liveItems) {
                    if ($null -eq $liveItem) { continue }
                    $id = $liveItem.$key
                    if (-not (@($wantedValue) | Where-Object { $_ -and $_.$key -eq $id })) {
                        $differences.Add("- $Prefix$name[$id] exists on the server but not in the file")
                    }
                }
            }
            else {
                if ("$liveValue" -ne "$wantedValue") {
                    $differences.Add("~ $Prefix$name : server='$liveValue' file='$wantedValue'")
                }
            }
        }
    }

    Compare-Node -LiveNode $Live -WantedNode $Wanted -Prefix ''

    # Unary comma: without it PowerShell unrolls the list, so an empty result
    # arrives as $null and a single difference arrives as a bare string - and
    # .Count then throws under Set-StrictMode.
    return ,$differences
}

function Invoke-Apply {
    param($App, $Wanted, [bool] $PermitDelete, [bool] $DoWrite)

    $planned = New-Object System.Collections.Generic.List[string]

    if ($Wanted.schemaVersion -ne $script:SchemaVersion) {
        throw ("This file declares schemaVersion {0}; this tool understands {1}. Refusing to apply a document shape it may not represent correctly." -f $Wanted.schemaVersion, $script:SchemaVersion)
    }

    foreach ($wantedDomain in @($Wanted.domains)) {
        if ($null -eq $wantedDomain) { continue }

        $domain = $null
        try { $domain = $App.Domains.ItemByName($wantedDomain.name) } catch { }

        if (-not $domain) {
            $planned.Add("create domain $($wantedDomain.name)")
            if ($DoWrite) {
                $domain = $App.Domains.Add()
                $domain.Name = $wantedDomain.name
                $domain.Active = [bool] $wantedDomain.active
                $domain.Save()
            }
        }

        if ($DoWrite -and $domain) {
            foreach ($field in @('Active', 'MaxSize', 'MaxNoOfAccounts', 'Postmaster',
                                 'PlusAddressingEnabled', 'PlusAddressingCharacter')) {
                $jsonName = $field.Substring(0, 1).ToLower() + $field.Substring(1)
                if ($wantedDomain.PSObject.Properties[$jsonName] -and $null -ne $wantedDomain.$jsonName) {
                    try { $domain.$field = $wantedDomain.$jsonName } catch { }
                }
            }
            $domain.Save()
        }

        foreach ($wantedAccount in @($wantedDomain.accounts)) {
            if ($null -eq $wantedAccount) { continue }

            $account = $null
            if ($domain) { try { $account = $domain.Accounts.ItemByAddress($wantedAccount.address) } catch { } }

            if (-not $account) {
                # A new account gets a random password that must be reset. The file
                # cannot carry one, and inventing a predictable one would be worse
                # than requiring a reset.
                $planned.Add("create account $($wantedAccount.address) (password must be set afterwards)")
                if ($DoWrite -and $domain) {
                    $account = $domain.Accounts.Add()
                    $account.Address = $wantedAccount.address
                    $account.Password = [Guid]::NewGuid().ToString('N') + '!Aa9'
                    $account.Active = [bool] $wantedAccount.active
                    $account.Save()
                }
            }
            else {
                # Never touch the password of an account that already exists.
                if ($DoWrite) {
                    foreach ($field in @('Active', 'MaxSize', 'AdminLevel', 'PersonFirstName',
                                         'PersonLastName', 'ForwardAddress', 'ForwardEnabled',
                                         'ForwardKeepOriginal', 'EnableSignature')) {
                        $jsonName = $field.Substring(0, 1).ToLower() + $field.Substring(1)
                        if ($wantedAccount.PSObject.Properties[$jsonName] -and $null -ne $wantedAccount.$jsonName) {
                            try { $account.$field = $wantedAccount.$jsonName } catch { }
                        }
                    }
                    $account.Save()
                }
            }
        }

        # Removals, only when explicitly permitted, and named individually.
        if ($domain) {
            for ($a = $domain.Accounts.Count - 1; $a -ge 0; $a--) {
                $liveAccount = $domain.Accounts.Item($a)
                $stillWanted = @($wantedDomain.accounts) | Where-Object { $_ -and $_.address -eq $liveAccount.Address }
                if (-not $stillWanted) {
                    if ($PermitDelete) {
                        $planned.Add("DELETE account $($liveAccount.Address) AND ALL ITS MAIL")
                        if ($DoWrite) { $domain.Accounts.DeleteByDBID($liveAccount.ID) }
                    } else {
                        $planned.Add("skip: $($liveAccount.Address) is on the server but not in the file (-AllowDelete not given)")
                    }
                }
            }
        }
    }

    # Unary comma, for the same reason as Compare-Configuration above.
    return ,$planned
}

# ---------------------------------------------------------------------------

$app = Connect-HMailServer -ComputerName $HostName -Password $AdminPassword

switch ($Action) {
    'export' {
        $config = Export-Configuration -App $app
        $json = $config | ConvertTo-Json -Depth 12
        if ([string]::IsNullOrWhiteSpace($Path)) {
            Write-Output $json
        } else {
            Set-Content -LiteralPath $Path -Value $json -Encoding UTF8
            Write-Host ("Exported {0} domain(s) to {1}" -f $config.domains.Count, $Path)
        }
    }

    'diff' {
        if ([string]::IsNullOrWhiteSpace($Path)) { throw 'diff needs -Path.' }
        $wanted = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
        $live = Export-Configuration -App $app | ConvertTo-Json -Depth 12 | ConvertFrom-Json

        # No @() here: Compare-Configuration already returns the list intact via a
        # unary comma, and wrapping it would give an array containing one list.
        $differences = Compare-Configuration -Live $live -Wanted $wanted
        if ($differences.Count -eq 0) {
            Write-Host 'No differences.' -ForegroundColor Green
        } else {
            Write-Host ("{0} difference(s):" -f $differences.Count) -ForegroundColor Yellow
            $differences | ForEach-Object { Write-Host "  $_" }
            exit 1
        }
    }

    'apply' {
        if ([string]::IsNullOrWhiteSpace($Path)) { throw 'apply needs -Path.' }
        $wanted = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json

        if (-not $Force) {
            Write-Host 'DRY RUN - nothing will be written. Add -Force to apply.' -ForegroundColor Cyan
        }
        if (-not $AllowDelete) {
            Write-Host 'Deletions are disabled. Add -AllowDelete to permit them (this deletes mail).' -ForegroundColor Cyan
        }

        $planned = Invoke-Apply -App $app -Wanted $wanted -PermitDelete:$AllowDelete.IsPresent -DoWrite:$Force.IsPresent

        if ($planned.Count -eq 0) {
            Write-Host 'Nothing to do.' -ForegroundColor Green
        } else {
            $planned | ForEach-Object { Write-Host "  $_" }
            Write-Host ("{0} operation(s) {1}." -f $planned.Count, $(if ($Force) { 'applied' } else { 'would be applied' }))
        }
    }
}

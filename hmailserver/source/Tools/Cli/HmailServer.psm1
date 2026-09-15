# https://www.progressiverobot.com
# Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
# SPDX-License-Identifier: AGPL-3.0-or-later

<#
   The hMailServer PowerShell module: the REST API with PowerShell's own grammar
   in front of it.

   build/hmconfig.ps1 came first and does a different job - it exports the whole
   configuration as one reviewable JSON document and applies it somewhere else -
   and it talks COM, so it is Windows-only and needs the Control Panel's world.
   This is the other half: per-object verbs over HTTP, so it works against a
   server on another machine, against the Linux build, and from a session that
   has never heard of COM.

       Connect-HmServer -Url https://mail.example.com:8045
       Get-HmDomain
       New-HmAccount -Domain example.com -Address ann@example.com -Password (Read-Host -AsSecureString)
       Set-HmSetting -Group antispam -Property @{ use_spf = $true }
       Get-HmQueue | Where-Object retry_count -gt 3

   Every function answers objects, not text, because that is what a PowerShell
   user will do something with. Nothing here formats anything.

   The session - the address and the credential - is held in the module for the
   life of it, and every function takes -Url, -Password and -ApiKey of its own
   for a one-off call against another server.

   hmctl beside this file is the same vocabulary for a shell that is not
   PowerShell; the two are checked against one recorded server by
   build/check-cli.py, so they cannot drift apart in what they ask for.
#>

Set-StrictMode -Version Latest

$script:Session = $null

# ---------------------------------------------------------------------------
# The session
# ---------------------------------------------------------------------------

function Connect-HmServer {
    <#
    .SYNOPSIS
        Remembers which server to administer, and how to prove who you are.
    .DESCRIPTION
        Either an administrator password or an API key. Nothing is sent by this
        function: the credential is held and used by the first call that needs
        it, so a wrong password is reported by the call that fails, not here.

        -Insecure accepts a certificate nobody verified, and is refused for any
        host but the loopback: sending an administrator password to a remote
        host over an unverified connection is not a convenience.
    .EXAMPLE
        Connect-HmServer -Url https://mail.example.com:8045
    .EXAMPLE
        Connect-HmServer -Url https://localhost:8045 -Insecure -ApiKey $key
    #>
    [CmdletBinding(DefaultParameterSetName = 'Password')]
    param(
        [Parameter(Mandatory = $true)]
        [string] $Url,

        [Parameter(ParameterSetName = 'Password')]
        [System.Security.SecureString] $Password,

        [Parameter(Mandatory = $true, ParameterSetName = 'ApiKey')]
        [string] $ApiKey,

        [switch] $Insecure,

        [int] $TimeoutSeconds = 30
    )

    $uri = [System.Uri] $Url

    if ($Insecure -and $uri.Host -notin @('localhost', '127.0.0.1', '::1')) {
        throw "-Insecure is refused for $($uri.Host): it is allowed for the loopback only, because it would send the credential to a host nobody verified. Give the server a certificate the client trusts."
    }

    if ($PSCmdlet.ParameterSetName -eq 'Password' -and -not $Password) {
        $Password = Read-Host -Prompt "Administrator password for $Url" -AsSecureString
    }

    $script:Session = [pscustomobject] @{
        Url            = $Url.TrimEnd('/')
        Password       = $Password
        ApiKey         = if ($PSCmdlet.ParameterSetName -eq 'ApiKey') { $ApiKey } else { $null }
        Insecure       = [bool] $Insecure
        TimeoutSeconds = $TimeoutSeconds
    }

    $script:Session
}

function Disconnect-HmServer {
    <#
    .SYNOPSIS
        Forgets the server and the credential held for it.
    #>
    [CmdletBinding()]
    param()
    $script:Session = $null
}

function Get-HmSession {
    <#
    .SYNOPSIS
        The session Connect-HmServer is holding, without the credential's value.
    #>
    [CmdletBinding()]
    param()
    if (-not $script:Session) { return $null }
    [pscustomobject] @{
        Url        = $script:Session.Url
        Credential = if ($script:Session.ApiKey) { 'API key' } elseif ($script:Session.Password) { 'administrator password' } else { 'none' }
        Insecure   = $script:Session.Insecure
    }
}

# ---------------------------------------------------------------------------
# The one place that talks to the server
# ---------------------------------------------------------------------------

function Invoke-HmApi {
    <#
    .SYNOPSIS
        Any method on any path of the REST API - the escape hatch, and what
        every other function here is built on.
    .DESCRIPTION
        Use it for a route newer than this module. -Body takes a hashtable or
        any object; it is sent as JSON.
    .EXAMPLE
        Invoke-HmApi -Method GET -Path /api/v1/status
    .EXAMPLE
        Invoke-HmApi -Method POST -Path /api/v1/domains -Body @{ name = 'example.com' }
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [ValidateSet('GET', 'POST', 'PUT', 'DELETE', 'PATCH')]
        [string] $Method,

        [Parameter(Mandatory = $true)]
        [string] $Path,

        [object] $Body,

        [string] $Url,
        [System.Security.SecureString] $Password,
        [string] $ApiKey,
        [switch] $Insecure
    )

    $session = $script:Session

    if ($Url -or $Password -or $ApiKey) {
        $session = [pscustomobject] @{
            Url            = if ($Url) { $Url.TrimEnd('/') } elseif ($session) { $session.Url } else { $null }
            Password       = if ($Password) { $Password } elseif ($session) { $session.Password } else { $null }
            ApiKey         = if ($ApiKey) { $ApiKey } elseif ($session) { $session.ApiKey } else { $null }
            Insecure       = if ($Insecure) { $true } elseif ($session) { $session.Insecure } else { $false }
            TimeoutSeconds = if ($session) { $session.TimeoutSeconds } else { 30 }
        }
    }

    if (-not $session -or -not $session.Url) {
        throw 'No server: run Connect-HmServer first, or pass -Url and a credential.'
    }

    $headers = @{ Accept = 'application/json' }

    if ($session.ApiKey) {
        $headers['Authorization'] = 'Bearer ' + $session.ApiKey
    }
    elseif ($session.Password) {
        # Built by hand rather than with -Credential: the API takes Basic with
        # the user name Administrator, and PowerShell's own credential handling
        # would negotiate before it sent one.
        $plain = [System.Net.NetworkCredential]::new('', $session.Password).Password
        $raw = [System.Text.Encoding]::UTF8.GetBytes('Administrator:' + $plain)
        $headers['Authorization'] = 'Basic ' + [System.Convert]::ToBase64String($raw)
        $plain = $null
    }
    else {
        throw 'No credential: Connect-HmServer with a password or an API key.'
    }

    $arguments = @{
        Uri             = $session.Url + $Path
        Method          = $Method
        Headers         = $headers
        TimeoutSec      = $session.TimeoutSeconds
        ErrorAction     = 'Stop'
    }

    if ($null -ne $Body) {
        $arguments['Body'] = ($Body | ConvertTo-Json -Depth 10 -Compress)
        $arguments['ContentType'] = 'application/json'
    }

    if ($session.Insecure -and $PSVersionTable.PSVersion.Major -ge 6) {
        $arguments['SkipCertificateCheck'] = $true
    }

    try {
        Invoke-RestMethod @arguments
    }
    catch [System.Net.WebException], [Microsoft.PowerShell.Commands.HttpResponseException] {
        $status = $null
        try { $status = [int] $_.Exception.Response.StatusCode } catch { }

        $detail = $null
        try {
            $detail = ($_.ErrorDetails.Message | ConvertFrom-Json -ErrorAction Stop).error
        }
        catch {
            if ($_.ErrorDetails) { $detail = $_.ErrorDetails.Message }
        }

        switch ($status) {
            401 { throw "The server refused the credential (401). Check the administrator password or the API key." }
            403 { throw "Forbidden (403)$(if ($detail) { ': ' + $detail })." }
            404 { throw "Not found (404): $Path$(if ($detail) { ' - ' + $detail })." }
            default { throw "$Method $Path failed$(if ($status) { " ($status)" })$(if ($detail) { ': ' + $detail })." }
        }
    }
}

function Get-HmCollection {
    <#
    .SYNOPSIS
        The rows out of an answer, whether the route returns a list or an object
        holding one. Internal, but exported because every Get- here uses it.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] [AllowNull()] $Answer,
        [Parameter(Mandatory = $true)] [string] $Key
    )

    if ($null -eq $Answer) { return @() }
    if ($Answer -is [System.Array]) { return $Answer }
    if ($Answer.PSObject.Properties.Name -contains $Key) { return @($Answer.$Key) }

    foreach ($property in $Answer.PSObject.Properties) {
        if ($property.Value -is [System.Array]) { return $property.Value }
    }

    return @($Answer)
}

# ---------------------------------------------------------------------------
# The server
# ---------------------------------------------------------------------------

function Get-HmStatus {
    <#
    .SYNOPSIS
        What the server says about itself: version, database, uptime, listeners.
    #>
    [CmdletBinding()]
    param()
    Invoke-HmApi -Method GET -Path '/api/v1/status'
}

# ---------------------------------------------------------------------------
# Domains
# ---------------------------------------------------------------------------

function Get-HmDomain {
    <#
    .SYNOPSIS
        Every domain, or one of them whole.
    .EXAMPLE
        Get-HmDomain | Where-Object active -eq $false
    #>
    [CmdletBinding()]
    param(
        [Parameter(Position = 0, ValueFromPipelineByPropertyName = $true)]
        [Alias('Domain')]
        [string] $Name
    )
    process {
        if ($Name) {
            Invoke-HmApi -Method GET -Path ("/api/v1/domains/" + [uri]::EscapeDataString($Name))
        }
        else {
            Get-HmCollection -Answer (Invoke-HmApi -Method GET -Path '/api/v1/domains') -Key 'domains'
        }
    }
}

function New-HmDomain {
    <#
    .SYNOPSIS
        Adds a domain.
    .EXAMPLE
        New-HmDomain -Name example.com
    #>
    [CmdletBinding(SupportsShouldProcess = $true)]
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [string] $Name,

        # No default: a field this module did not send is a field the server
        # decides, and inventing one here would mean the two clients ask for
        # different things for the same command.
        [bool] $Active,

        [hashtable] $Property
    )

    $body = @{ name = $Name }
    if ($PSBoundParameters.ContainsKey('Active')) { $body['active'] = $Active }
    if ($Property) { foreach ($key in $Property.Keys) { $body[$key] = $Property[$key] } }

    if ($PSCmdlet.ShouldProcess($Name, 'Add domain')) {
        Invoke-HmApi -Method POST -Path '/api/v1/domains' -Body $body
    }
}

function Set-HmDomain {
    <#
    .SYNOPSIS
        Changes a domain. The server replaces the whole domain from what it is
        given, so pass every field you mean to keep, or read it first.
    .EXAMPLE
        Set-HmDomain -Name example.com -Property @{ active = $false }
    #>
    [CmdletBinding(SupportsShouldProcess = $true)]
    param(
        [Parameter(Mandatory = $true, Position = 0, ValueFromPipelineByPropertyName = $true)]
        [string] $Name,

        [Parameter(Mandatory = $true)]
        [hashtable] $Property
    )
    process {
        if ($PSCmdlet.ShouldProcess($Name, 'Change domain')) {
            Invoke-HmApi -Method PUT -Path ("/api/v1/domains/" + [uri]::EscapeDataString($Name)) -Body $Property
        }
    }
}

function Remove-HmDomain {
    <#
    .SYNOPSIS
        Deletes a domain, its accounts and their mail.
    #>
    [CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
    param(
        [Parameter(Mandatory = $true, Position = 0, ValueFromPipelineByPropertyName = $true)]
        [string] $Name
    )
    process {
        if ($PSCmdlet.ShouldProcess($Name, 'Delete domain, its accounts and their mail')) {
            Invoke-HmApi -Method DELETE -Path ("/api/v1/domains/" + [uri]::EscapeDataString($Name)) | Out-Null
        }
    }
}

# ---------------------------------------------------------------------------
# Accounts
# ---------------------------------------------------------------------------

function Get-HmAccount {
    <#
    .SYNOPSIS
        The accounts of a domain, or one account by its address.
    .DESCRIPTION
        Listed under the domain that owns them and read at their own address,
        which is how the API is shaped: an account belongs to a domain, and an
        address is unique in itself.
    .EXAMPLE
        Get-HmAccount -Domain example.com | Select-Object address, active
    #>
    [CmdletBinding(DefaultParameterSetName = 'Domain')]
    param(
        [Parameter(Mandatory = $true, Position = 0, ParameterSetName = 'Domain')]
        [string] $Domain,

        [Parameter(Mandatory = $true, ParameterSetName = 'Address', ValueFromPipelineByPropertyName = $true)]
        [string] $Address
    )
    process {
        if ($PSCmdlet.ParameterSetName -eq 'Address') {
            Invoke-HmApi -Method GET -Path ("/api/v1/accounts/" + [uri]::EscapeDataString($Address))
        }
        else {
            Get-HmCollection -Answer (Invoke-HmApi -Method GET -Path ("/api/v1/domains/" + [uri]::EscapeDataString($Domain) + "/accounts")) -Key 'accounts'
        }
    }
}

function New-HmAccount {
    <#
    .SYNOPSIS
        Adds a mailbox.
    .EXAMPLE
        New-HmAccount -Domain example.com -Address ann@example.com -Password (Read-Host -AsSecureString)
    #>
    [CmdletBinding(SupportsShouldProcess = $true)]
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [string] $Domain,

        [Parameter(Mandatory = $true, Position = 1)]
        [string] $Address,

        [Parameter(Mandatory = $true, Position = 2)]
        [object] $Password,

        [bool] $Active,

        [hashtable] $Property
    )

    $plain = if ($Password -is [System.Security.SecureString]) {
        [System.Net.NetworkCredential]::new('', $Password).Password
    }
    else {
        [string] $Password
    }

    $body = @{ address = $Address; password = $plain }
    if ($PSBoundParameters.ContainsKey('Active')) { $body['active'] = $Active }
    if ($Property) { foreach ($key in $Property.Keys) { $body[$key] = $Property[$key] } }

    if ($PSCmdlet.ShouldProcess($Address, 'Add account')) {
        Invoke-HmApi -Method POST -Path ("/api/v1/domains/" + [uri]::EscapeDataString($Domain) + "/accounts") -Body $body
    }
}

function Set-HmAccount {
    <#
    .SYNOPSIS
        Changes an account.
    .EXAMPLE
        Set-HmAccount -Address ann@example.com -Property @{ max_size_mb = 500 }
    #>
    [CmdletBinding(SupportsShouldProcess = $true)]
    param(
        [Parameter(Mandatory = $true, Position = 0, ValueFromPipelineByPropertyName = $true)]
        [string] $Address,

        [Parameter(Mandatory = $true)]
        [hashtable] $Property
    )
    process {
        if ($PSCmdlet.ShouldProcess($Address, 'Change account')) {
            Invoke-HmApi -Method PUT -Path ("/api/v1/accounts/" + [uri]::EscapeDataString($Address)) -Body $Property
        }
    }
}

function Remove-HmAccount {
    <#
    .SYNOPSIS
        Deletes an account and its mail.
    #>
    [CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
    param(
        [Parameter(Mandatory = $true, Position = 0, ValueFromPipelineByPropertyName = $true)]
        [string] $Address
    )
    process {
        if ($PSCmdlet.ShouldProcess($Address, 'Delete account and its mail')) {
            Invoke-HmApi -Method DELETE -Path ("/api/v1/accounts/" + [uri]::EscapeDataString($Address)) | Out-Null
        }
    }
}

# ---------------------------------------------------------------------------
# Aliases, groups
# ---------------------------------------------------------------------------

function Get-HmAlias {
    <#
    .SYNOPSIS
        A domain's address aliases.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [string] $Domain
    )
    Get-HmCollection -Answer (Invoke-HmApi -Method GET -Path ("/api/v1/domains/" + [uri]::EscapeDataString($Domain) + "/aliases")) -Key 'aliases'
}

function New-HmAlias {
    <#
    .SYNOPSIS
        Adds an alias: one address answering for another.
    #>
    [CmdletBinding(SupportsShouldProcess = $true)]
    param(
        [Parameter(Mandatory = $true, Position = 0)] [string] $Domain,
        [Parameter(Mandatory = $true, Position = 1)] [string] $Name,
        [Parameter(Mandatory = $true, Position = 2)] [string] $Value,
        [bool] $Active
    )
    $body = @{ name = $Name; value = $Value }
    if ($PSBoundParameters.ContainsKey('Active')) { $body['active'] = $Active }
    if ($PSCmdlet.ShouldProcess("$Name -> $Value", 'Add alias')) {
        Invoke-HmApi -Method POST -Path ("/api/v1/domains/" + [uri]::EscapeDataString($Domain) + "/aliases") -Body $body
    }
}

function Remove-HmAlias {
    <#
    .SYNOPSIS
        Deletes an alias by its address.
    #>
    [CmdletBinding(SupportsShouldProcess = $true)]
    param(
        [Parameter(Mandatory = $true, Position = 0, ValueFromPipelineByPropertyName = $true)]
        [Alias('Name')]
        [string] $Address
    )
    process {
        if ($PSCmdlet.ShouldProcess($Address, 'Delete alias')) {
            Invoke-HmApi -Method DELETE -Path ("/api/v1/aliases/" + [uri]::EscapeDataString($Address)) | Out-Null
        }
    }
}

function Get-HmGroup {
    <#
    .SYNOPSIS
        The account groups, which a folder permission can name as one principal.
    #>
    [CmdletBinding()]
    param([Parameter(Position = 0)] [int] $Id)
    if ($Id) {
        Invoke-HmApi -Method GET -Path "/api/v1/groups/$Id"
    }
    else {
        Get-HmCollection -Answer (Invoke-HmApi -Method GET -Path '/api/v1/groups') -Key 'groups'
    }
}

function New-HmGroup {
    <#
    .SYNOPSIS
        Adds an account group.
    #>
    [CmdletBinding(SupportsShouldProcess = $true)]
    param([Parameter(Mandatory = $true, Position = 0)] [string] $Name)
    if ($PSCmdlet.ShouldProcess($Name, 'Add group')) {
        Invoke-HmApi -Method POST -Path '/api/v1/groups' -Body @{ name = $Name }
    }
}

function Remove-HmGroup {
    <#
    .SYNOPSIS
        Deletes an account group. The accounts themselves are untouched.
    #>
    [CmdletBinding(SupportsShouldProcess = $true)]
    param([Parameter(Mandatory = $true, Position = 0, ValueFromPipelineByPropertyName = $true)] [int] $Id)
    process {
        if ($PSCmdlet.ShouldProcess("group $Id", 'Delete group')) {
            Invoke-HmApi -Method DELETE -Path "/api/v1/groups/$Id" | Out-Null
        }
    }
}

function Get-HmGroupMember {
    <#
    .SYNOPSIS
        The accounts in a group.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory = $true, Position = 0)] [int] $Id)
    Get-HmCollection -Answer (Invoke-HmApi -Method GET -Path "/api/v1/groups/$Id/members") -Key 'members'
}

function Add-HmGroupMember {
    <#
    .SYNOPSIS
        Puts an account in a group, by address or by account id.
    #>
    [CmdletBinding(SupportsShouldProcess = $true)]
    param(
        [Parameter(Mandatory = $true, Position = 0)] [int] $Id,
        [Parameter(Mandatory = $true, Position = 1, ValueFromPipelineByPropertyName = $true)] [string] $Account
    )
    process {
        $body = if ($Account -match '@') { @{ address = $Account } } else { @{ account_id = [int] $Account } }
        if ($PSCmdlet.ShouldProcess("$Account into group $Id", 'Add group member')) {
            Invoke-HmApi -Method POST -Path "/api/v1/groups/$Id/members" -Body $body
        }
    }
}

function Remove-HmGroupMember {
    <#
    .SYNOPSIS
        Takes an account out of a group.
    #>
    [CmdletBinding(SupportsShouldProcess = $true)]
    param(
        [Parameter(Mandatory = $true, Position = 0)] [int] $Id,
        [Parameter(Mandatory = $true, Position = 1, ValueFromPipelineByPropertyName = $true)] [string] $Account
    )
    process {
        if ($PSCmdlet.ShouldProcess("$Account out of group $Id", 'Remove group member')) {
            Invoke-HmApi -Method DELETE -Path ("/api/v1/groups/$Id/members/" + [uri]::EscapeDataString($Account)) | Out-Null
        }
    }
}

# ---------------------------------------------------------------------------
# Settings
# ---------------------------------------------------------------------------

$script:SettingGroups = @('settings', 'antispam', 'antivirus', 'logging', 'backup', 'cache', 'indexing', 'scripting', 'directories')

function Get-HmSettingPath {
    param([string] $Group)
    if (-not $Group -or $Group -eq 'settings') { return '/api/v1/settings' }
    return "/api/v1/settings/$Group"
}

function Get-HmSetting {
    <#
    .SYNOPSIS
        A settings group whole, or one setting out of it.
    .EXAMPLE
        Get-HmSetting -Group antispam
    .EXAMPLE
        (Get-HmSetting).max_message_size_kb
    #>
    [CmdletBinding()]
    param(
        [Parameter(Position = 0)]
        [ValidateSet('settings', 'antispam', 'antivirus', 'logging', 'backup', 'cache', 'indexing', 'scripting', 'directories')]
        [string] $Group = 'settings',

        [Parameter(Position = 1)]
        [string] $Name
    )

    $answer = Invoke-HmApi -Method GET -Path (Get-HmSettingPath -Group $Group)

    if ($Name) {
        if ($answer.PSObject.Properties.Name -notcontains $Name) {
            throw "No setting '$Name' in the $Group group."
        }
        return $answer.$Name
    }

    $answer
}

function Set-HmSetting {
    <#
    .SYNOPSIS
        Changes settings in a group. Any subset of its keys; the server
        validates them as the Control Panel does and applies them only when
        every one is accepted.
    .EXAMPLE
        Set-HmSetting -Property @{ max_message_size_kb = 51200 }
    .EXAMPLE
        Set-HmSetting -Group antispam -Property @{ use_spf = $true; spam_mark_threshold = 5 }
    #>
    [CmdletBinding(SupportsShouldProcess = $true)]
    param(
        [Parameter(Position = 0)]
        [ValidateSet('settings', 'antispam', 'antivirus', 'logging', 'backup', 'cache', 'indexing', 'scripting', 'directories')]
        [string] $Group = 'settings',

        [Parameter(Mandatory = $true)]
        [hashtable] $Property
    )

    if ($PSCmdlet.ShouldProcess("the $Group settings", 'Change ' + ($Property.Keys -join ', '))) {
        Invoke-HmApi -Method PUT -Path (Get-HmSettingPath -Group $Group) -Body $Property
    }
}

function Get-HmIniSetting {
    <#
    .SYNOPSIS
        The hMailServer.ini keys the server exposes, or one of them.
    #>
    [CmdletBinding()]
    param([Parameter(Position = 0)] [string] $Name)
    if ($Name) {
        Invoke-HmApi -Method GET -Path ("/api/v1/settings/ini/" + [uri]::EscapeDataString($Name))
    }
    else {
        Get-HmCollection -Answer (Invoke-HmApi -Method GET -Path '/api/v1/settings/ini') -Key 'settings'
    }
}

function Set-HmIniSetting {
    <#
    .SYNOPSIS
        Writes an hMailServer.ini key. Some take effect only on a restart; the
        server's OpenAPI document says which.
    #>
    [CmdletBinding(SupportsShouldProcess = $true)]
    param(
        [Parameter(Mandatory = $true, Position = 0)] [string] $Name,
        [Parameter(Mandatory = $true, Position = 1)] [string] $Value
    )
    if ($PSCmdlet.ShouldProcess($Name, "Set to $Value")) {
        Invoke-HmApi -Method PUT -Path ("/api/v1/settings/ini/" + [uri]::EscapeDataString($Name)) -Body @{ value = $Value }
    }
}

# ---------------------------------------------------------------------------
# The queue, held mail, the logs, the backup
# ---------------------------------------------------------------------------

function Get-HmQueue {
    <#
    .SYNOPSIS
        The delivery queue.
    .EXAMPLE
        Get-HmQueue | Where-Object retry_count -gt 3 | Format-Table
    #>
    [CmdletBinding()]
    param()
    Get-HmCollection -Answer (Invoke-HmApi -Method GET -Path '/api/v1/queue') -Key 'messages'
}

function Start-HmQueueDelivery {
    <#
    .SYNOPSIS
        Retries a queued message now rather than at its next try.
    #>
    [CmdletBinding(SupportsShouldProcess = $true)]
    param([Parameter(Mandatory = $true, Position = 0, ValueFromPipelineByPropertyName = $true)] [int] $Id)
    process {
        if ($PSCmdlet.ShouldProcess("message $Id", 'Retry delivery now')) {
            Invoke-HmApi -Method POST -Path "/api/v1/queue/$Id/retry"
        }
    }
}

function Remove-HmQueueMessage {
    <#
    .SYNOPSIS
        Takes a message out of the delivery queue. It is not delivered and not
        bounced.
    #>
    [CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
    param([Parameter(Mandatory = $true, Position = 0, ValueFromPipelineByPropertyName = $true)] [int] $Id)
    process {
        if ($PSCmdlet.ShouldProcess("message $Id", 'Delete from the queue')) {
            Invoke-HmApi -Method DELETE -Path "/api/v1/queue/$Id" | Out-Null
        }
    }
}

function Get-HmQuarantine {
    <#
    .SYNOPSIS
        The held mail.
    #>
    [CmdletBinding()]
    param()
    Get-HmCollection -Answer (Invoke-HmApi -Method GET -Path '/api/v1/quarantine') -Key 'messages'
}

function Restore-HmQuarantineMessage {
    <#
    .SYNOPSIS
        Releases a held message to its original recipients. Delivery is direct
        rather than back through the filters: a release is an administrator
        overruling them.
    #>
    [CmdletBinding(SupportsShouldProcess = $true)]
    param([Parameter(Mandatory = $true, Position = 0, ValueFromPipelineByPropertyName = $true)] [int] $Id)
    process {
        if ($PSCmdlet.ShouldProcess("held message $Id", 'Release to its recipients')) {
            Invoke-HmApi -Method POST -Path "/api/v1/quarantine/$Id/release"
        }
    }
}

function Get-HmLog {
    <#
    .SYNOPSIS
        The log files, or the tail of one.
    .EXAMPLE
        Get-HmLog
    .EXAMPLE
        Get-HmLog -Name hmailserver_2026-09-15.log -Lines 200
    #>
    [CmdletBinding()]
    param(
        [Parameter(Position = 0)] [string] $Name,
        [int] $Lines = 50
    )
    if ($Name) {
        $answer = Invoke-HmApi -Method GET -Path ("/api/v1/logs/" + [uri]::EscapeDataString($Name) + "?lines=$Lines")
        if ($answer.PSObject.Properties.Name -contains 'lines') { return $answer.lines }
        return $answer
    }
    Get-HmCollection -Answer (Invoke-HmApi -Method GET -Path '/api/v1/logs') -Key 'logs'
}

function Get-HmBackup {
    <#
    .SYNOPSIS
        What the server's own backup is doing, and when it last ran.
    #>
    [CmdletBinding()]
    param()
    Invoke-HmApi -Method GET -Path '/api/v1/backup'
}

function Start-HmBackup {
    <#
    .SYNOPSIS
        Starts the server's own backup. It runs in the server; this returns as
        soon as it has begun.
    #>
    [CmdletBinding(SupportsShouldProcess = $true)]
    param()
    if ($PSCmdlet.ShouldProcess('the server', 'Start a backup')) {
        Invoke-HmApi -Method POST -Path '/api/v1/backup' -Body @{}
    }
}

function Test-HmRuleCriterion {
    <#
    .SYNOPSIS
        Asks the server what a rule criterion would decide, without a rule.
    .EXAMPLE
        Test-HmRuleCriterion -Type contains -Value viagra -Against 'cheap viagra here'
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [ValidateSet('equals', 'contains', 'less_than', 'greater_than', 'regex', 'not_contains', 'not_equals', 'wildcard')]
        [string] $Type,

        [Parameter(Mandatory = $true, Position = 1)] [string] $Value,
        [Parameter(Mandatory = $true, Position = 2)] [string] $Against
    )

    $answer = Invoke-HmApi -Method POST -Path '/api/v1/rules/match' -Body @{
        match_value = $Value
        match_type  = $Type
        test_value  = $Against
    }

    [bool] $answer.match
}

# ---------------------------------------------------------------------------
# Accounts in bulk
# ---------------------------------------------------------------------------

function Import-HmAccount {
    <#
    .SYNOPSIS
        Makes accounts from a CSV file, with a dry run.
    .DESCRIPTION
        The header row names the fields; address is the only one required, and
        anything the account resource takes may be a column. An address that
        already exists is left alone and counted, because an import that
        silently overwrote a password would lock a hundred people out with one
        command.
    .EXAMPLE
        Import-HmAccount -Path .\new-people.csv -WhatIf
    #>
    [CmdletBinding(SupportsShouldProcess = $true)]
    param(
        [Parameter(Mandatory = $true, Position = 0)] [string] $Path,
        [string[]] $Domain
    )

    $rows = @(Import-Csv -Path $Path)
    if (-not $rows) { throw "$Path has no rows under its header." }
    if ($rows[0].PSObject.Properties.Name -notcontains 'address') {
        throw "$Path has no 'address' column; its columns are: " + ($rows[0].PSObject.Properties.Name -join ', ')
    }

    $domains = if ($Domain) { $Domain } else {
        $rows | ForEach-Object { if ($_.address -match '@') { ($_.address -split '@', 2)[1] } } | Sort-Object -Unique
    }

    $existing = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($name in $domains) {
        try {
            foreach ($account in (Get-HmAccount -Domain $name)) { [void] $existing.Add([string] $account.address) }
        }
        catch {
            Write-Verbose "No accounts read for $name : $_"
        }
    }

    $made = @(); $skipped = @(); $failed = @()

    foreach ($row in $rows) {
        $address = ([string] $row.address).Trim()
        if (-not $address) { $failed += [pscustomobject] @{ Address = ''; Error = 'no address' }; continue }
        if ($existing.Contains($address)) { $skipped += $address; continue }

        $body = @{}
        foreach ($property in $row.PSObject.Properties) {
            $value = [string] $property.Value
            if ($null -eq $property.Value -or $value -eq '') { continue }
            $body[$property.Name] = switch -regex ($value.Trim().ToLowerInvariant()) {
                '^(true|yes|on)$'  { $true; break }
                '^(false|no|off)$' { $false; break }
                '^-?\d+$'          { if ($property.Name -eq 'address') { $value } else { [int] $value }; break }
                default            { $value }
            }
        }

        if (-not $PSCmdlet.ShouldProcess($address, 'Add account')) { $made += $address; continue }

        try {
            Invoke-HmApi -Method POST -Path ("/api/v1/domains/" + [uri]::EscapeDataString(($address -split '@', 2)[1]) + "/accounts") -Body $body | Out-Null
            $made += $address
        }
        catch {
            $failed += [pscustomobject] @{ Address = $address; Error = "$_" }
        }
    }

    [pscustomobject] @{
        Made    = $made
        Skipped = $skipped
        Failed  = $failed
    }
}

function Export-HmAccount {
    <#
    .SYNOPSIS
        Writes a domain's accounts as a CSV file, or to the pipeline.
    .DESCRIPTION
        No password column: the server does not have the passwords to give, and
        a column of empty passwords re-imported would be a column of accounts
        nobody can sign in to.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true, Position = 0)] [string] $Domain,
        [Parameter(Position = 1)] [string] $Path
    )

    $accounts = Get-HmAccount -Domain $Domain |
        Select-Object address, name, active, max_size_mb, admin_level

    if ($Path) {
        $accounts | Export-Csv -Path $Path -NoTypeInformation -Encoding utf8
    }
    else {
        $accounts
    }
}

# ---------------------------------------------------------------------------
# The whole configuration as one document
# ---------------------------------------------------------------------------

# The sections a document holds, in the order they are applied: a domain before
# the accounts in it. The same list hmctl keeps, and build/check-cli.py holds
# the two to the same requests.
$script:ConfigurationSections = @(
    @{ Name = 'domains';  Collection = '/api/v1/domains';                     Item = '/api/v1/domains/{0}';  Key = 'name';    Parent = $null },
    @{ Name = 'accounts'; Collection = '/api/v1/domains/{1}/accounts';        Item = '/api/v1/accounts/{0}'; Key = 'address'; Parent = 'domain' },
    @{ Name = 'aliases';  Collection = '/api/v1/domains/{1}/aliases';         Item = '/api/v1/aliases/{0}';  Key = 'name';    Parent = 'domain' },
    @{ Name = 'lists';    Collection = '/api/v1/domains/{1}/lists';           Item = '/api/v1/lists/{0}';    Key = 'address'; Parent = 'domain' },
    @{ Name = 'groups';   Collection = '/api/v1/groups';                      Item = '/api/v1/groups/{0}';   Key = 'name';    Parent = $null },
    @{ Name = 'rules';    Collection = '/api/v1/rules';                       Item = '/api/v1/rules/{0}';    Key = 'name';    Parent = $null },
    @{ Name = 'routes';   Collection = '/api/v1/routes';                      Item = '/api/v1/routes/{0}';   Key = 'domain_name'; Parent = $null },
    @{ Name = 'ports';    Collection = '/api/v1/ports';                       Item = '/api/v1/ports/{0}';    Key = 'port';    Parent = $null },
    @{ Name = 'ipranges'; Collection = '/api/v1/ipranges';                    Item = '/api/v1/ipranges/{0}'; Key = 'name';    Parent = $null }
)

# What the server allocated or computed rather than what was configured. None of
# it travels into a document, and none of it is compared.
$script:NotConfiguration = @(
    'id', 'account_id', 'domain_id', 'rule_id', 'route_id', 'parent_id',
    'account_count', 'alias_count', 'member_count', 'size_mb', 'size',
    'created', 'modified', 'last_used', 'last_login', 'last_logon'
)

function ConvertTo-HmConfigurationRow {
    param([Parameter(Mandatory = $true)] $Row)
    $ordered = [ordered] @{}
    foreach ($property in ($Row.PSObject.Properties | Sort-Object Name)) {
        if ($script:NotConfiguration -contains $property.Name) { continue }
        if ($property.Name -like '*_id') { continue }
        $ordered[$property.Name] = $property.Value
    }
    $ordered
}

function Export-HmConfiguration {
    <#
    .SYNOPSIS
        The whole configuration as one object, or one file.
    .DESCRIPTION
        The settings groups, the ini keys the server exposes, the domains and
        their accounts, aliases and distribution lists, the groups, rules,
        routes, listeners and IP ranges. Sorted throughout and carrying nothing
        the server allocated, so two exports of an unchanged server are the same
        bytes - which is what makes a document usable in a repository.

        No passwords: the server does not give them out. No certificates and no
        directories: files and paths of one machine.
    .EXAMPLE
        Export-HmConfiguration -Path .\server.json
    #>
    [CmdletBinding()]
    param([Parameter(Position = 0)] [string] $Path)

    $settings = [ordered] @{}
    foreach ($group in ($script:SettingGroups | Sort-Object)) {
        if ($group -eq 'directories') { continue }
        try {
            $answer = Invoke-HmApi -Method GET -Path (Get-HmSettingPath -Group $group)
        }
        catch {
            continue
        }
        $settings[$group] = ConvertTo-HmConfigurationRow -Row $answer
    }

    $ini = [ordered] @{}
    try {
        foreach ($row in (Get-HmIniSetting | Sort-Object name)) {
            if ($row.PSObject.Properties.Name -contains 'name') { $ini[[string] $row.name] = $row.value }
        }
    }
    catch { }

    $sections = [ordered] @{ settings = $settings; ini = $ini }

    $domains = @()
    foreach ($section in $script:ConfigurationSections) {
        $rows = @()
        if ($section.Parent -eq 'domain') {
            foreach ($domain in $domains) {
                try {
                    $answer = Invoke-HmApi -Method GET -Path ($section.Collection -f '', [uri]::EscapeDataString($domain))
                }
                catch { continue }
                foreach ($row in (Get-HmCollection -Answer $answer -Key $section.Name)) {
                    $entry = ConvertTo-HmConfigurationRow -Row $row
                    $entry['domain'] = $domain
                    $rows += [pscustomobject] $entry
                }
            }
        }
        else {
            try {
                $answer = Invoke-HmApi -Method GET -Path $section.Collection
            }
            catch { continue }
            foreach ($row in (Get-HmCollection -Answer $answer -Key $section.Name)) {
                $rows += [pscustomobject] (ConvertTo-HmConfigurationRow -Row $row)
            }
        }

        $rows = @($rows | Sort-Object { $_ | ConvertTo-Json -Depth 10 -Compress })
        $sections[$section.Name] = $rows

        if ($section.Name -eq 'domains') {
            $domains = @($rows | ForEach-Object { [string] $_.name } | Where-Object { $_ } | Sort-Object)
        }
    }

    $document = [ordered] @{ version = 1; sections = $sections }

    if ($Path) {
        ($document | ConvertTo-Json -Depth 20) | Set-Content -Path $Path -Encoding utf8
        return
    }

    $document
}

function Compare-HmConfiguration {
    <#
    .SYNOPSIS
        What would have to change for the server to match a document.
    .DESCRIPTION
        Answers one object per step - Section, Verb, Key, Detail - so that
        Where-Object and Measure-Object work on the plan. Nothing is changed.
    .EXAMPLE
        Compare-HmConfiguration -Path .\server.json | Where-Object Verb -eq delete
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory = $true, Position = 0)] [string] $Path)

    $wanted = Get-Content -Path $Path -Raw | ConvertFrom-Json
    $live = Export-HmConfiguration

    $plan = @()

    foreach ($group in $wanted.sections.settings.PSObject.Properties.Name) {
        $here = $live.sections.settings.$group
        $there = $wanted.sections.settings.$group
        foreach ($key in ($there.PSObject.Properties.Name | Sort-Object)) {
            $before = if ($here) { $here.$key } else { $null }
            if ($before -ne $there.$key) {
                $plan += [pscustomobject] @{ Section = "settings/$group"; Verb = 'set'; Key = $key
                                             Detail = "$before -> $($there.$key)" }
            }
        }
    }

    foreach ($key in ($wanted.sections.ini.PSObject.Properties.Name | Sort-Object)) {
        $before = $live.sections.ini.$key
        if ($before -ne $wanted.sections.ini.$key) {
            $plan += [pscustomobject] @{ Section = 'ini'; Verb = 'set'; Key = $key
                                         Detail = "$before -> $($wanted.sections.ini.$key)" }
        }
    }

    foreach ($section in $script:ConfigurationSections) {
        $here = @{}
        foreach ($row in @($live.sections.($section.Name))) {
            if ($row) { $here[[string] $row.($section.Key)] = $row }
        }
        $there = @{}
        foreach ($row in @($wanted.sections.($section.Name))) {
            if ($row) { $there[[string] $row.($section.Key)] = $row }
        }

        foreach ($key in ($there.Keys | Sort-Object)) {
            if (-not $here.ContainsKey($key)) {
                $plan += [pscustomobject] @{ Section = $section.Name; Verb = 'create'; Key = $key; Detail = '' }
            }
            elseif (($here[$key] | ConvertTo-Json -Depth 10 -Compress) -ne ($there[$key] | ConvertTo-Json -Depth 10 -Compress)) {
                $plan += [pscustomobject] @{ Section = $section.Name; Verb = 'update'; Key = $key; Detail = '' }
            }
        }

        foreach ($key in ($here.Keys | Sort-Object)) {
            if (-not $there.ContainsKey($key)) {
                $plan += [pscustomobject] @{ Section = $section.Name; Verb = 'delete'; Key = $key; Detail = '' }
            }
        }
    }

    $plan
}

function Import-HmConfiguration {
    <#
    .SYNOPSIS
        Makes the server match a document.
    .DESCRIPTION
        A plan unless -Force, and a deletion needs -AllowDelete on top of that:
        the common case is a document written from one server and applied to
        another that has things of its own, and silently removing them is not a
        thing a configuration tool may do.
    .EXAMPLE
        Import-HmConfiguration -Path .\server.json -WhatIf
    #>
    [CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
    param(
        [Parameter(Mandatory = $true, Position = 0)] [string] $Path,
        [switch] $Force,
        [switch] $AllowDelete
    )

    $wanted = Get-Content -Path $Path -Raw | ConvertFrom-Json
    $plan = @(Compare-HmConfiguration -Path $Path)

    $deletions = @($plan | Where-Object Verb -eq 'delete')
    if (-not $AllowDelete) { $plan = @($plan | Where-Object Verb -ne 'delete') }

    if (-not $Force) {
        if ($deletions -and -not $AllowDelete) {
            Write-Warning ("{0} entr(ies) on the server are not in the document. They are left alone; -AllowDelete removes them." -f $deletions.Count)
        }
        if ($plan) { Write-Warning 'Nothing was changed. Add -Force to apply this plan.' }
        return $plan
    }

    foreach ($step in $plan) {
        if (-not $PSCmdlet.ShouldProcess("$($step.Section) $($step.Key)", $step.Verb)) { continue }

        if ($step.Section -like 'settings/*') {
            $group = $step.Section.Split('/')[1]
            $value = $wanted.sections.settings.$group.($step.Key)
            Invoke-HmApi -Method PUT -Path (Get-HmSettingPath -Group $group) -Body @{ $step.Key = $value } | Out-Null
            continue
        }

        if ($step.Section -eq 'ini') {
            Set-HmIniSetting -Name $step.Key -Value ([string] $wanted.sections.ini.($step.Key)) -Confirm:$false | Out-Null
            continue
        }

        $section = $script:ConfigurationSections | Where-Object Name -eq $step.Section | Select-Object -First 1
        $row = @($wanted.sections.($step.Section)) | Where-Object { [string] $_.($section.Key) -eq $step.Key } | Select-Object -First 1

        if ($step.Verb -eq 'delete') {
            Invoke-HmApi -Method DELETE -Path ($section.Item -f [uri]::EscapeDataString($step.Key)) | Out-Null
            continue
        }

        $body = @{}
        foreach ($property in $row.PSObject.Properties) {
            if ($property.Name -eq 'domain') { continue }
            $body[$property.Name] = $property.Value
        }

        if ($step.Verb -eq 'create') {
            $path = $section.Collection
            if ($section.Parent -eq 'domain') {
                $path = $section.Collection -f '', [uri]::EscapeDataString([string] $row.domain)
            }
            Invoke-HmApi -Method POST -Path $path -Body $body | Out-Null
        }
        else {
            Invoke-HmApi -Method PUT -Path ($section.Item -f [uri]::EscapeDataString($step.Key)) -Body $body | Out-Null
        }
    }

    $plan
}

# ---------------------------------------------------------------------------
# Completion: a domain or an address completes from the server itself
# ---------------------------------------------------------------------------

$script:CompleterDomain = {
    param($commandName, $parameterName, $wordToComplete, $commandAst, $fakeBoundParameters)
    if (-not $script:Session) { return }
    try {
        Get-HmDomain |
            Where-Object { $_.name -like "$wordToComplete*" } |
            ForEach-Object { [System.Management.Automation.CompletionResult]::new($_.name, $_.name, 'ParameterValue', $_.name) }
    }
    catch { }
}

$script:CompleterAddress = {
    param($commandName, $parameterName, $wordToComplete, $commandAst, $fakeBoundParameters)
    if (-not $script:Session) { return }
    try {
        $domains = if ($fakeBoundParameters.ContainsKey('Domain')) { @($fakeBoundParameters['Domain']) } else { (Get-HmDomain).name }
        foreach ($domain in $domains) {
            Get-HmAccount -Domain $domain |
                Where-Object { $_.address -like "$wordToComplete*" } |
                ForEach-Object { [System.Management.Automation.CompletionResult]::new($_.address, $_.address, 'ParameterValue', $_.address) }
        }
    }
    catch { }
}

Register-ArgumentCompleter -CommandName Get-HmAccount, New-HmAccount, Get-HmAlias, New-HmAlias, Export-HmAccount, Get-HmDomain, Set-HmDomain, Remove-HmDomain -ParameterName Domain -ScriptBlock $script:CompleterDomain
Register-ArgumentCompleter -CommandName Get-HmDomain, Set-HmDomain, Remove-HmDomain -ParameterName Name -ScriptBlock $script:CompleterDomain
Register-ArgumentCompleter -CommandName Get-HmAccount, Set-HmAccount, Remove-HmAccount -ParameterName Address -ScriptBlock $script:CompleterAddress

Export-ModuleMember -Function @(
    'Connect-HmServer', 'Disconnect-HmServer', 'Get-HmSession', 'Invoke-HmApi', 'Get-HmCollection',
    'Get-HmStatus',
    'Get-HmDomain', 'New-HmDomain', 'Set-HmDomain', 'Remove-HmDomain',
    'Get-HmAccount', 'New-HmAccount', 'Set-HmAccount', 'Remove-HmAccount',
    'Get-HmAlias', 'New-HmAlias', 'Remove-HmAlias',
    'Get-HmGroup', 'New-HmGroup', 'Remove-HmGroup', 'Get-HmGroupMember', 'Add-HmGroupMember', 'Remove-HmGroupMember',
    'Get-HmSetting', 'Set-HmSetting', 'Get-HmIniSetting', 'Set-HmIniSetting',
    'Get-HmQueue', 'Start-HmQueueDelivery', 'Remove-HmQueueMessage',
    'Get-HmQuarantine', 'Restore-HmQuarantineMessage',
    'Get-HmLog', 'Get-HmBackup', 'Start-HmBackup', 'Test-HmRuleCriterion',
    'Import-HmAccount', 'Export-HmAccount',
    'Export-HmConfiguration', 'Compare-HmConfiguration', 'Import-HmConfiguration',
    'ConvertTo-HmConfigurationRow', 'Get-HmSettingPath'
)

# Dotty shell integration for PowerShell 5+ with PSReadLine 2.2+.
# Explicit opt-in: dot-source this file in the current session; it never edits a profile.
# Unsupported hosts (no PSReadLine 2.2, non-console host, constrained language mode) get a silent
# no-op, and nothing in this file is allowed to fail shell startup.
try {
    if ($global:DottyShellIntegrationInstalled) { return }
    if ($env:TERM -eq 'dumb') { return }
    if ($PSVersionTable.PSVersion.Major -lt 5) { return }
    if ($Host.Name -ne 'ConsoleHost') { return }
    if ($ExecutionContext.SessionState.LanguageMode -ne 'FullLanguage') { return }
    $dottyReadLine = @(Get-Module PSReadLine -ErrorAction SilentlyContinue) | Select-Object -First 1
    if (-not $dottyReadLine) { return }
    if ($dottyReadLine.Version -lt [version]'2.2') { return }
    if (-not (Get-Command Set-PSReadLineOption -ErrorAction SilentlyContinue)) { return }

    ## The opt-in file may be dot-sourced more than once. Keep the command marker
    ## and the original prompt/history callbacks intact on subsequent loads.

    $global:DottyShellIntegrationCommandActive = $false
    $script:DottyPreviousPrompt = (Get-Item Function:prompt -ErrorAction SilentlyContinue).ScriptBlock
    $script:DottyPreviousHistoryHandler = (Get-PSReadLineOption).AddToHistoryHandler

    # Builds the OSC 7 payload by hand (no [Uri]: it does not re-encode '%' and throws for
    # non-filesystem providers). Everything outside [A-Za-z0-9/._~-] becomes %XX of the UTF-8 bytes.
    $script:DottyOsc7Payload = {
        $location = Get-Location
        if ($location.Provider.Name -ne 'FileSystem') { return $null }
        $path = $location.ProviderPath
        if ([string]::IsNullOrEmpty($path)) { return $null }
        $isWindowsPath = [System.IO.Path]::DirectorySeparatorChar -eq '\'
        $hostName = ''
        if ($isWindowsPath) {
            $path = $path.Replace('\', '/')
            if ($path.StartsWith('//')) {
                $slash = $path.IndexOf('/', 2)
                if ($slash -lt 0) { $hostName = $path.Substring(2); $path = '/' }
                else { $hostName = $path.Substring(2, $slash - 2); $path = $path.Substring($slash) }
            }
            elseif ($path -match '^[A-Za-z]:') {
                $path = '/' + $path
            }
        }
        $encoded = New-Object System.Text.StringBuilder
        $prefixLength = 0
        if ($isWindowsPath -and $path -match '^/[A-Za-z]:') { $prefixLength = 3 }
        for ($i = 0; $i -lt $prefixLength; $i++) { [void]$encoded.Append($path[$i]) }
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($path.Substring($prefixLength))
        foreach ($b in $bytes) {
            if (($b -ge 0x30 -and $b -le 0x39) -or ($b -ge 0x41 -and $b -le 0x5A) -or ($b -ge 0x61 -and $b -le 0x7A) -or
                $b -eq 0x2F -or $b -eq 0x2E -or $b -eq 0x5F -or $b -eq 0x7E -or $b -eq 0x2D) {
                [void]$encoded.Append([char]$b)
            }
            else {
                [void]$encoded.Append('%').Append($b.ToString('X2'))
            }
        }
        return "file://$hostName$($encoded.ToString())"
    }

    $script:DottyPromptBlock = {
        $commandSucceeded = $?
        try {
            if ($global:DottyShellIntegrationCommandActive) {
                if ($commandSucceeded) { $code = 0 }
                elseif ($null -ne $LASTEXITCODE) { $code = $LASTEXITCODE }
                else { $code = 1 }
                $global:DottyShellIntegrationCommandActive = $false
                [Console]::Write("$([char]27)]133;D;$code$([char]27)\")
            }
            [Console]::Write("$([char]27)]133;A$([char]27)\")
            $uri = & $script:DottyOsc7Payload
            if ($uri) { [Console]::Write("$([char]27)]7;$uri$([char]27)\") }
        }
        catch { }
        if ($script:DottyPreviousPrompt) { & $script:DottyPreviousPrompt } else { "PS $((Get-Location).Path)> " }
    }.GetNewClosure()
    Set-Item Function:global:prompt -Value $script:DottyPromptBlock
    Set-PSReadLineOption -AddToHistoryHandler {
        param($line)
        if ($line.Trim()) {
            [Console]::Write("$([char]27)]133;B$([char]27)\")
            [Console]::Write("$([char]27)]133;C$([char]27)\")
            $global:DottyShellIntegrationCommandActive = $true
        }
        if ($script:DottyPreviousHistoryHandler -is [scriptblock]) {
            return [bool](& $script:DottyPreviousHistoryHandler $line)
        }
        if ($null -ne $script:DottyPreviousHistoryHandler) {
            return [bool]$script:DottyPreviousHistoryHandler.Invoke($line)
        }
        return $true
    }

    $global:DottyShellIntegrationInstalled = $true
    Remove-Variable dottyReadLine -ErrorAction SilentlyContinue
}
catch { }

# Dotty shell integration for PowerShell 7+ with PSReadLine 2.2+.
# Explicit opt-in: dot-source this file in the current session; it never edits a profile.
if ($global:DottyShellIntegrationInstalled) { return }

## The opt-in file may be dot-sourced more than once. Keep the command marker
## and the original prompt/history callbacks intact on subsequent loads.

$global:DottyShellIntegrationCommandActive = $false
$script:DottyPreviousPrompt = (Get-Item Function:prompt -ErrorAction SilentlyContinue).ScriptBlock
$script:DottyPreviousHistoryHandler = (Get-PSReadLineOption).AddToHistoryHandler
$script:DottyPromptBlock = {
    $commandSucceeded = $?
    if ($global:DottyShellIntegrationCommandActive) {
        if ($commandSucceeded) { $code = 0 }
        elseif ($null -ne $LASTEXITCODE) { $code = $LASTEXITCODE }
        else { $code = 1 }
        $global:DottyShellIntegrationCommandActive = $false
        [Console]::Write("$([char]27)]133;D;$code$([char]27)\")
    }
    [Console]::Write("$([char]27)]133;A$([char]27)\")
    $uri = [Uri]::new((Get-Location).Path).AbsoluteUri
    [Console]::Write("$([char]27)]7;$uri$([char]27)\")
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

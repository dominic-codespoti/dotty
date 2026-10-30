<#
.SYNOPSIS
  Verify a Dotty Windows publish directory (win-*): required files are present and
  the published executable reports the expected version and answers +explain-config.
  Unix output is verified by verify-publish.sh.

  Every check runs; all failures are reported before the script exits non-zero.

.PARAMETER Rid
  win-x64 or win-arm64.
.PARAMETER PublishDir
  dotnet publish output directory.
.PARAMETER Version
  Expected version (the release version or the central MSBuild Version).
.PARAMETER VersionMode
  exact: `dotty --version` must equal Version (release builds; default).
  dev:   Version, Version-dev+<rev> or Version.dev+<rev> are accepted (CI builds).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Rid,
    [Parameter(Mandatory = $true)][string]$PublishDir,
    [Parameter(Mandatory = $true)][string]$Version,
    [ValidateSet('exact', 'dev')][string]$VersionMode = 'exact'
)

# Native stderr is captured with 2>&1 below; keep the default 'Continue' preference so
# that output is recorded as a failure message instead of terminating the script.
if (-not $Rid.StartsWith('win-')) {
    [Console]::Error.WriteLine("verify-publish: unsupported RID '$Rid' (use verify-publish.sh for Linux and macOS)")
    exit 2
}
if (-not (Test-Path -LiteralPath $PublishDir -PathType Container)) {
    [Console]::Error.WriteLine("verify-publish: publish directory '$PublishDir' does not exist")
    exit 1
}

$failures = [System.Collections.Generic.List[string]]::new()

function Require-File([string]$relative) {
    if (-not (Test-Path -LiteralPath (Join-Path $PublishDir $relative) -PathType Leaf)) {
        $failures.Add("missing file $relative")
    }
}

Require-File 'dotty.exe'
Require-File 'glfw3.dll'
Require-File 'libSkiaSharp.dll'
Require-File 'lua54.dll'
foreach ($asset in @('dotty.bash', 'Dotty.ps1', 'inject\bash\dotty-rcfile.bash', 'inject\zsh\.zshenv')) {
    Require-File "shell-integration\$asset"
}
Require-File 'License.md'
Require-File 'THIRD-PARTY-NOTICES.md'
Require-File 'licenses\GLFW-LICENSE.md'

$exe = Join-Path $PublishDir 'dotty.exe'
if (Test-Path -LiteralPath $exe -PathType Leaf) {
    # Isolate the run from the runner's real configuration directories.
    $sandbox = Join-Path ([System.IO.Path]::GetTempPath()) ("dotty-verify-" + [System.Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path (Join-Path $sandbox 'appdata') -Force -ErrorAction Stop | Out-Null
    $savedAppData = $env:APPDATA
    $savedConfigHome = $env:DOTTY_CONFIG_HOME
    $env:APPDATA = Join-Path $sandbox 'appdata'
    $env:DOTTY_CONFIG_HOME = Join-Path $sandbox 'config'
    try {
        $versionOutput = & $exe --version 2>&1
        $versionExit = $LASTEXITCODE
        if ($versionExit -ne 0) {
            $failures.Add("dotty --version exited with status ${versionExit}: $versionOutput")
        }
        else {
            $actual = ($versionOutput | Out-String).Trim()
            $accepted = switch ($VersionMode) {
                'exact' { $actual -ceq $Version }
                'dev' {
                    ($actual -ceq $Version) -or
                    ($actual.StartsWith("$Version-dev+", [System.StringComparison]::Ordinal) -and $actual.Length -gt "$Version-dev+".Length) -or
                    ($actual.StartsWith("$Version.dev+", [System.StringComparison]::Ordinal) -and $actual.Length -gt "$Version.dev+".Length)
                }
            }
            if (-not $accepted) {
                $failures.Add("dotty --version printed '$actual', expected '$Version' (mode $VersionMode)")
            }
        }

        $explainOutput = & $exe '+explain-config' 'font.size' 2>&1
        $explainExit = $LASTEXITCODE
        if ($explainExit -ne 0) {
            $failures.Add("dotty +explain-config font.size exited with status ${explainExit}: $explainOutput")
        }
        elseif (-not (($explainOutput | Out-String).Contains('font.size'))) {
            $failures.Add("dotty +explain-config font.size did not document font.size: $explainOutput")
        }
    }
    finally {
        $env:APPDATA = $savedAppData
        $env:DOTTY_CONFIG_HOME = $savedConfigHome
        Remove-Item -LiteralPath $sandbox -Recurse -Force -ErrorAction SilentlyContinue
    }
}

if ($failures.Count -ne 0) {
    foreach ($failure in $failures) {
        Write-Host "FAIL: $failure"
    }
    Write-Host "verify-publish: $($failures.Count) check(s) failed for $Rid in $PublishDir"
    exit 1
}
Write-Host "verify-publish: $Rid publish output in $PublishDir passed"

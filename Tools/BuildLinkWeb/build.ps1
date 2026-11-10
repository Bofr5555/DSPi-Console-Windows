<#
.SYNOPSIS
    Rebuilds the DSPi Link browser page the hub serves (DSPiConsole\Web).

.DESCRIPTION
    Exports dspi-link's js/ workspace at one commit into a temporary folder,
    runs its library tests, builds it, and copies the control page into
    DSPiConsole\Web. The bridge's Wi-Fi setup page (setup.html and its own
    script and stylesheet) is left out: a PC hub has no setup network.

    Needs git and Node.js 20.19 or later on PATH.
    Use the commit recorded in third_party\dspi-link\VENDORED.md, so the page
    and the hub follow the same spec, and record the commit here when it
    changes.

.EXAMPLE
    ./build.ps1 -Repo 'C:\Users\me\Projects\dspi-link' -Commit 86f10e3
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Repo,
    [string]$Commit = '86f10e3'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Native tools judged by exit code: Windows PowerShell 5.1 under 'Stop' turns
# any stderr line (npm's warnings) into a terminating error.
function Invoke-Native([string]$What, [scriptblock]$Command) {
    $saved = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & $Command 2>&1 | ForEach-Object { "$_" } }
    finally { $ErrorActionPreference = $saved }
    if ($LASTEXITCODE) { throw "$What failed ($LASTEXITCODE)" }
}

$web = Join-Path (Split-Path (Split-Path $PSScriptRoot)) 'DSPiConsole\Web'
$work = Join-Path ([IO.Path]::GetTempPath()) ("dspi-link-web-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $work | Out-Null
try {
    $tar = Join-Path $work 'src.tar'
    Invoke-Native "git archive $Commit" { git -C $Repo archive $Commit --format=tar -o $tar }
    Invoke-Native 'tar' { tar -xf $tar -C $work }

    Push-Location (Join-Path $work 'js')
    try {
        Invoke-Native 'npm ci' { npm ci }
        Invoke-Native 'library tests' { npm test }
        Invoke-Native 'build' { npm run build }
    }
    finally { Pop-Location }

    $dist = Join-Path $work 'js\apps\web\dist'
    $index = Get-Content (Join-Path $dist 'index.html') -Raw
    # Everything index.html names, then whatever those name in turn (a chunk
    # loaded lazily, a font in a stylesheet), and nothing else.
    $found = [System.Collections.Generic.List[string]]::new()
    $queue = [System.Collections.Generic.Queue[string]]::new()
    foreach ($m in [regex]::Matches($index, '\./assets/([\w.-]+)')) { $queue.Enqueue($m.Groups[1].Value) }
    while ($queue.Count -gt 0) {
        $name = $queue.Dequeue()
        if ($found.Contains($name)) { continue }
        $path = Join-Path $dist "assets\$name"
        if (-not (Test-Path $path)) { throw "index.html needs assets\$name, which the build didn't produce" }
        $found.Add($name)
        if ($name -match '\.(js|css)$') {
            $text = Get-Content $path -Raw
            foreach ($m in [regex]::Matches($text, '["''(]\./([\w.-]+\.\w+)["'')]')) { $queue.Enqueue($m.Groups[1].Value) }
        }
    }
    $assets = @($found | Sort-Object)
    if ($assets.Count -eq 0) { throw 'index.html names no assets' }

    # Emptied rather than deleted, so a shell or Explorer window open in it
    # can't block the swap.
    New-Item -ItemType Directory -Force (Join-Path $web 'assets') | Out-Null
    Get-ChildItem $web -Recurse -File | Remove-Item -Force
    Copy-Item (Join-Path $dist 'index.html') $web
    foreach ($a in $assets) { Copy-Item (Join-Path $dist "assets\$a") (Join-Path $web 'assets') }
    Write-Host "Web page from dspi-link $Commit -> $web ($($assets.Count) assets)"
}
finally {
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}

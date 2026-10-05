<#
.SYNOPSIS
  Runs the mock OCPP charge point backend together with the Angular web UI.

.DESCRIPTION
  prod (default)  Builds the Angular app into the backend's wwwroot, then runs the backend, which serves
                  the UI and the API from one address:  http://localhost:5080
  dev             Runs the backend and `ng serve` (live reload) side by side:  http://localhost:4200
                  The dev server proxies /api to the backend on port 5080.

  The fleet starts empty - open the UI and use the Connect page to enter the CSMS URL and how many
  charge points to start. Anything after the named options is passed to the mock, e.g.
  `.\run.ps1 --url ws://127.0.0.1:8887/ocpp --count 3` starts three units straight away.

.EXAMPLE
  .\run.ps1
  .\run.ps1 -Mode dev
  .\run.ps1 -SkipBuild -Port 6000
#>
param(
    [ValidateSet('prod', 'dev')][string]$Mode = 'prod',
    [int]$Port = 5080,
    [switch]$SkipBuild,    # prod: reuse the existing UI build
    [switch]$NoBrowser,
    [Parameter(ValueFromRemainingArguments)][string[]]$MockArgs
)

$ErrorActionPreference = 'Stop'
$root     = $PSScriptRoot
$frontend = Join-Path $root 'frontend'
$backend  = Join-Path $root 'backend\Mock-OCPP-ChargePoint'

function Need($cmd, $hint) {
    if (-not (Get-Command $cmd -ErrorAction SilentlyContinue)) { throw "$cmd not found. $hint" }
}
Need 'dotnet'  'Install the .NET 8 SDK: https://dotnet.microsoft.com/download'
Need 'npm.cmd' 'Install Node.js 20+ (includes npm): https://nodejs.org'

# npm.cmd, not npm: Windows PowerShell's execution policy often blocks npm.ps1.
if (-not (Test-Path (Join-Path $frontend 'node_modules'))) {
    Write-Host 'Installing frontend dependencies (first run only)...' -ForegroundColor Cyan
    Push-Location $frontend
    try { npm.cmd install --no-audit --no-fund; if ($LASTEXITCODE) { throw 'npm install failed' } }
    finally { Pop-Location }
}

if ($Mode -eq 'dev') { $Port = 5080 }   # frontend/proxy.conf.json points at 5080
$mockArgs = @('--http-port', $Port) + $MockArgs

if ($Mode -eq 'prod') {
    $built = Test-Path (Join-Path $backend 'wwwroot\index.html')
    if (-not ($SkipBuild -and $built)) {
        Write-Host 'Building the web UI...' -ForegroundColor Cyan
        Push-Location $frontend
        try { npm.cmd run build; if ($LASTEXITCODE) { throw 'ng build failed' } }
        finally { Pop-Location }
    }

    $url = "http://localhost:$Port"
    if (-not $NoBrowser) { Start-Job { Start-Sleep 4; Start-Process $using:url } | Out-Null }
    Write-Host "Mock OCPP charge point -> $url   (Ctrl+C to stop)" -ForegroundColor Green
    Push-Location $backend   # the backend looks for wwwroot next to the working directory
    try { dotnet run -- @mockArgs } finally { Pop-Location }
    return
}

# dev: backend in its own window, ng serve here.
Write-Host 'Starting the backend in a new window...' -ForegroundColor Cyan
$dotnetArgs = @('run', '--project', "`"$backend\Mock-OCPP-ChargePoint.csproj`"", '--') + $mockArgs
$server = Start-Process dotnet -ArgumentList $dotnetArgs -WorkingDirectory $backend -PassThru
try {
    Push-Location $frontend
    try {
        if (-not $NoBrowser) { Start-Job { Start-Sleep 8; Start-Process 'http://localhost:4200' } | Out-Null }
        npm.cmd start
    } finally { Pop-Location }
} finally {
    if ($server -and -not $server.HasExited) { taskkill /PID $server.Id /T /F | Out-Null }
}

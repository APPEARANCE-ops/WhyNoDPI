$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot

$project = Join-Path $PSScriptRoot 'WhyNoDPI.csproj'
$publishDir = Join-Path $PSScriptRoot 'publish'
$proxyKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings'
$proxyChanged = $false
$exitCode = 0

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host 'ERROR: .NET SDK was not found. Install the .NET 10 SDK first.' -ForegroundColor Red
    Read-Host 'Press Enter to close'
    exit 1
}

# The earlier build log showed this exact dead local proxy. Disable it only
# during NuGet restore, then put the original enabled state back.
if (Test-Path -LiteralPath $proxyKey) {
    $proxySettings = Get-ItemProperty -Path $proxyKey
    if ([int]$proxySettings.ProxyEnable -eq 1 -and [string]$proxySettings.ProxyServer -eq '127.0.0.1:8080') {
        Set-ItemProperty -Path $proxyKey -Name ProxyEnable -Value 0
        $proxyChanged = $true
        Write-Host 'Temporarily disabled the configured localhost proxy for NuGet restore.' -ForegroundColor Yellow
    }
}

if (Test-Path -LiteralPath $publishDir) {
    Remove-Item -LiteralPath $publishDir -Recurse -Force
}

Write-Host 'Restoring win-x64 packages...' -ForegroundColor Cyan
& dotnet restore $project -r win-x64 -p:NuGetAudit=false
$restoreCode = $LASTEXITCODE
if ($restoreCode -ne 0) {
    Write-Host ('RESTORE FAILED. Exit code: ' + $restoreCode) -ForegroundColor Red
    if ($proxyChanged) {
        Set-ItemProperty -Path $proxyKey -Name ProxyEnable -Value 1
        Write-Host 'Restored the original Windows proxy setting.' -ForegroundColor Yellow
    }
    Read-Host 'Press Enter to close'
    exit $restoreCode
}

Write-Host 'Publishing a self-contained single-file EXE...' -ForegroundColor Cyan
& dotnet publish $project -c Release -r win-x64 --no-restore --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:NuGetAudit=false -o $publishDir
$publishCode = $LASTEXITCODE

if ($proxyChanged) {
    Set-ItemProperty -Path $proxyKey -Name ProxyEnable -Value 1
    Write-Host 'Restored the original Windows proxy setting.' -ForegroundColor Yellow
}

if ($publishCode -ne 0) {
    Write-Host ('PUBLISH FAILED. Exit code: ' + $publishCode) -ForegroundColor Red
    Read-Host 'Press Enter to close'
    exit $publishCode
}

$exe = Join-Path $publishDir 'WhyNoDPI.exe'
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
    Write-Host ('ERROR: EXE was not created at ' + $exe) -ForegroundColor Red
    Read-Host 'Press Enter to close'
    exit 1
}

Write-Host ''
Write-Host ('SUCCESS: ' + $exe) -ForegroundColor Green
Start-Process -FilePath explorer.exe -ArgumentList $publishDir
Read-Host 'Press Enter to close'
exit 0

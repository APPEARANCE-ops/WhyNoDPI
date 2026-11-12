<#
.SYNOPSIS
    Собирает версию WhyNoDPI «для пользователей».

.DESCRIPTION
    Отличия от обычной сборки:
      * адрес панели статистики вшит в EXE — пользователю ничего настраивать не нужно;
      * карточка «Сервер статистики» в настройках скрыта, чтобы не путать людей;
      * рядом с EXE кладётся короткая инструкция.

    Если адрес панели не указан, собирается обычная версия (с настройкой адреса).

.EXAMPLE
    .\Build-User.ps1 -PanelUrl https://whynodpi.vercel.app

.EXAMPLE
    .\Build-User.ps1 -PanelUrl https://whynodpi.vercel.app -NoZip
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, HelpMessage = 'Адрес панели, например https://whynodpi.vercel.app')]
    [string]$PanelUrl,

    [string]$OutDir,

    [switch]$NoZip
)

$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'Не найден dotnet SDK. Установите .NET 10 SDK: https://dot.net'
}

# Проверяем и нормализуем адрес панели.
if ($PanelUrl -notmatch '^https?://') { $PanelUrl = 'https://' + $PanelUrl.TrimStart('/') }
$uri = [Uri]$PanelUrl
if ($uri.Scheme -notin @('http', 'https')) { throw 'Адрес панели должен начинаться с http:// или https://' }
$panelUrl = $uri.GetLeftPart([UriPartial]::Authority)
Write-Host "Адрес панели: $panelUrl" -ForegroundColor Cyan

if (-not $OutDir) {
    $projectRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
    $OutDir = Join-Path $projectRoot ('dist\WhyNoDPI-User-' + (Get-Date -Format 'yyyyMMdd'))
}
if (Test-Path -LiteralPath $OutDir) { Remove-Item -LiteralPath $OutDir -Recurse -Force }

Write-Host 'Сборка однопользовательского EXE (это занимает до минуты)...' -ForegroundColor Cyan
$publishDir = Join-Path $PSScriptRoot 'publish-user'
if (Test-Path -LiteralPath $publishDir) { Remove-Item -LiteralPath $publishDir -Recurse -Force }

& dotnet publish (Join-Path $PSScriptRoot 'WhyNoDPI.csproj') `
    -c Release -r win-x64 --self-contained true -t:Rebuild `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -p:NuGetAudit=false `
    "-p:UserEdition=true" "-p:DefaultPanelUrl=$panelUrl" `
    -o $publishDir

if ($LASTEXITCODE -ne 0) { throw "Сборка не удалась (код $LASTEXITCODE)." }

$exe = Join-Path $publishDir 'WhyNoDPI.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "EXE не найден: $exe" }

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
Copy-Item -LiteralPath $exe -Destination (Join-Path $OutDir 'WhyNoDPI.exe')

$readme = @"
WhyNoDPI — обход блокировок YouTube, Discord и Telegram
=======================================================

Что делать (3 шага)
-------------------
1. Скачайте пакет Flowseal (zapret) со страницы релизов:
   https://github.com/Flowseal/zapret-discord-youtube/releases/latest
   Распакуйте архив в папку без пробелов и кириллицы, например C:\zapret-flowseal

2. Запустите WhyNoDPI.exe от имени администратора
   (правой кнопкой -> «Запустить от имени администратора»).
   Права нужны драйверу WinDivert — без них обход не работает.

3. В окне приложения нажмите «Выбрать папку» и укажите папку из шага 1,
   затем нажмите «Включить Zapret».
   Через пару секунд на главной появится «Защита включена».

Если что-то не работает
-----------------------
* «Не найден bin\winws.exe» — распакован не весь архив Flowseal.
* «Нужны права администратора» — запустите EXE от имени администратора.
* YouTube не открывается — нажмите «Подобрать стратегию»: приложение само
  проверит профили и выберет рабочий.
* Журнал событий и папка логов есть в самом приложении (раздел «Журнал»).

Приложение отправляет анонимную статистику (просто факт запуска), чтобы автор
видел, сколько людей им пользуется. Никаких личных данных, паролей и списка
сайтов не передаётся. Отключить можно в настройках.

Автор интерфейса: Xcuv. Спасибо Flowseal за пакет профилей zapret.
"@
Set-Content -LiteralPath (Join-Path $OutDir 'КАК_ПОЛЬЗОВАТЬСЯ.txt') -Value $readme -Encoding UTF8

if (-not $NoZip) {
    $zipPath = "$OutDir.zip"
    if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
    Compress-Archive -Path (Join-Path $OutDir '*') -DestinationPath $zipPath -CompressionLevel Optimal
    Write-Host ''
    Write-Host "Готово: $zipPath" -ForegroundColor Green
} else {
    Write-Host ''
    Write-Host "Готово: $OutDir" -ForegroundColor Green
}

$sizeMb = [math]::Round((Get-Item -LiteralPath (Join-Path $OutDir 'WhyNoDPI.exe')).Length / 1MB, 1)
Write-Host "EXE: $sizeMb МБ, адрес панели: $panelUrl" -ForegroundColor Gray
Write-Host 'Раздавайте пользователям ZIP-архив, а не сам EXE: внутри инструкция.' -ForegroundColor Gray

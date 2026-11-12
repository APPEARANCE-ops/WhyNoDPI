@echo off
chcp 65001 >nul
setlocal

rem Запуск Build-User.ps1 в обход политики выполнения скриптов
rem (на Windows по умолчанию PowerShell запрещает .ps1).
rem
rem Использование:
rem   Build-User.cmd https://whynodpi.vercel.app
rem или просто дважды кликнуть по файлу и вставить адрес панели.

if "%~1"=="" (
    set /p PANELURL=Введите адрес панели (например https://whynodpi.vercel.app): 
) else (
    set PANELURL=%~1
)

if "%PANELURL%"=="" (
    echo Адрес панели не указан. Выход.
    pause
    exit /b 1
)

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Build-User.ps1" -PanelUrl "%PANELURL%"
set RESULT=%ERRORLEVEL%

echo.
pause
exit /b %RESULT%

@echo off
rem Tec.LimitTest publish script for Windows. Usage: scripts\publish-limit-test.cmd [VERSION]
rem   VERSION = the patch number you are on, e.g. 0323 (shown in the title bar; default for the sheet's "patch version" cell).
rem   Output: dist\tec-limit-test-win-x64\ plus dist\Tec.LimitTest-win-x64-VERSION.zip. Target PCs need no .NET.
rem Keep this file ASCII-only: cmd reads batch files in the OEM code page (GBK on Chinese Windows),
rem so a UTF-8 Chinese path inside a command would not match the real file name.
setlocal
cd /d "%~dp0\.."
set VER=%1
if "%VER%"=="" set VER=0325
rem strip leading zeros (0323 -> 323): "set /a" would read a leading zero as octal, and /p:Version wants plain digits
for /f "tokens=* delims=0" %%a in ("%VER%") do set VERNUM=%%a
if "%VERNUM%"=="" set VERNUM=0
set OUT=dist\tec-limit-test-win-x64

rem NEVER add PublishTrimmed: Avalonia's reflection bindings break when trimmed (same rule as publish-hmi.sh).
dotnet publish src\Tec.LimitTest -c Release -r win-x64 --self-contained ^
    /p:PublishSingleFile=true ^
    /p:IncludeNativeLibrariesForSelfExtract=true ^
    /p:Version=1.0.%VERNUM% ^
    /p:InformationalVersion=%VER% ^
    -o "%OUT%"
if errorlevel 1 exit /b 1

rem the usage README travels with the exe (wildcard: the file name is Chinese)
copy /y "src\Tec.LimitTest\README-*.md" "%OUT%\" >nul
rem zip it with the PowerShell that ships with Windows
powershell -NoProfile -Command "Compress-Archive -Force -Path '%OUT%\*' -DestinationPath 'dist\Tec.LimitTest-win-x64-%VER%.zip'"
if errorlevel 1 (
    echo zip failed - the folder %OUT% is still complete, copy it as is
    exit /b 1
)
echo.
echo Done: %OUT% (version %VER%), zip in dist\
echo Copy the whole folder to the target PC and double-click Tec.LimitTest.exe; see README-*.md in the folder.
endlocal

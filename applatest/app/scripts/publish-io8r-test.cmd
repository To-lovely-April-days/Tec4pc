@echo off
rem Tec.Io8rTest publish script for Windows. Usage: scripts\publish-io8r-test.cmd
rem   Output: dist\tec-io8r-test-win-x64\ plus dist\Tec.Io8rTest-win-x64.zip. Target PCs need no .NET.
rem Keep this file ASCII-only: cmd reads batch files in the OEM code page (GBK on Chinese Windows),
rem so a UTF-8 Chinese path inside a command would not match the real file name.
setlocal
cd /d "%~dp0\.."
set OUT=dist\tec-io8r-test-win-x64

rem NEVER add PublishTrimmed: Avalonia's reflection bindings break when trimmed (same rule as publish-hmi.sh).
dotnet publish src\Tec.Io8rTest -c Release -r win-x64 --self-contained ^
    /p:PublishSingleFile=true ^
    /p:IncludeNativeLibrariesForSelfExtract=true ^
    -o "%OUT%"
if errorlevel 1 exit /b 1

rem the usage README travels with the exe (wildcard: the file name is Chinese)
copy /y "src\Tec.Io8rTest\README-*.md" "%OUT%\" >nul
powershell -NoProfile -Command "Compress-Archive -Force -Path '%OUT%\*' -DestinationPath 'dist\Tec.Io8rTest-win-x64.zip'"
if errorlevel 1 (
    echo zip failed - the folder %OUT% is still complete, copy it as is
    exit /b 1
)
echo.
echo Done: %OUT%, zip in dist\
echo Copy the whole folder to the target PC and double-click Tec.Io8rTest.exe; see README-*.md in the folder.
endlocal

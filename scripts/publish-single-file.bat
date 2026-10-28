@echo off
setlocal
cd /d "%~dp0\.."

echo ========================================================
echo   PrintBridge - Publishing Single-File .exe Application
echo ========================================================
echo.

where dotnet >nul 2>nul
if %ERRORLEVEL% neq 0 (
    echo [ERROR] .NET 8 SDK not found in PATH!
    echo Please install the .NET 8 SDK from:
    echo https://dotnet.microsoft.com/download/dotnet/8.0
    pause
    exit /b 1
)

echo [1/2] Creating publish directory...
if not exist "publish" mkdir "publish"

echo [2/2] Publishing PrintBridge.exe (Single-file Windows x64)...
dotnet publish src\PrintBridge\PrintBridge.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "publish"

if %ERRORLEVEL% neq 0 (
    echo.
    echo [ERROR] Publishing failed!
    pause
    exit /b %ERRORLEVEL%
)

echo.
echo ========================================================
echo   SUCCESS! Single .exe created:
echo   %~dp0..\publish\PrintBridge.exe
echo ========================================================
echo.
echo You can copy PrintBridge.exe anywhere and double-click to run!
echo.
pause

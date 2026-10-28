@echo off
setlocal
cd /d "%~dp0\.."

echo ========================================================
echo   PrintBridge - Building Application
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

echo Restoring packages and building...
dotnet build PrintBridge.sln -c Release
if %ERRORLEVEL% neq 0 (
    echo [ERROR] Build failed!
    pause
    exit /b %ERRORLEVEL%
)

echo.
echo ========================================================
echo   Build Successful!
echo   Output located in: src\PrintBridge\bin\Release\net8.0-windows\
echo ========================================================
pause

@echo off
setlocal
echo ========================================================
echo   PrintBridge - Cleanup & Uninstallation
echo ========================================================
echo.

:: Check for administrative permissions
net session >nul 2>&1
if %ERRORLEVEL% neq 0 (
    echo [REQUEST] Requesting Administrator privileges...
    powershell -Command "Start-Process '%~f0' -Verb RunAs"
    exit /b
)

echo [1/3] Closing running PrintBridge instances...
taskkill /F /IM PrintBridge.exe >nul 2>&1

echo [2/3] Removing Windows Firewall rules...
netsh advfirewall firewall delete rule name="PrintBridge IPP Server (TCP 631)" >nul 2>&1
netsh advfirewall firewall delete rule name="PrintBridge mDNS Discovery (UDP 5353)" >nul 2>&1
netsh advfirewall firewall delete rule name="PrintBridge*" >nul 2>&1

echo [3/3] Removing Windows Startup entry...
reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v "PrintBridge" /f >nul 2>&1

echo.
echo ========================================================
echo   PrintBridge has been cleaned up!
echo   Settings and firewall rules have been removed.
echo ========================================================
echo.
pause

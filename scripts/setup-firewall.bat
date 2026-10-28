@echo off
setlocal
echo ========================================================
echo   PrintBridge - Configuring Windows Firewall Rules
echo ========================================================
echo.

:: Check for administrative permissions
net session >nul 2>&1
if %ERRORLEVEL% neq 0 (
    echo [REQUEST] Requesting Administrator privileges...
    powershell -Command "Start-Process '%~f0' -Verb RunAs"
    exit /b
)

echo [1/3] Adding rule for IPP Printing (TCP port 631, all profiles)...
netsh advfirewall firewall delete rule name="PrintBridge IPP Server (TCP 631)" >nul 2>&1
netsh advfirewall firewall add rule name="PrintBridge IPP Server (TCP 631)" dir=in action=allow protocol=TCP localport=631 profile=any description="Allows iPhone and Android devices to send print jobs via IPP"

echo [2/3] Adding rule for AirPrint & mDNS discovery (UDP port 5353, all profiles)...
netsh advfirewall firewall delete rule name="PrintBridge mDNS Discovery (UDP 5353)" >nul 2>&1
netsh advfirewall firewall add rule name="PrintBridge mDNS Discovery (UDP 5353)" dir=in action=allow protocol=UDP localport=5353 profile=any description="Allows AirPrint and IPP Everywhere service discovery"

echo [3/3] Adding application rule for PrintBridge.exe...
set "EXE_PATH=%~dp0..\publish\PrintBridge.exe"
if exist "%EXE_PATH%" (
    netsh advfirewall firewall delete rule name="PrintBridge Application" >nul 2>&1
    netsh advfirewall firewall add rule name="PrintBridge Application" dir=in action=allow program="%EXE_PATH%" profile=any description="Allows PrintBridge application traffic"
)

echo.
echo ========================================================
echo   Firewall ports successfully opened!
echo   - TCP 631  (IPP Printing)
echo   - UDP 5353 (mDNS Discovery)
echo ========================================================
echo.
pause

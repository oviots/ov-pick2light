@echo off
REM Pick2Light (P2L) Installer Script for Windows
REM Provides simple installation without requiring WiX Toolset
REM Usage: p2l-install.bat [install|uninstall]
REM Requires: Administrator privileges

setlocal enabledelayedexpansion
set "VERSION=1.0.0"
set "PRODUCT_NAME=Pick2Light (P2L)"

if "%1"=="" (
    call :usage
    exit /b 1
)

if /i "%1"=="install" (
    call :install_p2l
    exit /b !errorlevel!
) else if /i "%1"=="uninstall" (
    call :uninstall_p2l
    exit /b !errorlevel!
) else if /i "%1"=="help" (
    call :usage
    exit /b 0
) else (
    echo Unknown command: %1
    call :usage
    exit /b 1
)

:usage
echo.
echo  %PRODUCT_NAME% Installer - Version %VERSION%
echo  ============================================
echo.
echo  Usage:
echo    p2l-install.bat install      - Install P2L to Program Files
echo    p2l-install.bat uninstall    - Remove P2L from system
echo    p2l-install.bat help         - Show this help
echo.
echo  Requirements:
echo    - Administrator privileges
echo    - Windows 7 or later
echo    - p2l.exe binary in the same directory as this script
echo.
goto :eof

:install_p2l
echo.
echo  Installing %PRODUCT_NAME% (v%VERSION%)...
echo.

REM Check for admin privileges
net session >nul 2>&1
if !errorlevel! neq 0 (
    echo Error: This script requires Administrator privileges.
    echo Please run as Administrator.
    exit /b 1
)

REM Verify binary exists
if not exist "%~dp0p2l.exe" (
    echo Error: p2l.exe not found in %~dp0
    echo Please ensure p2l.exe is in the same directory as this script.
    exit /b 1
)

REM Create installation directory
set "INSTALL_DIR=%ProgramFiles%\Pick2Light\bin"
echo [1/5] Creating installation directory: %INSTALL_DIR%
if not exist "!INSTALL_DIR!" (
    mkdir "!INSTALL_DIR!" || (
        echo Error: Failed to create installation directory
        exit /b 1
    )
)

REM Copy binary
echo [2/5] Copying p2l.exe to installation directory...
copy /Y "%~dp0p2l.exe" "!INSTALL_DIR!\p2l.exe" >nul || (
    echo Error: Failed to copy p2l.exe
    exit /b 1
)

REM Add to PATH (registry)
echo [3/5] Adding to system PATH...
setx /M PATH "%PATH%;!INSTALL_DIR!" >nul || (
    echo Warning: Failed to update PATH environment variable
)

REM Update PATH for current session
set "PATH=%PATH%;!INSTALL_DIR!"

REM Create registry entries for uninstall
echo [4/5] Creating uninstall registry entries...
reg add "HKLM\Software\Microsoft\Windows\CurrentVersion\Uninstall\Pick2Light_P2L" /v DisplayName /d "%PRODUCT_NAME%" /f >nul
reg add "HKLM\Software\Microsoft\Windows\CurrentVersion\Uninstall\Pick2Light_P2L" /v DisplayVersion /d "%VERSION%" /f >nul
reg add "HKLM\Software\Microsoft\Windows\CurrentVersion\Uninstall\Pick2Light_P2L" /v InstallLocation /d "!INSTALL_DIR!" /f >nul
reg add "HKLM\Software\Microsoft\Windows\CurrentVersion\Uninstall\Pick2Light_P2L" /v Publisher /d "Pick2Light" /f >nul
reg add "HKLM\Software\Microsoft\Windows\CurrentVersion\Uninstall\Pick2Light_P2L" /v UninstallString /d "cmd /c """ & "%~dp0p2l-install.bat" uninstall """ /f >nul

REM Create config directory
echo [5/5] Creating configuration directory...
set "CONFIG_DIR=%ProgramData%\Pick2Light\config"
if not exist "!CONFIG_DIR!" (
    mkdir "!CONFIG_DIR!" >nul 2>&1
)

echo.
echo ✓ Installation complete!
echo.
echo Installed to: !INSTALL_DIR!
echo.
echo Next steps:
echo   1. Open a new Command Prompt or PowerShell window
echo   2. Run: p2l --help
echo   3. Configure your gateway connection
echo   4. Start picking!
echo.
exit /b 0

:uninstall_p2l
echo.
echo  Uninstalling %PRODUCT_NAME% (v%VERSION%)...
echo.

REM Check for admin privileges
net session >nul 2>&1
if !errorlevel! neq 0 (
    echo Error: This script requires Administrator privileges.
    echo Please run as Administrator.
    exit /b 1
)

set "INSTALL_DIR=%ProgramFiles%\Pick2Light\bin"

REM Remove binary
echo [1/3] Removing p2l.exe...
if exist "!INSTALL_DIR!\p2l.exe" (
    del /F /Q "!INSTALL_DIR!\p2l.exe" >nul 2>&1 || echo Warning: Could not delete p2l.exe
)

REM Remove registry entries
echo [2/3] Removing registry entries...
reg delete "HKLM\Software\Microsoft\Windows\CurrentVersion\Uninstall\Pick2Light_P2L" /f >nul 2>&1

REM Remove directories if empty
echo [3/3] Cleaning up directories...
if exist "!INSTALL_DIR!" (
    rmdir "!INSTALL_DIR!" >nul 2>&1
)
if exist "%ProgramFiles%\Pick2Light" (
    rmdir "%ProgramFiles%\Pick2Light" >nul 2>&1
)

echo.
echo ✓ Uninstallation complete!
echo.
echo Note: Configuration files in %ProgramData%\Pick2Light\config have been preserved.
echo To completely remove, delete: %ProgramData%\Pick2Light
echo.
exit /b 0

endlocal

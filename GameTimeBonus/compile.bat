@echo off
chcp 65001 >nul
echo ========================================
echo Building GameTimeBonus Plugin
echo ========================================
echo.

:: Check for dotnet
where dotnet >nul 2>&1
if %ERRORLEVEL% NEQ 0 (
    echo ERROR: .NET SDK not found!
    echo Please install .NET 8.0 SDK from https://dotnet.microsoft.com/download
    PAUSE
    exit /b 1
)

echo .NET SDK found
echo.

:: Clean previous build
if exist "bin" rmdir /s /q "bin"
if exist "compile" rmdir /s /q "compile"

:: Restore and publish
echo Publishing plugin...
dotnet publish -c Release --self-contained false -o "bin\publish"
if %ERRORLEVEL% NEQ 0 (
    echo Publish failed!
    PAUSE
    exit /b 1
)
echo Publish complete
echo.

echo Creating compile folder structure...

:: Create compile directories
if not exist "compile\addons\counterstrikesharp\plugins\GameTimeBonus" mkdir "compile\addons\counterstrikesharp\plugins\GameTimeBonus"
if not exist "compile\addons\counterstrikesharp\plugins\GameTimeBonus\lang" mkdir "compile\addons\counterstrikesharp\plugins\GameTimeBonus\lang"

:: Copy all published files
xcopy /Y /E "bin\publish\*" "compile\addons\counterstrikesharp\plugins\GameTimeBonus\"

:: Copy lang files
xcopy /Y /E "lang\*" "compile\addons\counterstrikesharp\plugins\GameTimeBonus\lang\"

:: Create default config
if not exist "compile\addons\counterstrikesharp\configs\plugins\GameTimeBonus" mkdir "compile\addons\counterstrikesharp\configs\plugins\GameTimeBonus"

echo.
echo ========================================
echo Build complete!
echo Compile folder: compile\
echo ========================================
echo.
echo Files in compile folder:
dir /b "compile\addons\counterstrikesharp\plugins\GameTimeBonus\"
echo.
echo ========================================
echo Files to copy to server:
echo   - compile\addons\counterstrikesharp\plugins\GameTimeBonus\  (plugin + dependencies)
echo   - compile\addons\counterstrikesharp\configs\plugins\GameTimeBonus\  (config)
echo ========================================

PAUSE

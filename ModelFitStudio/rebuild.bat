@echo off
setlocal enabledelayedexpansion
title ModelFit Studio - rebuild

rem ===================================================================
rem  ModelFit Studio - one-click rebuild
rem
rem  Double-click this file after editing anything in the project.
rem  It publishes "Models Fit Studio.exe" (single file, self-contained),
rem  verifies llmfit + the UI really landed in the output folder,
rem  and rebuilds the shareable zip.
rem
rem  Do NOT add -p:IncludeNativeLibrariesForSelfExtract=true to the
rem  publish line: it folds tools\llmfit.exe into the bundle where the
rem  app can no longer run it, and the model list comes up empty.
rem ===================================================================

set "HERE=%~dp0"
cd /d "%HERE%"

echo.
echo ============================================================
echo   ModelFit Studio  -  rebuild
echo ============================================================
echo   project: %HERE%
echo.

where dotnet >nul 2>nul
if errorlevel 1 (
    echo   [X] The .NET SDK is not on PATH.
    echo       Install the .NET 10 SDK: https://dotnet.microsoft.com/download
    goto :fail
)

rem ---- 0. the app must be closed --------------------------------------
rem A running app holds its exe in dist open. Windows then refuses to
rem delete dist, and the publish dies inside GenerateBundle with an MSB4018
rem that never names the real cause. Say it plainly instead.
set /a APPWAIT=0
:waitapp
tasklist /FI "IMAGENAME eq Models Fit Studio.exe" /NH 2>nul | find /I "Models Fit Studio.exe" >nul
if errorlevel 1 goto :appclosed
set /a APPWAIT+=1
if %APPWAIT% GEQ 4 (
    echo.
    echo   [X] Models Fit Studio is still running. It holds "dist\Models Fit Studio.exe"
    echo       open, so the rebuild cannot replace it. Close its window and the
    echo       tray icon, then run this again - or run:
    echo           taskkill /IM "Models Fit Studio.exe" /F /T
    goto :fail
)
echo.
echo   [!] Models Fit Studio is running and holds "dist\Models Fit Studio.exe" open.
echo       Close its window (and the tray icon), then press any key...
pause >nul
echo.
goto :waitapp
:appclosed

rem ---- 1. icon ------------------------------------------------------
echo   [1/4] app icon
where python >nul 2>nul
if errorlevel 1 (
    echo         python not found - keeping the existing Assets\app.ico
) else (
    python "tools\make-icon.py" >nul 2>nul
    if errorlevel 1 ( echo         icon generator failed - keeping the existing icon ) else ( echo         Assets\app.ico regenerated )
)

rem ---- 2. publish ---------------------------------------------------
echo   [2/4] publishing win-x64 single file
if exist "dist" rmdir /s /q "dist"
rem If a file in dist is still open the delete half-fails, and the publish then
rem tries to overwrite the leftover exe and crashes with the same MSB4018.
if exist "dist" (
    echo.
    echo   [X] Could not clear the old dist folder - something in it is still open.
    echo       Close ModelFit Studio and any other app using dist\, then run again.
    goto :fail
)
dotnet publish -c Release -r win-x64 --self-contained true ^
    -p:PublishSingleFile=true ^
    -p:EnableCompressionInSingleFile=true ^
    -o dist --nologo -v m
if errorlevel 1 goto :fail

rem ---- 3. verify ----------------------------------------------------
echo.
echo   [3/4] checking the output
set "MISSING="
for %%F in ("dist\Models Fit Studio.exe" "dist\llmfit.exe" "dist\engine\llama-server.exe" "dist\THIRD-PARTY-NOTICES.txt" "dist\Web\index.html" "dist\Web\app.js" "dist\Web\styles.css" "dist\Web\i18n.js" "dist\Web\lang\de.json") do (
    if not exist "%%~F" set "MISSING=!MISSING!  %%~nxF"
    if exist "%%~F" echo         ok  %%~nxF
)
if defined MISSING (
    echo.
    echo   [X] Missing from dist:!MISSING!
    echo       The exe would start but llmfit or the UI would be gone.
    goto :fail
)

rem ---- 4. zip -------------------------------------------------------
echo.
echo   [4/4] packaging Models-Fit-Studio-^<version^>-win-x64.zip
del /q "Models-Fit-Studio-*-win-x64.zip" 2>nul
rem Deliberately not Compress-Archive: Windows PowerShell 5.1 stores nested
rem entries with backslash separators ("Web\app.js"), so anything but Explorer
rem unpacks a flat file whose name contains a backslash and the app finds no
rem Web\ folder. tools\make-zip.ps1 writes the entries itself, then reads the
rem archive back and fails if any path is still Windows-only. Its exit codes:
rem 1 = nothing to package, 2 = the archive would not unpack into a runnable app.
powershell -NoProfile -ExecutionPolicy Bypass -File "tools\make-zip.ps1"
if errorlevel 1 goto :zipbad

echo.
echo ============================================================
echo   Done.  Ready to run:
echo.
for %%F in ("dist\Models Fit Studio.exe") do echo     exe : %%~fF  (%%~zF bytes)
for %%F in ("Models-Fit-Studio-*-win-x64.zip") do echo     zip : %%~fF  (%%~zF bytes)
echo.
echo   Double-click "dist\Models Fit Studio.exe" to start it.
echo ============================================================
echo.
pause
exit /b 0

:fail
echo.
echo ============================================================
echo   BUILD FAILED - nothing was published.
echo   Scroll up for the first error line from dotnet.
echo ============================================================
echo.
pause
exit /b 1

:zipbad
echo.
echo   [X] The zip has no "Web/app.js" at a real path, or some entry uses a
echo       Windows-only backslash path. Unpacked by anything but Explorer that
echo       is a broken app, so the zip was deleted.
echo       dist\ is unaffected - it is the app, ready to run.
pause
exit /b 1

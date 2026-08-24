@echo off
setlocal enabledelayedexpansion

REM Publish Foundation for Debian server (linux-x64)
REM Output goes to deploy\out\

set "SCRIPT_DIR=%~dp0"
set "ROOT_DIR=%SCRIPT_DIR%.."
set "OUT_DIR=%SCRIPT_DIR%out"

echo ==^> Cleaning previous publish...
if exist "%OUT_DIR%" (
    rd /s /q "%OUT_DIR%" 2>nul

    REM One stuck file is enough to fail the whole rd — a stale handle, or a
    REM file left delete-pending by an interrupted publish, which Windows then
    REM reports as "Access is denied" on every subsequent open. The old tree
    REM survives, and dotnet publish fails several screens later on MSB3021
    REM instead, which reads like a build error rather than a cleanup problem.
    REM Renaming the directory never opens the stuck file, so it still works.
    if exist "%OUT_DIR%" (
        set "STALE=%OUT_DIR%.stale-%RANDOM%"
        move "%OUT_DIR%" "!STALE!" >nul 2>&1
        if exist "%OUT_DIR%" (
            echo ERROR: Could not clear "%OUT_DIR%".
            echo        Something is holding a file open in it. Close anything
            echo        running from that folder, or reboot, then re-run.
            exit /b 1
        )
        echo   WARNING: the old publish could not be deleted - moved aside to
        echo            !STALE!
        echo            Delete that folder later; a reboot frees the stuck file.
    )
)
mkdir "%OUT_DIR%\foundation"
if errorlevel 1 (
    echo ERROR: Could not create "%OUT_DIR%\foundation".
    exit /b 1
)

echo ==^> Publishing Foundation.Web (linux-x64, self-contained)...
dotnet publish "%ROOT_DIR%\web\Foundation.Web\Foundation.Web.csproj" -c Release -r linux-x64 --self-contained true -o "%OUT_DIR%\foundation" /p:PublishSingleFile=false
if errorlevel 1 (
    echo ERROR: dotnet publish failed
    exit /b 1
)

echo ==^> Copying service files...
copy "%SCRIPT_DIR%foundation.service" "%OUT_DIR%\" >nul
copy "%SCRIPT_DIR%install.sh" "%OUT_DIR%\" >nul

echo ==^> Creating deploy tarball...
where tar >nul 2>&1
if errorlevel 1 (
    echo ERROR: tar not found. Windows 10+ includes tar, or install Git for Windows.
    exit /b 1
)
pushd "%OUT_DIR%"
tar -czf "%SCRIPT_DIR%foundation-deploy.tar.gz" .
popd

echo.
echo ==========================================
echo   Publish complete!
echo ==========================================
echo   Tarball: deploy\foundation-deploy.tar.gz
echo   Web App: deploy\out\foundation\
echo.
echo   Deploy with:
echo     deploy\deploy.bat admin@^<server^> --domain your.domain.com --email you@email.com
echo.
echo   To rebuild the database (WARNING: deletes all data):
echo     deploy\deploy.bat admin@^<server^> --domain your.domain.com --email you@email.com --rebuild-db
echo ==========================================

@echo off
setlocal enabledelayedexpansion

REM Deploy Foundation to every site in a manifest, in one pass.
REM
REM Usage: deploy-all.bat --email <address> [options]
REM
REM Options:
REM   --sites <file>       Site manifest (default: deploy\sites.txt)
REM   --email <email>      Let's Encrypt email, shared by every site
REM   --key <ssh-key>      SSH key file, used for every site
REM   --port <port>        Default app port for sites that don't name one
REM   --setup-keys         Install your SSH public key on every host, then exit.
REM                        Prompts once per host; afterwards deploys need no
REM                        password at all. Run this once per fleet.
REM   --skip-build         Reuse the existing tarball instead of publishing fresh
REM   --dry-run            Print what would be deployed and exit
REM
REM Examples:
REM   deploy-all.bat --setup-keys
REM   deploy-all.bat --email ssolleder@michelli.com
REM
REM On passwords: there is deliberately no way to hand this script a password.
REM SSH asks twice per site (upload, then install), so a fleet of ten is twenty
REM prompts, and any file holding that password becomes the fleet's single
REM point of compromise. --setup-keys pays the prompts once and removes them.
REM
REM --rebuild-db is deliberately NOT forwarded. It destroys the database, and a
REM fleet-wide wipe should never be one flag away. Use deploy.bat for that, one
REM site at a time, on purpose.

set "SCRIPT_DIR=%~dp0"
set "TARBALL=%SCRIPT_DIR%foundation-deploy.tar.gz"

set "SITES_FILE=%SCRIPT_DIR%sites.txt"
set "EMAIL="
set "SSH_KEY="
set "DEFAULT_PORT=5110"
set "SETUP_KEYS=0"
set "SKIP_BUILD=0"
set "DRY_RUN=0"

:parse_args
if "%~1"=="" goto :done_args
if "%~1"=="--sites"      ( set "SITES_FILE=%~2" & shift & shift & goto :parse_args )
if "%~1"=="--email"      ( set "EMAIL=%~2" & shift & shift & goto :parse_args )
if "%~1"=="--key"        ( set "SSH_KEY=%~2" & shift & shift & goto :parse_args )
if "%~1"=="--port"       ( set "DEFAULT_PORT=%~2" & shift & shift & goto :parse_args )
if "%~1"=="--setup-keys" ( set "SETUP_KEYS=1" & shift & goto :parse_args )
if "%~1"=="--skip-build" ( set "SKIP_BUILD=1" & shift & goto :parse_args )
if "%~1"=="--dry-run"    ( set "DRY_RUN=1" & shift & goto :parse_args )
echo Unknown option: %~1
exit /b 1
:done_args

if not exist "%SITES_FILE%" (
    echo ERROR: No site manifest at %SITES_FILE%
    echo        Copy deploy\sites.example.txt to deploy\sites.txt and edit it,
    echo        or point at another file with --sites.
    exit /b 1
)

REM ---- Parse the manifest --------------------------------------------------
REM eol=# skips comment lines; for /f skips blank lines on its own.
set "SITE_COUNT=0"
for /f "usebackq eol=# tokens=1-3" %%a in ("%SITES_FILE%") do (
    set /a SITE_COUNT+=1
    set "HOST_!SITE_COUNT!=%%a"

    REM Default the domain to the host we SSH to.
    set "DOM=%%b"
    if "%%b"=="" (
        set "DOM=%%a"
        for /f "tokens=2 delims=@" %%x in ("%%a") do set "DOM=%%x"
    )
    set "DOMAIN_!SITE_COUNT!=!DOM!"

    set "PRT=%%c"
    if "%%c"=="" set "PRT=%DEFAULT_PORT%"
    set "PORT_!SITE_COUNT!=!PRT!"
)

if "%SITE_COUNT%"=="0" (
    echo ERROR: %SITES_FILE% lists no sites.
    exit /b 1
)

echo ==^> %SITE_COUNT% site^(s^):
for /l %%i in (1,1,%SITE_COUNT%) do (
    echo       !HOST_%%i!  -^>  https://!DOMAIN_%%i!  ^(port !PORT_%%i!^)
)
echo.

REM ---- Key setup mode ------------------------------------------------------
if "%SETUP_KEYS%"=="1" (
    set "PUBKEY="
    if not "%SSH_KEY%"=="" (
        if exist "%SSH_KEY%.pub" set "PUBKEY=%SSH_KEY%.pub"
    ) else (
        if exist "%USERPROFILE%\.ssh\id_ed25519.pub" set "PUBKEY=%USERPROFILE%\.ssh\id_ed25519.pub"
        if not defined PUBKEY if exist "%USERPROFILE%\.ssh\id_rsa.pub" set "PUBKEY=%USERPROFILE%\.ssh\id_rsa.pub"
    )
    if not defined PUBKEY (
        echo ERROR: No SSH public key found. Create one with:
        echo          ssh-keygen -t ed25519
        exit /b 1
    )

    echo ==^> Installing your public key on %SITE_COUNT% host^(s^).
    echo     You'll be asked for each host's password once.
    echo.
    set "KEY_FAILURES=0"
    for /l %%i in (1,1,%SITE_COUNT%) do (
        echo --- !HOST_%%i!
        REM Done inline rather than via ssh-copy-id, which stock Windows
        REM OpenSSH does not ship. Appending is idempotent enough: a repeat
        REM run adds a duplicate line, which sshd ignores.
        ssh -o StrictHostKeyChecking=no "!HOST_%%i!" "mkdir -p ~/.ssh && chmod 700 ~/.ssh && cat >> ~/.ssh/authorized_keys && chmod 600 ~/.ssh/authorized_keys" < "!PUBKEY!"
        if errorlevel 1 (
            echo     FAILED.
            set /a KEY_FAILURES+=1
        ) else (
            echo     key installed.
        )
    )
    echo.
    if not "!KEY_FAILURES!"=="0" (
        echo !KEY_FAILURES! host^(s^) failed. Fix those, then re-run --setup-keys.
        exit /b 1
    )
    echo All hosts keyed. Deploys from here need no password:
    echo   deploy-all.bat --email ^<address^>
    exit /b 0
)

if "%EMAIL%"=="" (
    echo ERROR: --email is required ^(Let's Encrypt notifications for every site^).
    exit /b 1
)

if "%DRY_RUN%"=="1" (
    echo Dry run - nothing deployed.
    exit /b 0
)

REM ---- Build once, deploy many ---------------------------------------------
REM deploy.bat publishes per invocation; across a fleet that's one full build
REM per site for byte-identical output. Build here, hand each site --skip-build.
if "%SKIP_BUILD%"=="1" if exist "%TARBALL%" (
    echo ==^> Reusing existing tarball ^(--skip-build^).
    goto :deploy_loop
)
echo ==^> Publishing one build for all %SITE_COUNT% site^(s^)...
call "%SCRIPT_DIR%publish.bat"
if errorlevel 1 (
    echo ERROR: Build failed - nothing deployed.
    exit /b 1
)
:deploy_loop
echo.

set "OK_COUNT=0"
set "FAIL_COUNT=0"
set "FAILED_HOSTS="

for /l %%i in (1,1,%SITE_COUNT%) do (
    echo ==============================================
    echo   [%%i/%SITE_COUNT%] !HOST_%%i! -^> !DOMAIN_%%i!
    echo ==============================================

    set "KEY_ARG="
    if not "%SSH_KEY%"=="" set "KEY_ARG=--key %SSH_KEY%"

    call "%SCRIPT_DIR%deploy.bat" "!HOST_%%i!" --domain "!DOMAIN_%%i!" --email "%EMAIL%" --port "!PORT_%%i!" --skip-build !KEY_ARG!
    if errorlevel 1 (
        echo !!! !HOST_%%i! FAILED - continuing with the remaining sites.
        set /a FAIL_COUNT+=1
        set "FAILED_HOSTS=!FAILED_HOSTS! !HOST_%%i!"
    ) else (
        set /a OK_COUNT+=1
        set "OK_%%i=1"
    )
    echo.
)

echo ==============================================
echo   Fleet deploy: !OK_COUNT! ok, !FAIL_COUNT! failed
echo ==============================================
for /l %%i in (1,1,%SITE_COUNT%) do (
    if defined OK_%%i (
        echo   ok      https://!DOMAIN_%%i!
    ) else (
        echo   FAILED  !HOST_%%i!
    )
)
echo ==============================================

if not "!FAIL_COUNT!"=="0" exit /b 1
exit /b 0

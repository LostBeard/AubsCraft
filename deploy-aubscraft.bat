@echo off
REM -----------------------------------------------------------
REM  Deploy AubsCraft.Admin.Server to aubscraft VM
REM  VM: aubscraft (192.168.1.142) | Service: aubscraft_admin
REM  SSH config: aubscraft -> zed@192.168.1.142
REM
REM  Files go over SSH (tar stream) into a STAGING folder while the site keeps running; only then is the
REM  service stopped and the staged files copied into place ON the VM. The old copy over the mapped M:
REM  drive failed mid-way whenever the SSHFS mount dropped, AFTER the service was stopped - site down.
REM
REM  First-time setup: ssh aubscraft "sudo bash /srv/aubscraft/setup-service.sh"
REM -----------------------------------------------------------

setlocal

set HOST=aubscraft
set SERVICE=aubscraft_admin
set REMOTE_DIR=/srv/aubscraft
set STAGING=/home/zed/aubscraft-staging
set PROJECT=AubsCraft.Admin.Server\AubsCraft.Admin.Server.csproj
set PUBLISH_DIR=publish

echo -----------------------------------------------------------
echo   Deploy AubsCraft.Admin.Server
echo   Target: %HOST%:%REMOTE_DIR% (via SSH, staged in %STAGING%)
echo   Service: %SERVICE%
echo -----------------------------------------------------------
echo.

REM -- Step 1: Build and Publish --
echo [1/4] Publishing release build for linux-x64...
REM Start from an empty publish folder: publishing into the old one kept every previous build's fingerprinted
REM _framework files, and the copy then shipped them to the server (stale SpawnDev.BlazorJS*.wasm after the SpawnJS port).
if exist "%PUBLISH_DIR%" rmdir /s /q "%PUBLISH_DIR%"
REM The client is AOT compiled: build it clean so no stale AOT/webcil output from an earlier build is reused.
if exist "AubsCraft.Admin\obj\Release" rmdir /s /q "AubsCraft.Admin\obj\Release"
if exist "AubsCraft.Admin\bin\Release" rmdir /s /q "AubsCraft.Admin\bin\Release"
dotnet publish "%PROJECT%" -c Release -r linux-x64 --self-contained true -o "%PUBLISH_DIR%"
if errorlevel 1 (
    echo PUBLISH FAILED
    exit /b 1
)
echo       Published to %PUBLISH_DIR%
echo.

REM -- Step 2: Stage on the VM (site still running) --
echo [2/4] Uploading to %STAGING% (the site stays up)...
ssh %HOST% "rm -rf %STAGING% && mkdir -p %STAGING%"
if errorlevel 1 (
    echo STAGING FAILED - nothing changed on the server
    exit /b 1
)
tar -C "%PUBLISH_DIR%" -cf - . | ssh %HOST% "tar -C %STAGING% -xf -"
if errorlevel 1 (
    echo UPLOAD FAILED - nothing changed on the server, the site is still running the old build
    exit /b 1
)
echo       Uploaded.
echo.

REM -- Step 3: Swap in place on the VM (local copy, no network in the window the site is down) --
echo [3/4] Stopping %SERVICE%, installing, restoring server config...
REM The server keeps its own appsettings.json (real RCON password, paths); servers.json, users.json, bans.json,
REM activity-log.json etc. are not in the publish output, so they are never touched. Config files stay owner-only.
ssh %HOST% "set -e; sudo systemctl stop %SERVICE%; cp -p %REMOTE_DIR%/appsettings.json %REMOTE_DIR%/appsettings.json.bak; rm -f %STAGING%/appsettings.json; cp -a %STAGING%/. %REMOTE_DIR%/; chmod +x %REMOTE_DIR%/AubsCraft.Admin.Server; chmod 600 %REMOTE_DIR%/appsettings*.json %REMOTE_DIR%/servers.json 2>/dev/null || true"
if errorlevel 1 (
    echo INSTALL FAILED - starting the service anyway so the site comes back
    ssh %HOST% "sudo systemctl start %SERVICE%"
    exit /b 1
)
echo       Installed.
echo.

REM -- Step 4: Start service --
echo [4/4] Starting %SERVICE%...
ssh %HOST% "sudo systemctl start %SERVICE% && sleep 3 && systemctl is-active %SERVICE% && rm -rf %STAGING%"
if errorlevel 1 (
    echo START FAILED
    ssh %HOST% "sudo systemctl status %SERVICE% --no-pager -l | tail -20"
    exit /b 1
)
echo.

echo -----------------------------------------------------------
echo   Deploy complete!
echo   http://192.168.1.142:5080
echo -----------------------------------------------------------

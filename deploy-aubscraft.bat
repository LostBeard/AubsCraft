@echo off
REM -----------------------------------------------------------
REM  Deploy AubsCraft.Admin.Server to aubscraft VM
REM  VM: aubscraft (192.168.1.142), root mapped to M:
REM  Service: aubscraft_admin | SSH config: aubscraft -> zed@192.168.1.142
REM
REM  First-time setup: ssh aubscraft "sudo bash /srv/aubscraft/setup-service.sh"
REM -----------------------------------------------------------

setlocal

set HOST=aubscraft
set SERVICE=aubscraft_admin
set DEPLOY_DIR=M:\srv\aubscraft
set PROJECT=AubsCraft.Admin.Server\AubsCraft.Admin.Server.csproj
set PUBLISH_DIR=publish

echo -----------------------------------------------------------
echo   Deploy AubsCraft.Admin.Server
echo   Target: %DEPLOY_DIR% (via mapped M: drive)
echo   Service: %SERVICE%
echo -----------------------------------------------------------
echo.

REM -- Step 1: Build and Publish --
echo [1/5] Publishing release build for linux-x64...
dotnet publish "%PROJECT%" -c Release -r linux-x64 --self-contained true -o "%PUBLISH_DIR%"
if errorlevel 1 (
    echo PUBLISH FAILED
    exit /b 1
)
echo       Published to %PUBLISH_DIR%
echo.

REM -- Step 2: Stop remote service --
echo [2/5] Stopping %SERVICE%...
ssh %HOST% "sudo systemctl stop %SERVICE%"
echo       Service stopped.
echo.

REM -- Step 3: Backup server config --
echo [3/5] Backing up server config...
if exist "%DEPLOY_DIR%\appsettings.json" (
    copy /y "%DEPLOY_DIR%\appsettings.json" "%DEPLOY_DIR%\appsettings.json.bak" >nul
    echo       appsettings.json backed up.
) else (
    echo       No existing config to back up.
)
echo.

REM -- Step 4: Copy files and restore config --
echo [4/5] Copying to %DEPLOY_DIR%...
xcopy /s /y /q "%PUBLISH_DIR%\*" "%DEPLOY_DIR%\"
if errorlevel 1 (
    echo COPY FAILED
    exit /b 1
)
REM Restore server-specific config (has real RCON password, paths, etc.)
if exist "%DEPLOY_DIR%\appsettings.json.bak" (
    copy /y "%DEPLOY_DIR%\appsettings.json.bak" "%DEPLOY_DIR%\appsettings.json" >nul
    echo       Files copied. Server config preserved.
) else (
    echo       Files copied. WARNING: No config backup - edit appsettings.json on server!
)
echo.

REM -- Step 5: Start service --
echo [5/5] Starting %SERVICE%...
ssh %HOST% "chmod +x /srv/aubscraft/AubsCraft.Admin.Server && sudo systemctl start %SERVICE%"
timeout /t 2 /nobreak >nul
ssh %HOST% "sudo systemctl status %SERVICE% --no-pager -l"
echo.

echo -----------------------------------------------------------
echo   Deploy complete!
echo   http://192.168.1.142:5080
echo -----------------------------------------------------------

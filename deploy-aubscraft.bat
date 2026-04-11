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
echo [1/4] Publishing release build for linux-x64...
dotnet publish "%PROJECT%" -c Release -r linux-x64 --self-contained true -o "%PUBLISH_DIR%"
if errorlevel 1 (
    echo PUBLISH FAILED
    exit /b 1
)
echo       Published to %PUBLISH_DIR%
echo.

REM -- Step 2: Stop remote service --
echo [2/4] Stopping %SERVICE%...
ssh %HOST% "sudo systemctl stop %SERVICE%"
echo       Service stopped.
echo.

REM -- Step 3: Copy files via mapped drive --
echo [3/4] Copying to %DEPLOY_DIR%...
xcopy /s /y /q "%PUBLISH_DIR%\*" "%DEPLOY_DIR%\"
if errorlevel 1 (
    echo COPY FAILED
    exit /b 1
)
echo       Files copied.
echo.

REM -- Step 4: Start service --
echo [4/4] Starting %SERVICE%...
ssh %HOST% "chmod +x /srv/aubscraft/AubsCraft.Admin.Server && sudo systemctl start %SERVICE%"
timeout /t 2 /nobreak >nul
ssh %HOST% "sudo systemctl status %SERVICE% --no-pager -l"
echo.

echo -----------------------------------------------------------
echo   Deploy complete!
echo   http://192.168.1.142:5080
echo -----------------------------------------------------------

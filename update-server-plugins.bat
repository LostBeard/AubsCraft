@echo off
REM -----------------------------------------------------------
REM  AubsCraft - update cross-play / cross-version plugins
REM  Updates Geyser + Floodgate (Bedrock) and ViaVersion +
REM  ViaBackwards + ViaRewind (any Java version), then restarts
REM  the minecraft service on the aubscraft VM.
REM
REM  Just double-click this file. Requires: M: mounted to the VM
REM  and `ssh aubscraft` configured (same as deploy-aubscraft.bat).
REM -----------------------------------------------------------
setlocal
echo Running AubsCraft plugin updater...
echo.
dotnet run "%~dp0sync-plugins.cs"
echo.
echo Done. Review the output above.
pause

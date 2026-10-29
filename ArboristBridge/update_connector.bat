@echo off
cd /d "%~dp0"
echo Make sure Claude is fully closed (also from the system tray) before continuing.
pause
copy /Y "ArboristMcp.next.exe" "ArboristMcp.exe"
if errorlevel 1 (
  echo.
  echo FAILED - ArboristMcp.exe is still in use. Quit Claude completely and run this again.
) else (
  del "ArboristMcp.next.exe"
  echo.
  echo Connector updated. You can reopen Claude now.
)
pause

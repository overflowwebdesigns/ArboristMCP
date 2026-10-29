@echo off
cd /d "%~dp0"
echo Running ArboristMcp.exe test...
"%~dp0ArboristMcp.exe" < nul > "%~dp0mcp_test_output.txt" 2>&1
echo Exit code: %ERRORLEVEL% >> "%~dp0mcp_test_output.txt"
type "%~dp0mcp_test_output.txt"
pause

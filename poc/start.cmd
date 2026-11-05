@echo off
rem Start the POC: backend (5180) + frontend (9180), one window each. Close the windows to stop.
cd /d "%~dp0"
if not exist frontend\node_modules (pushd frontend & bun install & popd)
start "POC backend" dotnet run --project backend\src\DynamicModules.WebAPI
start "POC frontend" /D frontend cmd /k bunx quasar dev
set /a tries=0
echo Waiting for backend http://127.0.0.1:5180/api/health ...
:waitback
curl -s -o NUL http://127.0.0.1:5180/api/health
if not errorlevel 1 goto backok
set /a tries+=1
if %tries% geq 120 goto giveup
timeout /t 1 /nobreak >NUL
goto waitback
:backok
set /a tries=0
echo Waiting for frontend http://127.0.0.1:9180/ ...
:waitfront
curl -s -o NUL http://127.0.0.1:9180/
if not errorlevel 1 goto frontok
set /a tries+=1
if %tries% geq 120 goto giveup
timeout /t 1 /nobreak >NUL
goto waitfront
:frontok
start "" http://127.0.0.1:9180/
goto :eof
:giveup
echo Timed out after 120 seconds. Check the backend/frontend windows for errors, then close them.
exit /b 1

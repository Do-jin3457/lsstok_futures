@echo off
:loop
cd /d C:\LSHeadless\LS.Futures\bin\Debug
echo ===== START %date% %time% ===== >> C:\LSHeadless\futures_standalone.log
LS.Futures.exe >> C:\LSHeadless\futures_standalone.log 2>&1
echo ===== EXIT %errorlevel% %date% %time% ===== >> C:\LSHeadless\futures_standalone.log
timeout /t 5 /nobreak > nul
goto loop

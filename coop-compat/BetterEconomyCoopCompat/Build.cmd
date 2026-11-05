@echo off
rem Builds BetterEconomy.CoopCompat (and its Coop adapter) and packs the module zip into artifacts\compat.
setlocal
cd /d "%~dp0"
set "OUT=%~dp0..\..\artifacts\compat"
set "MOD=%OUT%\BetterEconomy.CoopCompat"
set "LOG=%OUT%\bettereconomy-coopcompat-build.log"
if not exist "%OUT%" mkdir "%OUT%"
echo Building BetterEconomy.CoopCompat... > "%LOG%"
dotnet build BetterEconomyCoopCompat.csproj -c Release -o "%~dp0bin\Release" >> "%LOG%" 2>&1
if errorlevel 1 goto failed
dotnet build Coop\BetterEconomyCoopCompat.Coop.csproj -c Release -o "%~dp0bin\Release" >> "%LOG%" 2>&1
if errorlevel 1 goto failed
if exist "%MOD%" rmdir /s /q "%MOD%"
mkdir "%MOD%\bin\Win64_Shipping_Client"
mkdir "%MOD%\bin\Win64_Shipping_Server"
copy /y SubModule.xml "%MOD%\" >> "%LOG%"
copy /y README.txt "%MOD%\" >> "%LOG%"
for %%S in (Client Server) do (
  copy /y "bin\Release\BetterEconomy.CoopCompat.dll" "%MOD%\bin\Win64_Shipping_%%S\" >> "%LOG%"
  copy /y "bin\Release\BetterEconomy.CoopCompat.Coop.dll" "%MOD%\bin\Win64_Shipping_%%S\" >> "%LOG%"
)
if exist "%OUT%\BetterEconomy.CoopCompat-v1.0.0.zip" del /q "%OUT%\BetterEconomy.CoopCompat-v1.0.0.zip"
rem Windows' own tar writes / in zip entry names. PowerShell's ZipFile and
rem Compress-Archive on Windows PowerShell 5 write \, which breaks the zip elsewhere.
tar -a -c -f "%OUT%\BetterEconomy.CoopCompat-v1.0.0.zip" -C "%OUT%" BetterEconomy.CoopCompat >> "%LOG%" 2>&1
echo BUILD OK >> "%LOG%"
echo Done. %OUT%\BetterEconomy.CoopCompat-v1.0.0.zip
pause
exit /b 0
:failed
echo BUILD FAILED >> "%LOG%"
echo Build failed. See %LOG%
pause
exit /b 1

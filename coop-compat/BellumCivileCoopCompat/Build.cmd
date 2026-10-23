@echo off
rem Builds BellumCivile.CoopCompat and packs the module zip into artifacts\compat.
setlocal
cd /d "%~dp0"
set "OUT=%~dp0..\..\artifacts\compat"
set "MOD=%OUT%\BellumCivile.CoopCompat"
set "LOG=%OUT%\bellumcivile-coopcompat-build.log"
if not exist "%OUT%" mkdir "%OUT%"
echo Building BellumCivile.CoopCompat... > "%LOG%"
dotnet build BellumCivileCoopCompat.csproj -c Release -o "%~dp0bin\Release" >> "%LOG%" 2>&1
if errorlevel 1 (
  echo BUILD FAILED >> "%LOG%"
  echo Build failed. See %LOG%
  pause
  exit /b 1
)
if exist "%MOD%" rmdir /s /q "%MOD%"
mkdir "%MOD%\bin\Win64_Shipping_Client"
mkdir "%MOD%\bin\Win64_Shipping_Server"
copy /y SubModule.xml "%MOD%\" >> "%LOG%"
copy /y README.txt "%MOD%\" >> "%LOG%"
copy /y "bin\Release\BellumCivile.CoopCompat.dll" "%MOD%\bin\Win64_Shipping_Client\" >> "%LOG%"
copy /y "bin\Release\BellumCivile.CoopCompat.dll" "%MOD%\bin\Win64_Shipping_Server\" >> "%LOG%"
if exist "%OUT%\BellumCivile.CoopCompat-v1.0.0.zip" del /q "%OUT%\BellumCivile.CoopCompat-v1.0.0.zip"
rem Windows' own tar writes / in zip entry names. PowerShell's ZipFile and
rem Compress-Archive on Windows PowerShell 5 write \, which breaks the zip elsewhere.
tar -a -c -f "%OUT%\BellumCivile.CoopCompat-v1.0.0.zip" -C "%OUT%" BellumCivile.CoopCompat >> "%LOG%" 2>&1
echo BUILD OK >> "%LOG%"
echo Done. %OUT%\BellumCivile.CoopCompat-v1.0.0.zip
pause

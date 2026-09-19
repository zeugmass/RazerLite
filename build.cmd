@echo off
setlocal EnableExtensions
chcp 65001 >nul
cd /d "%~dp0"

echo.
echo  ================================================
echo   RazerLite - tek EXE derleyici
echo  ================================================
echo.

where dotnet >nul 2>nul
if errorlevel 1 (
  echo  [HATA] .NET SDK bulunamadi.
  echo.
  echo  Kurmak icin PowerShell'de:   winget install Microsoft.DotNet.SDK.8
  echo  veya: https://dotnet.microsoft.com/download/dotnet/8.0  ^(SDK, x64^)
  echo.
  pause
  exit /b 1
)

set "SDKOK="
for /f "tokens=1 delims=." %%v in ('dotnet --version 2^>nul') do if %%v GEQ 8 set SDKOK=1
if not defined SDKOK (
  echo  [HATA] .NET SDK 8 veya ustu gerekiyor. Yuklu surum:
  dotnet --version
  echo.
  echo  Kurmak icin:   winget install Microsoft.DotNet.SDK.8
  echo.
  pause
  exit /b 1
)

tasklist /FI "IMAGENAME eq RazerLite.exe" 2>nul | find /I "RazerLite.exe" >nul
if not errorlevel 1 (
  echo  Calisan RazerLite.exe kapatiliyor...
  taskkill /IM RazerLite.exe /F >nul 2>nul
  timeout /t 1 /nobreak >nul
)

set "OUT=%TEMP%\RazerLite-build"
if exist "%OUT%" rmdir /s /q "%OUT%"

echo  Derleniyor (ilk seferde .NET runtime indirilebilir, 1-2 dk surebilir)...
echo.
dotnet publish "src\RazerLite.csproj" -c Release -o "%OUT%" --nologo -v q
if errorlevel 1 (
  echo.
  echo  [HATA] Derleme basarisiz. Yukaridaki hata mesajlarina bakin.
  pause
  exit /b 1
)

copy /Y "%OUT%\RazerLite.exe" "%~dp0RazerLite.exe" >nul
if errorlevel 1 (
  echo  [HATA] RazerLite.exe kopyalanamadi.
  pause
  exit /b 1
)

rmdir /s /q "%OUT%" >nul 2>nul
if exist "src\bin" rmdir /s /q "src\bin"
if exist "src\obj" rmdir /s /q "src\obj"

echo.
echo  ================================================
echo   TAMAM:  %~dp0RazerLite.exe
echo  ================================================
echo.
choice /C EH /N /M "  Simdi calistirilsin mi? [E/H] "
if errorlevel 2 goto :end
start "" "%~dp0RazerLite.exe"
:end
endlocal

@echo off
chcp 65001 >nul
title Stream Light Control - .exe maken
cd /d "%~dp0"
echo.
echo  ===============================================
echo   Stream Light Control - losse .exe maken
echo  ===============================================
echo.

where dotnet >nul 2>nul
if errorlevel 1 (
  echo  [FOUT] .NET SDK niet gevonden.
  echo  Installeer de .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0
  echo  ^(kies "SDK x64" voor Windows^) en start dit bestand opnieuw.
  echo.
  pause
  exit /b 1
)

rem Bureaublad zoeken (werkt ook als je bureaublad in OneDrive staat)
for /f "usebackq delims=" %%D in (`powershell -NoProfile -Command "[Environment]::GetFolderPath('Desktop')"`) do set "DESKTOP=%%D"
set "OUT=%DESKTOP%\Stream Light Control"

rem Draaiende app sluiten (anders zijn de bestanden vergrendeld) en oude build opruimen
taskkill /im StreamLightControl.exe /f >nul 2>nul
if exist "%OUT%\StreamLightControl.exe" rmdir /s /q "%OUT%"
if exist "bin\Release" rmdir /s /q "bin\Release"
if exist "obj\Release" rmdir /s /q "obj\Release"

echo  Bezig met bouwen... ^(de eerste keer duurt dit 1-3 minuten^)
echo.
dotnet publish StreamLightControl2.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o "%OUT%"
if errorlevel 1 (
  echo.
  echo  [FOUT] Bouwen mislukt. Maak een screenshot van dit venster en stuur het op.
  echo.
  pause
  exit /b 1
)

rem Snelkoppeling op het bureaublad
powershell -NoProfile -Command "$s=(New-Object -ComObject WScript.Shell).CreateShortcut('%DESKTOP%\Stream Light Control.lnk'); $s.TargetPath='%OUT%\StreamLightControl.exe'; $s.WorkingDirectory='%OUT%'; $s.IconLocation='%OUT%\StreamLightControl.exe,0'; $s.Save()"

echo.
echo  ===============================================
echo   Klaar!
echo   App:            %OUT%\StreamLightControl.exe
echo   Snelkoppeling:  op je bureaublad
echo  ===============================================
echo.
echo  Laat de andere bestanden in die map staan: de app heeft ze nodig.
echo  Je instellingen, lichtshows en koppelingen blijven gewoon bewaard.
echo  Vraagt Windows Firewall om toegang? Kies "Prive-netwerken" en Toestaan.
echo.
start "" "%OUT%"
pause

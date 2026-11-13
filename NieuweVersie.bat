@echo off
chcp 65001 >nul
title Stream Light Control - nieuwe versie publiceren
cd /d "%~dp0"
echo.
echo  Nieuwe versie publiceren voor iedereen
echo  --------------------------------------
echo  GitHub bouwt de app en alle gebruikers krijgen automatisch de update.
echo.
where git >nul 2>nul
if errorlevel 1 ( echo  [FOUT] Git is niet geinstalleerd: https://git-scm.com/download/win & pause & exit /b 1 )
set /p VERSIE= Versienummer (bijv. 3.2.0): 
if "%VERSIE%"=="" ( echo  Geen versie ingevuld. & pause & exit /b 1 )
set /p NOTITIE= Wat is er veranderd (korte zin): 
git add -A
git commit -m "Versie %VERSIE%: %NOTITIE%"
git tag -a v%VERSIE% -m "%NOTITIE%"
git push
git push origin v%VERSIE%
if errorlevel 1 ( echo. & echo  [FOUT] Pushen mislukt. Ben je ingelogd bij GitHub? & pause & exit /b 1 )
echo.
echo  Klaar! GitHub bouwt nu versie %VERSIE% (± 5 minuten).
echo  Volg het op github.com bij jouw repository, tabblad "Actions".
echo  Daarna krijgen alle apps de update bij het opstarten.
echo.
pause

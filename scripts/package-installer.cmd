@echo off
rem Runs package-installer.ps1 from Command Prompt with the same parameters, for example:
rem   scripts\package-installer.cmd -Version 1.0.5 -OutputDirectory artifacts\installer-1.0.5 -WixTermsReviewedByOwner -AcceptWixEula
rem PowerShell 7 (pwsh) is used when installed, otherwise the Windows PowerShell that comes with Windows. The execution policy
rem is bypassed for this one run only; no Windows setting is changed. The exit code is passed back.
setlocal
set "LUNAR_PS=powershell.exe"
where pwsh.exe >/dev/null 2>/dev/null && set "LUNAR_PS=pwsh.exe"
"%LUNAR_PS%" -NoProfile -ExecutionPolicy Bypass -File "%~dp0package-installer.ps1" %*
exit /b %ERRORLEVEL%

@echo off
rem ============================================================
rem  Compila o Ultra VPN Free (100% C#, sem motor PowerShell)
rem  com o csc que ja vem no Windows.
rem ============================================================
setlocal
set CS=%~dp0
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
if not exist "%FW%\csc.exe" set FW=%WINDIR%\Microsoft.NET\Framework\v4.0.30319
"%FW%\csc.exe" /nologo /t:winexe /win32icon:"%CS%res\UltraVPNFree.ico" ^
  /out:"%CS%UltraVPNFree.exe" ^
  /r:"%FW%\System.Windows.Forms.dll" /r:"%FW%\System.Drawing.dll" /r:"%FW%\System.Core.dll" ^
  /r:"%FW%\System.Web.Extensions.dll" /r:"%FW%\System.ServiceProcess.dll" ^
  /res:"%CS%res\tray.gif",tray.gif /res:"%CS%res\fundo.alien.jpg",fundo.alien.jpg ^
  /res:"%CS%res\fundo.goku.jpg",fundo.goku.jpg /res:"%CS%res\alien.escuro.png",alien.escuro.png ^
  /res:"%CS%res\alien.claro.png",alien.claro.png ^
  /res:"%CS%res\servers.inicial",servers.inicial /res:"%CS%res\pvl.inicial",pvl.inicial ^
  /res:"%CS%res\seed.ovpn",uvpn.seed.ovpn ^
  /res:"%CS%res\OpenVPN-2.7.8-I001-amd64.msi",uvpn.openvpn.msi ^
  "%CS%UltraVPNFree.cs"
if exist "%CS%UltraVPNFree.exe" (echo BUILD OK: UltraVPNFree.exe) else (echo BUILD FALHOU)

rem ============================================================
rem  Assinatura digital (Authenticode). Sem isto o Windows mostra
rem  "editor nao reconhecido" em quem baixa. Precisa do certificado
rem  CN=Virgilio Marques em Cert:\CurrentUser\My. Se faltar, apenas
rem  avisa - o build NAO falha por causa da assinatura.
rem ============================================================
if exist "%CS%UltraVPNFree.exe" (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%CS%Assinar.ps1" "%CS%UltraVPNFree.exe"
)
@echo off
rem ============================================================
rem  Compila o Ultra VPN Free (100% C#, sem motor PowerShell)
rem  com o csc que ja vem no Windows.
rem
rem  A tela e HTML dentro de um WebView2. Isso pede duas DLLs
rem  gerenciadas (lib\) e a WebView2Loader nativa, que vai
rem  embutida como recurso e e extraida em tempo de execucao
rem  (ver TelaWeb no fonte). Sem elas o build FALHA - e de
rem  proposito: melhor erro na hora de compilar do que .exe
rem  quebrado so na hora de abrir.
rem ============================================================
setlocal
set CS=%~dp0
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
if not exist "%FW%\csc.exe" set FW=%WINDIR%\Microsoft.NET\Framework\v4.0.30319

if not exist "%CS%lib\Microsoft.Web.WebView2.Core.dll" (
  echo FALTOU: falta %CS%lib\Microsoft.Web.WebView2.Core.dll
  echo Baixe o pacote Microsoft.Web.WebView2 do NuGet e extraia
  echo a pasta lib\net462\ para dentro da pasta lib\ do projeto.
  exit /b 1
)

rem apaga o .exe antigo ANTES de compilar: se o csc falhar, o arquivo
rem velho nao pode ficar ali fazendo o BUILD OK parecer certo
if exist "%CS%UltraVPNFree.exe" del "%CS%UltraVPNFree.exe"

"%FW%\csc.exe" /nologo /t:winexe /win32icon:"%CS%res\UltraVPNFree.ico" ^
  /out:"%CS%UltraVPNFree.exe" ^
  /r:"%CS%lib\Microsoft.Web.WebView2.Core.dll" ^
  /r:"%CS%lib\Microsoft.Web.WebView2.WinForms.dll" ^
  /r:"%FW%\System.Windows.Forms.dll" /r:"%FW%\System.Drawing.dll" /r:"%FW%\System.Core.dll" ^
  /r:"%FW%\System.Web.Extensions.dll" /r:"%FW%\System.ServiceProcess.dll" ^
  /res:"%CS%res\tray.gif",tray.gif /res:"%CS%res\fundo.alien.jpg",fundo.alien.jpg ^
  /res:"%CS%res\fundo.goku.jpg",fundo.goku.jpg /res:"%CS%res\alien.escuro.png",alien.escuro.png ^
  /res:"%CS%res\alien.claro.png",alien.claro.png ^
  /res:"%CS%res\servers.inicial",servers.inicial /res:"%CS%res\pvl.inicial",pvl.inicial ^
  /res:"%CS%res\seed.ovpn",uvpn.seed.ovpn ^
  /res:"%CS%res\OpenVPN-2.7.8-I001-amd64.msi",uvpn.openvpn.msi ^
  /res:"%CS%ui\index.html",uvpn.ui.index.html ^
  /res:"%CS%ui\app.css",uvpn.ui.app.css ^
  /res:"%CS%ui\app.js",uvpn.ui.app.js ^
  /res:"%CS%lib\WebView2Loader.x64.dll",WebView2Loader.x64.dll ^
  /res:"%CS%lib\WebView2Loader.x86.dll",WebView2Loader.x86.dll ^
  /res:"%CS%lib\Microsoft.Web.WebView2.Core.dll",wv2.Core.dll ^
  /res:"%CS%lib\Microsoft.Web.WebView2.WinForms.dll",wv2.WinForms.dll ^
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
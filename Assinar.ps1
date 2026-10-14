# =====================================================================
#  Assina o UltraVPNFree.exe com o certificado de codigo.
#  Chamado pelo build.bat. Se o certificado nao existir, apenas avisa
#  e devolve 0 - a assinatura nunca quebra o build.
#  Uso:  powershell -NoProfile -ExecutionPolicy Bypass -File Assinar.ps1 <caminho.do.exe>
# =====================================================================
param([string]$Exe)

$ErrorActionPreference = 'Continue'
if ($null -eq $Exe -or $Exe -eq '') {
    $Exe = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'UltraVPNFree.exe'
}
if (-not (Test-Path -LiteralPath $Exe)) {
    Write-Host '   assinatura: o .exe nao existe' -ForegroundColor Yellow
    exit 0
}

$cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert -ErrorAction SilentlyContinue |
        Where-Object { $_.Subject -like 'CN=Virgilio Marques*' } |
        Select-Object -First 1

if (-not $cert) {
    Write-Host '   AVISO: nao ha certificado CN=Virgilio Marques em Cert:\CurrentUser\My' -ForegroundColor Yellow
    Write-Host '           o .exe saiu sem assinatura (o Windows vai avisar ao abrir)' -ForegroundColor Yellow
    exit 0
}

try {
    $sig = Set-AuthenticodeSignature -FilePath $Exe -Certificate $cert `
            -TimestampServer 'http://timestamp.digicert.com' -HashAlgorithm SHA256

    if ($sig.Status -eq 'Valid') {
        Write-Host ('   assinatura OK: ' + $sig.Status + '  (' + $sig.SignerCertificate.Subject + ')') -ForegroundColor Green
        if ($sig.TimeStamperCertificate) {
            Write-Host ('   carimbo de tempo: ' + $sig.TimeStamperCertificate.Subject) -ForegroundColor DarkGray
        }
    } else {
        Write-Host ('   assinatura: ' + $sig.Status + ' - ' + $sig.StatusMessage) -ForegroundColor Yellow
    }
} catch {
    Write-Host ('   assinatura falhou: ' + $_.Exception.Message) -ForegroundColor Yellow
}
exit 0
$ErrorActionPreference = "Stop"

$cert = New-SelfSignedCertificate `
    -Type Custom `
    -Subject "CN=33FC47D7-8283-45FC-BB5D-297D1476BB29" `
    -KeyUsage DigitalSignature `
    -FriendlyName "Easydict Dev Signing" `
    -CertStoreLocation "Cert:\CurrentUser\My" `
    -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")

$pwd = ConvertTo-SecureString -String "password" -Force -AsPlainText

$pfxPath = Join-Path $PSScriptRoot "dev-signing.pfx"
Export-PfxCertificate -Cert "Cert:\CurrentUser\My\$($cert.Thumbprint)" -FilePath $pfxPath -Password $pwd | Out-Null

Write-Host "Certificate created successfully" -ForegroundColor Green
Write-Host "  Subject:    $($cert.Subject)"
Write-Host "  Thumbprint: $($cert.Thumbprint)"
Write-Host "  PFX Path:   $pfxPath"
Write-Host "  Password:   password"

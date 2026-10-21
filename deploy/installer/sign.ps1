param([Parameter(Mandatory)][string]$Path)
$ErrorActionPreference = 'Stop'
Import-Module TrustedSigning -RequiredVersion 0.4.1
Invoke-TrustedSigning -Endpoint 'https://eus.codesigning.azure.net/' `
    -CodeSigningAccountName 'viberails' -CertificateProfileName 'viberails-signing' `
    -Files $Path -FileDigest SHA256 -TimestampRfc3161 'http://timestamp.acs.microsoft.com' `
    -TimestampDigest SHA256
if ((Get-AuthenticodeSignature -LiteralPath $Path).Status -ne 'Valid') {
    throw "Trusted Signing did not produce a valid signature: $Path"
}

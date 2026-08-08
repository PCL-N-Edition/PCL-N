# PCL N API mTLS certificate

Production launcher builds must embed `pcln-api-client.pfx`. The file contains
the Cloudflare API Shield client certificate and its private key, must use an
empty password, is ignored by Git, and must never be committed.

The GitHub Actions build restores it from this repository secret:

```text
PCLN_API_CLIENT_PFX_BASE64
```

Create the value in PowerShell after exporting the certificate and key as an
empty-password PKCS#12 file:

```powershell
$pfx = [Convert]::ToBase64String([IO.File]::ReadAllBytes('pcln-api-client.pfx'))
gh secret set PCLN_API_CLIENT_PFX_BASE64 --body $pfx --repo PCL-N-Edition/PCL-N
```

Local development can keep the certificate outside the repository:

```powershell
$env:PCLN_API_CLIENT_CERT_PATH = 'C:\secure\pcln-api-client.pfx'
$env:PCLN_API_CLIENT_CERT_PASSWORD = ''
```

The production workflow fails if the secret is absent. Local/PR builds may run
without the certificate, but Cloudflare correctly rejects their update API
requests with `mtls_required`; release discovery for custom repositories and
legacy GitHub update paths remains usable.

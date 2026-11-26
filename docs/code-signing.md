# Code signing: getting rid of "Windows protected your PC"

When you download `Declutter.exe`, Windows SmartScreen may say **"Windows protected your PC"**. That's
because the file isn't *code-signed*: there's no certificate proving who published it. Signing needs a paid
identity check, so it can't be switched on from this repository alone. It needs an account in the
publisher's name. Here are the options, cheapest first.

## Options

| Option | Cost | Notes |
|---|---|---|
| **SignPath Foundation** | Free for open-source projects | Signs releases built by GitHub Actions. You apply, and the project must meet their open-source criteria (an OSI licence, public source, a release process they can verify). Signed as "SignPath Foundation" rather than your own name. |
| **Azure Trusted Signing** (now *Azure Artifact Signing*) | About US$10 a month (Basic plan) | Microsoft's own service, built for this. Needs an Azure subscription and identity validation. Check Microsoft's current eligibility rules for individuals in your country. Certificates renew automatically and keys never leave Microsoft's HSMs. |
| **OV code-signing certificate** (Sectigo, DigiCert, SSL.com…) | About US$200–400 a year | Since 2023 the private key must live on a hardware token or a cloud HSM, which makes signing in CI awkward unless the vendor offers cloud signing (e.g. SSL.com eSigner). |
| **EV certificate** | More | No longer gives instant SmartScreen trust (Microsoft changed this in 2024), so it's not worth the extra cost here. |

**What to expect:** signing alone doesn't make the warning vanish on day one. SmartScreen builds *reputation* for a
signed publisher as people download and run the file. A signed app gets there much faster and keeps that
reputation across new versions, whereas every unsigned version starts from zero.

## Wiring it into CI (Azure Trusted Signing)

Once you have an account, a certificate profile and an app registration, add these repository secrets:
`AZURE_TENANT_ID`, `AZURE_CLIENT_ID`, `AZURE_CLIENT_SECRET`. Then add a step right after "Publish single-file executables" in
`.github/workflows/build.yml` that signs both exes before they're uploaded, using Microsoft's
[`azure/trusted-signing-action`](https://github.com/Azure/trusted-signing-action) (check its README for the
current version and input names):

```yaml
      - name: Sign
        uses: azure/trusted-signing-action@<version>
        with:
          azure-tenant-id: ${{ secrets.AZURE_TENANT_ID }}
          azure-client-id: ${{ secrets.AZURE_CLIENT_ID }}
          azure-client-secret: ${{ secrets.AZURE_CLIENT_SECRET }}
          endpoint: https://<region>.codesigning.azure.net/
          trusted-signing-account-name: <account>
          certificate-profile-name: <profile>
          files-folder: release
          files-folder-filter: exe
          file-digest: SHA256
          timestamp-rfc3161: http://timestamp.acs.microsoft.com
          timestamp-digest: SHA256
```

Sign **before** the smoke test and the release upload: the self-updater checks the
downloaded file against the size and checksum GitHub reports, so they must describe the signed file.

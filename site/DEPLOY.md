# Nearlink website and Go service

The frontend is built with Vue 3, TypeScript, and Vite. Vite produces static files in `web/`; Go embeds those files into one deployable binary. Node.js is only required while developing or building the site, and is not required on the production server. The Go service exposes `/healthz`, `/readyz`, and `/api/v1/site`, applies bounded HTTP timeouts and security headers, and shuts down gracefully on SIGTERM. systemd restarts the service after failures and runs it under a restricted, unprivileged account. Caddy provides automatic HTTPS and proxies public traffic to the loopback-only Go listener.

## Local build

```powershell
cd site
npm ci
npm run dev
```

For a production bundle and embedded Go service:

```powershell
npm ci
npm run build
go build -trimpath -ldflags="-s -w" -o nearlink-site.exe .
```

Use Node.js 20.19 or newer for Vite 8. `npm ci` installs the versions recorded in `package-lock.json`; the Vite production bundle is emitted to `web/` for Go embedding.

## Production notes

This single-instance setup improves process recovery; one VPS is not multi-node high availability. For multi-node HA, use at least two instances, an external health-checked load balancer, shared or object storage for mutable data, and tested backups. This site currently has no mutable server-side state or database.

The production Caddy configuration is in `deploy/Caddyfile`. Keep domain DNS pointed at the server and allow inbound TCP ports 80 and 443 so ACME certificate issuance and renewal can work. For mainland China hosting, complete the required ICP filing before offering the site publicly.

## Client release updates

The Go service reads per-platform manifests and packages from `NEARLINK_RELEASES_DIR` (production: `/var/lib/nearlink-site/releases`). The manifests are `windows.json` and `android.json`; package names are `Nearlink-Windows-x64.zip` and `Nearlink-Android.apk`. The release workflow installs these four files atomically per file, while the app installer verifies the SHA-256 from the manifest.

To enable the workflow's server publish step, install `deploy/nearlink-install-release` as root at `/usr/local/sbin/nearlink-install-release`, owned by root and executable, then install `deploy/nearlink-release-sudoers` as `/etc/sudoers.d/nearlink-release` and validate it with `visudo -cf /etc/sudoers.d/nearlink-release`. This grants the `ubuntu` deployment account passwordless access only to the guarded release installer. Add an SSH key for GitHub Actions and store the host, user, private key, and pinned `known_hosts` entry as repository Actions secrets; do not use the server account password in the workflow.

# Deploying to a server (VPS)

This runs the stack with Docker Compose behind a TLS reverse proxy. **Read "What this setup is and is not" first.**

## What this setup is and is not

It is fine for an evaluation, a pilot, or your own devices. It is **not yet production-grade for paying customers**:

* The agent endpoint (port 8443) uses a self-signed development certificate that agents pin, and the device CA is generated on
  first start and stored on a Docker volume. Production needs a real TLS certificate and a managed CA bundle
  (`Solvix:Devices:CertificateAuthority:PfxPath`, see `docs/ARCHITECTURE.md`), set `ASPNETCORE_ENVIRONMENT=Production` and
  turn both generators off. That configuration has not been exercised end to end yet.
* The Windows agent bundled in the Docker image is unsigned. Mount your code-signed `Solvix.Agent.exe` over it for real rollouts.
* Backups, monitoring and updates are yours to run (see below).

None of these steps have been run on a server by the author of this document: follow them with a test domain first.

## Before you start

* A Linux VPS with Docker Engine and the Compose plugin, 2 vCPU / 4 GB RAM or more.
* A domain name whose **A record points at the VPS** (for example `solvix.example.com`).
* Firewall: open **80** and **443** (website) and **8443** (agents). Keep everything else closed, in particular 5432 and 6379.

## 1. Get the code and configure it

```bash
git clone https://github.com/Abdullah3san/Solvix-EndPoints.git && cd Solvix-EndPoints
git checkout claude/focused-albattani-5xt601   # or the branch you merged it into
cp .env.example .env
```

Edit `.env`. Every value below must be changed; generate secrets with `openssl rand -base64 32`.

| Setting | Value |
|---------|-------|
| `POSTGRES_PASSWORD`, `SOLVIX_DB_MIGRATOR_PASSWORD`, `SOLVIX_DB_APP_PASSWORD`, `REDIS_PASSWORD` | long random secrets |
| `SOLVIX_JWT_SIGNING_KEY` | `openssl rand -base64 48` |
| `SOLVIX_PLATFORM_OPERATOR_EMAIL` / `_PASSWORD` | your operator login (see `PLATFORM-OPERATIONS.md`); use a strong, unique password |
| `SOLVIX_ALLOW_PUBLIC_REGISTRATION` | `false` |
| `SOLVIX_COOKIE_SECURE` | `true` (the site is served over HTTPS) |
| `SOLVIX_CORS_ALLOWED_ORIGINS` | `https://solvix.example.com` |
| `SOLVIX_AGENT_PUBLIC_URL` | `https://solvix.example.com:8443/` |
| `SOLVIX_AGENT_TLS_HOSTNAMES` | `solvix.example.com,api` |
| `SOLVIX_AGENT_TLS_PASSWORD`, `SOLVIX_DEVICE_CA_PASSWORD` | long random secrets |

`.env` holds secrets: it is git-ignored, keep it that way and restrict it (`chmod 600 .env`).

Because the database password variables are used when the database is first created, set them **before** the first start.

## 2. Start the stack

```bash
docker compose up -d --build
docker compose ps          # postgres, redis, api, web healthy; migrator exited 0
docker compose logs api | grep -i "operator"   # "Platform operator account ... created from configuration"
```

Database, Redis, API and web ports are bound to `127.0.0.1` by default (`SOLVIX_BIND_ADDRESS`), so only the agent port
(8443) is reachable from outside until you add the proxy below.

## 3. HTTPS with Caddy (automatic certificates)

Install Caddy on the host (<https://caddyserver.com/docs/install>) and use this `/etc/caddy/Caddyfile`:

```caddy
solvix.example.com {
    encode zstd gzip
    reverse_proxy 127.0.0.1:3000
}
```

```bash
sudo systemctl reload caddy
```

Caddy obtains and renews the certificate itself (ports 80 and 443 must be reachable). Then open
`https://solvix.example.com/login` and sign in with the operator account; create companies under **Platform → Companies**.

## 4. Agents

The agent connects to `https://solvix.example.com:8443/` with mutual TLS. In this setup the server certificate is
self-signed; the downloaded agent package pins its fingerprint automatically (`SOLVIX_AGENT_TLS_CERT_SHA256` can be left empty
with the generated certificate). Download the package from **Download agent** in the app and run it on a Windows computer.

## Backups and updates

* **Database:** `docker compose exec -T postgres pg_dump -U postgres solvix | gzip > solvix-$(date +%F).sql.gz`, daily, copied off the server.
* **Keys:** back up the `solvix-pki` volume (device CA and agent TLS certificate). Losing it means every enrolled agent must re-enroll.
* **Update:** `git pull && docker compose up -d --build`. Migrations run automatically (the `migrator` service) before the API starts.
* **Logs:** `docker compose logs -f api web`.

## Checklist

- [ ] `.env` secrets changed, file permissions `600`
- [ ] Ports 5432, 6379, 8080 and 3000 are **not** reachable from the internet (`nmap` from another machine)
- [ ] `https://…/register` is a 404 and `POST /api/v1/tenant/register` answers 403
- [ ] Operator can sign in; a test company can be created, suspended and reactivated
- [ ] A test agent enrolls and appears under Devices
- [ ] A backup restored successfully on another machine

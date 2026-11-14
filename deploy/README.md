# Deploying Solvix EndPoints on a VPS

One Docker Compose stack: **Caddy** (automatic HTTPS) in front of the **web app**, the **API**, **PostgreSQL** and **Redis**.
Only Caddy is reachable from the internet; the API, database and Redis have no published ports.

```
https://solvix.example.com          -> Caddy -> web (Next.js) -> api (internal)         people
https://agents.solvix.example.com   -> Caddy -> api, ONLY /api/v1/agent/*                devices (mutual TLS)
```

## Requirements

* A VPS with 2 vCPU / 4 GB RAM / 20 GB disk, Ubuntu 22.04+ (any Linux with Docker works).
* Docker Engine and the Compose v2 plugin (`docker compose version`).
* A domain, with **two DNS records pointing at the VPS** (A and/or AAAA):
  `solvix.example.com` and `agents.solvix.example.com`.
* Ports **80 and 443** reachable (Let's Encrypt needs 80), and nothing else already listening on them.
  Firewall: allow 22, 80, 443 only (`ufw allow OpenSSH && ufw allow 80,443/tcp && ufw enable`).

## First install

```bash
git clone <your repository URL> solvix && cd solvix/deploy
cp .env.production.example .env
nano .env            # set SOLVIX_DOMAIN, ACME_EMAIL and every secret (the file explains how to generate them)
chmod 600 .env
./scripts/deploy.sh
```

`deploy.sh` checks the configuration, builds the images, applies database migrations (a one-shot `migrator`
container with the schema-owner role), starts everything and waits until it is healthy. Caddy obtains the
certificates on first start (give it a minute).

Then open `https://<SOLVIX_DOMAIN>/register` and create your workspace.

> **Workspace registration is public.** Anyone who can reach the site can create *their own* workspace (tenants are
> isolated from each other, but it uses your server). For a private install, restrict `/register` after you have
> created yours, e.g. in the `{$SOLVIX_DOMAIN}` block of `Caddyfile`:
> ```
> @register path /register /api/auth/register
> respond @register 404
> ```
> and reload (`docker compose restart caddy`).

## Verify the agent endpoint (do this once after the first deploy, and after any proxy change)

```bash
./scripts/verify-proxy.sh https://agents.solvix.example.com
```

Without arguments it checks that nothing but agent routes is exposed and that a request without a device certificate
is refused. For the full check (genuine certificate accepted, **forged certificate header refused**), enroll a test
agent first and pass its certificate: `./scripts/verify-proxy.sh https://agents.… device.crt device.key`
(the Linux agent keeps these in its data directory: `device.crt`, `keys/*.pem`; Windows agents keep the key
non-exportable, so use a test agent). It must print `PASS`.

Why it matters: the API trusts the `X-Client-Cert` header from Caddy as proof of the device certificate (public
certificates are not secret, the TLS handshake is the proof). That is safe only because (a) the API is not published,
and (b) Caddy *sets* that header on every request from the TLS session. Keep it that way: never publish port 8080,
and never add a `header_up -X-Client-Cert` next to the set in the `Caddyfile` (Caddy applies deletions last and would
delete the value, breaking every device).

## Enrolling Windows computers

1. In the web app: *Endpoints → Enrollment → New enrollment token* (optionally with group, branch and department,
   e.g. one token per store) → **Download agent package**.
2. On each Windows computer: extract the zip and double-click `Solvix.Agent.exe` (approve the administrator prompt).
   For deployment tools: `Solvix.Agent.exe install --silent`.

The package is served from `deploy/agent-package` when you set `SOLVIX_AGENT_PACKAGE_DIR=/opt/solvix/signed-agent`;
otherwise from the **unsigned** build inside the image. For production, sign it:

```bash
# on a Windows build machine (see src/agent/installer/Build-AgentPackage.ps1): publish, then sign Solvix.Agent.exe
# with your code-signing certificate; copy Solvix.Agent.exe and appsettings.json to deploy/agent-package/
# set SOLVIX_AGENT_PACKAGE_DIR=/opt/solvix/signed-agent in .env, then:
docker compose up -d api
```

Put your certificate's thumbprint into `Updates:TrustedPublisherThumbprints` of that `appsettings.json` so agents
accept signed updates. The server serves the exe byte-for-byte, so the signature stays valid.

## Updating

```bash
cd solvix/deploy && ./scripts/deploy.sh --pull
```

This backs up first, pulls, rebuilds, migrates and restarts. Downtime is a few seconds while the API restarts.
Images are tagged with the git commit (`solvix-api:<commit>`).

## Backups

`./scripts/backup.sh` writes `backups/db-<time>.dump` (PostgreSQL custom format) and `backups/pki-<time>.tgz` (the
**device CA private key**), keeps the newest 14 of each, and runs automatically before every `deploy.sh` update.
**Copy them off the server** (another host or object storage) and run it daily:

```bash
( crontab -l 2>/dev/null; echo "17 3 * * * cd /opt/solvix/deploy && ./scripts/backup.sh >> backups/backup.log 2>&1" ) | crontab -
```

Losing the PKI backup means every enrolled device has to re-enroll.

**Restore** (this exact procedure was tested by wiping the schema and restoring; replace `<time>`):

```bash
cd deploy
docker compose stop api web
docker compose exec -T postgres pg_restore -U postgres -d solvix --clean --if-exists --no-owner --role=solvix_migrator \
    < backups/db-<time>.dump
docker compose up -d
```

`--role=solvix_migrator` matters: objects must be owned by the schema-owner role so the runtime role's privileges and
row-level security apply exactly as before (use your `SOLVIX_DB_MIGRATOR_USER` / `POSTGRES_DB` if you changed them).

**On a new server:** clone the repository, create `deploy/.env` with the *same* secrets, `docker compose up -d postgres`
(its init script creates the database roles), restore the database as above, restore the device CA, then `./scripts/deploy.sh`:

```bash
docker volume create solvix_solvix-pki
docker run --rm -v solvix_solvix-pki:/pki -v "$PWD/backups":/b alpine tar xzf /b/pki-<time>.tgz -C /pki
```

## Rollback

Migrations only go forward. To go back to a previous version: `git checkout <previous commit> && ./scripts/deploy.sh`.
If that version is older than the last migration, first restore the database backup taken by the update
(`backups/db-<time>.dump`, see above), otherwise the old code runs against the newer schema.

## Operations

```bash
docker compose ps                      # status and health
docker compose logs -f api             # structured JSON logs (correlation id, tenant, user)
docker compose logs --tail 100 caddy
curl -s https://<SOLVIX_DOMAIN>/api/proxy/v1/devices   # 401 without a session = the stack is answering
```

* API health: `docker compose exec api wget -qO- http://localhost:8080/health/ready` (PostgreSQL + Redis).
* Rotate the JWT key: change `SOLVIX_JWT_SIGNING_KEY`, `docker compose up -d api` (everyone signs in again).
* Security updates for the OS and `docker compose pull && ./scripts/deploy.sh` for base images, regularly.

## Troubleshooting

| Symptom | Likely cause |
|---------|--------------|
| Browser shows a certificate error | DNS does not point at the VPS yet, or port 80 is blocked; `docker compose logs caddy` shows the ACME error. |
| `deploy.sh` says a variable is empty | Fill `deploy/.env` (no placeholders). |
| Devices enroll but stay `401` | The agent endpoint is misconfigured: run `verify-proxy.sh`; check `Caddyfile` against the repository version. |
| Enrollment page says "agent package not configured" | `SOLVIX_AGENT_PACKAGE_DIR` points at a folder without `Solvix.Agent.exe`. |
| Agents cannot reach the server | `agents.<domain>` DNS record missing, or outbound HTTPS from the device is filtered. |

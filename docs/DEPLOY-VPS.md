# Deploying Solvix (Phase 01) to a VPS

Single-host deployment with Docker Compose. Only ports 80 and 443 are public; Caddy
obtains and renews the TLS certificate automatically.

## 1. Prerequisites

- A VPS with Ubuntu 22.04+ (or any Linux with Docker), at least 2 GB RAM.
- Docker Engine and the Compose plugin:
  ```bash
  curl -fsSL https://get.docker.com | sh
  ```
- A DNS **A** record (and **AAAA** if IPv6) for your domain pointing at the VPS.
- Firewall allowing 22, 80 and 443 only, e.g. with UFW:
  ```bash
  ufw allow OpenSSH && ufw allow 80/tcp && ufw allow 443/tcp && ufw enable
  ```

## 2. Get the code

```bash
git clone <repo-url> /opt/solvix
cd /opt/solvix
git checkout <release-branch-or-tag>
```

## 3. Configure secrets

```bash
cp .env.production.example .env.production
chmod 600 .env.production
```

Edit `.env.production` and replace every `CHANGE_ME`:

```bash
openssl rand -base64 48   # use a distinct output for each password and for SOLVIX_JWT_SIGNING_KEY
```

Set `SOLVIX_DOMAIN` and `SOLVIX_PUBLIC_ORIGIN` to your real domain (`https://...`).

### No domain yet? Test over plain HTTP on the IP

Set these in `.env.production` instead of the domain values:

```
SOLVIX_DOMAIN=:80
SOLVIX_PUBLIC_ORIGIN=http://<VPS_IP>
SOLVIX_COOKIE_SECURE=false
```

Then open `http://<VPS_IP>/register`. This is for testing only: traffic is unencrypted.
Once you have a domain, switch back to the HTTPS values, set `SOLVIX_COOKIE_SECURE=true`,
and run the same `up -d --build` command again.

## 4. Build and start

```bash
docker compose --env-file .env.production -f docker-compose.prod.yml up -d --build
```

Start-up order is enforced by health checks: PostgreSQL and Redis → `migrator` (applies
EF Core migrations, then exits) → `api` → `web` → `caddy`.

## 5. Verify

```bash
docker compose --env-file .env.production -f docker-compose.prod.yml ps
docker compose --env-file .env.production -f docker-compose.prod.yml logs migrator
curl -sS https://<your-domain>/login -o /dev/null -w "%{http_code}\n"   # expect 200
```

Then open `https://<your-domain>/register` and create the first workspace.

## 6. Update

```bash
cd /opt/solvix
git pull
docker compose --env-file .env.production -f docker-compose.prod.yml up -d --build
```

The migrator runs again on every deploy and is a no-op when the schema is current.

## 7. Backups

Back up the PostgreSQL volume regularly; Redis holds only cache and short-lived data.

```bash
docker compose --env-file .env.production -f docker-compose.prod.yml exec -T postgres \
  pg_dump -U postgres solvix | gzip > solvix-$(date +%F).sql.gz
```

Store dumps off the VPS. Test a restore before relying on them.

## Notes and known limits (Phase 01)

- The API is not publicly reachable; all browser traffic goes through the Next.js BFF.
- No refresh tokens, password reset, e-mail verification or login rate limiting yet
  (see `docs/PHASE-01-SUMMARY.md`, "Known limitations"). Treat this deployment as
  pre-production until those are addressed.
- Logs are JSON on stdout: `docker compose ... logs -f api web`.

# Deploying Solvix on a single VPS

Everything runs on one server with Docker Compose (`docker-compose.prod.yml`):

```
Internet ──443/80──► caddy ──► web (Next.js BFF) ──► api ──► postgres
                     (TLS)        edge network    │   backend network   └─► redis
                                                  └── private, no published ports
```

* **Caddy** is the only container with published ports. It obtains and renews the
  Let's Encrypt certificate, redirects HTTP to HTTPS, adds HSTS, and overwrites
  `X-Forwarded-For` with the real client address, so a client cannot fake its address
  to escape rate limits or pollute the audit log.
* **web** holds the session cookies (`__Host-`, Secure, httpOnly) and is the only thing
  that talks to the API.
* **api**, **postgres** and **redis** have no published ports. This matters on a VPS:
  ports published by Docker bypass UFW/iptables rules, so anything published is
  reachable from the internet whatever the firewall says.
* **migrator** applies database migrations and exits before the API starts, on every
  deploy.
* With a single API instance, the in-memory rate limiter covers all traffic.

## 1. Server

* Ubuntu 24.04 LTS (or Debian 12), **2 vCPU / 4 GB RAM** minimum. Images are built on the
  server, and the .NET and Next.js builds need about 2–3 GB of memory at their peak.
  Add 2 GB of swap on smaller machines. 20 GB disk or more.
* A domain name with an **A record** (and AAAA if the VPS has IPv6) pointing at the server.
* An SMTP account at a transactional mail provider (Postmark, Amazon SES, Mailgun,
  Brevo, …) with SPF and DKIM set up for the sender domain. Do not send directly from
  the VPS: many providers block port 25, and such mail lands in spam.

```bash
# as root, once
apt update && apt -y full-upgrade && apt -y install unattended-upgrades ufw git
curl -fsSL https://get.docker.com | sh
adduser --disabled-password --gecos "" solvix && usermod -aG docker solvix

ufw default deny incoming && ufw default allow outgoing
ufw allow OpenSSH && ufw allow 80/tcp && ufw allow 443/tcp && ufw allow 443/udp
ufw enable
```

Use SSH keys only (`PasswordAuthentication no` in `/etc/ssh/sshd_config`). Membership
of the `docker` group is equivalent to root, so give it only to the deploy user.

## 2. Configure

```bash
sudo mkdir -p /opt/solvix && sudo chown solvix: /opt/solvix
sudo -iu solvix
git clone <repository-url> /opt/solvix && cd /opt/solvix
cp .env.production.example .env.production && chmod 600 .env.production
```

Fill in every value in `.env.production`. Generate each secret separately:

```bash
openssl rand -hex 32
```

| Value | Notes |
|-------|-------|
| `SOLVIX_DOMAIN`, `SOLVIX_ACME_EMAIL` | Public host name and Let's Encrypt contact |
| `POSTGRES_PASSWORD`, `SOLVIX_DB_MIGRATOR_PASSWORD`, `SOLVIX_DB_APP_PASSWORD`, `REDIS_PASSWORD` | Database passwords are applied only when the data volume is first created; changing them later needs `ALTER ROLE` |
| `SOLVIX_JWT_SIGNING_KEY` | Can be rotated: users are signed back in silently with their refresh token |
| `SOLVIX_IDENTITY_DATA_PROTECTION_KEY` | Encrypts MFA secrets. **Never change it and back it up outside the server**: losing it breaks every enrolled authenticator |
| `SOLVIX_SMTP_*`, `SOLVIX_EMAIL_FROM` | Required: sign-up, password reset and invitations depend on e-mail |

Store a copy of `.env.production` in your password manager.

## 3. Start

```bash
docker compose -f docker-compose.prod.yml --env-file .env.production up -d --build
docker compose -f docker-compose.prod.yml --env-file .env.production ps
```

The first start takes a few minutes (image builds and certificate issuance). Open
`https://<your domain>/register`, create the first workspace and confirm the e-mail.
That account is the workspace's Super Administrator.

A shorter command for everyday use:

```bash
alias dc='docker compose -f /opt/solvix/docker-compose.prod.yml --env-file /opt/solvix/.env.production'
```

### Verify

```bash
curl -sI http://<domain>/login | head -1                # 308 redirect to https
curl -sI https://<domain>/login | grep -i strict        # HSTS present
curl -s -o /dev/null -w '%{http_code}\n' https://<domain>/api/v1/auth/me   # 404: the API is not public
nc -zv <server-ip> 5432 6379 8080                        # all must fail from another machine
```

After signing in, open **Audit log** and check that the *Origin* column shows real
public addresses. If every entry shows a `172.x` address, Docker is replacing the
client address (this happens with IPv6 traffic through Docker's userland proxy). Set
`{"userland-proxy": false}` in `/etc/docker/daemon.json` and restart Docker, or remove
the AAAA record.

## 4. Updates

```bash
cd /opt/solvix
./scripts/backup-db.sh                       # always back up before upgrading
git pull
dc up -d --build                             # migrator runs before the new API starts
dc image prune -f
```

Migrations only move forward automatically. To roll back a release that changed the
schema, restore the backup taken before the upgrade (see below) and check out the
previous version.

## 5. Backups

`scripts/backup-db.sh` writes a compressed `pg_dump` (schema, data, row-level security
policies, audit triggers) to `./backups`, verifies it can be read, and keeps 14 days of
dumps. Schedule it and copy the dumps off the server:

```bash
crontab -e
15 3 * * * cd /opt/solvix && ./scripts/backup-db.sh >> /var/log/solvix-backup.log 2>&1
45 3 * * * rclone copy /opt/solvix/backups remote:solvix-backups   # or restic, borg, rsync, …
```

What must be recoverable: the database dumps, `.env.production` (above all
`SOLVIX_IDENTITY_DATA_PROTECTION_KEY`), and optionally the `caddy-data` volume (the
certificates can be re-issued, so it is not critical). Redis holds only caches and
rate-limit state and needs no backup.

Restore into a fresh stack:

```bash
dc up -d postgres
dc exec -T postgres sh -c 'dropdb -U postgres solvix && createdb -U postgres -O solvix_migrator solvix'
dc exec -T postgres pg_restore -U postgres -d solvix --no-owner --role=solvix_migrator /backups/solvix-<timestamp>.dump
dc up -d
```

Test a restore occasionally. A backup that has never been restored is an assumption,
not a backup.

## 6. Operations

| Task | Command |
|------|---------|
| Status / health | `dc ps` |
| Logs (JSON) | `dc logs -f api web caddy` (rotated at 5 × 20 MB per container) |
| Restart one service | `dc restart api` |
| Database shell | `dc exec postgres psql -U postgres solvix` |
| Disk usage | `docker system df` |

* **Sign-in problems:** the workspace audit log (Access → Audit log) shows failed
  sign-ins, lockouts and their reasons. A locked account unlocks by itself after the
  lockout period, or when its owner resets the password.
* **E-mail not arriving:** `dc logs api | grep -i "e-mail"` shows delivery failures. Check
  the SMTP credentials and the provider's sending log.
* **Certificate problems:** `dc logs caddy`. Ports 80 and 443 must be reachable and DNS
  must point at the server.

## 7. Security checklist

- [ ] `ufw status` allows only SSH, 80 and 443
- [ ] SSH: keys only, no root password login
- [ ] `.env.production` is `chmod 600`, not in git, and copied to a password manager
- [ ] `SOLVIX_IDENTITY_DATA_PROTECTION_KEY` backed up off the server
- [ ] Nightly backups run and are copied off the server; one restore has been tested
- [ ] `unattended-upgrades` enabled for OS security patches; images rebuilt monthly (`dc build --pull`)
- [ ] The audit log shows real client addresses (see *Verify*)
- [ ] Consider requiring MFA (Security policy → Require multi-factor authentication) for
      workspaces with administrators

## Tested

The stack in this repository was built from the committed Dockerfiles and run with
`docker-compose.prod.yml`, using the domain `localhost` (Caddy's internal CA) and a
local SMTP sink:

* All containers healthy. Only Caddy publishes ports; the API is unreachable from outside.
* HTTP redirects to HTTPS; HSTS and `__Host-` Secure cookies are set.
* Logins sent with spoofed `X-Forwarded-For` values were all recorded under the real
  client address, and the rate limit applied after 10 attempts despite the rotating
  fake addresses.
* The 21-step browser scenario passed over HTTPS with e-mail delivered by SMTP:
  sign-up, verification, invitation, permissions, MFA, role change, deactivation,
  audit log and sign-out.
* The backup script produced a dump that restored with all data, all 13 row-level
  security tables and both audit triggers. Data survived a full `down`/`up`.
* The restore commands in section 5 were rehearsed on a wiped stack: the workspace came
  back, the migrator found every migration already applied, and the app's database role
  still saw no rows without a tenant (row-level security intact).

This test found one defect, which is fixed: a server-side redirect used the
container's internal address (`0.0.0.0:3000`) instead of the public site.

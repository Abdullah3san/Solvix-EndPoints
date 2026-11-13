# Deploying Solvix on a single VPS

Everything runs on one server with Docker Compose (`docker-compose.prod.yml`):

```
Browsers ──443/80──► caddy ──► web (Next.js BFF) ──► api :8080 ──► postgres
                     (TLS)        edge network    │   backend network    └─► redis
                                                  └── private, no published ports
Windows agents ──8443 (mutual TLS)──────────────────► api :8443
```

* **Caddy** publishes 80/443 for browsers. It obtains and renews the Let's Encrypt
  certificate, redirects HTTP to HTTPS, adds HSTS, and overwrites `X-Forwarded-For`
  with the real client address, so a client cannot fake its address to escape rate
  limits or pollute the audit log.
* **web** holds the session cookies (`__Host-`, Secure, httpOnly) and is the only thing
  that talks to the API's HTTP port (8080).
* **api** publishes exactly one port, **8443**: the agents' mutual-TLS endpoint, where
  TLS is terminated by the API itself (see [Windows agents](#windows-agents)). Its HTTP
  port 8080, **postgres** and **redis** have no published ports. This matters on a VPS:
  ports published by Docker bypass UFW/iptables rules, so anything published is
  reachable from the internet whatever the firewall says.
* **migrator** applies database migrations and exits before the API starts, on every
  deploy.
* With a single API instance, the in-memory rate limiter covers all traffic.

> **No domain or SMTP yet?** See [Test mode on a bare IP](#test-mode-on-a-bare-ip) to run
> the same stack over plain HTTP on the server's IP for evaluation.

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
ufw allow 8443/tcp   # Windows agents (Docker publishes it regardless; the rule documents intent)
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
| `SOLVIX_DEVICE_CA_PASSWORD` | Encrypts the device CA file. **Never change it**; back it up with `secrets/device-ca.pfx` |
| `SOLVIX_COMMAND_STANDBY_PUBLIC_KEY` | Printed by `init-agent-pki.sh` (next step). Set it before enrolling any device |
| `SOLVIX_AGENT_PORT`, `SOLVIX_SECRETS_DIR` | Defaults `8443` and `./secrets` |

Store a copy of `.env.production` in your password manager.

Then create the agent PKI (once) and copy it off the server together with
`.env.production`:

```bash
./scripts/init-agent-pki.sh
```

It writes `secrets/device-ca.pfx` (device CA), `secrets/command-signing.pem` (command
signing key) and `secrets/command-signing-standby.pem`, and prints a
`SOLVIX_COMMAND_STANDBY_PUBLIC_KEY=…` line: add that line to `.env.production`, then move
the standby file off the server. Details in [Windows agents](#windows-agents).

## 3. Start

The agent endpoint uses Caddy's certificate, so on the very first start Caddy runs on
its own until it has one:

```bash
docker compose -f docker-compose.prod.yml --env-file .env.production up -d --no-deps caddy
docker compose -f docker-compose.prod.yml --env-file .env.production logs -f caddy   # wait for "certificate obtained successfully", then Ctrl+C
./scripts/sync-agent-tls.sh
docker compose -f docker-compose.prod.yml --env-file .env.production up -d --build
docker compose -f docker-compose.prod.yml --env-file .env.production ps
```

Later starts and updates need only the `up -d --build` line. The first start takes a few
minutes (image builds and certificate issuance). Open
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
# Agent endpoint: publicly trusted certificate, asks for a client certificate, 401 without one
openssl s_client -connect <domain>:8443 -servername <domain> </dev/null 2>/dev/null | grep -E 'issuer=|Verify return'
curl -s -o /dev/null -w '%{http_code}\n' -X POST https://<domain>:8443/api/v1/agent/heartbeat   # 401
```

After signing in, open **Audit log** and check that the *Origin* column shows real
public addresses. If every entry shows a `172.x` address, Docker is replacing the
client address (this happens with IPv6 traffic through Docker's userland proxy). Set
`{"userland-proxy": false}` in `/etc/docker/daemon.json` and restart Docker, or remove
the AAAA record.

## Windows agents

### Design: TLS terminates in the API, not in Caddy

Agents authenticate with an X.509 device certificate issued by the platform device CA
(see `docs/ARCHITECTURE.md`, *Device identity and authentication*). The API supports
two ways to receive it, and this deployment uses the first:

| | (a) Kestrel `Agents` endpoint — **used** | (b) Caddy terminates TLS, forwards the certificate in a header |
|---|---|---|
| Who checks proof of possession | the API, in its own TLS handshake | Caddy; the API trusts a header |
| What an attacker needs to impersonate a device | the device's private key (kept in the TPM / CNG) | any request reaching the API with that header — and device certificates are public (sent in clear in TLS 1.2 handshakes) |
| Trusted-proxy contract | none: agents connect directly, their address is the TCP peer | the API must be reachable *only* through Caddy, but the web container must reach it too; `Solvix:Network:TrustedProxies` would have to trust both, and the certificate-forwarding middleware does not check which hop sent the header |
| Server certificate | Caddy's Let's Encrypt certificate, copied by `scripts/sync-agent-tls.sh` | Caddy's, natively |

(b) would make device authentication only as strong as "nothing but Caddy can talk to
the API", which this topology cannot guarantee, so the API keeps
`Solvix__Devices__ForwardedClientCertificateHeader` explicitly empty and serves agents
itself on port 8443. `TrustedProxies` still lists only the web container.

### Files and secrets

All four files are mounted read-only into the api container as Compose file secrets
(`/run/secrets/…`), owned by the container user (uid 1654) with mode 0600. Nothing is
generated by the API in production (`…AutoGenerate=false`), so a missing or unreadable
file stops the API at start-up instead of failing later.

| File (in `SOLVIX_SECRETS_DIR`) | Created by | Configures | Lose it and… |
|---|---|---|---|
| `agent-tls.crt`, `agent-tls.key` | `sync-agent-tls.sh` (from Caddy) | `Kestrel__Endpoints__Agents__Certificate__Path` / `KeyPath` | nothing: run the script again |
| `device-ca.pfx` (+ `SOLVIX_DEVICE_CA_PASSWORD`) | `init-agent-pki.sh` | `Solvix__Devices__CertificateAuthority__PfxPath` / `PfxPassword` | every device must re-enroll |
| `command-signing.pem` | `init-agent-pki.sh` | `Solvix__RemoteManagement__CommandSigning__KeyPath` | agents reject every command until re-enrolled (or until you switch to the standby key) |
| `command-signing-standby.pem` | `init-agent-pki.sh` | public key only: `…CommandSigning__AdditionalPublicKeys__0` via `SOLVIX_COMMAND_STANDBY_PUBLIC_KEY` | keep it **off the server**; it is the only way to rotate the command key |

`secrets/device-ca.crt` is the CA's public certificate, for reference.

### Agent TLS certificate renewal

Caddy renews its certificate about 30 days before expiry; Kestrel reads the certificate
only at start-up. Schedule the sync script daily; it does nothing unless the certificate
changed, and then restarts the API (a few seconds, roughly every 60 days; agents retry):

```bash
crontab -e
20 4 * * * cd /opt/solvix && ./scripts/sync-agent-tls.sh >> /var/log/solvix-agent-tls.log 2>&1
```

Windows trusts Let's Encrypt, so agents need no certificate pin. To use a certificate
from another CA instead, place the PEM chain and key at `secrets/agent-tls.crt` /
`agent-tls.key` (owner 1654, mode 0600) and do not schedule the script.

### Enrolling a device

Create a token under *Endpoints → Enrollment*, then on the device, as administrator:

```powershell
.\Install-SolvixAgent.ps1 -ServerUrl https://<domain>:8443/ -EnrollmentToken svxe_...
```

No `-ServerCertificateSha256` is needed with a publicly trusted certificate. Enrollment
is rate limited per device address (`Solvix:Devices:Enrollment:RateLimitPerMinute`, 30).
If many devices share one NAT address, raise it with
`Solvix__Devices__Enrollment__RateLimitPerMinute` in the api environment.

### Rotating the command signing key

Agents pin the server's key set the first time they fetch commands and never accept a
new key afterwards. The standby key exists for this: its public key is published from
day one, so every agent pins both. To rotate:

1. Copy the standby private key back to `secrets/command-signing.pem` (owner 1654, mode 0600),
   keeping the old file as a backup.
2. Set `SOLVIX_COMMAND_STANDBY_PUBLIC_KEY` to the **old** key's public key
   (`openssl pkey -in old.pem -pubout -outform DER | base64 -w0`) for a transition period,
   or to empty.
3. `dc up -d`. Devices enrolled before the standby key was published must re-enroll.

Generate a new standby key afterwards only for devices enrolled from then on; already
enrolled agents never pick it up.

## Test mode on a bare IP

For evaluation before a domain and an SMTP provider exist. It runs the production
stack with `docker-compose.test-ip.yml` layered on top:

| | Production | Test mode |
|---|---|---|
| Browser access | `https://<domain>` (80/443, Let's Encrypt) | `http://<ip>:8080` (no TLS) |
| Session cookies | Secure, `__Host-` | not Secure (required over HTTP) |
| E-mail | sent via SMTP; verification required | only logged; verification **off** |
| Agent endpoint | `https://<domain>:8443`, public certificate | `https://<ip>:8443`, self-signed, agents pinned with `-ServerCertificateSha256` |

Password reset and e-mail invitations do not work in test mode (no mail is sent).
Traffic is unencrypted: **use test data only**. 8080 is used instead of 80 so the
stack can share a server whose 80/443 already belong to another project.

```bash
cd /opt/solvix
./scripts/setup-test-ip.sh <server-ip>      # as root, once (safe to re-run)
docker compose -f docker-compose.prod.yml -f docker-compose.test-ip.yml --env-file .env.production up -d --build
docker compose -f docker-compose.prod.yml -f docker-compose.test-ip.yml --env-file .env.production ps
```

`setup-test-ip.sh` checks that ports 8080/8443 and the `172.30.10.0/24` backend subnet
are free, generates every secret into `.env.production` (never overwriting existing
ones), creates the agent PKI and a self-signed agent certificate for the IP, and prints
the certificate pin. Use `SOLVIX_HTTP_PORT=... SOLVIX_AGENT_PORT=...` in front of it to
pick other ports. Back up `.env.production` and `secrets/` off the server afterwards.

**Moving to production later.** Set `SOLVIX_DOMAIN`, `SOLVIX_ACME_EMAIL` and the SMTP
values in `.env.production`, free ports 80/443, replace `secrets/agent-tls.*` with
`./scripts/sync-agent-tls.sh` (see [Agent TLS certificate renewal](#agent-tls-certificate-renewal)),
then start with the normal command (without `docker-compose.test-ip.yml`). Devices
enrolled against the IP with a pinned self-signed certificate must be re-installed
with the domain URL.

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
`SOLVIX_IDENTITY_DATA_PROTECTION_KEY` and `SOLVIX_DEVICE_CA_PASSWORD`),
`secrets/device-ca.pfx` and `secrets/command-signing.pem` (without them every device has
to be re-enrolled), the offline standby command key, and optionally the `caddy-data`
volume (the certificates can be re-issued, so it is not critical). Back the secrets up
once, after `init-agent-pki.sh`; they do not change. Redis holds only caches and
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
| Agent endpoint certificate | `./scripts/sync-agent-tls.sh` (daily from cron) |
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
* **Agents cannot connect:** `dc logs api | grep -i "device certificate"` shows rejected
  certificates and why. Check that port 8443 is reachable from the device's network and
  that `secrets/agent-tls.crt` has not expired
  (`openssl x509 -in secrets/agent-tls.crt -noout -enddate`; is the cron job running?).

## 7. Security checklist

- [ ] `ufw status` allows only SSH, 80, 443 and 8443; `nc -zv <ip> 8080` fails from outside
- [ ] `secrets/` is mode 700, its files 0600 owned by uid 1654; `device-ca.pfx`,
      `command-signing.pem` and the standby key are backed up off the server, and the
      standby key is NOT on the server
- [ ] `SOLVIX_COMMAND_STANDBY_PUBLIC_KEY` was set before the first device enrolled
- [ ] `sync-agent-tls.sh` runs daily from cron
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

The agent endpoint was tested on the same stack (domain `localhost`, so Caddy's internal
CA stood in for Let's Encrypt), with the API image built from the committed Dockerfile:

* `init-agent-pki.sh` created the CA and keys owned by uid 1654; a second run changed nothing.
  The API loaded the device CA and the command signing key at start-up (key id matched
  the file); with the standby public key set it published both keys.
* `sync-agent-tls.sh` installed Caddy's certificate, reported "unchanged" on the next run,
  and after the installed copy was altered reinstalled it and restarted the API.
* Port 8443 served Caddy's certificate and asked for a client certificate. Requests
  without a certificate, with a certificate in an `X-Client-Cert` header, and with a
  self-signed certificate were rejected (401); a certificate signed by the device CA
  passed chain validation and was rejected only by the database check. Port 8080 was not
  reachable from the host.
* With the command key path pointing at a missing file, the API refused to start.

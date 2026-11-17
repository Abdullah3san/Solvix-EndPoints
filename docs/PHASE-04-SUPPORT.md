# Help desk: tickets and chat through the agent

People using enrolled laptops can report a problem and talk to a technician without opening a browser;
technicians work from the web console. Remote command sessions are described at the end; screen sharing is not built.

## For the person at the laptop

```
Solvix.Agent.exe ticket                       # prompts: what is wrong? any details?
Solvix.Agent.exe ticket --subject "VPN will not connect" --priority high
Solvix.Agent.exe tickets                      # status of tickets raised from this computer
Solvix.Agent.exe chat                         # live chat with IT; /quit to leave
```

These work as an ordinary user. The device key is machine-protected and only the agent *service* can use
it, so the commands talk to the running service over a local named pipe (`solvix-agent-local`):

* Windows: the pipe is open to authenticated local users only and explicitly denied to the network.
  Who is asking is taken from the operating system (`RunAsClient`), never from what the caller claims.
* The pipe offers four fixed operations (submit ticket, list tickets, poll chat, send chat), 64 KB request
  limit, 8 concurrent requests, friendly errors (server unreachable, not enrolled, throttled).

## For technicians (web console, administrators)

* *Help desk → Tickets*: list/filter/search, create, reply, internal notes (never sent to the laptop),
  change status and priority, assign to a member, jump to the device.
* *Help desk → Chat* and a device's *Chat* tab: conversations with people at their laptops (polling every 3 s).

## API

| Caller | Endpoint |
|--------|----------|
| Agent (mTLS) | `POST/GET /api/v1/agent/tickets`, `GET /api/v1/agent/chat?after=N&wait=S` (long poll ≤ 25 s), `POST /api/v1/agent/chat` |
| Admin | `GET/POST /api/v1/tickets`, `GET/PATCH /api/v1/tickets/{id}`, `POST /api/v1/tickets/{id}/comments` |
| Admin | `GET /api/v1/chat/conversations`, `GET/POST /api/v1/devices/{id}/chat` |
| Member | `POST /api/v1/tickets` (any member may raise a ticket) |

All traffic is still outbound from the laptop. Responses are data only; nothing here can instruct the agent to run anything.

## Data and security

New module `Solvix.Modules.Support`; tables `tickets`, `ticket_comments`, `device_chat_messages` with forced
Row-Level Security, tenant-safe composite foreign keys, migrations `SupportTicketsAndChat` and `SupportSecurity`.
Deleting a device keeps its tickets (`device_id` is cleared); its chat is deleted with it. Per-device throttle:
5 agent tickets / 10 minutes. Technicians cannot chat with retired devices.

## Verification

* Integration (real PostgreSQL/Redis): ticket round trip agent → console → agent, validation and throttling,
  chat both directions and long poll, tenant isolation (tickets, chat, linking another tenant's device),
  authentication, device deletion, assignment; plus a full-chain test of the real agent core and local pipe
  against the real API.
* Agent: pipe protocol, OS-identity override, error mapping, garbage/oversized input.
* Frontend: helpers and the chat panel.

### Not verified / not built

* The Windows pipe ACL, `RunAsClient` identity and the CLI on a real Windows laptop were compiled, not executed.
* There is no graphical tray app yet; the CLI is the user interface. A small tray/window app that talks to the same
  pipe is the natural next step, as are chat notifications to the user when a technician writes.
* Chat uses long polling and 3 s polling in the console; WebSockets/SignalR would make it instant.
* Ticket numbers are global (database identity), not per tenant. No e-mail notifications, SLAs or attachments.


---

# Remote command sessions

A technician opens a command shell (PowerShell on Windows) on a laptop from the console. This reverses the
Phase 03 rule that the agent never runs server-supplied input, so it is **off by default and controlled on the computer**.

## Turning it on (on the computer, by whoever installs the agent)

```
Solvix.Agent.exe install --remote-access consent       # the person at the computer approves each session
Solvix.Agent.exe install --remote-access unattended    # no prompt (servers, kiosks); still audited
```

This writes `SolvixAgent:RemoteAccess:Mode` to the machine's `agent.json`. Neither the server nor the downloadable
enrollment package can enable it. The agent reports its mode with each heartbeat; the console only shows it and
refuses to start a session when it is `Disabled` (or unknown, e.g. an older agent).

## How a session works

1. Administrator: *Device → Remote → Start remote session* (with a reason).
2. The agent, which long-polls outbound, receives the request. In *consent* mode a Yes/No dialog is shown to the
   signed-in person (`WTSSendMessage`; no answer, no signed-in user or any failure = **no**). In *unattended* mode it accepts.
3. The agent starts the shell and relays text: technician input → shell, shell output → console (long polls both ways,
   so it also works through the web proxy; this is a line-oriented shell, not a full terminal emulator).
4. Either side can end it; the agent kills the shell when the session ends or the shell exits.

## Safeguards

* Administrators only (`TenantAdmin`); one open session per computer; a request expires after 2 minutes unanswered,
  an active session ends after 15 minutes idle or 4 hours total.
* Audit (`remote_sessions`): who asked, why, device, consent (person vs. unattended policy), start/end and why it ended.
  Rows outlive the device and the user. **Terminal content is relayed, not stored**; in-flight text is deleted after the session.
* A computer can only read and answer its own sessions; other tenants and other computers get 404. Row-Level Security
  on both tables. Every request, acceptance, denial and technician end is also logged as a warning.
* The shell runs as the agent's own account (`NT SERVICE\SolvixAgent`), with that account's limited rights.

## Verification

Integration (real PostgreSQL): full lifecycle with audit, policy gate (disabled/unknown), single open session, deny,
expiry and idle timeout, long polls, tenant/device isolation, authentication and input limits, retired device,
audit surviving device deletion. End-to-end with the real agent worker and a **real shell** (`/bin/sh`) against the
real API: unattended session runs `echo`, `exit` ends the session; consent mode with no one to ask declines.
Agent unit tests: disabled mode never polls, decline never starts a shell, input/output relay, shell exit, shell failure,
installer flag parsing, and that the package cannot enable remote access. Frontend: helpers and panel.

## Not verified / not built

* The Windows consent dialog (`WTSSendMessage`), the PowerShell shell and the install flag on a real Windows laptop:
  compiled, not executed.
* **Not a privileged shell**: it runs with the service account's rights. Running as SYSTEM or an admin would need a
  deliberate, separate design (like the updater task) and is not done.
* No screen sharing or file transfer. No keystroke/command transcript is kept (a tenant-level "record sessions" option
  would be the next audit step). No tenant-wide kill switch yet beyond not enabling it on computers.
* Latency is ~0.3 s per round trip (polling relay); WebSockets would remove it.

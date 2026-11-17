# Help desk: tickets and chat through the agent

People using enrolled laptops can report a problem and talk to a technician without opening a browser;
technicians work from the web console. Remote sessions (command shell, screen sharing with optional control, file transfer) are described at the end.

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
* Screen sharing and file transfer are covered in the next section. No keystroke/command transcript is kept (a tenant-level "record sessions" option
  would be the next audit step). No tenant-wide kill switch yet beyond not enabling it on computers.
* Latency is ~0.3 s per round trip (polling relay); WebSockets would remove it.


---

# Screen sharing and file transfer

Both are *sessions* like the shell: the same request, consent, audit, limits and isolation (a computer can have one
open session of each kind). Each kind is switched on separately, on the computer.

```
Solvix.Agent.exe install --remote-access consent --remote-features screen,control,files --files-folder "C:\Users\Public\Solvix"
```

* `--remote-features`: any of `screen`, `files`, `control` (`control` implies `screen`). Shell is always included when
  remote access is on. Default: shell only.
* `--files-folder <path>` (repeatable): folders a technician may use, in addition to the agent's own `transfer` folder.
  A whole drive or the Windows folder is refused.
* Nothing here can be enabled by the server or the downloadable package. The console only shows what the computer reported.

## Screen sharing

A Windows service runs in session 0 and cannot see a user's desktop, so screen sharing uses a **per-user helper**:
`Solvix.Agent.exe screen-helper`, registered by the installer to start at each sign-in *as that user, with no extra rights*.
The helper holds no credentials and talks only to the service over a local pipe (denied to the network; the service only
accepts a helper running in the active console session). The service captures frames through it and uploads them over the
agent's mTLS channel.

* The technician sees about 5 frames per second of changed screen (JPEG, at most 1920 px wide); only the newest frame is kept,
  in the cache for ≤ 30 s, never in the database.
* **View only by default.** The technician must *ask* to control; control is granted only if the technician asked, the computer
  allows `control` locally, **and** the person approved a dialog that says "see your screen and control your mouse and keyboard".
  Otherwise input is refused by the server and ignored by the agent. Input events are small, validated JSON (mouse position as
  fractions of the screen, keys, typed text) applied with `SendInput`.
* The consent dialog names exactly what is requested (view only / view and control). Without a signed-in person or a running
  helper the agent declines *before* asking anyone.
* Limits: the secure desktop (UAC prompts, lock screen, Ctrl+Alt+Del) cannot be captured or controlled from a user session.
  Primary monitor only. The helper starts at the *next* sign-in after installation.

## File transfer

List, download and upload inside folders the computer allows, nowhere else.

* **The computer is the authority.** Every path from the server is checked by the agent against its allowed folders: it must be
  absolute, inside a folder (after normalisation, so `..` fails), not through a link/junction/reparse point, not an alternate
  data stream, not a device name. A listing with no path shows only the allowed folders.
* Size limit 100 MB per file (configurable lower with `MaxFileMegabytes`). Uploads never replace an existing file unless the
  technician ticks *Replace if it exists*; they are received into a temporary file, **verified against the SHA-256 the console
  computed**, and only then moved into place, so a damaged transfer never overwrites anything.
* Downloads are announced with their size, streamed chunk by chunk, and the agent reports the SHA-256; an incomplete transfer
  is recorded as failed and the stream is cut.
* Audit: each operation (kind, path, size, SHA-256, outcome, error) is stored in `remote_file_operations` (kept); file data in
  flight (`remote_file_chunks`) is deleted when delivered and shortly after a session ends. Every step is also logged as a
  warning on the computer.
* The default folder `<data directory>\transfer` is only writable by the agent and administrators. To exchange files with the
  person using the laptop, add a folder they can reach with `--files-folder`.

## Verification

* Integration (real PostgreSQL/Redis): feature gating per kind, shell and screen open together but not two of a kind, frames
  (headers, newer-frame long poll, validation, isolation, wrong kind, ended session), control rules (not asked / asked but not
  allowed / granted, invalid or oversized events), file list/download/upload round trips with checksums, refusal and incomplete
  transfers, input limits, tenant and computer isolation, audit.
* End to end with the real agent worker: a real file uploaded to and downloaded from a real folder, traversal / outside-folder
  list, download and upload all refused; a screen session streaming frames from a synthetic desktop with view-only and control.
* Agent: sandbox (traversal, siblings with the same prefix, links, streams, device names), file runner (chunking, hashing,
  damaged/oversized/overwrite cases), screen runner (changed frames only, control gating, malformed input), helper bridge over
  real pipes (capture, input, lost helper, unauthorised helper, closed unless enabled), per-kind policy and consent text,
  installer (helper registration/removal, feature and folder flags).
* Frontend: helpers and the panels' enabled/disabled states.

## Not verified here

* On a real Windows laptop: the GDI capture, `SendInput`, the per-user helper start at sign-in (registry Run key), the
  session check that only accepts the console user's helper, the Windows consent dialog, DPI behaviour, multiple monitors.
  These were compiled, not executed. Expect a first-contact fix cycle when you try it on a laptop.
* Screen sharing needs Redis (frames travel through the shared cache); if it is down, no picture appears.
* Not built: audio, clipboard sync, multi-monitor selection, drag-and-drop folders, deleting or renaming remote files,
  recording sessions, and file access to user profile folders (the service account cannot read them; use a shared folder).

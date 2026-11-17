# Help desk: tickets and chat through the agent

People using enrolled laptops can report a problem and talk to a technician without opening a browser;
technicians work from the web console. **Remote access (screen/shell) is not part of this phase.**

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

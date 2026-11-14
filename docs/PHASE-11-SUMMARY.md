# Phase 11 – Solvix AI IT Assistant: technical summary

## What was built

A new backend module `Solvix.Modules.Assistant` (+ SharedKernel ports, one EF migration, a Next.js UI) that lets
authorised users talk to the platform in natural language. The model never touches the database or a device: it can
only call a closed set of backend **tools**, each executed *as the signed-in user*, and it can only **propose**
actions, which a human must approve.

| # | Requirement | Where / how |
|---|-------------|-------------|
| 1 | Natural language interface | `POST /api/v1/assistant/chat`, `AssistantChatService` (tool-calling loop), UI `/assistant` |
| 2 | Device information queries | tools `search_devices`, `get_device_details`, `list_branches`, `list_departments` |
| 3 | Online/offline fleet analysis | tool `get_fleet_summary`, `search_devices(status, offline_for_hours)` |
| 4 | Hardware diagnostics | tool `get_hardware_diagnostics` + deterministic `HardwareAnalyzer` findings |
| 5 | Software inventory analysis | tools `find_software`, `list_device_software` (outdated detection) |
| 6 | Performance analysis | tool `get_device_performance` + deterministic `PerformanceAnalyzer` findings |
| 7 | Security recommendations | tool `get_security_posture` + `SecurityAdvisor` (prioritised recommendations) |
| 8 | Knowledge base integration | upload → extract → redact → chunk → embed → retrieve with `[S1]` source references; tool `search_knowledge_base` |
| 9 | Ticket generation | tool `create_ticket`, REST `/assistant/tickets`, per-tenant `TKT-0001` numbering |
| 10 | Troubleshooting workflows | tool `run_troubleshooting_workflow` (`high_cpu`, `low_disk`, `slow_performance`, `device_offline`, `general_health`): fixed read-only pipelines, not model-authored |
| 11 | AI-generated reports | `ReportService`: platform-generated data tables + AI narrative from the same facts; degrades to data-only if the model is down |
| 12 | Approved remediation proposals | tool `propose_remediation` → `RemediationService` → human approval endpoint → `IDeviceCommandGateway` |

Example requests from the brief are covered by integration tests (offline computers, <10 GB free, outdated software,
POS-018 high CPU, Beirut branch, Finance deployment).

## Architecture

```
Browser ─► Next.js BFF ─► POST /assistant/chat ─► AssistantChatService ─► IChatModel (Claude Messages API)
                                                   │   ▲  tool_use / tool_result
                                                   ▼   │
                                              ToolExecutor  (registry · role check · strict args · redaction · size cap · audit)
                                                   │
        ┌──────────────┬───────────────┬───────────┴──────┬────────────────┬─────────────────┐
   IFleetQueryService  IBranch/IDepartment  IKnowledgeService  ITicketService  IReportService  IRemediationService
   (SharedKernel port)  (Organization)       (tenant RLS)       (tenant RLS)    (tenant RLS)    │ validate · persist PENDING
                                                                                               ▼
   Human ─► POST /assistant/proposals/{id}/approve ─► re-validate ─► IDeviceCommandGateway (SharedKernel port)
```

* **Ports instead of a hard dependency on phases 2-10.** The repository contained only Phase 01 (tenancy, identity,
  organisation); there is no device, inventory or command module. `Solvix.SharedKernel/Fleet` therefore defines the
  contract the assistant consumes: `IFleetQueryService` (read) and `IDeviceCommandGateway` (validate/execute typed
  commands). Both take a `FleetAccess(tenant, user, role)` so the implementing module applies its own authorisation.
  Until a real module registers them, `NotConnectedFleet`/`NotConnectedCommandGateway` answer "no data source
  connected" (never fabricated data). `Solvix:Assistant:UseDemoFleet=true` (Development/Testing only; startup fails in
  Production) swaps in `DemoFleet`/`DemoCommandGateway`: deterministic synthetic devices spread over the tenant's
  **real** branches/departments, simulated execution. This is what the tests and the dev UI use.
* **Tool set is closed and enumerated** in `AssistantModule`. There is no SQL tool, no shell tool, no approve/execute tool.
* **LLM provider** is behind `IChatModel`; `AnthropicChatModel` implements Messages API tool use with retry/backoff and a
  key that is never logged or echoed. Embeddings are behind `IEmbeddingProvider` (offline hashing embedder by default,
  OpenAI-compatible endpoint optional).

## Security model

| Threat | Control (and the test that proves it) |
|--------|---------------------------------------|
| AI reads data directly | Model only sees `ToolExecutor`; tools call application services; no DbContext in the orchestrator (architecture) |
| AI reads another tenant's / user's data | Tenant from signed JWT only; every table is `ITenantScoped` (EF filter + FORCE RLS); conversations additionally scoped to their user. `AssistantIsolationTests` (raw SQL on all 9 tables), cross-tenant 404 tests |
| AI exceeds the user's permissions | Role enforced when offering tools **and again** when executing (`ToolExecutorTests`, forged `propose_remediation` by a Member → denied + audited) |
| AI runs arbitrary PowerShell/shell | No such capability exists: `ActionCatalog` is a closed set of typed actions; string parameters are identifier-only regexes; unknown parameters rejected; `DeviceCommandType` has no script member (`ActionCatalogTests`, injection theory tests) |
| AI acts without approval | Proposals are inert rows. Approval is a REST call for signed-in humans; `ExecutedCount` is asserted unchanged until approval |
| Approval of something different from what was shown | SHA-256 fingerprint of action+params+targets verified at approval (tamper test) |
| Privilege confusion | Approver role by risk: Low/Medium → Admin, High (deploy/uninstall, or > 25 devices) → Owner; approver's own access is used for re-validation and execution |
| Stale/replayed/double approval | TTL (24 h), single-use state machine, `xmin` optimistic concurrency (6 concurrent approvals → exactly 1 execution) |
| Backend validation bypass | `ProposalService` validates action, params, targets (resolved through the fleet port in the caller's access, pinned by id) and calls `IDeviceCommandGateway.ValidateAsync` at proposal time **and** at execution time |
| Secrets to the model / DB / logs | `SecretRedactor` on user messages, tool results, KB ingestion & retrieval, tickets, audit; API keys only in server config. (`SecretRedactorTests`, chat test asserting the model request and DB rows contain no secret) |
| Prompt injection via device names / documents | Prompt marks tool data untrusted; but the guarantee does not rely on it: worst case is a pending proposal that a human reviews (`Prompt_injection_through_data_cannot_cause_execution`) |
| Audit tampering | `assistant_audit_log` is append-only via DB trigger (UPDATE/DELETE/TRUNCATE rejected even for the table owner); audit is written **before** execution and a failed audit write aborts execution |
| Cost/abuse | Per-user rate limit on chat/report generation, max tool iterations (6), message/result size caps |
| Malicious uploads | Extension allow-list + content sniffing (zip/PDF disguised as text, NULs, invalid UTF-8), size cap, DOCX read with DTD disabled and size bound, per-tenant document cap, nothing persisted on validation failure |

## Data model (all tenant-scoped with RLS; migration `AddAssistant`)

`assistant_conversations`, `assistant_messages`, `assistant_audit_log` (append-only), `assistant_proposals`,
`assistant_tickets`, `assistant_ticket_counters`, `assistant_reports`, `knowledge_documents`, `knowledge_chunks`
(`real[]` embeddings). Parent/child tables use tenant-safe composite foreign keys like Phase 01.

## REST API (`/api/v1/assistant/…`)

`status` · `chat` · `conversations[/{id}]` · `proposals[/{id}[/approve|/reject]]` · `tickets[/{id}[/status]]` ·
`reports[/{id}]` · `knowledge/documents[/{id}[/reindex]]` · `knowledge/search` · `audit` (Admin).
Roles: Members can chat, read, create tickets and generate reports; Admins propose actions, approve Low/Medium risk,
manage the knowledge base and read the audit log; Owners also approve High risk.

## Configuration (`Solvix:Assistant`, env `Solvix__Assistant__…`)

| Key | Default | Notes |
|-----|---------|-------|
| `Llm:ApiKey` | empty | Secret. Empty ⇒ chat returns 503 `assistant_unavailable`; UI explains |
| `Llm:Model` | `claude-sonnet-5-5` | |
| `UseDemoFleet` | `false` (`true` in Development) | Refused in Production |
| `ChatPermitsPerMinute` | 20 | per user |
| `Remediation:ProposalTtlMinutes` / `MaxTargetsPerProposal` / `HighRiskTargetThreshold` / `AutoExecuteReadOnly` | 1440 / 100 / 25 / false | |
| `Knowledge:Embeddings:Provider` | `local` | `openai` sends document text to that provider |

## Contract for the device modules (phases 2-10)

Register `IFleetQueryService` and `IDeviceCommandGateway` implementations (they override the "not connected" ones).
They must: enforce tenant isolation and the supplied `FleetAccess`; return no secrets; own the *semantic* command
validation (service deny-lists, approved package catalogue, maintenance windows, ownership) since the assistant only
checks shape; and treat `DeviceCommandRequest.IdempotencyKey` (= proposal id) as an idempotency key.

## Verification

| Suite | Result |
|-------|--------|
| `Solvix.UnitTests` | 107 / 107 (44 existing + 63 new: redactor, action catalog, proposal state machine, chunker, embeddings, text extraction, tool executor, Anthropic wire format/retries, analyzers) |
| `Solvix.IntegrationTests` (real PostgreSQL 16 + Redis 7, two-role RLS layout) | 77 / 77 (31 existing + 46 new, see below) |
| Frontend `tsc`, `eslint`, `vitest` (21 tests, 11 new), `next build` | all clean |

New integration coverage: chat/tool loop incl. loop cap, model outage, auth, validation; role-filtered tools; forged
tool call; secret redaction end-to-end; conversation privacy; propose→approve→execute with audit order; reject; role/risk
approval rules; escalation >25 devices; unsafe/unsupported actions; target validation incl. foreign-tenant device ids;
partial failure; expiry; tamper; concurrent approval; prompt-injection scenario; audit immutability; KB upload/search/
citations/redaction/isolation/delete/reindex/limits; tickets (numbering, concurrency, isolation); reports (all kinds,
scope, outage); RLS on every new table. Mutation check: weakening the High-risk approver rule makes the approval test fail.

## Known limitations / follow-ups (please read)

* **Not verified against the live Claude API** (no key in the build environment). The wire format is unit-tested
  against the documented Messages API shape; run one real conversation before relying on it.
* **No real fleet/command backend exists yet** (see ports above). Everything beyond the ports is exercised with the
  demo implementation only.
* **PDF upload is not supported** (no trustworthy PDF library could be restored in the build environment); PDFs are
  rejected with a clear message. Supported: txt, md, csv, json, html, docx.
* Embeddings default to a lexical hashing model (good keyword/phrase recall, not deep semantics) and similarity is
  computed in-process over the tenant's chunks (capped at 50k). For large corpora move to pgvector/neural embeddings
  behind the existing `IEmbeddingProvider`/`KnowledgeService.SearchAsync` seam.
* Document processing is synchronous inside the upload request (fine for ≤10 MB with the local embedder).
* No token streaming; replies arrive when the tool loop finishes.
* The chat rate limiter is configured but not covered by an automated test; the UI was type/lint/unit-tested and
  built, but not exercised in a browser against the running API, and the Docker images were not rebuilt here.
* Tickets live in this module because no ticketing module exists; migrate if one appears.
* Data sent to the LLM provider (tool results, redacted) leaves the deployment; the audit log records what was called.

# Reporting

Module: `Solvix.Modules.Reporting` (read-only; references the Devices and Helpdesk modules). Admin-only
(`/api/v1/reports`, `/api/v1/reports/{key}?format=csv`, UI under *Reports*). Every report is one uniform structure
(summary metrics, typed columns, rows, optional notes), rendered by the UI and exported as CSV from the same data. Every
query runs inside the caller's tenant session, so row-level security confines it to the caller's data (tested for all eight
reports, JSON and CSV).

CSV: UTF-8 with BOM, RFC 4180 quoting, **formula-injection protection** (text beginning `= + - @` tab or CR is prefixed with
`'`, so a device named `=HYPERLINK(…)` cannot execute in Excel). Result size is capped at 20,000 rows (a `truncated` flag is set).

| Key | Answers | Filters | Definition and limits |
|-----|---------|---------|------------------------|
| `device-inventory` | What do we have? | `branchId`, `search` | Hardware, OS, network, assignment, branch/department, agent version, last seen. Online = seen within `OfflineAfterSeconds` (300 s). |
| `device-availability` | What is online, who is silent, how available is the fleet? | `branchId`, `days`, `search` | Current state buckets (online, <1 h, <24 h, <7 d, >7 d, never) and a **trend from hourly samples** the platform takes (`availability_snapshots`). The trend starts when sampling starts; there is no history before that. |
| `asset-lifecycle` | Which devices are old or out of warranty? | `branchId`, `search` | Uses purchase date / warranty end entered by admins (`PUT /devices/{id}/lifecycle`); age falls back to BIOS release date, then enrollment date (labelled in the "Age based on" column). *Refresh candidate* = 5+ years, unsupported OS, or out of warranty and 3+ years. |
| `software-inventory` | What is installed, how widely? | `search` | Applications from the latest inventories of **active** devices, grouped by name/version/publisher with device counts (aggregated in PostgreSQL from the inventory JSON). |
| `patch-compliance` | Who is behind on updates? | `branchId`, `search` | **Relative, inventory-derived:** a device is *Behind* if its cumulative-update revision is lower than the newest revision of the same Windows release seen in this workspace; also *Unsupported OS*, *Stale data* (no inventory in 14 days), long uptime (>30 days). The current agent does **not** read Windows Update history, so this cannot name missing KBs. |
| `security-compliance` | Who meets baseline? | `branchId`, `search` | Per active device: OS supported, Secure Boot, BitLocker on all fixed volumes, agent healthy, agent up to date, seen in 7 days. Missing facts are **Unknown**, never "Compliant". Firewall and antivirus are not collected by the agent and are not assessed. |
| `deployment` | How is the agent rollout going? | `branchId`, `days`, `search` | Enrollments in period, agent version spread, devices pending an update, re-enrollments, enrollment-token usage. This is *agent* deployment; the platform has no software/patch deployment jobs. |
| `technician-activity` | How is the helpdesk team doing? | `days` | Per technician: open now, assigned and resolved in period, average first response (minutes) and resolution (hours), % resolved within SLA, SLA breaches, public replies, internal notes, reopened. |

Each report's `notes` field repeats its limitations in the UI so nobody over-reads the data.

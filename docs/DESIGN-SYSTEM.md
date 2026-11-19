# Solvix-EndPoint design system

The design system for the Solvix-EndPoint web console: design tokens, the component library, the
navigation shell and the patterns every screen follows. It lives in `src/frontend` and is built on
Next.js (App Router), TypeScript, Tailwind CSS 4, shadcn/ui conventions on Radix UI primitives,
Lucide icons and Recharts.

Run `npm run dev` in `src/frontend` and open **/design-system** for the living reference: every
token and component rendered in both themes. That page shows sample content, so it is only served
by the development server.

## Principles

1. **Minimal and professional.** Neutral surfaces, one navy identity, colour reserved for meaning.
2. **Clear hierarchy.** One `h1` per page, one primary action per view, secondary text is muted.
3. **Dense, not cluttered.** 14 px body, 36 px controls, 12 px gaps in toolbars, 16 px between cards.
4. **Consistent interaction.** One focus ring, one way to confirm, one way to report results.
5. **Accessible.** Keyboard reachable, visible focus, labelled controls, state never by colour alone,
   reduced-motion respected.
6. **Honest data.** A figure is real, explicitly unavailable, or an error. Never invented.

## Design tokens

All values are CSS custom properties in [`src/app/globals.css`](../src/frontend/src/app/globals.css),
in three layers. Components only use layers 2 and 3.

| Layer | What | Example |
|-------|------|---------|
| 1. Primitives | Raw palette steps | `--navy-700`, `--blue-600`, `--cyan-400`, `--slate-200` |
| 2. Semantic roles | What a component asks for; re-pointed by `.dark` | `--primary`, `--card`, `--border`, `--success` |
| 3. Tailwind theme | Roles exposed as utilities via `@theme inline` | `bg-primary`, `text-muted-foreground`, `border-border` |

`src/lib/design-tokens.ts` mirrors the token *names* for TypeScript (chart colours, the `Tone`
type). Values are never duplicated there.

### Colour roles

| Role | Utility | Light | Dark | Use |
|------|---------|-------|------|-----|
| Background | `bg-background` | `#f5f7fb` | `#081020` | Page canvas |
| Card / popover | `bg-card`, `bg-popover` | `#ffffff` | `#0f1a30` / `#121f3a` | Raised surfaces, overlays |
| Foreground | `text-foreground` | `#0c1a33` | `#e6ecf7` | Primary text |
| Muted | `bg-muted`, `text-muted-foreground` | `#eef2f8` / `#55637d` | `#15223d` / `#93a3bf` | Subtle fills, secondary text |
| Primary | `bg-primary` | navy `#14306a` | blue `#4c8dff` | The primary action |
| Secondary / accent | `bg-secondary`, `bg-accent` | `#e6edfa` / `#eaf1fe` | `#17274a` | Quiet actions, selection, hover wash |
| Link | `text-link` | `#2563eb` | `#7fb0ff` | Text links |
| Highlight | `text-highlight` | cyan `#0891b2` | cyan `#22d3ee` | Accent details (active nav indicator, active tab) |
| Border / input / ring | `border-border`, `border-input`, `outline-ring` | `#dde3ee` / `#c6cfdf` / `#2563eb` | `#1e2c4a` / `#2a3a5e` / `#5b9bff` | Dividers, control outlines, focus |
| Success / warning / destructive / info | `text-success` … | `#177a45` / `#a76200` / `#c8322f` / `#1d5fd6` | `#3ecf8e` / `#f2b441` / `#f0625f` / `#6aa5ff` | Status only, always with an icon or label |
| Sidebar | `bg-sidebar` … | navy `#0b1730` | `#060c1a` | Navigation shell (navy in both themes) |

Dark mode is selected, not inverted: the primary action becomes blue because navy has no contrast
on a navy canvas. The theme follows the system by default and can be set from the header.

### Chart colours

`--chart-1` … `--chart-5` are a fixed categorical order (blue, orange, cyan, violet, green) with
separate light and dark steps. Both sets were validated for colour-vision deficiency and contrast
against the card surface. Rules: assign colours by entity, never by rank; never cycle past five
(fold the tail into "Other"); status colours are not series colours; text never wears a series colour.

### Other tokens

| Token | Values |
|-------|--------|
| Typography | `font-sans` Inter Variable (self-hosted), `font-mono` system monospace. Sizes: `text-2xs` 11, `text-xs` 12, `text-sm` 14 (body), `text-lg` 18, `text-2xl` 24. Use `tabular-nums` for aligned figures. |
| Radius | `rounded-sm` 4, `rounded-md` 6, `rounded-lg` 8 (cards), `rounded-xl` 12 (dialogs), `rounded-full` (badges) |
| Elevation | `shadow-xs` cards and controls, `shadow-md` menus and popovers, `shadow-xl` dialogs and drawers |
| Layout | `h-header` 56 px, `w-sidebar` 248 px, `w-sidebar-collapsed` 60 px, `max-w-content` 1536 px |
| Motion | 120–200 ms; disabled under `prefers-reduced-motion` |
| Breakpoints | Tailwind defaults. The sidebar becomes a drawer below `lg` (1024 px). |

## Components

All components are in `src/components/ui` unless noted. They accept `className` for layout
adjustments (margins, width), not for restyling.

### Actions and status

| Component | File | Notes |
|-----------|------|-------|
| `Button` | `button.tsx` | `variant`: `primary` (default), `secondary`, `outline`, `ghost`, `destructive`, `link`. `size`: `sm`, `md`, `lg`, `icon`, `icon-sm`. `loading` shows a spinner and disables. `asChild` styles a `<Link>` as a button. Icon-only buttons need `aria-label`. |
| `Badge` | `badge.tsx` | `tone`: `neutral`, `brand`, `info`, `success`, `warning`, `danger`. For labels and counts. |
| `StatusBadge` | `badge.tsx` | A badge with a leading dot; `pulse` marks a live state. Use for device and token state. |
| `Avatar` | `avatar.tsx` | Initials from `name`; sizes `sm`, `md`, `lg`. |
| `Kbd` | `kbd.tsx` | Keyboard shortcut hint. |

### Forms

| Component | File | Notes |
|-----------|------|-------|
| `Input`, `Textarea`, `Select` | `input.tsx` | Native controls, 36 px high. Set `aria-invalid` for the error style. |
| `Checkbox` | `input.tsx` | Native checkbox; `indeterminate` for "some selected". |
| `SearchInput` | `input.tsx` | Controlled (`value`, `onValueChange`) with icon and clear button. Debounce in the caller. |
| `Field` | `field.tsx` | Label, control, `hint` or `error` (the error replaces the hint and is announced), `required`. |
| `FormSection`, `FormActions` | `field.tsx` | Titled field group; right-aligned action row. |

Form rules: labels above controls; validate on submit and show API field errors next to the field
(`fieldErrorsFrom` in `lib/forms.ts`); the submit button shows `loading`; success is a toast.

### Data display

| Component | File | Notes |
|-----------|------|-------|
| `Card` (+ `CardHeader`, `CardTitle`, `CardDescription`, `CardContent`, `CardFooter`) | `card.tsx` | The standard surface. |
| `MetricCard` | `metric-card.tsx` | KPI tile. `state`: `ready`, `loading`, `unavailable`, `error`; `message` explains the last two. `tone` colours the icon only. `href` links the tile to its list. `badge` marks demo values. |
| `DataTable` | `data-table.tsx` | See below. |
| `Table`, `THead`, `TBody`, `TR`, `TH`, `TD` | `table.tsx` | Low-level parts for simple static tables. |
| `Pagination` | `pagination.tsx` | Row range, rows per page, first/previous/next/last. |
| `ChartCard` | `chart.tsx` | Frame for every chart: title, description, `state` (`ready`, `loading`, `empty`, `error`), `onRetry`, and `table` for a "View as table" alternative. |
| `HorizontalBarChart` | `chart.tsx` | Recharts ranked bars for comparing categories; values at the bar tips, hover tooltip. |
| `SegmentedBar` | `chart.tsx` | One whole split into parts, with a list of names, amounts and shares. Preferred over a donut. |
| `Tabs` (+ `TabsList`, `TabsTrigger`, `TabsContent`) | `tabs.tsx` | Views of the same object. |

#### DataTable

Built on TanStack Table for server-driven lists. The caller owns sorting, filtering and pagination
(the API does the work); the table handles selection and column visibility.

```tsx
<DataTable
  label="Devices"
  columns={columns}            // ColumnDef<T>[]; meta: { label, align, className }
  data={page.items}
  getRowId={(d) => d.id}
  loading={loading} error={error} onRetry={reload}
  empty={<EmptyState … />}
  sorting={sorting} onSortingChange={setSorting}
  enableSelection rowSelection={selection} onRowSelectionChange={setSelection}
  bulkActions={(ids, clear) => <Button …>Retire</Button>}
  columnVisibility={visibility} onColumnVisibilityChange={setVisibility}
  toolbar={(table) => (<><SearchInput … /><FacetedFilter … /><DataTableViewOptions table={table} /></>)}
  pagination={{ page, pageSize, totalCount, onPageChange, onPageSizeChange, itemLabel: "devices" }}
/>
```

* **Sorting.** A column is sortable when it has an `accessorFn` and does not set
  `enableSorting: false`. Only mark columns the API can sort. Use `DataTableColumnHeader` for the
  header; `aria-sort` is set for you.
* **Filtering.** `FacetedFilter` (single-select by default, `multiple` optional) with real counts
  only; `ResetFiltersButton` while any filter is active.
* **Columns.** `DataTableViewOptions` lists hideable columns by `meta.label`. Set
  `enableHiding: false` on the identifying column and on actions.
* **Selection.** Selecting rows replaces the toolbar with the bulk action bar.
* **States.** Skeleton rows on first load, dimmed rows while refreshing, an error state with retry,
  and the `empty` node otherwise.

### Overlays

| Component | File | Use |
|-----------|------|-----|
| `Dialog` | `dialog.tsx` | A focused task: `open`, `onClose`, `title`, `description`, `footer`. Primitives (`DialogRoot`, `DialogContent`, …) are exported for custom layouts. |
| `ConfirmDialog` | `confirm-dialog.tsx` | Destructive or irreversible actions. `onConfirm` may return a promise: the dialog shows progress, closes on success and stays open on failure. `confirmText` requires typing a phrase for high-impact actions. Cannot be dismissed by clicking outside; focus starts on Cancel. |
| `Sheet` (drawer) | `sheet.tsx` | Detail or a secondary task beside the page; `side`: `right`, `left`, `bottom`. |
| `DropdownMenu` | `dropdown-menu.tsx` | Menus of actions: items, checkbox items, radio items, labels, separators, shortcuts; `tone="danger"` for destructive items. |
| `Popover` | `popover.tsx` | Non-menu floating content: filters, pickers, the notification centre. |
| `Tooltip` | `tooltip.tsx` | Label for an icon-only control or a truncated value. Never essential information. |
| `Command` | `command.tsx` | Command palette parts (cmdk) used by the global search. |

### Feedback and states

| Component | File | Use |
|-----------|------|-----|
| `toast` / `useToast` | `components/providers/toast-provider.tsx` | Result of an action: `{ tone, title, description?, action? }`; tones `success`, `error`, `warning`, `info`. Errors stay longer. |
| `Alert` | `alert.tsx` | A message that stays on the page; optional `action`. |
| `Skeleton` | `skeleton.tsx` | Loading placeholder shaped like the content it replaces. |
| `EmptyState` | `empty-state.tsx` | Nothing to show: icon, title, description and the next step. |
| `ErrorState` | `empty-state.tsx` | Loading failed: message and "Try again". |
| `PageHeader` | `page-header.tsx` | Page title, description, actions. |

Every data view implements four states: **loading** (route `loading.tsx` or skeletons), **empty**,
**error** and **success** (toast, plus a notification for events worth keeping).

## Navigation shell

`src/components/shell`:

| Part | File | Behaviour |
|------|------|-----------|
| `AppShell` | `app-shell.tsx` | Sidebar, sticky header, content column (`max-w-content`), skip link. |
| `Sidebar` | `sidebar.tsx` | Navy rail with brand, workspace and sections. Collapses to icons (remembered per browser); a drawer below `lg`. Active item has `aria-current="page"`. |
| Navigation config | `nav-config.ts` | Single source for sidebar, search and breadcrumbs. `adminOnly`, `platformOnly` (provider console, operator only) and `devOnly` flags. Only real routes are listed. |
| `Header` | `header.tsx` | Breadcrumbs, search, theme, notifications, account. |
| `Breadcrumbs` | `breadcrumbs.tsx` | Derived from the URL; the current page only on small screens. |
| `GlobalSearch` | `global-search.tsx` | ⌘K / Ctrl+K. Jumps to pages and searches devices through the API (name, serial, model, IP, MAC). |
| `NotificationsMenu` | `notifications-menu.tsx` | Results of actions in this session; opening marks them read. |
| `UserMenu`, `ThemeToggle` | | Account, workspace role, sign out; light, dark, system. |

## Provider console

`/platform/companies` (operator only, 404 for everyone else; see [PLATFORM-OPERATIONS.md](PLATFORM-OPERATIONS.md)):
company list on the shared `DataTable` (search, status filter, pagination), summary metric cards, a
"New company" dialog that generates the owner's initial password, a one-time hand-over dialog, and a
confirmed suspend action. When the API refuses a session with 403 (suspended workspace or removed
membership) the app shows `AccessBlocked` instead of redirecting, so there is no sign-in loop.

## Dashboard

`/dashboard` is the executive overview: eight metric cards, fleet connectivity, agent versions and
recent activity. `lib/dashboard/overview.ts` loads it; `lib/dashboard/model.ts` turns API results
into widget states.

| Widget | Source today |
|--------|--------------|
| Managed, online, offline devices | `GET /devices/stats` |
| Devices requiring attention | `GET /devices/stats` → `requiringAttention` (active devices whose agent reports degraded or unhealthy) |
| Fleet connectivity, agent versions | `GET /devices/stats` |
| Managed mobile devices, IT assets, critical security alerts, pending deployments, recent activity | No backend module yet: shown as "not connected" |

To connect a widget, return a real value for its key in `buildOverview`; the UI needs no change.

## Device table

`/endpoints/devices` (`components/features/devices/devices-view.tsx`): device name, assigned user,
operating system, IP address, branch, status, last check-in, agent version and row actions, plus
optional serial number, hardware and enrolled columns.

* Search (debounced), filters for status, branch, group, agent version and "needs attention".
* Server-side sorting by device, operating system, last check-in and enrolled date.
* Column choices are remembered per browser. Pagination with 10/25/50/100 rows.
* Bulk selection (admins) for the current page: refresh inventory, retire, delete. Bulk actions call
  the per-device endpoints and report how many succeeded.
* Retire and delete are confirmed; deleting several devices requires typing `delete`.
* Deep links: `?search=`, `?connectivity=Online|Offline`, `?status=Retired`, `?attention=true`,
  `?branchId=`, `?groupId=`, `?agentVersion=`.

## Data honesty

* Production never shows fabricated figures, trends or charts. A widget without a data source
  renders its unavailable state; a failed request renders an error, not a zero.
* Charts only plot values returned by the API. No sparkline or delta is shown without history.
* **Demo data** (`src/lib/dev/demo-data.ts`) fills the not-yet-connected dashboard widgets for UI
  work. It is on only when the app runs under `next dev` **and**
  `NEXT_PUBLIC_SOLVIX_DEMO_DATA=true` is set (`isDemoDataEnabled` in `lib/env.ts`). Production
  builds, including the Docker image, ignore the flag. When it is on, the dashboard shows a banner
  and every affected widget carries a "Demo" badge. Device figures are always real.
* `/design-system` uses sample content and returns 404 outside the development server.

## Accessibility checklist

* All interactive elements are reachable by keyboard and show the focus ring.
* Dialogs, drawers and menus trap and restore focus (Radix UI) and close on Escape.
* Icon-only buttons have `aria-label`; decorative icons are `aria-hidden`.
* Status uses a label or icon in addition to colour; charts have a table view or a labelled list.
* Tables have an accessible name, `scope="col"` headers and `aria-sort`; loading sets `aria-busy`.
* Text contrast meets WCAG AA in both themes.

## Adding or changing components

1. Check `/design-system` and this document for an existing component first.
2. New primitives follow shadcn/ui anatomy on Radix UI (`components.json` is configured, so
   `npx shadcn add <component>` works); restyle them with semantic tokens only.
3. Never hard-code colours in components. Add or reuse a semantic token in `globals.css`.
4. Add the component to `/design-system` and to this document, and cover behaviour with a Vitest
   test in `src/__tests__`.
5. `npm run check` (typecheck, lint, tests) must pass.

## Agent section

`Endpoints → Agent overview`, `Devices` and `Staged computers` cover installation and health of the agents.
Everything on them is calculated from stored data; nothing is invented.

| Screen | What it shows | API |
|--------|---------------|-----|
| Agent overview (`/endpoints/agents`) | Managed, online, offline and yet-to-install counts; **alerts**; OS platform, operating system, agent version and agent health charts (each with a table view); computers per branch | `GET /devices/stats`, `/devices/overview`, `/devices/alerts`, `/staged-computers/summary` |
| Devices (`/endpoints/devices`) | The device table, now with a **Domain** column ("Workgroup" when none) and filters that alerts link to: `?health=`, `?notSeenHours=`, `?updateAvailable=true` (shown as removable chips) | `GET /devices` |
| Staged computers (`/endpoints/staged`) | Computers you expect to manage. Add one, **import** a pasted list or CSV/TXT (first column, up to 1000), assign a branch/department, remove entries | `/staged-computers` |
| Download agent (dialog, on all three) | Platform (Windows; macOS and Linux shown as not available yet), branch, department, group, validity and number of computers. Creates a short-lived enrollment token and downloads the package; if the package cannot be built the token is revoked | `POST /enrollment-tokens`, `/enrollment-tokens/package` |

### Alerts

Alerts are derived on every request (nothing is stored), most severe first, each with a count and a sample of up to 5 devices:

| Code | Severity | Condition |
|------|----------|-----------|
| `certificate_expired` | Critical | The newest unrevoked device certificate has expired |
| `agent_unhealthy` | Critical | Agent reports an unhealthy state |
| `not_reporting` | Warning | Active device has not checked in for 24 hours |
| `agent_degraded` | Warning | Agent reports a degraded state |
| `certificate_expiring` | Warning | The newest certificate expires within 7 days (a device that already received its renewed certificate is not flagged) |
| `agent_outdated` | Info | Agent is older than the latest configured release |
| `staged_pending` | Info | Staged computers whose hostname has no enrolled device |

Retired devices never raise alerts.

### Staged computers and enrollment

A staged entry is "pending" until a device with the same host name (case-insensitive) exists in the workspace; the
status (`Pending` / `Enrolled` / `Retired`) is derived when the list is read, so enrolling needs no extra bookkeeping.
Removing an entry never touches a device. The `Directory` source is reserved for Active Directory / Entra discovery,
which will write to the same table.

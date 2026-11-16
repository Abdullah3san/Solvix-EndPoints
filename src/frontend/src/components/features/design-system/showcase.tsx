"use client";

import * as React from "react";
import { Boxes, Download, Monitor, MoreHorizontal, Plus, Settings2, ShieldAlert, Trash2, WifiOff } from "lucide-react";
import { brandRamps, chartColors, colorTokens } from "@/lib/design-tokens";
import { Alert } from "@/components/ui/alert";
import { Avatar } from "@/components/ui/avatar";
import { Badge, StatusBadge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { ChartCard, HorizontalBarChart, SegmentedBar, type ChartDatum } from "@/components/ui/chart";
import { ConfirmDialog } from "@/components/ui/confirm-dialog";
import { DataTable, DataTableColumnHeader, DataTableViewOptions, FacetedFilter, type ColumnDef, type RowSelectionState, type SortingState, type VisibilityState } from "@/components/ui/data-table";
import { Dialog } from "@/components/ui/dialog";
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuLabel, DropdownMenuSeparator, DropdownMenuShortcut, DropdownMenuTrigger } from "@/components/ui/dropdown-menu";
import { EmptyState, ErrorState } from "@/components/ui/empty-state";
import { Field, FormActions, FormSection } from "@/components/ui/field";
import { Checkbox, Input, SearchInput, Select, Textarea } from "@/components/ui/input";
import { Kbd } from "@/components/ui/kbd";
import { MetricCard } from "@/components/ui/metric-card";
import { PageHeader } from "@/components/ui/page-header";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";
import { Sheet, SheetBody, SheetContent, SheetDescription, SheetFooter, SheetHeader, SheetTitle, SheetTrigger, SheetClose } from "@/components/ui/sheet";
import { Skeleton } from "@/components/ui/skeleton";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { Tooltip } from "@/components/ui/tooltip";
import { useToast } from "@/components/providers/toast-provider";

function Section({ id, title, description, children }: { id: string; title: string; description: string; children: React.ReactNode }) {
  return (
    <section id={id} aria-labelledby={`${id}-title`} className="scroll-mt-20 border-t border-border py-8 first:border-0 first:pt-0">
      <h2 id={`${id}-title`} className="text-lg font-semibold tracking-tight">
        {title}
      </h2>
      <p className="mt-1 max-w-3xl text-sm text-muted-foreground">{description}</p>
      <div className="mt-5">{children}</div>
    </section>
  );
}

function Swatch({ token }: { token: string }) {
  return (
    <div className="flex items-center gap-2.5">
      <span className="size-9 shrink-0 rounded-md border border-border" style={{ background: `var(--${token})` }} />
      <code className="truncate font-mono text-xs">--{token}</code>
    </div>
  );
}

interface SampleRow {
  id: string;
  name: string;
  owner: string;
  state: "Online" | "Offline";
}

const SAMPLE_ROWS: SampleRow[] = [
  { id: "1", name: "SAMPLE-LT-001", owner: "Sample User A", state: "Online" },
  { id: "2", name: "SAMPLE-LT-002", owner: "Sample User B", state: "Offline" },
  { id: "3", name: "SAMPLE-WS-003", owner: "Sample User C", state: "Online" },
];

const SAMPLE_BARS: ChartDatum[] = [
  { key: "a", label: "Series A", value: 48 },
  { key: "b", label: "Series B", value: 31 },
  { key: "c", label: "Series C", value: 12 },
];

const SAMPLE_PARTS: ChartDatum[] = [
  { key: "a", label: "Part A", value: 62, color: chartColors[0] },
  { key: "b", label: "Part B", value: 27, color: chartColors[1] },
  { key: "c", label: "Part C", value: 11, color: chartColors[2] },
];

const SECTIONS = [
  ["color", "Colour"],
  ["type", "Typography"],
  ["actions", "Buttons"],
  ["status", "Badges"],
  ["forms", "Forms"],
  ["feedback", "Feedback"],
  ["metrics", "Metric cards"],
  ["charts", "Charts"],
  ["table", "Data table"],
  ["overlays", "Overlays"],
  ["states", "States"],
] as const;

function SampleTable() {
  const [selection, setSelection] = React.useState<RowSelectionState>({});
  const [visibility, setVisibility] = React.useState<VisibilityState>({});
  const [sorting, setSorting] = React.useState<SortingState>([{ id: "name", desc: false }]);
  const [filter, setFilter] = React.useState<string[]>([]);
  const [search, setSearch] = React.useState("");

  const columns = React.useMemo<ColumnDef<SampleRow, unknown>[]>(
    () => [
      { id: "name", accessorFn: (r) => r.name, enableHiding: false, meta: { label: "Device" }, header: ({ column }) => <DataTableColumnHeader column={column} title="Device" />, cell: ({ row }) => <span className="font-medium">{row.original.name}</span> },
      { id: "owner", enableSorting: false, meta: { label: "Assigned user" }, header: "Assigned user", cell: ({ row }) => row.original.owner },
      {
        id: "state",
        enableSorting: false,
        meta: { label: "Status" },
        header: "Status",
        cell: ({ row }) => <StatusBadge tone={row.original.state === "Online" ? "success" : "warning"}>{row.original.state}</StatusBadge>,
      },
    ],
    [],
  );

  const rows = SAMPLE_ROWS.filter((r) => (filter.length === 0 || filter.includes(r.state)) && r.name.toLowerCase().includes(search.toLowerCase())).sort(
    (a, b) => (sorting[0]?.desc ? -1 : 1) * a.name.localeCompare(b.name),
  );

  return (
    <DataTable
      label="Sample devices"
      columns={columns}
      data={rows}
      getRowId={(r) => r.id}
      empty={<EmptyState size="sm" title="No rows match" description="Clear the search or the filter." />}
      sorting={sorting}
      onSortingChange={setSorting}
      enableSelection
      rowSelection={selection}
      onRowSelectionChange={setSelection}
      columnVisibility={visibility}
      onColumnVisibilityChange={setVisibility}
      toolbar={(table) => (
        <>
          <SearchInput value={search} onValueChange={setSearch} aria-label="Search sample rows" className="w-56" />
          <FacetedFilter title="Status" options={[{ value: "Online", label: "Online" }, { value: "Offline", label: "Offline" }]} value={filter} onChange={setFilter} />
          <div className="ml-auto">
            <DataTableViewOptions table={table} />
          </div>
        </>
      )}
      bulkActions={(ids) => (
        <Button variant="outline" size="sm">
          Act on {ids.length}
        </Button>
      )}
      pagination={{ page: 1, pageSize: 25, totalCount: rows.length, onPageChange: () => {}, itemLabel: "rows" }}
    />
  );
}

/** Every token and component in one place, rendered with clearly labelled sample content. */
export function DesignSystemShowcase() {
  const { toast } = useToast();
  const [dialog, setDialog] = React.useState(false);
  const [confirm, setConfirm] = React.useState(false);

  return (
    <>
      <PageHeader title="Design system" description="Tokens and components of Solvix EndPoint. Reference: docs/DESIGN-SYSTEM.md." />
      <Alert tone="warning" title="Development reference" className="mb-6">
        Everything on this page is sample content for illustration. The page is not served in production builds.
      </Alert>

      <nav aria-label="Sections" className="mb-8 flex flex-wrap gap-1.5">
        {SECTIONS.map(([id, label]) => (
          <a key={id} href={`#${id}`} className="rounded-full border border-border bg-card px-3 py-1 text-xs font-medium text-muted-foreground hover:text-foreground">
            {label}
          </a>
        ))}
      </nav>

      <Section id="color" title="Colour" description="Components use semantic roles, never raw palette steps. Roles re-point in dark mode; the brand ramps stay fixed.">
        <div className="grid gap-6 lg:grid-cols-2">
          {Object.entries(colorTokens).map(([group, tokens]) => (
            <div key={group}>
              <p className="mb-2 text-xs font-semibold uppercase tracking-wider text-muted-foreground">{group}</p>
              <div className="grid grid-cols-2 gap-2.5 sm:grid-cols-3">
                {tokens.map((token) => (
                  <Swatch key={token} token={token} />
                ))}
              </div>
            </div>
          ))}
        </div>
        <div className="mt-6 space-y-2">
          {(["navy", "blue", "cyan", "slate"] as const).map((ramp) => (
            <div key={ramp} className="flex items-center gap-3">
              <span className="w-12 text-xs font-medium capitalize text-muted-foreground">{ramp}</span>
              <div className="flex flex-1 overflow-hidden rounded-md border border-border">
                {brandRamps[ramp].map((step) => (
                  <span key={step} className="h-8 flex-1" style={{ background: `var(--${ramp}-${step})` }} title={`--${ramp}-${step}`} />
                ))}
              </div>
            </div>
          ))}
          <div className="flex items-center gap-3">
            <span className="w-12 text-xs font-medium text-muted-foreground">Chart</span>
            <div className="flex flex-1 gap-0.5 overflow-hidden rounded-md">
              {chartColors.map((color, index) => (
                <span key={color} className="flex h-8 flex-1 items-center justify-center text-xs font-medium text-white" style={{ background: color }}>
                  {index + 1}
                </span>
              ))}
            </div>
          </div>
        </div>
      </Section>

      <Section id="type" title="Typography" description="Inter Variable for interface text, the system monospace for identifiers. Sizes are compact: 14px body, 12px secondary.">
        <div className="space-y-3">
          <p className="text-2xl font-semibold tracking-tight">Page title · 24/32 semibold</p>
          <p className="text-lg font-semibold tracking-tight">Section title · 18/28 semibold</p>
          <p className="text-sm font-semibold">Card title · 14/20 semibold</p>
          <p className="text-sm">Body · 14/20 regular. Used for table cells, form controls and descriptions.</p>
          <p className="text-xs text-muted-foreground">Secondary · 12/16 muted. Captions, timestamps and helper text.</p>
          <p className="font-mono text-xs">Monospace · 12/16 — 10.20.4.118 · SN-0000-SAMPLE · v1.4.2</p>
        </div>
      </Section>

      <Section id="actions" title="Buttons" description="One primary action per view. Destructive actions are red and always confirmed. Icon-only buttons need an aria-label.">
        <div className="flex flex-wrap items-center gap-2">
          <Button>
            <Plus /> Primary
          </Button>
          <Button variant="secondary">Secondary</Button>
          <Button variant="outline">
            <Download /> Outline
          </Button>
          <Button variant="ghost">Ghost</Button>
          <Button variant="destructive">
            <Trash2 /> Destructive
          </Button>
          <Button variant="link">Link</Button>
          <Button loading>Saving</Button>
          <Button disabled>Disabled</Button>
          <Tooltip content="Settings">
            <Button variant="outline" size="icon" aria-label="Settings">
              <Settings2 />
            </Button>
          </Tooltip>
        </div>
        <div className="mt-3 flex flex-wrap items-center gap-2">
          <Button size="sm">Small</Button>
          <Button size="md">Medium</Button>
          <Button size="lg">Large</Button>
        </div>
      </Section>

      <Section id="status" title="Badges" description="Status badges pair a tone with a dot and a label, so state never depends on colour alone.">
        <div className="flex flex-wrap items-center gap-2">
          <StatusBadge tone="success" pulse>
            Online
          </StatusBadge>
          <StatusBadge tone="warning">Offline</StatusBadge>
          <StatusBadge tone="danger">Unhealthy</StatusBadge>
          <StatusBadge tone="info">Pending</StatusBadge>
          <StatusBadge tone="neutral">Retired</StatusBadge>
          <Badge tone="brand">Owner</Badge>
          <Badge tone="warning">Update</Badge>
          <Avatar name="Sample User" />
          <Kbd>⌘K</Kbd>
        </div>
      </Section>

      <Section id="forms" title="Forms" description="Labels sit above controls. Hints explain, errors replace the hint and are announced. Sections group related fields.">
        <Card>
          <CardContent className="space-y-5 pt-5">
            <FormSection title="Identity" description="How the device appears in lists.">
              <Field label="Display name" htmlFor="ds-name" hint="Shown instead of the hostname." required>
                <Input id="ds-name" placeholder="Reception PC" />
              </Field>
              <Field label="Asset tag" htmlFor="ds-tag" error="Asset tag is already in use.">
                <Input id="ds-tag" defaultValue="SAMPLE-001" aria-invalid />
              </Field>
            </FormSection>
            <FormSection title="Placement">
              <Field label="Branch" htmlFor="ds-branch">
                <Select id="ds-branch" defaultValue="">
                  <option value="">No branch</option>
                  <option>Sample branch</option>
                </Select>
              </Field>
              <Field label="Notes" htmlFor="ds-notes">
                <Textarea id="ds-notes" placeholder="Optional" />
              </Field>
              <label className="flex items-center gap-2 text-sm">
                <Checkbox defaultChecked /> Notify the assigned user
              </label>
            </FormSection>
            <FormActions>
              <Button variant="outline">Cancel</Button>
              <Button>Save changes</Button>
            </FormActions>
          </CardContent>
        </Card>
      </Section>

      <Section id="feedback" title="Feedback" description="Alerts stay on the page. Toasts confirm the result of an action and disappear; errors stay longer.">
        <div className="grid gap-3 md:grid-cols-2">
          <Alert tone="info" title="Information">Neutral context for the current view.</Alert>
          <Alert tone="success" title="Success">The change was saved.</Alert>
          <Alert tone="warning" title="Warning">Something needs attention soon.</Alert>
          <Alert tone="error" title="Error" action={<Button variant="outline" size="sm">Try again</Button>}>
            The request failed.
          </Alert>
        </div>
        <div className="mt-4 flex flex-wrap gap-2">
          <Button variant="outline" onClick={() => toast({ tone: "success", title: "Sample saved", description: "This is a sample success toast." })}>
            Success toast
          </Button>
          <Button variant="outline" onClick={() => toast({ tone: "error", title: "Sample failed", description: "This is a sample error toast." })}>
            Error toast
          </Button>
          <Button variant="outline" onClick={() => toast({ tone: "warning", title: "Sample warning" })}>
            Warning toast
          </Button>
          <Button variant="outline" onClick={() => toast({ tone: "info", title: "Sample notice", action: { label: "Undo", onClick: () => {} } })}>
            Toast with action
          </Button>
        </div>
      </Section>

      <Section id="metrics" title="Metric cards" description="A figure, a label and context. With no data source the card says so; it never shows a placeholder number.">
        <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
          <MetricCard label="Ready (sample)" value={1284} icon={Monitor} caption="Sample caption" href="#metrics" />
          <MetricCard label="Loading" icon={Boxes} state="loading" />
          <MetricCard label="Unavailable" icon={ShieldAlert} state="unavailable" message="Module not connected" />
          <MetricCard label="Error" icon={WifiOff} state="error" />
        </div>
      </Section>

      <Section id="charts" title="Charts" description="Bars for comparing categories, a segmented bar for one whole split into parts. Every chart has a table view and loading, empty and error states.">
        <div className="grid gap-4 lg:grid-cols-2 xl:grid-cols-4">
          <ChartCard title="Ranked bars" description="Sample data" table={{ columns: ["Series", "Value"], rows: SAMPLE_BARS }} className="xl:col-span-2">
            <HorizontalBarChart data={SAMPLE_BARS} unit="items" />
          </ChartCard>
          <ChartCard title="Segmented bar" description="Sample data" className="xl:col-span-2">
            <SegmentedBar data={SAMPLE_PARTS} unit="items" totalLabel="sample items" />
          </ChartCard>
          <ChartCard title="Loading" state="loading" minHeight={140}>
            {null}
          </ChartCard>
          <ChartCard title="Empty" state="empty" message="Nothing has been reported yet." minHeight={140}>
            {null}
          </ChartCard>
          <ChartCard title="Error" state="error" message="The request failed." onRetry={() => {}} minHeight={140} className="xl:col-span-2">
            {null}
          </ChartCard>
        </div>
      </Section>

      <Section id="table" title="Data table" description="Search, filters, column picker, sorting, selection with bulk actions, and pagination. Select a row to see the bulk action bar.">
        <SampleTable />
      </Section>

      <Section id="overlays" title="Overlays" description="Dialogs for focused tasks, confirmation dialogs for destructive actions, drawers for detail alongside the page, menus and popovers for choices.">
        <div className="flex flex-wrap gap-2">
          <Button variant="outline" onClick={() => setDialog(true)}>
            Dialog
          </Button>
          <Button variant="outline" onClick={() => setConfirm(true)}>
            Confirmation dialog
          </Button>
          <Sheet>
            <SheetTrigger asChild>
              <Button variant="outline">Drawer</Button>
            </SheetTrigger>
            <SheetContent>
              <SheetHeader>
                <SheetTitle>Sample drawer</SheetTitle>
                <SheetDescription>Detail that keeps the page in context.</SheetDescription>
              </SheetHeader>
              <SheetBody>
                <p className="text-sm text-muted-foreground">Drawers scroll independently and trap focus while open.</p>
              </SheetBody>
              <SheetFooter>
                <SheetClose asChild>
                  <Button variant="outline">Close</Button>
                </SheetClose>
              </SheetFooter>
            </SheetContent>
          </Sheet>
          <DropdownMenu>
            <DropdownMenuTrigger asChild>
              <Button variant="outline">
                <MoreHorizontal /> Dropdown menu
              </Button>
            </DropdownMenuTrigger>
            <DropdownMenuContent align="start">
              <DropdownMenuLabel>Actions</DropdownMenuLabel>
              <DropdownMenuItem>
                <Download /> Export <DropdownMenuShortcut>⌘E</DropdownMenuShortcut>
              </DropdownMenuItem>
              <DropdownMenuItem disabled>Unavailable action</DropdownMenuItem>
              <DropdownMenuSeparator />
              <DropdownMenuItem tone="danger">
                <Trash2 /> Delete…
              </DropdownMenuItem>
            </DropdownMenuContent>
          </DropdownMenu>
          <Popover>
            <PopoverTrigger asChild>
              <Button variant="outline">Popover</Button>
            </PopoverTrigger>
            <PopoverContent>
              <p className="text-sm font-medium">Sample popover</p>
              <p className="mt-1 text-sm text-muted-foreground">For filters, pickers and short forms.</p>
            </PopoverContent>
          </Popover>
        </div>
        <Dialog
          open={dialog}
          onClose={() => setDialog(false)}
          title="Sample dialog"
          description="A focused task that needs a decision before continuing."
          footer={
            <>
              <Button variant="outline" onClick={() => setDialog(false)}>
                Cancel
              </Button>
              <Button onClick={() => setDialog(false)}>Save</Button>
            </>
          }
        >
          <Field label="Name" htmlFor="ds-dialog-name">
            <Input id="ds-dialog-name" />
          </Field>
        </Dialog>
        <ConfirmDialog
          open={confirm}
          onOpenChange={setConfirm}
          title="Delete 3 sample records?"
          description="Says exactly what will be removed and that it cannot be undone."
          confirmLabel="Delete 3 records"
          confirmText="delete"
          onConfirm={() => toast({ tone: "success", title: "Sample confirmed" })}
        />
      </Section>

      <Section id="states" title="States" description="Every data view handles loading, empty and error. Skeletons mirror the layout they replace; empty states say what to do next.">
        <div className="grid gap-4 lg:grid-cols-3">
          <Card>
            <CardHeader>
              <CardTitle>Loading</CardTitle>
              <CardDescription>Skeleton</CardDescription>
            </CardHeader>
            <CardContent className="space-y-3">
              <Skeleton className="h-4 w-3/4" />
              <Skeleton className="h-4 w-1/2" />
              <Skeleton className="h-16 w-full" />
            </CardContent>
          </Card>
          <Card>
            <EmptyState title="No device groups yet" description="Groups let you target several devices at once." action={<Button size="sm"><Plus /> Create group</Button>} />
          </Card>
          <Card>
            <ErrorState description="The server didn't respond." onRetry={() => {}} />
          </Card>
        </div>
        <Tabs defaultValue="one" className="mt-6">
          <TabsList>
            <TabsTrigger value="one">Overview</TabsTrigger>
            <TabsTrigger value="two">Hardware</TabsTrigger>
            <TabsTrigger value="three">Software</TabsTrigger>
          </TabsList>
          <TabsContent value="one" className="text-sm text-muted-foreground">
            Tabs switch between views of the same object.
          </TabsContent>
          <TabsContent value="two" className="text-sm text-muted-foreground">
            Second panel.
          </TabsContent>
          <TabsContent value="three" className="text-sm text-muted-foreground">
            Third panel.
          </TabsContent>
        </Tabs>
      </Section>
    </>
  );
}

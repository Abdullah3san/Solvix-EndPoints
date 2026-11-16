"use client";

import * as React from "react";
import { Copy, Plus, RefreshCw, Trash2 } from "lucide-react";
import { api } from "@/lib/api/client";
import type {
  CreatedInboundChannelDto,
  EscalationAction,
  EscalationRuleDto,
  EscalationTrigger,
  InboundChannelDto,
  MemberDto,
  SlaPolicyDto,
  TechnicianDto,
  TicketCategoryDto,
  TicketPriority,
} from "@/lib/api/types";
import { daysToMask, formatMinutes, maskToDays, minuteToTime, ticketPriorities, timeToMinute, weekdayNames } from "@/lib/helpdesk";
import { emptyToNull, messageFrom } from "@/lib/forms";
import { Alert } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Field } from "@/components/ui/field";
import { Checkbox, Input, Select } from "@/components/ui/input";
import { Table, THead, TBody, TR, TH, TD, EmptyRow } from "@/components/ui/table";
import { useToast } from "@/components/providers/toast-provider";

type Tab = "categories" | "technicians" | "sla" | "escalation" | "email";

interface Props {
  categories: TicketCategoryDto[];
  technicians: TechnicianDto[];
  policies: SlaPolicyDto[];
  rules: EscalationRuleDto[];
  channels: InboundChannelDto[];
  members: MemberDto[];
}

export function HelpdeskSettingsView(props: Props) {
  const [tab, setTab] = React.useState<Tab>("categories");
  const [categories, setCategories] = React.useState(props.categories);
  const [technicians, setTechnicians] = React.useState(props.technicians);
  const [policies, setPolicies] = React.useState(props.policies);
  const [rules, setRules] = React.useState(props.rules);
  const [channels, setChannels] = React.useState(props.channels);

  const tabs: { key: Tab; label: string }[] = [
    { key: "categories", label: "Categories" },
    { key: "technicians", label: "Technicians" },
    { key: "sla", label: "SLA policies" },
    { key: "escalation", label: "Escalation" },
    { key: "email", label: "E-mail intake" },
  ];

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap gap-1" role="tablist">
        {tabs.map((t) => (
          <Button key={t.key} role="tab" aria-selected={tab === t.key} size="sm" variant={tab === t.key ? "primary" : "outline"} onClick={() => setTab(t.key)}>
            {t.label}
          </Button>
        ))}
      </div>
      {tab === "categories" ? <Categories items={categories} technicians={technicians} reload={async () => setCategories(await api.get("helpdesk/categories", { includeInactive: true }))} /> : null}
      {tab === "technicians" ? (
        <Technicians items={technicians} members={props.members} categories={categories} reload={async () => setTechnicians(await api.get("helpdesk/technicians"))} />
      ) : null}
      {tab === "sla" ? <SlaPolicies items={policies} reload={async () => setPolicies(await api.get("helpdesk/sla-policies"))} /> : null}
      {tab === "escalation" ? <Escalation items={rules} technicians={technicians} reload={async () => setRules(await api.get("helpdesk/escalation-rules"))} /> : null}
      {tab === "email" ? <EmailChannels items={channels} reload={async () => setChannels(await api.get("helpdesk/email-channels"))} /> : null}
    </div>
  );
}

// ------------------------------------------------------------------------------------------------ categories

function Categories({ items, technicians, reload }: { items: TicketCategoryDto[]; technicians: TechnicianDto[]; reload: () => Promise<void> }) {
  const { toast } = useToast();
  const [name, setName] = React.useState("");
  const [keywords, setKeywords] = React.useState("");
  const [defaultTech, setDefaultTech] = React.useState("");

  async function add(e: React.FormEvent) {
    e.preventDefault();
    try {
      await api.post("helpdesk/categories", { name, description: null, defaultTechnicianId: emptyToNull(defaultTech), keywords: keywords.split(",").map((k) => k.trim()).filter(Boolean) });
      setName("");
      setKeywords("");
      setDefaultTech("");
      toast({ tone: "success", title: "Category added" });
      await reload();
    } catch (err) {
      toast({ tone: "error", title: "Could not add category", description: messageFrom(err) });
    }
  }

  async function toggle(c: TicketCategoryDto) {
    try {
      await api.put(`helpdesk/categories/${c.id}`, { name: c.name, description: c.description, defaultTechnicianId: c.defaultTechnicianId, keywords: c.keywords, isActive: !c.isActive });
      await reload();
    } catch (err) {
      toast({ tone: "error", title: "Could not update category", description: messageFrom(err) });
    }
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle>Categories</CardTitle>
        <CardDescription>Keywords help suggest a category for tickets that arrive by e-mail. Suggestions are never applied automatically.</CardDescription>
      </CardHeader>
      <Table>
        <THead>
          <TR>
            <TH>Name</TH>
            <TH>Default technician</TH>
            <TH>Keywords</TH>
            <TH>Status</TH>
            <TH />
          </TR>
        </THead>
        <TBody>
          {items.length === 0 ? <EmptyRow colSpan={5}>No categories.</EmptyRow> : null}
          {items.map((c) => (
            <TR key={c.id}>
              <TD className="font-medium">{c.name}</TD>
              <TD>{technicians.find((t) => t.id === c.defaultTechnicianId)?.displayName ?? "—"}</TD>
              <TD className="text-muted-foreground">{c.keywords.join(", ") || "—"}</TD>
              <TD>
                <Badge tone={c.isActive ? "success" : "neutral"}>{c.isActive ? "Active" : "Inactive"}</Badge>
              </TD>
              <TD className="text-right">
                <Button size="sm" variant="ghost" onClick={() => toggle(c)}>
                  {c.isActive ? "Deactivate" : "Activate"}
                </Button>
              </TD>
            </TR>
          ))}
        </TBody>
      </Table>
      <CardContent>
        <form onSubmit={add} className="grid gap-3 pt-4 sm:grid-cols-4">
          <Input value={name} onChange={(e) => setName(e.target.value)} placeholder="New category" aria-label="Category name" required maxLength={100} />
          <Input value={keywords} onChange={(e) => setKeywords(e.target.value)} placeholder="Keywords, comma separated" aria-label="Keywords" />
          <Select value={defaultTech} onChange={(e) => setDefaultTech(e.target.value)} aria-label="Default technician">
            <option value="">No default technician</option>
            {technicians.filter((t) => t.isActive).map((t) => (
              <option key={t.id} value={t.id}>
                {t.displayName ?? t.email}
              </option>
            ))}
          </Select>
          <Button type="submit">
            <Plus className="h-4 w-4" /> Add
          </Button>
        </form>
      </CardContent>
    </Card>
  );
}

// ---------------------------------------------------------------------------------------------- technicians

function Technicians({ items, members, categories, reload }: { items: TechnicianDto[]; members: MemberDto[]; categories: TicketCategoryDto[]; reload: () => Promise<void> }) {
  const { toast } = useToast();
  const [userId, setUserId] = React.useState("");
  const [capacity, setCapacity] = React.useState("");
  const available = members.filter((m) => !items.some((t) => t.userId === m.userId));

  async function add(e: React.FormEvent) {
    e.preventDefault();
    try {
      await api.post("helpdesk/technicians", { userId, maxOpenTickets: capacity ? Number(capacity) : null, categoryIds: [], notes: null });
      setUserId("");
      setCapacity("");
      toast({ tone: "success", title: "Technician added" });
      await reload();
    } catch (err) {
      toast({ tone: "error", title: "Could not add technician", description: messageFrom(err) });
    }
  }

  async function update(t: TechnicianDto, patch: Partial<Pick<TechnicianDto, "isActive" | "isAvailable">>) {
    try {
      await api.put(`helpdesk/technicians/${t.id}`, { isActive: t.isActive, isAvailable: t.isAvailable, maxOpenTickets: t.maxOpenTickets, categoryIds: t.categoryIds, notes: t.notes, ...patch });
      await reload();
    } catch (err) {
      toast({ tone: "error", title: "Could not update technician", description: messageFrom(err) });
    }
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle>Technicians</CardTitle>
        <CardDescription>Members who work tickets. Available technicians receive automatic assignments, balanced by open workload and capacity.</CardDescription>
      </CardHeader>
      <Table>
        <THead>
          <TR>
            <TH>Technician</TH>
            <TH>Open tickets</TH>
            <TH>Capacity</TH>
            <TH>Skills</TH>
            <TH>On duty</TH>
            <TH>Status</TH>
          </TR>
        </THead>
        <TBody>
          {items.length === 0 ? <EmptyRow colSpan={6}>No technicians yet. Workspace admins can always work tickets.</EmptyRow> : null}
          {items.map((t) => (
            <TR key={t.id}>
              <TD>
                <div className="font-medium">{t.displayName ?? "—"}</div>
                <div className="text-xs text-muted-foreground">{t.email}</div>
              </TD>
              <TD>{t.openTickets}</TD>
              <TD>{t.maxOpenTickets ?? "∞"}</TD>
              <TD className="text-muted-foreground">{t.categoryIds.length === 0 ? "All" : t.categoryIds.map((id) => categories.find((c) => c.id === id)?.name ?? "?").join(", ")}</TD>
              <TD>
                <label className="flex items-center gap-2 text-sm">
                  <Checkbox checked={t.isAvailable} onChange={(e) => update(t, { isAvailable: e.target.checked })} aria-label={`${t.displayName} on duty`} />
                </label>
              </TD>
              <TD>
                <Button size="sm" variant="ghost" onClick={() => update(t, { isActive: !t.isActive })}>
                  {t.isActive ? "Deactivate" : "Activate"}
                </Button>
              </TD>
            </TR>
          ))}
        </TBody>
      </Table>
      <CardContent>
        <form onSubmit={add} className="grid gap-3 pt-4 sm:grid-cols-4">
          <Select value={userId} onChange={(e) => setUserId(e.target.value)} aria-label="Member" required className="sm:col-span-2">
            <option value="">Choose a member…</option>
            {available.map((m) => (
              <option key={m.userId} value={m.userId}>
                {m.displayName} ({m.email})
              </option>
            ))}
          </Select>
          <Input type="number" min={1} max={1000} value={capacity} onChange={(e) => setCapacity(e.target.value)} placeholder="Max open tickets" aria-label="Capacity" />
          <Button type="submit" disabled={!userId}>
            <Plus className="h-4 w-4" /> Add technician
          </Button>
        </form>
      </CardContent>
    </Card>
  );
}

// ------------------------------------------------------------------------------------------------------ SLA

const defaultTargets = (): Record<TicketPriority, { fr: string; res: string }> => ({
  Low: { fr: "1440", res: "10080" },
  Medium: { fr: "480", res: "4320" },
  High: { fr: "120", res: "480" },
  Critical: { fr: "30", res: "240" },
});

function SlaPolicies({ items, reload }: { items: SlaPolicyDto[]; reload: () => Promise<void> }) {
  const { toast } = useToast();
  const [name, setName] = React.useState("");
  const [business, setBusiness] = React.useState(false);
  const [zone, setZone] = React.useState("UTC");
  const [start, setStart] = React.useState("09:00");
  const [end, setEnd] = React.useState("17:00");
  const [days, setDays] = React.useState<number[]>([1, 2, 3, 4, 5]);
  const [targets, setTargets] = React.useState(defaultTargets);
  const [makeDefault, setMakeDefault] = React.useState(false);

  async function add(e: React.FormEvent) {
    e.preventDefault();
    const startMinute = timeToMinute(start);
    const endMinute = timeToMinute(end);
    if (business && (startMinute === null || endMinute === null || endMinute <= startMinute || days.length === 0)) {
      toast({ tone: "error", title: "Check the working hours", description: "End must be after start and at least one day must be selected." });
      return;
    }
    try {
      await api.post("helpdesk/sla-policies", {
        name,
        isDefault: makeDefault,
        businessHoursOnly: business,
        timeZoneId: zone,
        workdayStartMinute: startMinute ?? 540,
        workdayEndMinute: endMinute ?? 1020,
        workDaysMask: daysToMask(days),
        targets: ticketPriorities.map((p) => ({ priority: p, firstResponseMinutes: Number(targets[p].fr), resolutionMinutes: Number(targets[p].res) })),
      });
      setName("");
      toast({ tone: "success", title: "SLA policy created" });
      await reload();
    } catch (err) {
      toast({ tone: "error", title: "Could not create policy", description: messageFrom(err) });
    }
  }

  async function remove(p: SlaPolicyDto) {
    try {
      await api.delete(`helpdesk/sla-policies/${p.id}`);
      await reload();
    } catch (err) {
      toast({ tone: "error", title: "Could not delete policy", description: messageFrom(err) });
    }
  }

  return (
    <div className="space-y-4">
      {items.map((p) => (
        <Card key={p.id}>
          <CardHeader>
            <CardTitle className="flex items-center gap-2">
              {p.name} {p.isDefault ? <Badge tone="brand">Default</Badge> : null}
            </CardTitle>
            <CardDescription>
              {p.businessHoursOnly ? `Business hours ${minuteToTime(p.workdayStartMinute)}–${minuteToTime(p.workdayEndMinute)} ${p.timeZoneId}, ${maskToDays(p.workDaysMask).map((d) => weekdayNames[d]).join(" ")}` : "Round the clock (24×7)"}
            </CardDescription>
          </CardHeader>
          <Table>
            <THead>
              <TR>
                <TH>Priority</TH>
                <TH>First response</TH>
                <TH>Resolution</TH>
              </TR>
            </THead>
            <TBody>
              {p.targets.map((t) => (
                <TR key={t.priority}>
                  <TD>{t.priority}</TD>
                  <TD>{formatMinutes(t.firstResponseMinutes)}</TD>
                  <TD>{formatMinutes(t.resolutionMinutes)}</TD>
                </TR>
              ))}
            </TBody>
          </Table>
          {!p.isDefault ? (
            <CardContent className="pt-3">
              <Button size="sm" variant="ghost" className="text-destructive" onClick={() => remove(p)}>
                <Trash2 className="h-4 w-4" /> Delete
              </Button>
            </CardContent>
          ) : null}
        </Card>
      ))}

      <Card>
        <CardHeader>
          <CardTitle>New SLA policy</CardTitle>
          <CardDescription>Minutes per priority. The clock pauses while a ticket waits on the requester.</CardDescription>
        </CardHeader>
        <CardContent>
          <form onSubmit={add} className="space-y-4">
            <div className="grid gap-3 sm:grid-cols-2">
              <Field label="Name" htmlFor="sla-name" required>
                <Input id="sla-name" value={name} onChange={(e) => setName(e.target.value)} required maxLength={100} />
              </Field>
              <div className="flex flex-col justify-end gap-2 text-sm">
                <label className="flex items-center gap-2">
                  <Checkbox checked={business} onChange={(e) => setBusiness(e.target.checked)} /> Count business hours only
                </label>
                <label className="flex items-center gap-2">
                  <Checkbox checked={makeDefault} onChange={(e) => setMakeDefault(e.target.checked)} /> Use for new tickets (default)
                </label>
              </div>
            </div>
            {business ? (
              <div className="grid gap-3 sm:grid-cols-4">
                <Field label="Time zone (IANA)" htmlFor="sla-zone">
                  <Input id="sla-zone" value={zone} onChange={(e) => setZone(e.target.value)} placeholder="Europe/London" />
                </Field>
                <Field label="Start" htmlFor="sla-start">
                  <Input id="sla-start" value={start} onChange={(e) => setStart(e.target.value)} placeholder="09:00" />
                </Field>
                <Field label="End" htmlFor="sla-end">
                  <Input id="sla-end" value={end} onChange={(e) => setEnd(e.target.value)} placeholder="17:00" />
                </Field>
                <fieldset className="flex flex-wrap items-end gap-2 text-xs">
                  <legend className="mb-1 text-sm">Days</legend>
                  {weekdayNames.map((d, i) => (
                    <label key={d} className="flex items-center gap-1">
                      <Checkbox checked={days.includes(i)} onChange={(e) => setDays((cur) => (e.target.checked ? [...cur, i] : cur.filter((x) => x !== i)))} /> {d}
                    </label>
                  ))}
                </fieldset>
              </div>
            ) : null}
            <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
              {ticketPriorities.map((p) => (
                <fieldset key={p} className="space-y-2 rounded-md border border-border p-3">
                  <legend className="px-1 text-sm font-medium">{p}</legend>
                  <Field label="First response (min)" htmlFor={`fr-${p}`}>
                    <Input id={`fr-${p}`} type="number" min={1} value={targets[p].fr} onChange={(e) => setTargets((t) => ({ ...t, [p]: { ...t[p], fr: e.target.value } }))} />
                  </Field>
                  <Field label="Resolution (min)" htmlFor={`res-${p}`}>
                    <Input id={`res-${p}`} type="number" min={1} value={targets[p].res} onChange={(e) => setTargets((t) => ({ ...t, [p]: { ...t[p], res: e.target.value } }))} />
                  </Field>
                </fieldset>
              ))}
            </div>
            <Button type="submit">Create policy</Button>
          </form>
        </CardContent>
      </Card>
    </div>
  );
}

// ------------------------------------------------------------------------------------------------ escalation

const triggerLabels: Record<EscalationTrigger, string> = {
  FirstResponseElapsedPercent: "of the first-response time has passed with no reply",
  ResolutionElapsedPercent: "of the resolution time has passed",
};

const actionLabels: Record<EscalationAction, string> = {
  FlagOnly: "Flag as escalated",
  RaisePriority: "Raise the priority one level",
  Reassign: "Reassign to a technician",
};

function Escalation({ items, technicians, reload }: { items: EscalationRuleDto[]; technicians: TechnicianDto[]; reload: () => Promise<void> }) {
  const { toast } = useToast();
  const [name, setName] = React.useState("");
  const [trigger, setTrigger] = React.useState<EscalationTrigger>("ResolutionElapsedPercent");
  const [percent, setPercent] = React.useState("80");
  const [action, setAction] = React.useState<EscalationAction>("FlagOnly");
  const [target, setTarget] = React.useState("");
  const [min, setMin] = React.useState("");

  async function add(e: React.FormEvent) {
    e.preventDefault();
    try {
      await api.post("helpdesk/escalation-rules", { name, trigger, thresholdPercent: Number(percent), action, targetTechnicianId: action === "Reassign" ? emptyToNull(target) : null, minPriority: emptyToNull(min) });
      setName("");
      toast({ tone: "success", title: "Rule added" });
      await reload();
    } catch (err) {
      toast({ tone: "error", title: "Could not add rule", description: messageFrom(err) });
    }
  }

  async function remove(r: EscalationRuleDto) {
    try {
      await api.delete(`helpdesk/escalation-rules/${r.id}`);
      await reload();
    } catch (err) {
      toast({ tone: "error", title: "Could not delete rule", description: messageFrom(err) });
    }
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle>Escalation rules</CardTitle>
        <CardDescription>Each rule applies once per ticket. Checked every few minutes while a ticket is open and its SLA clock is running.</CardDescription>
      </CardHeader>
      <Table>
        <THead>
          <TR>
            <TH>Rule</TH>
            <TH>When</TH>
            <TH>Then</TH>
            <TH />
          </TR>
        </THead>
        <TBody>
          {items.length === 0 ? <EmptyRow colSpan={4}>No escalation rules. Overdue tickets are still marked as breached.</EmptyRow> : null}
          {items.map((r) => (
            <TR key={r.id}>
              <TD className="font-medium">{r.name}</TD>
              <TD>
                {r.thresholdPercent}% {triggerLabels[r.trigger]}
                {r.minPriority ? ` (${r.minPriority}+ only)` : ""}
              </TD>
              <TD>
                {actionLabels[r.action]}
                {r.action === "Reassign" ? ` → ${technicians.find((t) => t.id === r.targetTechnicianId)?.displayName ?? "?"}` : ""}
              </TD>
              <TD className="text-right">
                <Button size="icon" variant="ghost" aria-label={`Delete ${r.name}`} className="text-destructive" onClick={() => remove(r)}>
                  <Trash2 className="h-4 w-4" />
                </Button>
              </TD>
            </TR>
          ))}
        </TBody>
      </Table>
      <CardContent>
        <form onSubmit={add} className="grid gap-3 pt-4 sm:grid-cols-3">
          <Input value={name} onChange={(e) => setName(e.target.value)} placeholder="Rule name" aria-label="Rule name" required maxLength={100} />
          <div className="flex items-center gap-2">
            <Input type="number" min={1} max={200} value={percent} onChange={(e) => setPercent(e.target.value)} aria-label="Threshold percent" className="w-20" /> %
            <Select value={trigger} onChange={(e) => setTrigger(e.target.value as EscalationTrigger)} aria-label="Trigger">
              {(Object.keys(triggerLabels) as EscalationTrigger[]).map((t) => (
                <option key={t} value={t}>
                  {triggerLabels[t]}
                </option>
              ))}
            </Select>
          </div>
          <Select value={action} onChange={(e) => setAction(e.target.value as EscalationAction)} aria-label="Action">
            {(Object.keys(actionLabels) as EscalationAction[]).map((a) => (
              <option key={a} value={a}>
                {actionLabels[a]}
              </option>
            ))}
          </Select>
          {action === "Reassign" ? (
            <Select value={target} onChange={(e) => setTarget(e.target.value)} aria-label="Target technician" required>
              <option value="">Choose technician…</option>
              {technicians.filter((t) => t.isActive).map((t) => (
                <option key={t.id} value={t.id}>
                  {t.displayName ?? t.email}
                </option>
              ))}
            </Select>
          ) : null}
          <Select value={min} onChange={(e) => setMin(e.target.value)} aria-label="Minimum priority">
            <option value="">All priorities</option>
            {ticketPriorities.map((p) => (
              <option key={p} value={p}>
                {p} and above
              </option>
            ))}
          </Select>
          <Button type="submit">
            <Plus className="h-4 w-4" /> Add rule
          </Button>
        </form>
      </CardContent>
    </Card>
  );
}

// ---------------------------------------------------------------------------------------------- e-mail intake

function EmailChannels({ items, reload }: { items: InboundChannelDto[]; reload: () => Promise<void> }) {
  const { toast } = useToast();
  const [name, setName] = React.useState("");
  const [address, setAddress] = React.useState("");
  const [domains, setDomains] = React.useState("");
  const [priority, setPriority] = React.useState<TicketPriority>("Medium");
  const [secret, setSecret] = React.useState<{ name: string; token: string } | null>(null);

  const parseDomains = () => domains.split(",").map((d) => d.trim()).filter(Boolean);

  async function add(e: React.FormEvent) {
    e.preventDefault();
    try {
      const created = await api.post<CreatedInboundChannelDto>("helpdesk/email-channels", { name, address: emptyToNull(address), defaultPriority: priority, allowedSenderDomains: parseDomains() });
      setSecret({ name: created.channel.name, token: created.token });
      setName("");
      setAddress("");
      setDomains("");
      await reload();
    } catch (err) {
      toast({ tone: "error", title: "Could not create channel", description: messageFrom(err) });
    }
  }

  async function rotate(c: InboundChannelDto) {
    try {
      const created = await api.post<CreatedInboundChannelDto>(`helpdesk/email-channels/${c.id}/rotate`);
      setSecret({ name: c.name, token: created.token });
    } catch (err) {
      toast({ tone: "error", title: "Could not rotate the secret", description: messageFrom(err) });
    }
  }

  async function setEnabled(c: InboundChannelDto, enabled: boolean) {
    try {
      await api.put(`helpdesk/email-channels/${c.id}`, { name: c.name, address: c.address, isEnabled: enabled, defaultPriority: c.defaultPriority, allowedSenderDomains: c.allowedSenderDomains });
      await reload();
    } catch (err) {
      toast({ tone: "error", title: "Could not update the channel", description: messageFrom(err) });
    }
  }

  async function remove(c: InboundChannelDto) {
    try {
      await api.delete(`helpdesk/email-channels/${c.id}`);
      await reload();
    } catch (err) {
      toast({ tone: "error", title: "Could not delete the channel", description: messageFrom(err) });
    }
  }

  return (
    <div className="space-y-4">
      <Alert tone="info" title="How e-mail becomes a ticket">
        Forward mail from your support mailbox to the API with a mail provider webhook, an Azure Function or a Power Automate flow: POST the message as JSON to <span className="font-mono">/api/v1/helpdesk/inbound-email</span> with the channel secret in the{" "}
        <span className="font-mono">X-Channel-Token</span> header. Messages arrive uncategorised and wait in the Triage queue.
      </Alert>

      {secret ? (
        <Alert tone="success" title={`Secret for “${secret.name}”: copy it now`}>
          <div className="flex items-center gap-2">
            <code className="break-all rounded bg-muted px-2 py-1 text-xs">{secret.token}</code>
            <Button size="sm" variant="outline" onClick={() => navigator.clipboard.writeText(secret.token)}>
              <Copy className="h-3.5 w-3.5" /> Copy
            </Button>
            <Button size="sm" variant="ghost" onClick={() => setSecret(null)}>
              Done
            </Button>
          </div>
          <p className="mt-1 text-xs">It is shown only once. Anyone holding it can create tickets in this workspace, so store it like a password.</p>
        </Alert>
      ) : null}

      <Card>
        <Table>
          <THead>
            <TR>
              <TH>Channel</TH>
              <TH>Default priority</TH>
              <TH>Allowed senders</TH>
              <TH>Last received</TH>
              <TH>Status</TH>
              <TH />
            </TR>
          </THead>
          <TBody>
            {items.length === 0 ? <EmptyRow colSpan={6}>No e-mail channels yet.</EmptyRow> : null}
            {items.map((c) => (
              <TR key={c.id}>
                <TD>
                  <div className="font-medium">{c.name}</div>
                  <div className="text-xs text-muted-foreground">{c.address ?? ""}</div>
                </TD>
                <TD>{c.defaultPriority}</TD>
                <TD className="text-muted-foreground">{c.allowedSenderDomains.join(", ") || "Anyone"}</TD>
                <TD className="text-muted-foreground">{c.lastReceivedAtUtc ? new Date(c.lastReceivedAtUtc).toLocaleString() : "Never"}</TD>
                <TD>
                  <Badge tone={c.isEnabled ? "success" : "neutral"}>{c.isEnabled ? "Enabled" : "Disabled"}</Badge>
                </TD>
                <TD>
                  <div className="flex justify-end gap-1">
                    <Button size="sm" variant="ghost" onClick={() => setEnabled(c, !c.isEnabled)}>
                      {c.isEnabled ? "Disable" : "Enable"}
                    </Button>
                    <Button size="sm" variant="ghost" onClick={() => rotate(c)}>
                      <RefreshCw className="h-3.5 w-3.5" /> Rotate secret
                    </Button>
                    <Button size="icon" variant="ghost" className="text-destructive" aria-label={`Delete ${c.name}`} onClick={() => remove(c)}>
                      <Trash2 className="h-4 w-4" />
                    </Button>
                  </div>
                </TD>
              </TR>
            ))}
          </TBody>
        </Table>
        <CardContent>
          <form onSubmit={add} className="grid gap-3 pt-4 sm:grid-cols-5">
            <Input value={name} onChange={(e) => setName(e.target.value)} placeholder="Channel name" aria-label="Channel name" required maxLength={100} />
            <Input type="email" value={address} onChange={(e) => setAddress(e.target.value)} placeholder="support@yourcompany.com" aria-label="Mailbox address" />
            <Input value={domains} onChange={(e) => setDomains(e.target.value)} placeholder="Only from domains (optional)" aria-label="Allowed sender domains" />
            <Select value={priority} onChange={(e) => setPriority(e.target.value as TicketPriority)} aria-label="Default priority">
              {ticketPriorities.map((p) => (
                <option key={p}>{p}</option>
              ))}
            </Select>
            <Button type="submit">
              <Plus className="h-4 w-4" /> Add channel
            </Button>
          </form>
        </CardContent>
      </Card>
    </div>
  );
}

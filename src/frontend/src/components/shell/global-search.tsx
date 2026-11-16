"use client";

import * as React from "react";
import { useRouter } from "next/navigation";
import { CornerDownLeft, Loader2, Monitor, Search } from "lucide-react";
import { api } from "@/lib/api/client";
import type { DeviceSummaryDto, PagedResult } from "@/lib/api/types";
import { deviceLabel } from "@/lib/devices";
import { isDevelopment } from "@/lib/env";
import { messageFrom } from "@/lib/forms";
import { CommandDialog, CommandEmpty, CommandGroup, CommandInput, CommandItem, CommandList } from "@/components/ui/command";
import { Kbd } from "@/components/ui/kbd";
import { StatusBadge } from "@/components/ui/badge";
import { useCan } from "@/components/providers/session-provider";
import { visibleNavigation, type NavItem } from "./nav-config";
import { connectivityTone } from "@/lib/devices";

const MIN_QUERY = 2;
const DEBOUNCE_MS = 250;

type DeviceResults = { status: "idle" | "loading" } | { status: "ready"; query: string; items: DeviceSummaryDto[]; total: number } | { status: "error"; message: string };

export function matchPages(items: NavItem[], query: string): NavItem[] {
  const q = query.trim().toLowerCase();
  if (!q) return items;
  return items.filter((item) => `${item.label} ${item.keywords ?? ""}`.toLowerCase().includes(q));
}

/**
 * Global search (⌘K / Ctrl+K): jump to any page, or find a device by name,
 * serial number, model, IP or MAC address. Device results come from the API.
 */
export function GlobalSearch() {
  const router = useRouter();
  const { isAdmin } = useCan();
  const [open, setOpen] = React.useState(false);
  const [query, setQuery] = React.useState("");
  const [devices, setDevices] = React.useState<DeviceResults>({ status: "idle" });

  const pages = React.useMemo(() => visibleNavigation({ isAdmin, isDevelopment }).flatMap((section) => section.items), [isAdmin]);
  const matchedPages = matchPages(pages, query);
  const term = query.trim();

  React.useEffect(() => {
    function onKeyDown(event: KeyboardEvent) {
      if ((event.metaKey || event.ctrlKey) && event.key.toLowerCase() === "k") {
        event.preventDefault();
        setOpen((value) => !value);
      }
    }
    document.addEventListener("keydown", onKeyDown);
    return () => document.removeEventListener("keydown", onKeyDown);
  }, []);

  React.useEffect(() => {
    if (!open || term.length < MIN_QUERY) return;
    let cancelled = false;
    const timer = window.setTimeout(async () => {
      setDevices({ status: "loading" });
      try {
        const result = await api.get<PagedResult<DeviceSummaryDto>>("devices", { search: term, pageSize: 6 });
        if (!cancelled) setDevices({ status: "ready", query: term, items: result.items, total: result.totalCount });
      } catch (error) {
        if (!cancelled) setDevices({ status: "error", message: messageFrom(error, "Device search failed.") });
      }
    }, DEBOUNCE_MS);
    return () => {
      cancelled = true;
      window.clearTimeout(timer);
    };
  }, [open, term]);

  function onOpenChange(next: boolean) {
    setOpen(next);
    if (!next) {
      setQuery("");
      setDevices({ status: "idle" });
    }
  }

  function go(href: string) {
    onOpenChange(false);
    router.push(href);
  }

  const searching = term.length >= MIN_QUERY;
  const deviceItems = searching && devices.status === "ready" ? devices.items : [];
  const pending = searching && (devices.status === "loading" || devices.status === "idle" || (devices.status === "ready" && devices.query !== term));

  return (
    <>
      <button
        type="button"
        onClick={() => setOpen(true)}
        className="hidden h-9 w-56 items-center gap-2 rounded-md border border-input bg-card px-2.5 text-sm text-muted-foreground shadow-xs transition-colors hover:border-muted-foreground/50 md:flex xl:w-72"
        aria-label="Search"
        aria-keyshortcuts="Meta+K Control+K"
      >
        <Search className="size-4 shrink-0" aria-hidden />
        <span className="flex-1 truncate text-left">Search devices and pages…</span>
        <Kbd>⌘K</Kbd>
      </button>
      <button
        type="button"
        onClick={() => setOpen(true)}
        className="flex size-9 items-center justify-center rounded-md text-muted-foreground hover:bg-muted hover:text-foreground md:hidden"
        aria-label="Search"
      >
        <Search className="size-[18px]" />
      </button>

      <CommandDialog open={open} onOpenChange={onOpenChange} title="Search" description="Search devices and pages" shouldFilter={false}>
        <CommandInput
          value={query}
          onValueChange={setQuery}
          placeholder="Search devices by name, serial, IP or MAC, or jump to a page…"
          trailing={pending ? <Loader2 className="size-4 shrink-0 animate-spin text-muted-foreground" aria-label="Searching" /> : null}
        />
        <CommandList>
          {matchedPages.length === 0 && (!searching || (devices.status === "ready" && deviceItems.length === 0 && !pending)) ? (
            <CommandEmpty>No pages or devices match “{term}”.</CommandEmpty>
          ) : null}

          {deviceItems.length > 0 ? (
            <CommandGroup heading="Devices">
              {deviceItems.map((device) => (
                <CommandItem key={device.id} value={`device-${device.id}`} onSelect={() => go(`/endpoints/devices/${device.id}`)}>
                  <Monitor />
                  <span className="min-w-0 flex-1">
                    <span className="block truncate font-medium">{deviceLabel(device)}</span>
                    <span className="block truncate text-xs text-muted-foreground">
                      {[device.osName, device.ipAddresses[0], device.serialNumber].filter(Boolean).join(" · ") || device.hostname}
                    </span>
                  </span>
                  <StatusBadge tone={connectivityTone(device.status, device.connectivity)}>{device.status === "Retired" ? "Retired" : device.connectivity}</StatusBadge>
                </CommandItem>
              ))}
              {devices.status === "ready" && devices.total > deviceItems.length ? (
                <CommandItem value="all-devices" onSelect={() => go(`/endpoints/devices?search=${encodeURIComponent(term)}`)}>
                  <Search />
                  <span className="flex-1">
                    View all {devices.total} matching devices
                  </span>
                </CommandItem>
              ) : null}
            </CommandGroup>
          ) : null}

          {searching && devices.status === "error" ? (
            <p role="alert" className="px-4 py-3 text-sm text-destructive">
              {devices.message}
            </p>
          ) : null}

          {matchedPages.length > 0 ? (
            <CommandGroup heading={term ? "Pages" : "Go to"}>
              {matchedPages.map((item) => (
                <CommandItem key={item.href} value={`page-${item.href}`} onSelect={() => go(item.href)}>
                  <item.icon />
                  <span className="flex-1">{item.label}</span>
                </CommandItem>
              ))}
            </CommandGroup>
          ) : null}
        </CommandList>
        <div className="flex items-center justify-between border-t border-border px-4 py-2 text-xs text-muted-foreground">
          <span>{term.length > 0 && term.length < MIN_QUERY ? `Type at least ${MIN_QUERY} characters to search devices` : "Devices are matched by name, serial, model, IP and MAC"}</span>
          <span className="hidden items-center gap-1.5 sm:flex">
            <Kbd>↑</Kbd>
            <Kbd>↓</Kbd> navigate
            <Kbd>
              <CornerDownLeft className="size-3" />
            </Kbd>
            open
          </span>
        </div>
      </CommandDialog>
    </>
  );
}

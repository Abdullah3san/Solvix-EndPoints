"use client";

import * as React from "react";
import { cn } from "@/lib/utils";

/**
 * Minimal accessible dropdown. Click or Enter/Space on the trigger opens the
 * panel; Escape, outside click or selecting an item closes it.
 */
interface MenuContextValue {
  open: boolean;
  setOpen: (open: boolean) => void;
  id: string;
}

const MenuContext = React.createContext<MenuContextValue | null>(null);

function useMenu() {
  const ctx = React.useContext(MenuContext);
  if (!ctx) throw new Error("Menu components must be used inside <Menu>");
  return ctx;
}

export function Menu({ children, className }: { children: React.ReactNode; className?: string }) {
  const [open, setOpen] = React.useState(false);
  const id = React.useId();
  const ref = React.useRef<HTMLDivElement>(null);

  React.useEffect(() => {
    if (!open) return;
    function onPointerDown(event: PointerEvent) {
      if (ref.current && !ref.current.contains(event.target as Node)) setOpen(false);
    }
    function onKeyDown(event: KeyboardEvent) {
      if (event.key === "Escape") setOpen(false);
    }
    document.addEventListener("pointerdown", onPointerDown);
    document.addEventListener("keydown", onKeyDown);
    return () => {
      document.removeEventListener("pointerdown", onPointerDown);
      document.removeEventListener("keydown", onKeyDown);
    };
  }, [open]);

  return (
    <MenuContext.Provider value={{ open, setOpen, id }}>
      <div ref={ref} className={cn("relative", className)}>
        {children}
      </div>
    </MenuContext.Provider>
  );
}

export function MenuTrigger({ children, className, onClick, ...props }: React.ButtonHTMLAttributes<HTMLButtonElement>) {
  const { open, setOpen, id } = useMenu();
  return (
    <button
      type="button"
      aria-haspopup="menu"
      aria-expanded={open}
      aria-controls={`${id}-panel`}
      className={cn("inline-flex items-center rounded-md", className)}
      {...props}
      onClick={(event) => {
        onClick?.(event);
        if (!event.defaultPrevented) setOpen(!open);
      }}
    >
      {children}
    </button>
  );
}

export function MenuPanel({
  children,
  className,
  align = "end",
  label,
}: {
  children: React.ReactNode;
  className?: string;
  align?: "start" | "end";
  label?: string;
}) {
  const { open, id } = useMenu();
  if (!open) return null;
  return (
    <div
      id={`${id}-panel`}
      role="menu"
      aria-label={label}
      className={cn(
        "absolute z-50 mt-2 min-w-48 overflow-hidden rounded-lg border border-border bg-card p-1 text-card-foreground shadow-lg",
        align === "end" ? "right-0" : "left-0",
        className,
      )}
    >
      {children}
    </div>
  );
}

export function MenuItem({
  children,
  className,
  onSelect,
  disabled,
  tone = "default",
  active,
  ...props
}: Omit<React.ButtonHTMLAttributes<HTMLButtonElement>, "onSelect"> & {
  onSelect?: () => void;
  tone?: "default" | "danger";
  active?: boolean;
}) {
  const { setOpen } = useMenu();
  return (
    <button
      type="button"
      role="menuitem"
      disabled={disabled}
      aria-current={active ? "true" : undefined}
      onClick={() => {
        onSelect?.();
        setOpen(false);
      }}
      className={cn(
        "flex w-full items-center gap-2 rounded-md px-2.5 py-2 text-left text-sm transition-colors hover:bg-muted disabled:opacity-50",
        tone === "danger" && "text-destructive hover:bg-destructive/10",
        active && "bg-muted font-medium",
        className,
      )}
      {...props}
    >
      {children}
    </button>
  );
}

export function MenuSeparator() {
  return <div role="separator" className="my-1 h-px bg-border" />;
}

export function MenuLabel({ children }: { children: React.ReactNode }) {
  return <div className="px-2.5 py-1.5 text-xs font-medium text-muted-foreground">{children}</div>;
}

import * as React from "react";
import { cn } from "@/lib/utils";

/** Keyboard key hint, e.g. the ⌘K shortcut on the search trigger. */
export function Kbd({ className, ...props }: React.HTMLAttributes<HTMLElement>) {
  return (
    <kbd
      className={cn("inline-flex h-5 min-w-5 items-center justify-center rounded border border-border bg-muted px-1 font-sans text-2xs font-medium text-muted-foreground", className)}
      {...props}
    />
  );
}

"use client";

import * as React from "react";
import { Command as CommandPrimitive } from "cmdk";
import { Search } from "lucide-react";
import { cn } from "@/lib/utils";
import { DialogContent, DialogDescription, DialogRoot, DialogTitle } from "./dialog";

/** Command palette parts (cmdk). Used by the global search; reusable for pickers. */
export const Command = React.forwardRef<HTMLDivElement, React.ComponentPropsWithoutRef<typeof CommandPrimitive>>(function Command({ className, ...props }, ref) {
  return <CommandPrimitive ref={ref} className={cn("flex h-full w-full flex-col overflow-hidden", className)} {...props} />;
});

export function CommandDialog({
  open,
  onOpenChange,
  title,
  description,
  children,
  shouldFilter,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  title: string;
  description: string;
  children: React.ReactNode;
  shouldFilter?: boolean;
}) {
  return (
    <DialogRoot open={open} onOpenChange={onOpenChange}>
      <DialogContent hideClose className="top-[12vh] max-w-xl translate-y-0 overflow-hidden p-0">
        <DialogTitle className="sr-only">{title}</DialogTitle>
        <DialogDescription className="sr-only">{description}</DialogDescription>
        <Command shouldFilter={shouldFilter} label={title}>
          {children}
        </Command>
      </DialogContent>
    </DialogRoot>
  );
}

export const CommandInput = React.forwardRef<HTMLInputElement, React.ComponentPropsWithoutRef<typeof CommandPrimitive.Input> & { trailing?: React.ReactNode }>(
  function CommandInput({ className, trailing, ...props }, ref) {
    return (
      <div className="flex items-center gap-2 border-b border-border px-4">
        <Search className="size-4 shrink-0 text-muted-foreground" aria-hidden />
        <CommandPrimitive.Input
          ref={ref}
          className={cn("h-12 w-full bg-transparent text-sm outline-none placeholder:text-muted-foreground focus-visible:outline-none", className)}
          {...props}
        />
        {trailing}
      </div>
    );
  },
);

export function CommandList({ className, ...props }: React.ComponentPropsWithoutRef<typeof CommandPrimitive.List>) {
  return <CommandPrimitive.List className={cn("scrollbar-thin max-h-[min(24rem,60dvh)] overflow-y-auto p-1.5", className)} {...props} />;
}

export function CommandEmpty(props: React.ComponentPropsWithoutRef<typeof CommandPrimitive.Empty>) {
  return <CommandPrimitive.Empty className="px-4 py-8 text-center text-sm text-muted-foreground" {...props} />;
}

export function CommandGroup({ className, ...props }: React.ComponentPropsWithoutRef<typeof CommandPrimitive.Group>) {
  return (
    <CommandPrimitive.Group
      className={cn("[&_[cmdk-group-heading]]:px-2.5 [&_[cmdk-group-heading]]:py-1.5 [&_[cmdk-group-heading]]:text-xs [&_[cmdk-group-heading]]:font-medium [&_[cmdk-group-heading]]:text-muted-foreground", className)}
      {...props}
    />
  );
}

export function CommandItem({ className, ...props }: React.ComponentPropsWithoutRef<typeof CommandPrimitive.Item>) {
  return (
    <CommandPrimitive.Item
      className={cn(
        "flex cursor-default select-none items-center gap-2.5 rounded-md px-2.5 py-2 text-sm outline-none data-[disabled=true]:pointer-events-none data-[selected=true]:bg-muted data-[disabled=true]:opacity-50 [&_svg]:size-4 [&_svg]:shrink-0 [&_svg]:text-muted-foreground",
        className,
      )}
      {...props}
    />
  );
}

import { cn, initials } from "@/lib/utils";

const sizes = { sm: "size-6 text-2xs", md: "size-8 text-xs", lg: "size-10 text-sm" } as const;

/** Initials avatar. The name is exposed to assistive technology via `title`. */
export function Avatar({ name, size = "md", className }: { name: string | null | undefined; size?: keyof typeof sizes; className?: string }) {
  return (
    <span
      title={name ?? undefined}
      className={cn("inline-flex shrink-0 select-none items-center justify-center rounded-full bg-secondary font-semibold text-secondary-foreground", sizes[size], className)}
    >
      {initials(name)}
    </span>
  );
}

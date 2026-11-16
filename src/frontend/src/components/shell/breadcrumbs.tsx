"use client";

import Link from "next/link";
import { usePathname } from "next/navigation";
import { ChevronRight, Home } from "lucide-react";
import { segmentLabels } from "./nav-config";
import { cn, titleCase } from "@/lib/utils";

export interface Crumb {
  label: string;
  href?: string;
}

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/** Builds crumbs from a pathname; the last segment is the current page (no link). */
export function crumbsFromPathname(pathname: string): Crumb[] {
  const segments = pathname.split("/").filter(Boolean);
  return segments.map((segment, index) => {
    const href = "/" + segments.slice(0, index + 1).join("/");
    const label = segmentLabels[segment] ?? (UUID.test(segment) ? "Details" : titleCase(decodeURIComponent(segment)));
    return index === segments.length - 1 ? { label } : { label, href };
  });
}

/** Location trail in the header. On narrow screens only the current page is shown. */
export function Breadcrumbs({ pathname: override }: { pathname?: string }) {
  const current = usePathname();
  const pathname = override ?? current;
  const crumbs = crumbsFromPathname(pathname);

  return (
    <nav aria-label="Breadcrumb" className="min-w-0">
      <ol className="flex items-center gap-1 text-sm text-muted-foreground">
        <li className="hidden sm:block">
          <Link href="/dashboard" className="flex size-7 items-center justify-center rounded-md hover:bg-muted hover:text-foreground" aria-label="Home">
            <Home className="size-4" />
          </Link>
        </li>
        {crumbs.map((crumb, index) => (
          <li key={`${crumb.label}-${index}`} className={cn("min-w-0 items-center gap-1", crumb.href ? "hidden sm:flex" : "flex")}>
            <ChevronRight className="hidden size-3.5 shrink-0 opacity-60 sm:block" aria-hidden />
            {crumb.href ? (
              <Link href={crumb.href} className="truncate rounded px-1 py-0.5 hover:bg-muted hover:text-foreground">
                {crumb.label}
              </Link>
            ) : (
              <span className="truncate px-1 font-medium text-foreground" aria-current="page">
                {crumb.label}
              </span>
            )}
          </li>
        ))}
      </ol>
    </nav>
  );
}

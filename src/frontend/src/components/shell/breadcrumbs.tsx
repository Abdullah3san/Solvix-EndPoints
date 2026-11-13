"use client";

import Link from "next/link";
import { usePathname } from "next/navigation";
import { ChevronRight, Home } from "lucide-react";
import { segmentLabels } from "./nav-config";
import { titleCase } from "@/lib/utils";

export interface Crumb {
  label: string;
  href?: string;
}

/** Builds crumbs from a pathname; the last segment is the current page (no link). */
export function crumbsFromPathname(pathname: string): Crumb[] {
  const segments = pathname.split("/").filter(Boolean);
  return segments.map((segment, index) => {
    const href = "/" + segments.slice(0, index + 1).join("/");
    const label =
      segmentLabels[segment] ?? (/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(segment) ? "Details" : titleCase(decodeURIComponent(segment)));
    return index === segments.length - 1 ? { label } : { label, href };
  });
}

export function Breadcrumbs({ pathname: override }: { pathname?: string }) {
  const current = usePathname();
  const pathname = override ?? current;
  const crumbs = crumbsFromPathname(pathname);

  return (
    <nav aria-label="Breadcrumb" className="min-w-0">
      <ol className="flex items-center gap-1 text-sm text-muted-foreground">
        <li>
          <Link href="/dashboard" className="flex items-center rounded hover:text-foreground" aria-label="Home">
            <Home className="h-4 w-4" />
          </Link>
        </li>
        {crumbs.map((crumb, index) => (
          <li key={`${crumb.label}-${index}`} className="flex min-w-0 items-center gap-1">
            <ChevronRight className="h-4 w-4 shrink-0 opacity-60" aria-hidden />
            {crumb.href ? (
              <Link href={crumb.href} className="truncate rounded hover:text-foreground">
                {crumb.label}
              </Link>
            ) : (
              <span className="truncate font-medium text-foreground" aria-current="page">
                {crumb.label}
              </span>
            )}
          </li>
        ))}
      </ol>
    </nav>
  );
}

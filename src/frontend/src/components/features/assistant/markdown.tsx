import * as React from "react";
import { cn } from "@/lib/utils";

/**
 * Minimal, dependency-free markdown renderer for assistant output. It builds React elements only
 * (never HTML strings), so model output can not inject markup or scripts. Supports headings,
 * paragraphs, bullet/numbered lists, fenced code, pipe tables, **bold**, `code` and [S1] citations.
 */
const INLINE = /(\*\*[^*]+\*\*|`[^`]+`|\[S\d{1,3}\])/g;

function inline(text: string, keyPrefix: string): React.ReactNode[] {
  return text.split(INLINE).map((part, i) => {
    const key = `${keyPrefix}-${i}`;
    if (part.startsWith("**") && part.endsWith("**") && part.length > 4) return <strong key={key}>{part.slice(2, -2)}</strong>;
    if (part.startsWith("`") && part.endsWith("`") && part.length > 2)
      return (
        <code key={key} className="rounded bg-muted px-1 py-0.5 font-mono text-[0.85em]">
          {part.slice(1, -1)}
        </code>
      );
    if (/^\[S\d{1,3}\]$/.test(part))
      return (
        <sup key={key} className="mx-0.5 rounded bg-secondary px-1 text-[10px] font-semibold text-secondary-foreground" title="Knowledge-base source">
          {part.slice(1, -1)}
        </sup>
      );
    return <React.Fragment key={key}>{part}</React.Fragment>;
  });
}

function splitRow(line: string): string[] {
  return line.trim().replace(/^\|/, "").replace(/\|$/, "").split(/(?<!\\)\|/).map((c) => c.trim().replace(/\\\|/g, "|"));
}

export function Markdown({ text, className }: { text: string; className?: string }) {
  const lines = text.replace(/\r\n/g, "\n").split("\n");
  const blocks: React.ReactNode[] = [];
  let i = 0;
  let k = 0;

  while (i < lines.length) {
    const line = lines[i];
    if (line.trim() === "") {
      i++;
      continue;
    }

    if (line.trim().startsWith("```")) {
      const code: string[] = [];
      i++;
      while (i < lines.length && !lines[i].trim().startsWith("```")) code.push(lines[i++]);
      i++;
      blocks.push(
        <pre key={k++} className="overflow-x-auto rounded-md bg-muted p-3 font-mono text-xs">
          {code.join("\n")}
        </pre>,
      );
      continue;
    }

    const heading = /^(#{1,6})\s+(.*)$/.exec(line);
    if (heading) {
      const level = Math.min(heading[1].length + 2, 6);
      const Tag = `h${level}` as "h3" | "h4" | "h5" | "h6";
      blocks.push(
        <Tag key={k++} className="mt-3 font-semibold">
          {inline(heading[2], `h${k}`)}
        </Tag>,
      );
      i++;
      continue;
    }

    if (line.includes("|") && i + 1 < lines.length && /^\s*\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?\s*$/.test(lines[i + 1])) {
      const header = splitRow(line);
      i += 2;
      const rows: string[][] = [];
      while (i < lines.length && lines[i].includes("|") && lines[i].trim() !== "") rows.push(splitRow(lines[i++]));
      blocks.push(
        <div key={k++} className="overflow-x-auto">
          <table className="w-full text-left text-xs">
            <thead>
              <tr className="border-b border-border">
                {header.map((h, c) => (
                  <th key={c} className="px-2 py-1.5 font-semibold">
                    {inline(h, `th${k}-${c}`)}
                  </th>
                ))}
              </tr>
            </thead>
            <tbody>
              {rows.map((r, ri) => (
                <tr key={ri} className="border-b border-border/60 last:border-0">
                  {r.map((cell, ci) => (
                    <td key={ci} className="px-2 py-1.5">
                      {inline(cell, `td${k}-${ri}-${ci}`)}
                    </td>
                  ))}
                </tr>
              ))}
            </tbody>
          </table>
        </div>,
      );
      continue;
    }

    const bullet = /^\s*[-*]\s+/;
    const numbered = /^\s*\d+[.)]\s+/;
    if (bullet.test(line) || numbered.test(line)) {
      const ordered = numbered.test(line);
      const re = ordered ? numbered : bullet;
      const items: string[] = [];
      while (i < lines.length && re.test(lines[i])) items.push(lines[i++].replace(re, ""));
      const List = ordered ? "ol" : "ul";
      blocks.push(
        <List key={k++} className={cn("ml-5 space-y-1", ordered ? "list-decimal" : "list-disc")}>
          {items.map((it, li) => (
            <li key={li}>{inline(it, `li${k}-${li}`)}</li>
          ))}
        </List>,
      );
      continue;
    }

    const para: string[] = [];
    while (i < lines.length && lines[i].trim() !== "" && !/^(#{1,6}\s|```)/.test(lines[i]) && !bullet.test(lines[i]) && !numbered.test(lines[i])) para.push(lines[i++]);
    blocks.push(<p key={k++}>{inline(para.join(" "), `p${k}`)}</p>);
  }

  return <div className={cn("space-y-2 text-sm leading-relaxed", className)}>{blocks}</div>;
}

/** Header names a spreadsheet export usually starts with; skipped when found as the first entry. */
const HEADERS = new Set(["hostname", "host name", "computer", "computer name", "computername", "name", "device", "device name", "pc"]);

/**
 * Turns pasted text or the contents of a CSV/TXT file into hostnames: one per line, or separated by commas,
 * semicolons or tabs (only the first column of a multi-column row is kept). Blank entries and a leading header
 * row are dropped; validation is the server's job.
 */
export function parseHostnames(text: string): string[] {
  const names: string[] = [];
  for (const line of text.split(/\r?\n/)) {
    const cells = line.split(/[,;\t]/).map((c) => c.trim().replace(/^"|"$/g, "").trim());
    // A line with several cells is a CSV row (take its first cell); a single cell is the hostname itself.
    const first = cells[0] ?? "";
    if (first) names.push(first);
  }
  if (names.length > 0 && HEADERS.has(names[0].toLowerCase())) names.shift();
  return names;
}

export const MAX_IMPORT = 1000;

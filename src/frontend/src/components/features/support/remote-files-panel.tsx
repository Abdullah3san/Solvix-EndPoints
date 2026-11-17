"use client";

import * as React from "react";
import { ArrowUp, Download, Folder, FolderOpen, Square, Upload } from "lucide-react";
import { api, PROXY_PREFIX } from "@/lib/api/client";
import type { RemoteAccessMode, RemoteFileEntry, RemoteFileOperationDto } from "@/lib/api/types";
import { fileStatusTone, hasRemoteFeature, joinPath, parentPath, remoteStatusTone } from "@/lib/support";
import { formatBytes, formatRelative } from "@/lib/devices";
import { messageFrom } from "@/lib/forms";
import { Alert } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Table, THead, TBody, TR, TH, TD, EmptyRow } from "@/components/ui/table";
import { useToast } from "@/components/providers/toast-provider";
import { useRemoteSession } from "@/components/features/support/use-remote-session";

/** Browse the folders the computer offers, download from it and upload to it. Every step is recorded. */
export function RemoteFilesPanel({ deviceId, mode, features, retired }: { deviceId: string; mode: RemoteAccessMode | undefined; features: string | undefined; retired: boolean }) {
  const { toast } = useToast();
  const { session, open, error, setError, busy, start, end } = useRemoteSession(deviceId, "Files");
  const [reason, setReason] = React.useState("");
  const [folder, setFolder] = React.useState("");
  const [entries, setEntries] = React.useState<RemoteFileEntry[] | null>(null);
  const [overwrite, setOverwrite] = React.useState(false);
  const [working, setWorking] = React.useState(false);
  const [history, setHistory] = React.useState<RemoteFileOperationDto[]>([]);
  const picker = React.useRef<HTMLInputElement>(null);
  const sessionId = session?.id ?? null;
  const active = session?.status === "Active";
  const enabled = mode !== undefined && mode !== "Disabled" && hasRemoteFeature(features, "Files");

  const loadHistory = React.useCallback(async () => {
    if (!sessionId) return;
    try {
      setHistory(await api.get<RemoteFileOperationDto[]>(`remote-sessions/${sessionId}/files/operations`));
    } catch {
      // The audit list is informational.
    }
  }, [sessionId]);

  const list = React.useCallback(
    async (path: string) => {
      if (!sessionId) return;
      setWorking(true);
      setError(null);
      try {
        const op = await api.post<RemoteFileOperationDto>(`remote-sessions/${sessionId}/files/list`, { path });
        if (op.status === "Failed") setError(op.error ?? "The computer could not list that folder.");
        else if (op.status === "Completed") {
          setEntries(op.entries ?? []);
          setFolder(path);
        } else setError("The computer has not answered yet. It may be offline.");
      } catch (e) {
        setError(messageFrom(e));
      } finally {
        setWorking(false);
        void loadHistory();
      }
    },
    [sessionId, setError, loadHistory],
  );

  // First listing once the computer accepted the session.
  React.useEffect(() => {
    if (!active || entries !== null) return;
    const timer = setTimeout(() => void list(""), 0);
    return () => clearTimeout(timer);
  }, [active, entries, list]);

  async function upload(file: File) {
    if (!sessionId || !folder) {
      setError("Open one of the computer's folders first.");
      return;
    }

    setWorking(true);
    setError(null);
    try {
      const target = joinPath(folder, file.name);
      const response = await fetch(`${PROXY_PREFIX}/remote-sessions/${sessionId}/files/upload?path=${encodeURIComponent(target)}&overwrite=${overwrite}`, {
        method: "POST",
        headers: { "content-type": "application/octet-stream" },
        body: file,
      });
      if (!response.ok) throw new Error((await response.json().catch(() => null))?.detail ?? `Upload failed (${response.status}).`);
      const staged = (await response.json()) as RemoteFileOperationDto;
      const done = await api.get<RemoteFileOperationDto>(`remote-sessions/${sessionId}/files/operations/${staged.id}`, { wait: 25 });
      if (done.status === "Completed") {
        toast({ tone: "success", title: "File uploaded", description: `${file.name} was saved on the computer and its checksum verified.` });
        await list(folder);
      } else if (done.status === "Failed") {
        setError(done.error ?? "The computer refused the file.");
      } else {
        toast({ tone: "info", title: "Waiting for the computer", description: "The file is queued and will be saved when the computer fetches it." });
      }
    } catch (e) {
      setError(messageFrom(e));
    } finally {
      setWorking(false);
      void loadHistory();
    }
  }

  const parent = folder ? parentPath(folder) : null;

  return (
    <div className="space-y-4">
      <Alert tone={enabled ? "info" : "error"}>
        <strong>File transfer:</strong>{" "}
        {enabled
          ? "Enabled on this computer, in the folders it allows. The person at the computer may be asked to approve; every transfer is recorded with its checksum."
          : "Not enabled on this computer. It is switched on locally, in the agent's installation, together with the folders that may be used."}
      </Alert>
      {error ? <Alert tone="error">{error}</Alert> : null}

      {!open ? (
        <form
          onSubmit={(e) => {
            e.preventDefault();
            setEntries(null);
            setFolder("");
            void start(reason);
          }}
          className="flex flex-wrap items-end gap-3"
        >
          <div className="min-w-64 flex-1">
            <label htmlFor="files-reason" className="mb-1.5 block text-sm font-medium">
              Reason (shown to the person at the computer)
            </label>
            <Input id="files-reason" value={reason} onChange={(e) => setReason(e.target.value)} maxLength={500} placeholder="e.g. Collecting the log file you mentioned" />
          </div>
          <Button type="submit" loading={busy} disabled={!enabled || retired}>
            <FolderOpen className="h-4 w-4" /> Start file session
          </Button>
        </form>
      ) : (
        <div className="flex items-center justify-between">
          <Badge tone={remoteStatusTone(session!.status)}>{session!.status === "Pending" ? "Waiting for the computer" : "Connected"}</Badge>
          <Button variant="destructive" onClick={end}>
            <Square className="h-4 w-4" /> End session
          </Button>
        </div>
      )}

      {session && !open ? (
        <p className="text-sm text-muted-foreground">
          Session {session.status.toLowerCase()}
          {session.endReason ? `: ${session.endReason}` : ""}.
        </p>
      ) : null}

      {active ? (
        <>
          <div className="flex flex-wrap items-center gap-2">
            <Button variant="outline" size="sm" disabled={working} onClick={() => void list(parent ?? "")}>
              <ArrowUp className="h-4 w-4" /> {parent ? "Up" : "Folders"}
            </Button>
            <code className="flex-1 truncate rounded bg-muted px-2 py-1 text-xs">{folder || "Folders this computer offers"}</code>
            <label className="flex items-center gap-2 text-sm">
              <input type="checkbox" checked={overwrite} onChange={(e) => setOverwrite(e.target.checked)} /> Replace if it exists
            </label>
            <input
              ref={picker}
              type="file"
              className="hidden"
              aria-label="File to upload"
              onChange={(e) => {
                const file = e.target.files?.[0];
                e.target.value = "";
                if (file) void upload(file);
              }}
            />
            <Button size="sm" disabled={working || !folder} onClick={() => picker.current?.click()}>
              <Upload className="h-4 w-4" /> Upload here
            </Button>
          </div>
          <Card>
            <Table>
              <THead>
                <TR>
                  <TH>Name</TH>
                  <TH>Size</TH>
                  <TH>Modified</TH>
                  <TH className="text-right">Action</TH>
                </TR>
              </THead>
              <TBody>
                {entries === null ? (
                  <EmptyRow colSpan={4}>{working ? "Asking the computer…" : "Nothing listed yet."}</EmptyRow>
                ) : entries.length === 0 ? (
                  <EmptyRow colSpan={4}>This folder is empty.</EmptyRow>
                ) : (
                  entries.map((entry) => (
                    <TR key={entry.path}>
                      <TD>
                        {entry.isDirectory ? (
                          <button className="flex items-center gap-2 font-medium text-primary hover:underline" onClick={() => void list(entry.path)}>
                            <Folder className="h-4 w-4" /> {entry.name}
                          </button>
                        ) : (
                          entry.name
                        )}
                      </TD>
                      <TD>{entry.isDirectory ? "—" : formatBytes(entry.sizeBytes)}</TD>
                      <TD className="text-muted-foreground">{formatRelative(entry.modifiedAtUtc)}</TD>
                      <TD className="text-right">
                        {entry.isDirectory ? null : (
                          <a
                            className="inline-flex items-center gap-1 text-primary hover:underline"
                            href={`${PROXY_PREFIX}/remote-sessions/${sessionId}/files/download?path=${encodeURIComponent(entry.path)}`}
                            download={entry.name}
                          >
                            <Download className="h-4 w-4" /> Download
                          </a>
                        )}
                      </TD>
                    </TR>
                  ))
                )}
              </TBody>
            </Table>
          </Card>
        </>
      ) : null}

      {history.length > 0 ? (
        <>
          <h3 className="text-sm font-medium text-muted-foreground">Activity in this session</h3>
          <Card>
            <Table>
              <THead>
                <TR>
                  <TH>Operation</TH>
                  <TH>Path</TH>
                  <TH>Size</TH>
                  <TH>Result</TH>
                </TR>
              </THead>
              <TBody>
                {history.map((op) => (
                  <TR key={op.id}>
                    <TD>{op.kind}</TD>
                    <TD className="max-w-xs truncate text-muted-foreground" title={op.path}>
                      {op.path || "(folders)"}
                    </TD>
                    <TD>{op.sizeBytes ? formatBytes(op.sizeBytes) : "—"}</TD>
                    <TD>
                      <Badge tone={fileStatusTone(op.status)}>{op.status}</Badge>
                      {op.error ? <span className="ml-2 text-xs text-muted-foreground">{op.error}</span> : null}
                    </TD>
                  </TR>
                ))}
              </TBody>
            </Table>
          </Card>
        </>
      ) : null}
    </div>
  );
}

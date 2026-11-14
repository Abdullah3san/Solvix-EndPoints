"use client";

import * as React from "react";
import { Button } from "@/components/ui/button";
import { Dialog } from "@/components/ui/dialog";
import { Field } from "@/components/ui/field";
import { Textarea } from "@/components/ui/input";
import type { PagedResult } from "@/lib/api/types";

export function Pager<T>({ data, onPage }: { data: PagedResult<T>; onPage: (page: number) => void }) {
  if (data.totalPages <= 1) return null;
  return (
    <div className="flex items-center justify-between border-t border-border px-3 py-2 text-sm text-muted-foreground">
      <span>
        Page {data.page} of {data.totalPages} · {data.totalCount} items
      </span>
      <div className="flex gap-1">
        <Button variant="outline" size="sm" disabled={data.page <= 1} onClick={() => onPage(data.page - 1)}>
          Previous
        </Button>
        <Button variant="outline" size="sm" disabled={data.page >= data.totalPages} onClick={() => onPage(data.page + 1)}>
          Next
        </Button>
      </div>
    </div>
  );
}

/** Collects a (possibly required) note before a decision such as approve, reject or dismiss. */
export function NoteDialog(props: NoteDialogProps) {
  // Mounted only while open, so the note field starts empty every time without an effect.
  return props.open ? <NoteDialogBody {...props} /> : null;
}

interface NoteDialogProps {
  open: boolean;
  title: string;
  description?: string;
  confirmLabel: string;
  noteRequired?: boolean;
  destructive?: boolean;
  busy?: boolean;
  onClose: () => void;
  onConfirm: (note: string) => void;
}

function NoteDialogBody({
  open,
  title,
  description,
  confirmLabel,
  noteRequired,
  destructive,
  busy,
  onClose,
  onConfirm,
}: NoteDialogProps) {
  const [note, setNote] = React.useState("");
  const missing = noteRequired && note.trim().length === 0;
  return (
    <Dialog
      open={open}
      onClose={onClose}
      title={title}
      description={description}
      footer={
        <>
          <Button variant="outline" onClick={onClose}>
            Cancel
          </Button>
          <Button variant={destructive ? "destructive" : "primary"} loading={busy} disabled={missing} onClick={() => onConfirm(note.trim())}>
            {confirmLabel}
          </Button>
        </>
      }
    >
      <Field label={noteRequired ? "Reason" : "Note (optional)"} htmlFor="decision-note" required={noteRequired}>
        <Textarea id="decision-note" value={note} onChange={(e) => setNote(e.target.value)} maxLength={500} rows={3} />
      </Field>
    </Dialog>
  );
}

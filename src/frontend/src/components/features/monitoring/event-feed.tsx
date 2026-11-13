import Link from "next/link";
import type { ConnectionEventDto } from "@/lib/api/types";
import { eventLabel, eventTone, formatDuration } from "@/lib/monitoring";
import { formatRelative } from "@/lib/devices";
import { Badge } from "@/components/ui/badge";

export function EventFeed({ events, showDevice = false }: { events: ConnectionEventDto[]; showDevice?: boolean }) {
  if (events.length === 0) return <p className="text-sm text-muted-foreground">No events yet.</p>;
  return (
    <ol className="space-y-3 text-sm" aria-live="polite">
      {events.map((e) => (
        <li key={e.id} className="flex items-start gap-3">
          <Badge tone={eventTone(e)} className="mt-0.5 shrink-0">
            {eventLabel(e.type)}
          </Badge>
          <div className="min-w-0">
            {showDevice ? (
              <Link href={`/endpoints/devices/${e.deviceId}`} className="font-medium hover:underline">
                {e.deviceName}
              </Link>
            ) : null}
            <p className="text-muted-foreground">
              {e.detail}
              {e.durationSeconds && e.type === "HeartbeatRestored" ? ` Silent for ${formatDuration(e.durationSeconds)}.` : ""}
            </p>
            <p className="text-xs text-muted-foreground" title={e.occurredAtUtc}>
              {formatRelative(e.occurredAtUtc)}
            </p>
          </div>
        </li>
      ))}
    </ol>
  );
}

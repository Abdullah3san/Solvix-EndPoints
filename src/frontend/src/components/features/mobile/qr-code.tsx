"use client";

import * as React from "react";
import QRCode from "qrcode";

/** Renders text as a QR code in the browser, so enrollment secrets are never sent to a third-party service. */
export function QrCode({ value, size = 220 }: { value: string; size?: number }) {
  const [src, setSrc] = React.useState<string | null>(null);
  React.useEffect(() => {
    let cancelled = false;
    QRCode.toDataURL(value, { width: size, margin: 1, errorCorrectionLevel: "M" })
      .then((url) => !cancelled && setSrc(url))
      .catch(() => !cancelled && setSrc(null));
    return () => {
      cancelled = true;
    };
  }, [value, size]);
  if (!src) return <div className="flex items-center justify-center rounded-md border border-border text-xs text-muted-foreground" style={{ width: size, height: size }}>Generating…</div>;
  // eslint-disable-next-line @next/next/no-img-element
  return <img src={src} width={size} height={size} alt="Enrollment QR code" className="rounded-md border border-border bg-white p-1" />;
}

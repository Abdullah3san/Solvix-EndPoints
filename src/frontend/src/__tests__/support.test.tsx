import { afterEach, describe, expect, it, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { lastSequence, mergeMessages, priorityTone, statusLabel, statusTone } from "@/lib/support";
import { ChatPanel } from "@/components/features/support/chat-panel";

describe("support helpers", () => {
  it("labels and colours ticket states", () => {
    expect(statusLabel("InProgress")).toBe("In progress");
    expect(statusTone("Open")).toBe("warning");
    expect(statusTone("Resolved")).toBe("success");
    expect(priorityTone("Urgent")).toBe("danger");
    expect(priorityTone("Low")).toBe("neutral");
  });

  it("merges chat messages without duplicates and keeps them ordered", () => {
    const a = [{ sequence: 1 }, { sequence: 3 }];
    expect(mergeMessages(a, [{ sequence: 3 }, { sequence: 2 }])).toEqual([{ sequence: 1 }, { sequence: 2 }, { sequence: 3 }]);
    expect(mergeMessages(a, [])).toBe(a);
    expect(mergeMessages(a, [{ sequence: 1 }])).toBe(a);
    expect(lastSequence(a)).toBe(3);
    expect(lastSequence([])).toBe(0);
  });
});

describe("chat panel", () => {
  afterEach(() => vi.restoreAllMocks());

  it("shows incoming messages and sends a reply", async () => {
    const calls: { url: string; method: string; body?: string }[] = [];
    vi.stubGlobal(
      "fetch",
      vi.fn(async (url: string, init?: RequestInit) => {
        const method = init?.method ?? "GET";
        calls.push({ url: String(url), method, body: init?.body as string | undefined });
        const json = (data: unknown, status = 200) => new Response(JSON.stringify(data), { status, headers: { "content-type": "application/json" } });
        if (method === "POST") return json({ sequence: 2, sender: "Technician", senderName: "Alex", body: "On it", sentAtUtc: new Date().toISOString() }, 201);
        return json([{ sequence: 1, sender: "User", senderName: "Dana", body: "My VPN is down", sentAtUtc: new Date().toISOString() }]);
      }),
    );

    render(<ChatPanel deviceId="11111111-1111-1111-1111-111111111111" />);
    expect(await screen.findByText("My VPN is down")).toBeInTheDocument();

    await userEvent.type(screen.getByLabelText("Message"), "On it");
    await userEvent.click(screen.getByRole("button", { name: /send/i }));
    await waitFor(() => expect(screen.getByText("On it")).toBeInTheDocument());

    const post = calls.find((c) => c.method === "POST")!;
    expect(post.url).toContain("devices/11111111-1111-1111-1111-111111111111/chat");
    expect(JSON.parse(post.body!)).toEqual({ body: "On it" });
  });
});

describe("remote helpers", () => {
  it("describes the computer's policy and session states", async () => {
    const { remoteModeLabel, remoteStatusTone, appendTerminal } = await import("@/lib/support");
    expect(remoteModeLabel(undefined)).toMatch(/not enabled/i);
    expect(remoteModeLabel("Disabled")).toMatch(/not enabled/i);
    expect(remoteModeLabel("ConsentRequired")).toMatch(/approve/i);
    expect(remoteStatusTone("Active")).toBe("success");
    expect(remoteStatusTone("Denied")).toBe("danger");
    expect(appendTerminal("abc", "def", 4)).toBe("cdef");
  });
});

describe("remote session panel", () => {
  it("explains that remote access is off and blocks starting a session", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn(async () => new Response(JSON.stringify({ items: [], page: 1, pageSize: 20, totalCount: 0, totalPages: 0 }), { status: 200, headers: { "content-type": "application/json" } })),
    );
    const { RemoteSessionPanel } = await import("@/components/features/support/remote-session-panel");
    render(<RemoteSessionPanel deviceId="11111111-1111-1111-1111-111111111111" mode="Disabled" retired={false} />);
    expect(await screen.findByText(/not enabled on this computer/i)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /start remote session/i })).toBeDisabled();
    expect(await screen.findByText(/no remote sessions on this device/i)).toBeInTheDocument();
  });
});

describe("screen and file helpers", () => {
  it("reads the features a computer reported, defaulting to shell for older agents", async () => {
    const { hasRemoteFeature } = await import("@/lib/support");
    expect(hasRemoteFeature(undefined, "Shell")).toBe(true);
    expect(hasRemoteFeature(undefined, "Screen")).toBe(false);
    expect(hasRemoteFeature("Shell, Screen, Control", "Screen")).toBe(true);
    expect(hasRemoteFeature("Shell, Screen, Control", "Files")).toBe(false);
  });

  it("navigates Windows and POSIX paths", async () => {
    const { parentPath, joinPath } = await import("@/lib/support");
    expect(parentPath("C:\\Users\\Public\\Solvix")).toBe("C:\\Users\\Public");
    expect(parentPath("C:\\Users")).toBe("C:\\");
    expect(parentPath("C:\\")).toBeNull();
    expect(parentPath("/var/lib/solvix/")).toBe("/var/lib");
    expect(parentPath("/")).toBeNull();
    expect(joinPath("C:\\Drop", "a.txt")).toBe("C:\\Drop\\a.txt");
    expect(joinPath("C:\\Drop\\", "a.txt")).toBe("C:\\Drop\\a.txt");
    expect(joinPath("/srv/drop", "a.txt")).toBe("/srv/drop/a.txt");
  });

  it("turns browser keys into agent input", async () => {
    const { toKeyEvent, pointerFraction } = await import("@/lib/support");
    const plain = { ctrlKey: false, altKey: false, metaKey: false };
    expect(toKeyEvent({ key: "a", ...plain }, true)).toEqual({ type: "text", characters: "a" });
    expect(toKeyEvent({ key: "a", ...plain }, false)).toBeNull(); // typed once
    expect(toKeyEvent({ key: "Enter", ...plain }, true)).toEqual({ type: "key", name: "Enter", pressed: true });
    expect(toKeyEvent({ key: "c", ...plain, ctrlKey: true }, true)).toEqual({ type: "key", name: "c", pressed: true }); // shortcuts are key presses
    expect(toKeyEvent({ key: "Dead", ...plain }, true)).toBeNull();
    expect(pointerFraction(150, 120, { left: 100, top: 100, width: 200, height: 100 })).toEqual({ x: 0.25, y: 0.2 });
    expect(pointerFraction(0, 999, { left: 100, top: 100, width: 200, height: 100 })).toEqual({ x: 0, y: 1 });
  });
});

describe("screen and file panels", () => {
  function stubEmptyHistory() {
    vi.stubGlobal(
      "fetch",
      vi.fn(async () => new Response(JSON.stringify({ items: [], page: 1, pageSize: 20, totalCount: 0, totalPages: 0 }), { status: 200, headers: { "content-type": "application/json" } })),
    );
  }

  it("will not start a screen session on a computer that has not enabled it", async () => {
    stubEmptyHistory();
    const { RemoteScreenPanel } = await import("@/components/features/support/remote-screen-panel");
    render(<RemoteScreenPanel deviceId="11111111-1111-1111-1111-111111111111" mode="ConsentRequired" features="Shell" retired={false} />);
    expect(await screen.findByText(/not enabled on this computer/i)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /start screen session/i })).toBeDisabled();
  });

  it("offers a screen session, with control only where the computer allows it", async () => {
    stubEmptyHistory();
    const { RemoteScreenPanel } = await import("@/components/features/support/remote-screen-panel");
    const { unmount } = render(<RemoteScreenPanel deviceId="11111111-1111-1111-1111-111111111111" mode="ConsentRequired" features="Shell, Screen" retired={false} />);
    expect(await screen.findByText(/view only/i)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /start screen session/i })).toBeEnabled();
    expect(screen.getByLabelText(/ask to control/i)).toBeDisabled();
    unmount();
    render(<RemoteScreenPanel deviceId="11111111-1111-1111-1111-111111111111" mode="ConsentRequired" features="Shell, Screen, Control" retired={false} />);
    expect(await screen.findByLabelText(/ask to control/i)).toBeEnabled();
  });

  it("will not start a file session on a retired device or one without file transfer", async () => {
    stubEmptyHistory();
    const { RemoteFilesPanel } = await import("@/components/features/support/remote-files-panel");
    const { ToastProvider } = await import("@/components/providers/toast-provider");
    const { unmount } = render(
      <ToastProvider>
        <RemoteFilesPanel deviceId="11111111-1111-1111-1111-111111111111" mode="Unattended" features="Shell" retired={false} />
      </ToastProvider>,
    );
    expect(await screen.findByText(/not enabled on this computer/i)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /start file session/i })).toBeDisabled();
    unmount();
    render(
      <ToastProvider>
        <RemoteFilesPanel deviceId="11111111-1111-1111-1111-111111111111" mode="Unattended" features="Shell, Files" retired />
      </ToastProvider>,
    );
    expect(await screen.findByRole("button", { name: /start file session/i })).toBeDisabled();
  });
});

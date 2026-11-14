import { describe, expect, it, vi, beforeEach } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { Markdown } from "@/components/features/assistant/markdown";
import { ProposalCard } from "@/components/features/assistant/proposal-card";
import { ChatView, EXAMPLE_PROMPTS } from "@/components/features/assistant/chat-view";
import { SessionProvider } from "@/components/providers/session-provider";
import { ToastProvider } from "@/components/providers/toast-provider";
import { isActivePath } from "@/components/shell/nav-config";
import type { AssistantStatusDto, ProposalDto } from "@/lib/api/types";

const post = vi.fn();
const get = vi.fn();
vi.mock("@/lib/api/client", () => ({
  api: { post: (...a: unknown[]) => post(...a), get: (...a: unknown[]) => get(...a), delete: vi.fn(), patch: vi.fn() },
  PROXY_PREFIX: "/api/proxy/v1",
}));
vi.mock("next/link", () => ({ default: ({ href, children, ...p }: { href: string; children: React.ReactNode }) => <a href={href} {...p}>{children}</a> }));

function wrap(ui: React.ReactElement, role: "Member" | "Admin" | "Owner" = "Admin") {
  return render(
    <SessionProvider session={{ user: { id: "u", email: "a@b.c", displayName: "A" }, tenant: { id: "t", slug: "acme", name: "Acme", role } }}>
      <ToastProvider>{ui}</ToastProvider>
    </SessionProvider>,
  );
}

const proposal = (over: Partial<ProposalDto> = {}): ProposalDto => ({
  id: "p1", action: "deploy_software", title: "Deploy 7-Zip to Finance", rationale: "Missing archive tool.", risk: "High", status: "PendingApproval",
  parameters: { packageId: "7zip" }, targets: [{ id: "d1", name: "FIN-PC-001" }], targetCount: 1, proposedByEmail: "a@b.c",
  createdAtUtc: "2026-01-01T00:00:00Z", expiresAtUtc: "2026-01-02T00:00:00Z", requiredApproverRole: "Owner",
  decidedByEmail: null, decidedAtUtc: null, decisionNote: null, executedAtUtc: null, results: [], autoApproved: false, ...over,
});

const status = (over: Partial<AssistantStatusDto> = {}): AssistantStatusDto => ({
  modelConfigured: true, model: "m", embeddingModel: "e", fleetConnected: true, commandsConnected: true, demoData: false, tools: [], actions: [], ...over,
});

beforeEach(() => {
  post.mockReset();
  get.mockReset();
  get.mockResolvedValue({ items: [], page: 1, pageSize: 30, totalCount: 0, totalPages: 0 });
});

describe("markdown", () => {
  it("renders tables, lists, bold and citations", () => {
    render(<Markdown text={"**3** offline [S1]\n\n| Device | CPU |\n|---|---|\n| POS-018 | 97 |\n\n- one\n- two"} />);
    expect(screen.getByText("3").tagName).toBe("STRONG");
    expect(screen.getByText("S1")).toBeInTheDocument();
    expect(screen.getByRole("table")).toBeInTheDocument();
    expect(screen.getByText("POS-018")).toBeInTheDocument();
    expect(screen.getAllByRole("listitem")).toHaveLength(2);
  });

  it("never injects model-supplied HTML", () => {
    const { container } = render(<Markdown text={'<img src=x onerror="alert(1)"> <script>alert(1)</script>'} />);
    expect(container.querySelector("img")).toBeNull();
    expect(container.querySelector("script")).toBeNull();
    expect(container.textContent).toContain("<script>");
  });
});

describe("proposal card", () => {
  it("lets an owner review, confirm and approve a high-risk action", async () => {
    post.mockResolvedValue(proposal({ status: "Succeeded", decidedByEmail: "a@b.c", results: [{ deviceId: "d1", status: "Succeeded", message: "ok" }] }));
    wrap(<ProposalCard proposal={proposal()} />, "Owner");
    expect(screen.getByText("High risk")).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: /review & approve/i }));
    expect(post).not.toHaveBeenCalled(); // explicit confirmation step first
    await userEvent.click(screen.getByRole("button", { name: /approve and execute/i }));
    await waitFor(() => expect(post).toHaveBeenCalledWith("assistant/proposals/p1/approve", { confirm: true }));
    expect(await screen.findByText("Succeeded", { selector: "span" })).toBeInTheDocument();
  });

  it("does not offer approval to someone below the required role", () => {
    wrap(<ProposalCard proposal={proposal()} />, "Admin");
    expect(screen.queryByRole("button", { name: /review & approve/i })).not.toBeInTheDocument();
    expect(screen.getByText(/needs approval from a owner/i)).toBeInTheDocument();
  });

  it("shows API errors from a refused approval", async () => {
    const { ApiError } = await import("@/lib/api/errors");
    post.mockRejectedValue(new ApiError(409, { title: "Conflict", status: 409, detail: "The proposal has expired." }));
    wrap(<ProposalCard proposal={proposal({ risk: "Low", requiredApproverRole: "Admin" })} />, "Admin");
    await userEvent.click(screen.getByRole("button", { name: /review & approve/i }));
    await userEvent.click(screen.getByRole("button", { name: /approve and execute/i }));
    expect((await screen.findAllByText("The proposal has expired."))[0]).toBeInTheDocument();
  });

  it("offers no actions once decided", () => {
    wrap(<ProposalCard proposal={proposal({ status: "Rejected", decidedByEmail: "x@y.z" })} />, "Owner");
    expect(screen.queryByRole("button", { name: /approve/i })).not.toBeInTheDocument();
    expect(screen.getByText(/rejected by x@y.z/i)).toBeInTheDocument();
  });
});

describe("chat view", () => {
  it("sends a message, shows the reply with sources and tool calls", async () => {
    post.mockResolvedValue({
      conversationId: "c1",
      message: {
        id: "m2", role: "Assistant", content: "Restart the Print Spooler [S1].", createdAtUtc: "2026-01-01T00:00:00Z", artifacts: [],
        sources: [{ id: "S1", documentId: "d", documentTitle: "IT Runbook", chunkId: "k", ordinal: 0, section: "Printers", page: null, score: 0.5, cited: true }],
        toolCalls: [{ name: "search_knowledge_base", status: "ok", summary: "" }],
      },
    });
    wrap(<ChatView status={status()} />);
    await userEvent.type(screen.getByLabelText("Message"), "printer stuck");
    await userEvent.click(screen.getByRole("button", { name: /send/i }));
    await waitFor(() => expect(post).toHaveBeenCalledWith("assistant/chat", { conversationId: null, message: "printer stuck" }));
    expect(await screen.findByText(/IT Runbook › Printers/)).toBeInTheDocument();
    expect(screen.getByText(/1 tool call/)).toBeInTheDocument();
    expect(screen.getByText("printer stuck")).toBeInTheDocument();
  });

  it("offers the documented example prompts", async () => {
    post.mockResolvedValue({ conversationId: "c", message: { id: "m", role: "Assistant", content: "ok", sources: [], toolCalls: [], artifacts: [], createdAtUtc: "" } });
    wrap(<ChatView status={status()} />);
    for (const p of EXAMPLE_PROMPTS) expect(screen.getByRole("button", { name: p })).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Show me all offline computers." }));
    await waitFor(() => expect(post).toHaveBeenCalledWith("assistant/chat", { conversationId: null, message: "Show me all offline computers." }));
  });

  it("explains when the model or fleet is not connected and disables input", () => {
    wrap(<ChatView status={status({ modelConfigured: false, fleetConnected: false })} />);
    expect(screen.getByText("AI model not configured")).toBeInTheDocument();
    expect(screen.getByText("No device data source connected")).toBeInTheDocument();
    expect(screen.getByLabelText("Message")).toBeDisabled();
  });

  it("surfaces API failures", async () => {
    const { ApiError } = await import("@/lib/api/errors");
    post.mockRejectedValue(new ApiError(503, { title: "Service unavailable", status: 503, detail: "The AI model is temporarily unavailable." }));
    wrap(<ChatView status={status()} />);
    await userEvent.type(screen.getByLabelText("Message"), "hi");
    await userEvent.click(screen.getByRole("button", { name: /send/i }));
    expect(await screen.findByRole("alert")).toHaveTextContent("temporarily unavailable");
  });
});

describe("navigation", () => {
  it("highlights only the most specific assistant item", () => {
    expect(isActivePath("/assistant", "/assistant")).toBe(true);
    expect(isActivePath("/assistant/approvals", "/assistant")).toBe(false);
    expect(isActivePath("/assistant/approvals", "/assistant/approvals")).toBe(true);
  });
});

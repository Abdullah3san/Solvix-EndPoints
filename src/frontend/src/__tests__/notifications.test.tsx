import { beforeEach, describe, expect, it } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { NotificationsProvider, useNotifications } from "@/components/providers/notifications-provider";
import { NotificationsMenu } from "@/components/shell/notifications-menu";
import { notificationsStore } from "@/components/providers/notifications-store";

function Trigger() {
  const { notify } = useNotifications();
  return (
    <button type="button" onClick={() => notify({ title: "Branch created", description: "Berlin (BER)", tone: "success" })}>
      add
    </button>
  );
}

describe("notifications", () => {
  beforeEach(() => notificationsStore.reset());

  it("counts unread items and marks them read when opened", async () => {
    const user = userEvent.setup();
    render(
      <NotificationsProvider>
        <Trigger />
        <NotificationsMenu />
      </NotificationsProvider>,
    );
    expect(screen.getByRole("button", { name: "Notifications" })).toBeInTheDocument();
    await user.click(screen.getByText("add"));
    expect(screen.getByRole("button", { name: /1 unread/i })).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: /1 unread/i }));
    expect(screen.getByText("Branch created")).toBeInTheDocument();
    expect(screen.getByText("Berlin (BER)")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Notifications" })).toBeInTheDocument();
  });
});

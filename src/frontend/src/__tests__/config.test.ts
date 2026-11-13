import { afterEach, describe, expect, it, vi } from "vitest";

async function loadConfig() {
  vi.resetModules();
  return (await import("@/lib/config")).serverConfig;
}

describe("serverConfig.cookieSecure", () => {
  afterEach(() => {
    vi.unstubAllEnvs();
  });

  it("is on in production builds by default", async () => {
    vi.stubEnv("NODE_ENV", "production");
    vi.stubEnv("SOLVIX_COOKIE_SECURE", "");
    expect((await loadConfig()).cookieSecure).toBe(true);
  });

  it("is off outside production by default", async () => {
    vi.stubEnv("NODE_ENV", "development");
    vi.stubEnv("SOLVIX_COOKIE_SECURE", "");
    expect((await loadConfig()).cookieSecure).toBe(false);
  });

  it("can be turned off explicitly in production (plain-HTTP test mode)", async () => {
    vi.stubEnv("NODE_ENV", "production");
    vi.stubEnv("SOLVIX_COOKIE_SECURE", "false");
    expect((await loadConfig()).cookieSecure).toBe(false);
  });

  it("can be turned on explicitly outside production", async () => {
    vi.stubEnv("NODE_ENV", "development");
    vi.stubEnv("SOLVIX_COOKIE_SECURE", "true");
    expect((await loadConfig()).cookieSecure).toBe(true);
  });
});

describe("serverConfig.agentServerCertificateSha256", () => {
  afterEach(() => {
    vi.unstubAllEnvs();
  });

  it("treats an empty value as no pin", async () => {
    vi.stubEnv("SOLVIX_AGENT_SERVER_CERT_SHA256", "");
    expect((await loadConfig()).agentServerCertificateSha256).toBeNull();
  });
});

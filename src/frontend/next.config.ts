import type { NextConfig } from "next";

const nextConfig: NextConfig = {
  // The whole app is personalised per tenant and rendered behind an auth
  // cookie, so Cache Components (static shells) add nothing in Phase 01.
  cacheComponents: false,
  reactStrictMode: true,
  poweredByHeader: false,
  // Produces a self-contained server in .next/standalone for the Docker image.
  output: "standalone",
  turbopack: {
    rules: {
      "*.css": {
        loaders: ["@tailwindcss/turbopack"],
        as: "*.css",
      },
    },
  },
  async headers() {
    return [
      {
        source: "/:path*",
        headers: [
          { key: "X-Content-Type-Options", value: "nosniff" },
          { key: "X-Frame-Options", value: "DENY" },
          { key: "Referrer-Policy", value: "strict-origin-when-cross-origin" },
        ],
      },
    ];
  },
};

export default nextConfig;

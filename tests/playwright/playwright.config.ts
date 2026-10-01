import { defineConfig } from "@playwright/test";
export default defineConfig({
  testDir: ".",
  // Browser specs share one app database and administrator account.
  workers: 1,
  projects: [
    {
      name: "workspace",
      testIgnore:
        /field-access.spec.ts|session-recovery.spec.ts|schema-designer.spec.ts/,
    },
    {
      name: "field-security",
      testMatch: /field-access.spec.ts/,
      dependencies: ["workspace"],
    },
    {
      name: "schema-designer",
      testMatch: /schema-designer.spec.ts/,
      dependencies: ["field-security"],
    },
    {
      name: "session-recovery",
      testMatch: /session-recovery.spec.ts/,
      dependencies: ["schema-designer"],
    },
  ],
  outputDir: "../../artifacts/playwright-results",
  use: { baseURL: "http://localhost:5173" },
  webServer: [
    {
      command:
        "dotnet run --project ../../backend/DbWeb.Api --no-launch-profile --urls http://localhost:5190",
      url: "http://localhost:5190/health",
      reuseExistingServer: false,
      env: {
        ASPNETCORE_ENVIRONMENT: "Development",
        Bootstrap__Password: "browser-test-only-password",
        LoginRateLimit__PermitLimit: "100",
        DataDirectory: process.env.RUNNER_TEMP
          ? `${process.env.RUNNER_TEMP}/dbweb-browser`
          : "/tmp/dbweb-browser-data",
      },
    },
    {
      command: "npm --prefix ../../frontend run dev",
      env: { API_PROXY_TARGET: "http://localhost:5190" },
      url: "http://localhost:5173",
      reuseExistingServer: false,
    },
  ],
});

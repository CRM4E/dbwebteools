import { test, expect, type Page } from "@playwright/test";

test("configure all field levels and edit only allowed fields with hidden primary keys", async ({
  page,
  browser,
}) => {
  test.setTimeout(240_000);
  async function login(p: Page, username: string, password: string) {
    await p.goto("/");
    // This project runs after workspace tests. Honor the real per-IP login rate limit.
    await expect
      .poll(
        () =>
          p.evaluate(
            async ({ username, password }) => {
              const { token } = await (await fetch("/api/auth/csrf")).json();
              return (
                await fetch("/api/auth/login", {
                  method: "POST",
                  headers: {
                    "Content-Type": "application/json",
                    "X-CSRF-TOKEN": token,
                  },
                  body: JSON.stringify({ username, password }),
                })
              ).status;
            },
            { username, password },
          ),
        { timeout: 70_000, intervals: [1000, 5000] },
      )
      .toBe(200);
    await p.reload();
    await expect(
      p.getByRole("heading", { name: "Data browser", exact: true }),
    ).toBeVisible();
  }
  await login(page, "admin", "browser-test-only-password");
  const setup = await page.evaluate(
    async ({ port, password }) => {
      const { token } = await (await fetch("/api/auth/csrf")).json();
      const headers = {
        "Content-Type": "application/json",
        "X-CSRF-TOKEN": token,
      };
      const connection = await fetch("/api/admin/connections", {
        method: "POST",
        headers,
        body: JSON.stringify({
          name: "Field security browser",
          host: "127.0.0.1",
          port,
          database: "dbweb_tests",
          username: "root",
          password,
          verifyTls: false,
        }),
      });
      const user = await fetch("/api/admin/users", {
        method: "POST",
        headers,
        body: JSON.stringify({
          username: "field_browser",
          password: "field-browser-password-12345",
          isAdmin: false,
          enabled: true,
        }),
      });
      if (!connection.ok || !user.ok)
        throw new Error("Security fixture setup failed");
      return {
        connection: (await connection.json()).id,
        user: (await user.json()).id,
      };
    },
    {
      port: process.env.CI ? 3306 : 33079,
      password: process.env.CI ? "ci-disposable-root" : "",
    },
  );
  await page.reload();
  await page.getByRole("button", { name: "Table access", exact: true }).click();
  await page
    .getByRole("combobox", { name: "Connection", exact: true })
    .selectOption(String(setup.connection));
  await page
    .getByRole("combobox", { name: "Table", exact: true })
    .selectOption("z_field_records");
  await page
    .getByRole("combobox", { name: "User", exact: true })
    .selectOption(String(setup.user));
  await page
    .locator(".permission")
    .filter({ hasText: "Browse and search records" })
    .getByRole("checkbox")
    .check();
  await page
    .locator(".permission")
    .filter({ hasText: "Edit existing records" })
    .getByRole("checkbox")
    .check();
  const access = page.getByRole("region", {
    name: "Field access",
    exact: true,
  });
  for (const [label, value] of [
    ["No access", "none"],
    ["Read", "read"],
    ["Write", "write"],
  ]) {
    await access
      .getByRole("button", { name: `All fields: ${label}`, exact: true })
      .click();
    for (const field of ["id", "name", "note", "secret"])
      await expect(
        access.getByLabel(`${field} field access`, { exact: true }),
      ).toHaveValue(value);
  }
  await access
    .getByLabel("id field access", { exact: true })
    .selectOption("none");
  await access
    .getByLabel("secret field access", { exact: true })
    .selectOption("none");
  await access
    .getByLabel("note field access", { exact: true })
    .selectOption("read");
  await page
    .getByRole("button", { name: "Save permissions", exact: true })
    .click();
  await expect(
    page.getByText("Permissions saved.", { exact: true }),
  ).toBeVisible();
  await page.reload();
  await page.getByRole("button", { name: "Table access", exact: true }).click();
  await page
    .getByRole("combobox", { name: "Connection", exact: true })
    .selectOption(String(setup.connection));
  await page
    .getByRole("combobox", { name: "Table", exact: true })
    .selectOption("z_field_records");
  await page
    .getByRole("combobox", { name: "User", exact: true })
    .selectOption(String(setup.user));
  await expect(
    access.getByLabel("id field access", { exact: true }),
  ).toHaveValue("none");
  await expect(
    access.getByLabel("note field access", { exact: true }),
  ).toHaveValue("read");
  await page.setViewportSize({ width: 390, height: 844 });
  await access.scrollIntoViewIfNeeded();
  await page.screenshot({
    path: "../../artifacts/field-access-mobile.png",
    fullPage: true,
  });
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= window.innerWidth,
    ),
  ).toBe(true);
  const memberContext = await browser.newContext({
    viewport: { width: 390, height: 844 },
  });
  const member = await memberContext.newPage();
  await login(member, "field_browser", "field-browser-password-12345");
  await expect(
    member.getByRole("button", { name: "Object editor", exact: true }),
  ).toHaveCount(0);
  const records = await member.evaluate(
    async (id) =>
      await (
        await fetch(`/api/connections/${id}/tables/z_field_records/records`)
      ).json(),
    setup.connection,
  );
  expect(JSON.stringify(records)).not.toContain("BROWSER_SECRET");
  expect(JSON.stringify(records)).not.toContain("9007199254740993");
  await expect(member.getByRole("columnheader")).toHaveText([
    "name",
    "note",
    "Actions",
  ]);
  await member
    .getByRole("button", { name: /Edit record/ })
    .first()
    .click();
  const dialog = member.getByRole("dialog", {
    name: "Edit record",
    exact: true,
  });
  await expect(dialog.getByLabel("note", { exact: true })).toBeDisabled();
  await expect(dialog.getByLabel("secret", { exact: true })).toHaveCount(0);
  await expect(dialog.getByLabel("id", { exact: true })).toHaveCount(0);
  const editedName = `Allowed edit ${Date.now()}`;
  await dialog.getByLabel("name", { exact: true }).fill(editedName);
  const save = dialog.getByRole("button", { name: "Save record", exact: true });
  await expect(save).toBeEnabled();
  const [request] = await Promise.all([
    member.waitForRequest((r) => r.url().endsWith("/z_field_records/update")),
    save.click(),
  ]);
  const body = request.postDataJSON();
  expect(body.values).toEqual({ name: editedName });
  expect(body.key.$record).toBeTruthy();
  await expect(dialog).not.toBeVisible();
  await expect(
    member.getByRole("cell", { name: editedName, exact: true }),
  ).toBeVisible();
  await member
    .getByLabel("Search records", { exact: true })
    .fill("BROWSER_SECRET");
  await expect(
    member.getByText("No records found", { exact: true }),
  ).toBeVisible();
  await memberContext.close();
});

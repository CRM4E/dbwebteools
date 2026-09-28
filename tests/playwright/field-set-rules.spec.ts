import { test, expect } from "@playwright/test";

test("configure a field set rule and apply it when saving a record", async ({ page }) => {
  test.setTimeout(90_000);
  await page.goto("/", { waitUntil: "domcontentloaded" });
  await page.getByLabel("Username", { exact: true }).fill("admin");
  await page.getByLabel("Password", { exact: true }).fill("browser-test-only-password");
  await page.getByRole("button", { name: "Sign in →" }).click();
  await expect(page.getByRole("heading", { name: "Data browser", exact: true })).toBeVisible();
  const id = await page.evaluate(async ({ host, port, username, password }) => {
    const { token } = await (await fetch("/api/auth/csrf")).json();
    const response = await fetch("/api/admin/connections", {
      method: "POST",
      headers: { "Content-Type": "application/json", "X-CSRF-TOKEN": token },
      body: JSON.stringify({
        name: `Field set rule browser ${Date.now()}`,
        host,
        port,
        database: "dbweb_tests",
        username,
        password,
        verifyTls: false,
      }),
    });
    if (!response.ok) throw new Error(`Connection setup failed: ${response.status} ${await response.text()}`);
    return (await response.json()).id as number;
  }, {
    host: process.env.MARIADB_BROWSER_HOST || "127.0.0.1",
    port: Number(process.env.MARIADB_BROWSER_PORT || (process.env.CI ? 3306 : 33079)),
    username: process.env.MARIADB_BROWSER_USER || "root",
    password: process.env.MARIADB_BROWSER_PASSWORD || (process.env.CI ? "ci-disposable-root" : ""),
  });

  await page.reload();
  await page.getByRole("button", { name: "Object editor" }).click();
  await page.getByRole("combobox", { name: "Connection", exact: true }).selectOption(String(id));
  await page.getByRole("combobox", { name: "Table", exact: true }).selectOption("z_editor_records");
  await expect(page.getByRole("region", { name: "Field set rules" })).toBeVisible();
  await expect(page.getByRole("button", { name: "Add field" })).toBeVisible();
  await expect(page.getByRole("button", { name: "Add set rule" })).toBeVisible();
  const layoutOrder = await page.evaluate(() => {
    const rules = document.querySelector('section[aria-label="Field set rules"]');
    const rulesTable = rules?.querySelector("table");
    const buttons = Array.from(document.querySelectorAll("button"));
    const button = (name: string) => buttons.find((candidate) => candidate.textContent?.trim() === name);
    const precedes = (first?: Element | null, second?: Element | null) =>
      !!first && !!second && !!(first.compareDocumentPosition(second) & Node.DOCUMENT_POSITION_FOLLOWING);
    return {
      objectActionsAboveRules: ["Add field", "Add joined field", "Add formula field", "Save object"]
        .every((name) => precedes(button(name), rules)),
      addRuleBelowTable: precedes(rulesTable, button("Add set rule")),
    };
  });
  expect(layoutOrder).toEqual({ objectActionsAboveRules: true, addRuleBelowTable: true });
  await page.getByRole("button", { name: "Add set rule" }).click();
  await expect(page.getByLabel("Set rule field").locator('option[value="datatype"]')).toHaveCount(0);
  await expect(page.getByLabel("Set rule data type")).toHaveCount(0);
  await page.getByLabel("Set rule field").selectOption("status");
  await page.getByLabel("Set rule condition formula").fill("[title] =");
  await page.getByLabel("Set rule value formula").fill("'published'");
  const ruleDialog = page.getByRole("dialog", { name: "Add field set rule" });
  const rejectedSave = page.waitForResponse((response) =>
    response.request().method() === "PUT" && response.url().endsWith("/tables/z_editor_records/object"));
  await page.getByRole("button", { name: "Save set rule" }).click();
  expect((await rejectedSave).status()).toBe(400);
  await expect(ruleDialog).toBeVisible();
  await expect(ruleDialog.getByRole("alert")).toContainText("Invalid formula syntax");
  await page.getByRole("button", { name: "Validate formulas" }).click();
  await expect(ruleDialog.getByRole("alert")).toContainText("Invalid formula syntax");
  await page.getByLabel("Set rule condition formula").fill("[title] = 'Set rule browser'");
  await page.getByRole("button", { name: "Validate formulas" }).click();
  await expect(page.getByRole("status")).toContainText("Field set rule formulas are valid");
  const ruleSave = page.waitForResponse((response) =>
    response.request().method() === "PUT" && response.url().endsWith("/tables/z_editor_records/object"));
  await page.getByRole("button", { name: "Save set rule" }).click();
  expect((await ruleSave).ok()).toBe(true);
  await expect(page.getByText("Field set rule saved.", { exact: true })).toBeVisible();

  const configured = await page.evaluate(async (connectionId) =>
    await (await fetch(`/api/admin/connections/${connectionId}/tables/z_editor_records/object`)).json(), id);
  expect(configured.fieldSetRules).toEqual([
    { field: "status", condition: "[title] = 'Set rule browser'", value: "'published'" },
  ]);

  await page.getByRole("button", { name: "Data browser", exact: true }).click();
  await page.getByRole("combobox", { name: "Connection", exact: true }).selectOption(String(id));
  await page.getByRole("combobox", { name: "Table", exact: true }).selectOption("z_editor_records");
  await page.getByRole("button", { name: "Add record", exact: true }).click();
  const editor = page.getByRole("dialog", { name: "Add a record", exact: true });
  await editor.getByLabel("title", { exact: true }).fill("Set rule browser");
  await editor.getByLabel("status", { exact: true }).fill("draft");
  const saveResponse = page.waitForResponse((response) =>
    response.url().endsWith("/z_editor_records/create") && response.request().method() === "POST");
  await editor.getByRole("button", { name: "Save record", exact: true }).click();
  expect((await saveResponse).ok()).toBe(true);

  const saved = await page.evaluate(async (connectionId) =>
    await (await fetch(`/api/connections/${connectionId}/tables/z_editor_records/records?search=Set%20rule%20browser`)).json(), id);
  expect(saved.rows[0].values.status).toBe("published");

  await page.getByRole("button", { name: "Object editor" }).click();
  await page.getByRole("combobox", { name: "Connection", exact: true }).selectOption(String(id));
  await page.getByRole("combobox", { name: "Table", exact: true }).selectOption("z_editor_records");
  const deletion = page.waitForResponse((response) =>
    response.request().method() === "PUT" && response.url().endsWith("/tables/z_editor_records/object"));
  await page.getByRole("button", { name: "Delete set rule 1" }).click();
  expect((await deletion).ok()).toBe(true);
  await expect(page.getByText("No field set rules.", { exact: true })).toBeVisible();
  const afterDelete = await page.evaluate(async (connectionId) =>
    await (await fetch(`/api/admin/connections/${connectionId}/tables/z_editor_records/object`)).json(), id);
  expect(afterDelete.fieldSetRules).toEqual([]);
  await page.reload();
  await page.getByRole("button", { name: "Object editor" }).click();
  await page.getByRole("combobox", { name: "Connection", exact: true }).selectOption(String(id));
  await page.getByRole("combobox", { name: "Table", exact: true }).selectOption("z_editor_records");
  await expect(page.getByText("No field set rules.", { exact: true })).toBeVisible();
});

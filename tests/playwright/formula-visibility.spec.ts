import { test, expect } from "@playwright/test";

test("shows a formula using the managed datatype in layout and record editors", async ({ page }) => {
  await page.goto("/", { waitUntil: "domcontentloaded" });
  await page.getByLabel("Username", { exact: true }).fill("admin");
  await page.getByLabel("Password", { exact: true }).fill("browser-test-only-password");
  await page.getByRole("button", { name: "Sign in →" }).click();
  await expect(page.getByRole("heading", { name: "Data browser", exact: true })).toBeVisible();

  const id = await page.evaluate(async ({ port, password }) => {
    const { token } = await (await fetch("/api/auth/csrf")).json();
    const added = await fetch("/api/admin/connections", {
      method: "POST",
      headers: { "Content-Type": "application/json", "X-CSRF-TOKEN": token },
      body: JSON.stringify({
        name: "Formula visibility " + Date.now(),
        host: "127.0.0.1", port, database: "dbweb_tests", username: "root", password, verifyTls: false,
      }),
    });
    if (!added.ok) throw new Error("Connection setup failed: " + await added.text());
    const connectionId = (await added.json()).id as number;
    const path = "/api/admin/connections/" + connectionId + "/tables/z_editor_records/object";
    let definition = await (await fetch(path)).json();
    let saved = await fetch(path, {
      method: "PUT", headers: { "Content-Type": "application/json", "X-CSRF-TOKEN": token }, body: JSON.stringify(definition),
    });
    if (!saved.ok) throw new Error("Initial object save failed: " + await saved.text());
    definition = await (await fetch(path)).json();
    definition.fields.push({ name: "formula_datatype", label: "Record type", readOnly: true, widget: "formula", formula: "[datatype]" });
    for (const dataType of definition.dataTypes)
      dataType.fields.push({ name: "formula_datatype", required: false, mask: null, overrideDropdownOptions: false, enabledOptionKeys: [] });
    saved = await fetch(path, {
      method: "PUT", headers: { "Content-Type": "application/json", "X-CSRF-TOKEN": token }, body: JSON.stringify(definition),
    });
    if (!saved.ok) throw new Error("Formula save failed: " + await saved.text());
    return connectionId;
  }, { port: process.env.CI ? 3306 : 33079, password: process.env.CI ? "ci-disposable-root" : "" });

  await page.reload();
  await page.getByRole("button", { name: "Layout editor", exact: true }).click();
  await page.getByRole("combobox", { name: "Connection", exact: true }).selectOption(String(id));
  await page.getByRole("combobox", { name: "Table", exact: true }).selectOption("z_editor_records");
  await expect(page.getByLabel("formula_datatype label", { exact: true })).toHaveValue("Record type");

  await page.getByRole("button", { name: "Data browser", exact: true }).click();
  await page.getByRole("combobox", { name: "Connection", exact: true }).selectOption(String(id));
  await page.getByRole("combobox", { name: "Table", exact: true }).selectOption("z_editor_records");
  await page.getByRole("button", { name: "Add record", exact: true }).click();
  const editor = page.getByRole("dialog", { name: "Add a record", exact: true });
  await expect(editor.getByLabel("Record type", { exact: true })).toBeVisible();
  await expect(editor.getByLabel("Record type", { exact: true })).toHaveAttribute("readonly", "");
});

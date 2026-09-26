import { test, expect, type Locator } from "@playwright/test";

test("design tables through field dialogs, persist edits and delete metadata", async ({
  page,
}) => {
  test.setTimeout(150_000);
  await page.goto("/");
  await expect
    .poll(
      () =>
        page.evaluate(async () => {
          const { token } = await (await fetch("/api/auth/csrf")).json();
          return (
            await fetch("/api/auth/login", {
              method: "POST",
              headers: {
                "Content-Type": "application/json",
                "X-CSRF-TOKEN": token,
              },
              body: JSON.stringify({
                username: "admin",
                password: "browser-test-only-password",
              }),
            })
          ).status;
        }),
      { timeout: 70_000, intervals: [1000, 5000] },
    )
    .toBe(200);
  const id = await page.evaluate(
    async ({ port, password }) => {
      const { token } = await (await fetch("/api/auth/csrf")).json();
      const r = await fetch("/api/admin/connections", {
        method: "POST",
        headers: { "Content-Type": "application/json", "X-CSRF-TOKEN": token },
        body: JSON.stringify({
          name: "Schema browser",
          host: "127.0.0.1",
          port,
          database: "dbweb_tests",
          username: "root",
          password,
          verifyTls: false,
        }),
      });
      if (!r.ok) throw new Error("Connection fixture failed");
      return (await r.json()).id;
    },
    {
      port: process.env.CI ? 3306 : 33079,
      password: process.env.CI ? "ci-disposable-root" : "",
    },
  );
  await page.reload();
  await page.getByRole("button", { name: "Object editor", exact: true }).click();
  await page.locator(".object-editor > .selectors select").first().selectOption({ label: "Schema browser" });
  const parent = "z_schema_parent_" + Date.now();
  const child = "z_schema_child_" + Date.now();

  async function createTable(name: string) {
    await page.getByRole("button", { name: "New table", exact: true }).click();
    await page.getByLabel("New table name", { exact: true }).fill(name);
    await expect(page.getByLabel("Primary key name", { exact: true })).toHaveValue("id");
    await page.getByRole("button", { name: "Create table", exact: true }).click();
    await expect(page.getByRole("region", { name: "Object definition" })).toContainText("Primary key");
  }
  async function startField(name: string, control: string) {
    await page.getByRole("button", { name: "Add field", exact: true }).click();
    const dialog = page.getByRole("dialog", { name: "Add field", exact: true });
    await dialog.getByLabel("Field name", { exact: true }).fill(name);
    await dialog.getByLabel("Control / behavior", { exact: true }).selectOption(control);
    await expect(dialog.getByLabel("Column type", { exact: true })).toHaveCount(0);
    return dialog;
  }
  async function createField(dialog: Locator) {
    await dialog.getByRole("button", { name: "Create field", exact: true }).click();
    await expect(dialog).toHaveCount(0);
  }

  await createTable(parent);
  const fieldActions = page.locator(".object-field-actions");
  await expect(fieldActions.getByRole("button")).toHaveText([
    "Add field",
    "Add joined field",
    "Add formula field",
  ]);
  const addButtonBounds = await fieldActions
    .getByRole("button", { name: "Add field", exact: true })
    .boundingBox();
  const saveButtonBounds = await page
    .getByRole("button", { name: "Save object", exact: true })
    .boundingBox();
  const footerBounds = await page.locator(".object-editor-footer").boundingBox();
  expect(Math.abs(addButtonBounds!.y - saveButtonBounds!.y)).toBeLessThan(2);
  expect(addButtonBounds!.x).toBeLessThan(saveButtonBounds!.x);
  expect(addButtonBounds!.x - footerBounds!.x).toBeGreaterThanOrEqual(23);
  expect(addButtonBounds!.y - footerBounds!.y).toBeGreaterThanOrEqual(16);
  let dialog = await startField("name", "text");
  await dialog.getByLabel("Text length", { exact: true }).fill("80");
  await createField(dialog);
  await page.getByRole("button", { name: "Edit field name", exact: true }).click();
  dialog = page.getByRole("dialog", { name: "Edit database field", exact: true });
  await dialog.getByLabel("Text length", { exact: true }).fill("120");
  await dialog.getByRole("button", { name: "Save field", exact: true }).click();
  await expect(page.getByRole("region", { name: "Object definition" })).toContainText("varchar(120)");
  await page.getByRole("button", { name: "Edit field name", exact: true }).click();
  dialog = page.getByRole("dialog", { name: "Edit database field", exact: true });
  await expect(dialog.getByLabel("Text length", { exact: true })).toHaveValue("120");
  await dialog.getByRole("button", { name: "Cancel", exact: true }).click();

  dialog = await startField("email", "email");
  await expect(dialog.getByLabel("Text length", { exact: true })).toHaveCount(0);
  await expect(dialog.getByText("Allow NULL", { exact: false })).toHaveCount(0);
  await expect(dialog.getByLabel("email readOnly", { exact: true })).toBeVisible();
  await expect(dialog.getByLabel("email required", { exact: true })).toHaveCount(0);
  await expect(
    dialog.getByText(
      "Field requirements and input masks are configured separately for each data type.",
      { exact: true },
    ),
  ).toBeVisible();
  await createField(dialog);
  await expect(page.getByRole("region", { name: "Object definition" })).toContainText("varchar(255)");
  const dataTypes = page.getByLabel("Data types");
  await dataTypes.getByRole("button", { name: "Default", exact: true }).click();
  await dataTypes.getByLabel("Configure email for Default").click();
  const emailSettings = page.getByRole("dialog", {
    name: "Data type field settings",
  });
  await emailSettings.getByLabel("Required", { exact: true }).check();
  await emailSettings
    .getByRole("button", { name: "Save field settings" })
    .click();
  await expect(emailSettings).toHaveCount(0);

  // Deletion has an explicit cancel path and removes both schema and object metadata on confirm.
  dialog = await startField("temporary_note", "text");
  await createField(dialog);
  page.once("dialog", async (confirmation) => {
    expect(confirmation.message()).toContain("Delete field temporary_note?");
    await confirmation.dismiss();
  });
  await page.getByRole("button", { name: "Delete field temporary_note", exact: true }).click();
  await expect(page.getByRole("button", { name: "Edit field temporary_note", exact: true })).toBeVisible();
  page.once("dialog", async (confirmation) => confirmation.accept());
  await page.getByRole("button", { name: "Delete field temporary_note", exact: true }).click();
  await expect(page.getByRole("button", { name: "Edit field temporary_note", exact: true })).toHaveCount(0);
  const removed = await page.evaluate(
    async ({ id, table }) => {
      const [schema, settings] = await Promise.all([
        fetch(`/api/admin/connections/${id}/schema/tables/${table}`).then((r) => r.json()),
        fetch(`/api/connections/${id}/tables/${table}/settings`).then((r) => r.json()),
      ]);
      return {
        schema: schema.columns.some((column: { name: string }) => column.name === "temporary_note"),
        object: settings.fields.some((field: { name: string }) => field.name === "temporary_note"),
      };
    },
    { id, table: parent },
  );
  expect(removed).toEqual({ schema: false, object: false });

  await createTable(child);
  dialog = await startField("amount", "number");
  await dialog.getByLabel("Total digits", { exact: true }).fill("14");
  await dialog.getByLabel("Decimal places", { exact: true }).fill("4");
  await createField(dialog);
  await expect(page.getByRole("region", { name: "Object definition" })).toContainText("decimal(14,4)");
  dialog = await startField("parent_id", "lookup");
  await dialog.getByLabel("Related table", { exact: true }).selectOption(parent);
  await dialog.getByLabel("Related key", { exact: true }).selectOption("id");
  await dialog.getByLabel("Relation display column", { exact: true }).selectOption("name");
  await createField(dialog);
  await expect(page.getByText("Relation field created. Foreign key and object lookup configured.", { exact: true })).toBeVisible();

  await page.setViewportSize({ width: 390, height: 844 });
  await page.getByRole("button", { name: "Add field", exact: true }).click();
  dialog = page.getByRole("dialog", { name: "Add field", exact: true });
  const bounds = await dialog.boundingBox();
  expect(bounds!.x).toBeGreaterThanOrEqual(0);
  expect(bounds!.x + bounds!.width).toBeLessThanOrEqual(390);
  expect(await dialog.evaluate((element) => element.scrollWidth <= element.clientWidth)).toBe(true);
  await dialog.getByRole("button", { name: "Cancel", exact: true }).click();
  await page.screenshot({ path: "../../artifacts/schema-designer-mobile.png", fullPage: true });

  await page.setViewportSize({ width: 1280, height: 900 });
  await page.getByRole("button", { name: "Data browser", exact: true }).click();
  await page.getByRole("combobox", { name: "Connection", exact: true }).selectOption(String(id));
  await page.getByRole("combobox", { name: "Table", exact: true }).selectOption(parent);
  await page.getByRole("button", { name: "Add record", exact: true }).click();
  let editor = page.getByRole("dialog", { name: "Add a record", exact: true });
  await editor.getByLabel("name", { exact: true }).fill("Designer customer");
  const email = editor.getByLabel("email", { exact: true });
  await email.fill("not-an-email");
  await editor.getByRole("button", { name: "Save record", exact: true }).click();
  await expect(editor).toBeVisible();
  expect(await email.evaluate((input: HTMLInputElement) => input.validity.typeMismatch)).toBe(true);
  await email.fill("designer@example.com");
  await editor.getByRole("button", { name: "Save record", exact: true }).click();
  await expect(editor).toHaveCount(0);
  await page.getByRole("combobox", { name: "Table", exact: true }).selectOption(child);
  await page.getByRole("button", { name: "Add record", exact: true }).click();
  editor = page.getByRole("dialog", { name: "Add a record", exact: true });
  await editor.getByLabel("amount", { exact: true }).fill("12.3456");
  await editor.getByRole("button", { name: "Choose parent_id", exact: true }).click();
  await page.getByRole("button", { name: /Select.*Designer customer/ }).click();
  await editor.getByRole("button", { name: "Save record", exact: true }).click();
  await expect(editor).toHaveCount(0);
  await expect(page.getByRole("cell", { name: "Designer customer", exact: true })).toBeVisible();
  await expect(page.getByRole("cell", { name: "12.3456", exact: true })).toBeVisible();

  await page.getByRole("button", { name: "Object editor", exact: true }).click();
  await page.locator(".object-editor > .selectors select").first().selectOption({ label: "Schema browser" });
  await page.locator(".object-editor > .selectors select").nth(1).selectOption(parent);
  await page.getByRole("button", { name: "Edit field name", exact: true }).click();
  dialog = page.getByRole("dialog", { name: "Edit database field", exact: true });
  await dialog.getByLabel("Text length", { exact: true }).fill("2");
  await dialog.getByRole("button", { name: "Save field", exact: true }).click();
  await expect(page.getByRole("alert")).toContainText("Existing text exceeds");
  await page.screenshot({ path: "../../artifacts/schema-designer-desktop.png", fullPage: true });
});

import { expectDebouncedSearch } from "./search-debounce";
import { test, expect } from "@playwright/test";
test("desktop and mobile workspace with administration", async ({ page }) => {
  const errors: string[] = [];
  page.on("pageerror", (e) => errors.push(e.message));
  await page.goto("/");
  await page.getByLabel("Username", { exact: true }).fill("admin");
  await page
    .getByLabel("Password", { exact: true })
    .fill("browser-test-only-password");
  await page.getByRole("button", { name: "Sign in →" }).click();
  await expect(
    page.getByRole("heading", { name: "Data browser", exact: true }),
  ).toBeVisible();
  await page.getByRole("button", { name: "Users & roles" }).click();
  await expect(
    page.getByRole("heading", { name: "Users & roles", exact: true }),
  ).toBeVisible();
  await page.getByRole("button", { name: "New user" }).click();
  await expect(page.getByRole("dialog")).toBeVisible();
  await page.keyboard.press("Escape");
  await expect(page.getByRole("dialog")).toHaveCount(0);
  await page.getByRole("button", { name: "Connections", exact: true }).click();
  await expect(
    page.getByRole("button", { name: "Add connection" }),
  ).toBeVisible();
  await page.screenshot({
    path: "../../artifacts/desktop.png",
    fullPage: true,
  });
  await page.setViewportSize({ width: 390, height: 844 });
  await expect(page.getByRole("button", { name: "Sign out" })).toBeVisible();
  await page.getByRole("button", { name: "Data browser" }).click();
  await expect(
    page.getByRole("heading", { name: "Data browser", exact: true }),
  ).toBeVisible();
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= window.innerWidth,
    ),
  ).toBe(true);
  await page.screenshot({ path: "../../artifacts/mobile.png", fullPage: true });
  expect(errors).toEqual([]);
});

test("browse, create, edit and delete MariaDB records", async ({ page }) => {
  await page.clock.install();
  await page.goto("/");
  await page.getByLabel("Username", { exact: true }).fill("admin");
  await page
    .getByLabel("Password", { exact: true })
    .fill("browser-test-only-password");
  await page.getByRole("button", { name: "Sign in →" }).click();
  await expect(
    page.getByRole("heading", { name: "Data browser", exact: true }),
  ).toBeVisible();
  await page.evaluate(
    async ({ port, password }) => {
      const { token } = await (await fetch("/api/auth/csrf")).json();
      const r = await fetch("/api/admin/connections", {
        method: "POST",
        headers: { "Content-Type": "application/json", "X-CSRF-TOKEN": token },
        body: JSON.stringify({
          name: "Browser test database",
          host: "127.0.0.1",
          port,
          database: "dbweb_tests",
          username: "root",
          password,
          verifyTls: false,
        }),
      });
      if (!r.ok) throw new Error("Test connection setup failed");
    },
    {
      port: process.env.CI ? 3306 : 33079,
      password: process.env.CI ? "ci-disposable-root" : "",
    },
  );
  await page.reload();
  await expect(
    page.getByRole("heading", { name: "browser_records", exact: true }),
  ).toBeVisible();
  await page.getByRole("button", { name: "Add record" }).click();
  await page
    .getByRole("dialog")
    .getByLabel("name", { exact: true })
    .fill("Browser CRUD test");
  await page
    .getByRole("dialog")
    .getByLabel("email", { exact: true })
    .fill("clear-me@example.test");
  await page.getByRole("button", { name: "Save record" }).click();
  await expect(page.getByRole("dialog")).toHaveCount(0);
  const row = page.getByRole("row").filter({ hasText: "Browser CRUD test" });
  await expect(row).toBeVisible();
  await expectDebouncedSearch(
    page,
    page.getByLabel("Search records", { exact: true }),
    /\/tables\/browser_records\/records$/,
    "Browser",
  );

  await row.getByRole("button", { name: /Edit record/ }).click();
  const edit = page.getByRole("dialog", { name: "Edit record", exact: true });
  await edit.getByLabel("name", { exact: true }).fill("Browser CRUD updated");
  await expect(edit.getByText("Set NULL", { exact: true })).toHaveCount(0);
  await expect(edit.getByText("varchar", { exact: true })).toHaveCount(0);
  await edit.getByLabel("email", { exact: true }).fill("");
  const update = page.waitForRequest((request) =>
    request.url().endsWith("/browser_records/update"),
  );
  await page.getByRole("button", { name: "Save record" }).click();
  expect((await update).postDataJSON().values).toEqual({
    name: "Browser CRUD updated",
    email: null,
  });
  await expect(page.getByRole("dialog")).toHaveCount(0);
  const updated = page
    .getByRole("row")
    .filter({ hasText: "Browser CRUD updated" });
  await expect(updated).toBeVisible();
  await page.screenshot({
    path: "../../artifacts/records-desktop.png",
    fullPage: true,
  });
  await updated.getByRole("button", { name: /Delete record/ }).click();
  await page
    .getByRole("dialog")
    .getByRole("button", { name: "Delete record", exact: true })
    .click();
  await expect(page.getByRole("dialog")).toHaveCount(0);
  await expect(updated).toHaveCount(0);
});

test("configure a relation and select, search, reopen and clear its key", async ({
  page,
}) => {
  test.setTimeout(90_000);
  const errors: string[] = [];
  page.on("pageerror", (e) => errors.push(e.message));
  await page.goto("/");
  await page.getByLabel("Username", { exact: true }).fill("admin");
  await page
    .getByLabel("Password", { exact: true })
    .fill("browser-test-only-password");
  await page.getByRole("button", { name: "Sign in →" }).click();
  await expect(
    page.getByRole("heading", { name: "Data browser", exact: true }),
  ).toBeVisible();
  const id = await page.evaluate(
    async ({ port, password }) => {
      const { token } = await (await fetch("/api/auth/csrf")).json();
      const r = await fetch("/api/admin/connections", {
        method: "POST",
        headers: { "Content-Type": "application/json", "X-CSRF-TOKEN": token },
        body: JSON.stringify({
          name: "Lookup test database",
          host: "127.0.0.1",
          port,
          database: "dbweb_tests",
          username: "root",
          password,
          verifyTls: false,
        }),
      });
      if (!r.ok) throw new Error("Lookup connection setup failed");
      return (await r.json()).id as number;
    },
    {
      port: process.env.CI ? 3306 : 33079,
      password: process.env.CI ? "ci-disposable-root" : "",
    },
  );
  await page.reload();
  await page.getByRole("button", { name: "Object editor" }).click();
  await page
    .getByRole("combobox", { name: "Connection", exact: true })
    .selectOption(String(id));
  await page
    .getByRole("combobox", { name: "Table", exact: true })
    .selectOption("lookup_orders");
  await page.getByRole("button", { name: "Edit field person_id" }).click();
  let fieldDialog = page.getByRole("dialog", { name: "Edit database field" });
  await fieldDialog.getByLabel("Control / behavior").selectOption("lookup");
  await fieldDialog.getByLabel("person_id related table").selectOption("lookup_people");
  await fieldDialog.getByLabel("person_id key column").selectOption("id");
  await fieldDialog.getByLabel("person_id display column").selectOption("name");
  await fieldDialog.getByLabel("person_id search email").check();
  await fieldDialog.getByRole("button", { name: "Save field" }).click();
  await page.getByRole("button", { name: "Save object" }).click();
  await expect(
    page.getByText("Object saved. Application behavior updated.", {
      exact: true,
    }),
  ).toBeVisible();
  await page.getByRole("button", { name: "Layout editor", exact: true }).click();
  await page.getByRole("combobox", { name: "Connection", exact: true }).selectOption(String(id));
  await page.getByRole("combobox", { name: "Table", exact: true }).selectOption("lookup_orders");
  await page.getByLabel("person_id label", { exact: true }).fill("Customer");
  await page.getByRole("button", { name: "Save layout", exact: true }).click();
  await expect(page.getByText("Layout saved.", { exact: true })).toBeVisible();
  await page.getByRole("button", { name: "Data browser" }).click();
  await page
    .getByRole("combobox", { name: "Connection", exact: true })
    .selectOption(String(id));
  await page
    .getByRole("combobox", { name: "Table", exact: true })
    .selectOption("lookup_orders");
  await page.getByRole("button", { name: "Add record" }).click();
  const editor = page.getByRole("dialog", {
    name: "Add a record",
    exact: true,
  });
  await editor
    .getByLabel("title", { exact: true })
    .fill("Lookup browser order");
  await editor.getByRole("button", { name: "Choose Customer" }).click();
  const lookup = page.getByRole("dialog", {
    name: "Select Customer",
    exact: true,
  });
  await expect(lookup).toBeVisible();
  await lookup.getByLabel("Search related records").fill("no-such-person");
  await expect(
    lookup.getByText("No matching records. Try another search."),
  ).toBeVisible();
  await lookup.getByLabel("Search related records").fill("9007199254740993");
  await expect(
    lookup.getByRole("button", {
      name: "Select Alice Friendly (9007199254740993)",
    }),
  ).toBeVisible();
  await lookup.getByLabel("Search related records").fill("alice.lookup@");
  await lookup
    .getByRole("button", { name: "Select Alice Friendly (9007199254740993)" })
    .click();
  await expect(lookup).toHaveCount(0);
  await expect(editor.getByLabel("Customer", { exact: true })).toHaveValue(
    "Alice Friendly",
  );
  await editor.getByRole("button", { name: "Save record" }).click();
  await expect(editor).toHaveCount(0);
  const row = page.getByRole("row").filter({ hasText: "Lookup browser order" });
  await expect(row).toContainText("Alice Friendly");
  const stored = await page.evaluate(
    async (id) =>
      (
        await (
          await fetch(`/api/connections/${id}/tables/lookup_orders/records`)
        ).json()
      ).rows.find(
        (r: { values: { title: string } }) =>
          r.values.title === "Lookup browser order",
      ).values.person_id,
    id,
  );
  expect(stored).toBe("9007199254740993");
  await row.getByRole("button", { name: /Edit record/ }).click();
  const edit = page.getByRole("dialog", { name: "Edit record", exact: true });
  await expect(edit.getByLabel("Customer", { exact: true })).toHaveValue(
    "Alice Friendly",
  );
  await edit.getByRole("button", { name: "Choose Customer" }).click();
  await lookup.getByLabel("Search related records").fill("Bob");
  await expect(
    lookup.getByRole("button", { name: "Select Bob Friendly (42)" }),
  ).toBeVisible();
  await page.setViewportSize({ width: 390, height: 844 });
  const selectionBounds = await lookup
    .getByRole("button", { name: "Select Bob Friendly (42)" })
    .boundingBox();
  const dialogBounds = await lookup.boundingBox();
  expect(selectionBounds!.x + selectionBounds!.width).toBeLessThanOrEqual(
    dialogBounds!.x + dialogBounds!.width,
  );
  await page.screenshot({
    path: "../../artifacts/lookup-mobile.png",
    fullPage: true,
  });
  expect(
    await lookup.evaluate(
      (d) => d.getBoundingClientRect().width <= window.innerWidth,
    ),
  ).toBe(true);
  await page.keyboard.press("Escape");
  await expect(lookup).toHaveCount(0);
  await expect(edit).toBeVisible();
  await expect(edit.getByLabel("Customer", { exact: true })).toHaveValue(
    "Alice Friendly",
  );
  await edit.getByRole("button", { name: "Choose Customer" }).click();
  await lookup
    .getByRole("button", { name: "Select Bob Friendly (42)" })
    .click();
  await expect(edit.getByLabel("Customer", { exact: true })).toHaveValue(
    "Bob Friendly",
  );
  await edit.getByRole("button", { name: "Save record" }).click();
  await expect(row).toContainText("Bob Friendly");
  await row.getByRole("button", { name: /Edit record/ }).click();
  await edit.getByRole("button", { name: "Clear Customer" }).click();
  await edit.getByRole("button", { name: "Save record" }).click();
  await expect(row.getByRole("cell").nth(2)).toHaveText("");
  await row.getByRole("button", { name: /Delete record/ }).click();
  await page
    .getByRole("dialog")
    .getByRole("button", { name: "Delete record", exact: true })
    .click();
  await expect(row).toHaveCount(0);
  expect(errors).toEqual([]);
});

test("date/time and keyed dropdown layouts preserve values and enforce unique options", async ({
  page,
}) => {
  test.setTimeout(90_000);
  await page.goto("/");
  await page.getByLabel("Username", { exact: true }).fill("admin");
  await page
    .getByLabel("Password", { exact: true })
    .fill("browser-test-only-password");
  await page.getByRole("button", { name: "Sign in →" }).click();
  await expect(
    page.getByRole("heading", { name: "Data browser", exact: true }),
  ).toBeVisible();
  const id = await page.evaluate(
    async ({ port, password }) => {
      const { token } = await (await fetch("/api/auth/csrf")).json();
      const headers = {
        "Content-Type": "application/json",
        "X-CSRF-TOKEN": token,
      };
      const r = await fetch("/api/admin/connections", {
        method: "POST",
        headers,
        body: JSON.stringify({
          name: "Date dropdown test",
          host: "127.0.0.1",
          port,
          database: "dbweb_tests",
          username: "root",
          password,
          verifyTls: false,
        }),
      });
      if (!r.ok) throw new Error("Connection setup failed");
      const id = (await r.json()).id as number;
      const created = await fetch(
        `/api/connections/${id}/tables/z_editor_records/create`,
        {
          method: "POST",
          headers,
          body: JSON.stringify({
            values: {
              title: "Precision fixture",
              status: "draft",
              day: "2026-09-15",
              happened: "2026-09-15T13:14:15.123456",
              stamped: "2026-09-15T16:17:18.654321",
            },
          }),
        },
      );
      if (!created.ok) throw new Error("Fixture setup failed");
      return id;
    },
    {
      port: process.env.CI ? 3306 : 33079,
      password: process.env.CI ? "ci-disposable-root" : "",
    },
  );
  await page.reload();
  await page.getByRole("button", { name: "Object editor" }).click();
  await page
    .getByRole("combobox", { name: "Connection", exact: true })
    .selectOption(String(id));
  await page
    .getByRole("combobox", { name: "Table", exact: true })
    .selectOption("z_editor_records");
  for (const [field, control] of [["stamped", "date"], ["happened", "datetime"]] as const) {
    await page.getByRole("button", { name: `Edit field ${field}` }).click();
    const dialog = page.getByRole("dialog", { name: "Edit database field" });
    await dialog.getByLabel("Control / behavior").selectOption(control);
    await dialog.getByRole("button", { name: "Save field" }).click();
  }
  await page.getByRole("button", { name: "Edit field status" }).click();
  const statusDialog = page.getByRole("dialog", { name: "Edit database field" });
  await statusDialog.getByLabel("Control / behavior").selectOption("dropdown");
  const config = statusDialog.getByRole("group", { name: "status dropdown values" });
  await config.getByRole("button", { name: "Add option" }).click();
  await statusDialog.getByLabel("status option 1 key").fill("draft");
  await statusDialog.getByLabel("status option 1 display").fill("Draft document");
  await config.getByRole("button", { name: "Add option" }).click();
  await statusDialog.getByLabel("status option 2 key").fill("DRAFT");
  await statusDialog.getByLabel("status option 2 display").fill("Ready to publish");
  await expect(config.getByRole("alert")).toContainText("Keys must be unique");
  await expect(
    statusDialog.getByRole("button", { name: "Save field" }),
  ).toBeDisabled();
  await statusDialog.getByLabel("status option 2 key").fill("ready");
  await statusDialog.getByLabel("status option 2 display").fill("draft document");
  await expect(config.getByRole("alert")).toContainText(
    "Display labels must be unique",
  );
  await statusDialog.getByLabel("status option 2 display").fill("Ready to publish");
  await statusDialog.getByRole("button", { name: "Save field" }).click();
  await expect(statusDialog).toHaveCount(0);
  const dataTypes = page.getByLabel("Data types");
  await dataTypes.getByLabel("Configure status for Default").click();
  const statusTypeDialog = page.getByRole("dialog", {
    name: "Data type field settings",
  });
  await statusTypeDialog.getByLabel("Override dropdown options").check();
  await statusTypeDialog.getByLabel("Enable Ready to publish").check();
  await statusTypeDialog
    .getByRole("button", { name: "Save field settings" })
    .click();
  await expect(statusTypeDialog).toHaveCount(0);
  await page.getByRole("button", { name: "Data browser" }).click();
  await page
    .getByRole("combobox", { name: "Connection", exact: true })
    .selectOption(String(id));
  await page
    .getByRole("combobox", { name: "Table", exact: true })
    .selectOption("z_editor_records");
  const row = page.getByRole("row").filter({ hasText: "Precision fixture" });
  await expect(row).toContainText("Draft document");
  const search = page.getByLabel("Search records", { exact: true });
  await search.fill("DOCUMENT");
  await expect(row).toBeVisible();
  await search.fill("publish");
  await expect(row).toHaveCount(0);
  await search.fill("document");
  await expect(row).toBeVisible();
  await search.fill("");
  await row.getByRole("button", { name: /Edit record/ }).click();
  const edit = page.getByRole("dialog", { name: "Edit record", exact: true });
  await expect(edit.getByLabel("stamped", { exact: true })).toHaveAttribute(
    "type",
    "date",
  );
  await expect(edit.getByLabel("stamped", { exact: true })).toHaveValue(
    "2026-09-15",
  );
  await expect(edit.getByLabel("happened", { exact: true })).toHaveAttribute(
    "type",
    "datetime-local",
  );
  await expect(edit.getByLabel("happened", { exact: true })).toHaveValue(
    "2026-09-15T13:14:15.123",
  );
  await expect(edit.getByLabel("day", { exact: true })).toHaveValue(
    "2026-09-15",
  );
  await expect(edit.getByLabel("status", { exact: true })).toHaveValue("draft");
  await expect(
    edit.getByLabel("status", { exact: true }).getByRole("option", {
      name: "Draft document (not available)",
    }),
  ).toHaveAttribute("disabled", "");
  await expect(
    edit.getByLabel("status", { exact: true }).getByRole("option", {
      name: "Ready to publish",
    }),
  ).toBeEnabled();
  await edit
    .getByLabel("title", { exact: true })
    .fill("Precision fixture edited");
  await edit.getByRole("button", { name: "Save record" }).click();
  await expect(edit).toHaveCount(0);
  const readValues = () =>
    page.evaluate(
      async (id) =>
        (
          await (
            await fetch(
              `/api/connections/${id}/tables/z_editor_records/records`,
            )
          ).json()
        ).rows.find(
          (r: { values: { title: string } }) =>
            r.values.title === "Precision fixture edited",
        ).values,
      id,
    );
  const untouched = await readValues();
  expect(untouched.happened).toBe("2026-09-15T13:14:15.123456");
  expect(untouched.stamped).toBe("2026-09-15T16:17:18.654321");
  await row.getByRole("button", { name: /Edit record/ }).click();
  await search.fill("");
  await edit.getByLabel("stamped", { exact: true }).fill("2026-10-20");
  await edit
    .getByLabel("happened", { exact: true })
    .fill("2026-10-21T17:18:19.125");
  await edit.getByLabel("status", { exact: true }).selectOption("ready");
  await page.setViewportSize({ width: 390, height: 844 });
  await page.screenshot({
    path: "../../artifacts/date-dropdown-mobile.png",
    fullPage: true,
  });
  await edit.getByRole("button", { name: "Save record" }).click();
  await expect(edit).toHaveCount(0);
  await expect
    .poll(() => readValues().then((values) => values.status), { timeout: 15_000 })
    .toBe("ready");
  await expect(row).toContainText("Ready to publish", { timeout: 15_000 });
  const updated = await readValues();
  expect(updated.status).toBe("ready");
  expect(updated.stamped).toBe("2026-10-20T00:00:00.000000");
  expect(updated.happened).toBe("2026-10-21T17:18:19.125000");
  await row.getByRole("button", { name: /Edit record/ }).click();
  await expect(edit.getByLabel("status", { exact: true })).toHaveValue("ready");
  await edit.getByLabel("day", { exact: true }).fill("");
  await edit.getByRole("button", { name: "Save record" }).click();
  await expect(edit).toHaveCount(0);
  expect((await readValues()).day).toBeNull();
  await row.getByRole("button", { name: /Delete record/ }).click();
  await page
    .getByRole("dialog")
    .getByRole("button", { name: "Delete record", exact: true })
    .click();
  await expect(row).toHaveCount(0);
});

test("list columns and read-only joins refresh when a lookup changes", async ({
  page,
}) => {
  test.setTimeout(120_000);
  await page.goto("/");
  await page.getByLabel("Username", { exact: true }).fill("admin");
  await page
    .getByLabel("Password", { exact: true })
    .fill("browser-test-only-password");
  await page.getByRole("button", { name: "Sign in →" }).click();
  await expect(
    page.getByRole("heading", { name: "Data browser", exact: true }),
  ).toBeVisible();
  const id = await page.evaluate(
    async ({ port, password }) => {
      const { token } = await (await fetch("/api/auth/csrf")).json();
      const r = await fetch("/api/admin/connections", {
        method: "POST",
        headers: { "Content-Type": "application/json", "X-CSRF-TOKEN": token },
        body: JSON.stringify({
          name: "Joined layout test",
          host: "127.0.0.1",
          port,
          database: "dbweb_tests",
          username: "root",
          password,
          verifyTls: false,
        }),
      });
      if (!r.ok) throw new Error("Connection setup failed");
      return (await r.json()).id as number;
    },
    {
      port: process.env.CI ? 3306 : 33079,
      password: process.env.CI ? "ci-disposable-root" : "",
    },
  );
  await page.reload();
  await page.getByRole("button", { name: "Object editor" }).click();
  await page
    .getByRole("combobox", { name: "Connection", exact: true })
    .selectOption(String(id));
  await page
    .getByRole("combobox", { name: "Table", exact: true })
    .selectOption("lookup_orders");
  const objectDefinition = page.getByRole("region", {
    name: "Object definition",
  });
  await expect(
    page.getByRole("region", { name: "Table structure" }),
  ).toHaveCount(0);
  await expect(
    objectDefinition.getByRole("table").filter({
      has: page.getByRole("columnheader", { name: "Database definition" }),
    }),
  ).toHaveCount(1);
  await expect(objectDefinition).toContainText("Database definition");
  await expect(
    page.getByLabel("person_id section", { exact: true }),
  ).toHaveCount(0);
  await expect(page.getByLabel("id showInList", { exact: true })).toHaveCount(
    0,
  );
  await page.getByRole("button", { name: "Edit field person_id" }).click();
  let fieldDialog = page.getByRole("dialog", { name: "Edit database field" });
  await fieldDialog.getByLabel("Control / behavior").selectOption("lookup");
  await fieldDialog.getByLabel("person_id related table").selectOption("lookup_people");
  await fieldDialog.getByLabel("person_id key column").selectOption("id");
  await fieldDialog.getByLabel("person_id display column").selectOption("name");
  await fieldDialog.getByRole("button", { name: "Save field" }).click();
  for (const [i, value, label] of [
    [1, "email", "Customer email"],
    [2, "name", "Customer name"],
  ] as const) {
    await page.getByRole("button", { name: "Add joined field" }).click();
    fieldDialog = page.getByRole("dialog", { name: "Edit database field" });
    await fieldDialog.getByLabel(`joined_${i} source column`).selectOption("person_id");
    await fieldDialog.getByLabel(`joined_${i} joined table`).selectOption("lookup_people");
    await fieldDialog.getByLabel(`joined_${i} join key`, { exact: true }).selectOption("id");
    await fieldDialog.getByLabel(`joined_${i} joined value`).selectOption(value);
    await expect(fieldDialog.getByLabel(`joined_${i} readOnly`, { exact: true })).toBeDisabled();
    await fieldDialog.getByRole("button", { name: "Save field" }).click();
  }
  await page.getByRole("button", { name: "Save object" }).click();
  await expect(
    page.getByText("Object saved. Application behavior updated.", {
      exact: true,
    }),
  ).toBeVisible();
  await page
    .getByRole("button", { name: "Layout editor", exact: true })
    .click();
  await page
    .getByRole("combobox", { name: "Connection", exact: true })
    .selectOption(String(id));
  await page
    .getByRole("combobox", { name: "Table", exact: true })
    .selectOption("lookup_orders");
  await expect(
    page.getByLabel("person_id control", { exact: true }),
  ).toHaveCount(0);
  await page.getByLabel("person_id label", { exact: true }).fill("Customer");
  await page.getByLabel("joined_1 label", { exact: true }).fill("Customer email");
  await page.getByLabel("joined_2 label", { exact: true }).fill("Customer name");
  await page
    .getByLabel("person_id section", { exact: true })
    .fill("Customer details");
  await page
    .getByLabel("joined_1 section", { exact: true })
    .fill("Customer details");
  await expect(page.locator(".layout-section-row")).toHaveCount(0);
  await page.getByLabel("id showInList", { exact: true }).uncheck();
  await page.getByLabel("person_id showInList", { exact: true }).uncheck();
  await page.getByLabel("title listOrder", { exact: true }).fill("1");
  await page.getByLabel("joined_1 listOrder", { exact: true }).fill("0");
  await page.getByLabel("joined_2 showInList", { exact: true }).uncheck();
  await page.getByRole("button", { name: "Save layout", exact: true }).click();
  await expect(page.getByText("Layout saved.", { exact: true })).toBeVisible();
  await page.getByRole("button", { name: "Data browser" }).click();
  await page
    .getByRole("combobox", { name: "Connection", exact: true })
    .selectOption(String(id));
  await page
    .getByRole("combobox", { name: "Table", exact: true })
    .selectOption("lookup_orders");
  await expect(page.getByRole("columnheader")).toHaveText([
    "Customer email",
    "title",
    "Actions",
  ]);
  await page.getByRole("button", { name: "Add record", exact: true }).click();
  const create = page.getByRole("dialog", {
    name: "Add a record",
    exact: true,
  });
  const customerSection = create.getByRole("group", {
    name: "Customer details",
    exact: true,
  });
  await expect(customerSection).toContainText("Customer");
  await expect(customerSection).toContainText("Customer email");
  await create
    .getByLabel("title", { exact: true })
    .fill("Joined browser order");
  await expect(
    create.getByLabel("Customer email", { exact: true }),
  ).toHaveValue("");
  await create
    .getByRole("button", { name: "Choose Customer", exact: true })
    .click();
  const lookup = page.getByRole("dialog", {
    name: "Select Customer",
    exact: true,
  });
  await lookup
    .getByRole("button", {
      name: "Select Alice Friendly (9007199254740993)",
      exact: true,
    })
    .click();
  await expect(
    create.getByLabel("Customer email", { exact: true }),
  ).toHaveValue("alice.lookup@example.test");
  await expect(create.getByLabel("Customer name", { exact: true })).toHaveValue(
    "Alice Friendly",
  );
  await expect(
    create.getByLabel("Customer email", { exact: true }),
  ).toHaveAttribute("readonly", "");
  const mutation = page.waitForRequest((r) =>
    r.url().endsWith("/lookup_orders/create"),
  );
  await create
    .getByRole("button", { name: "Save record", exact: true })
    .click();
  expect((await mutation).postDataJSON().values).toEqual({
    title: "Joined browser order",
    person_id: "9007199254740993",
  });
  await expect(create).toHaveCount(0);
  const row = page.getByRole("row").filter({ hasText: "Joined browser order" });
  await expect(row).toContainText("alice.lookup@example.test");
  await row.getByRole("button", { name: /Edit record/ }).click();
  const edit = page.getByRole("dialog", { name: "Edit record", exact: true });
  await edit
    .getByRole("button", { name: "Choose Customer", exact: true })
    .click();
  await lookup
    .getByRole("button", { name: "Select Bob Friendly (42)", exact: true })
    .click();
  await expect(edit.getByLabel("Customer email", { exact: true })).toHaveValue(
    "bob.lookup@example.test",
  );
  await expect(edit.getByLabel("Customer name", { exact: true })).toHaveValue(
    "Bob Friendly",
  );
  await page.setViewportSize({ width: 390, height: 844 });
  await page.screenshot({
    path: "../../artifacts/joined-fields-mobile.png",
    fullPage: true,
  });
  await edit.getByRole("button", { name: "Save record", exact: true }).click();
  await expect(edit).toHaveCount(0);
  await expect(row).toContainText("bob.lookup@example.test");
  await row.getByRole("button", { name: /Edit record/ }).click();
  await edit
    .getByRole("button", { name: "Clear Customer", exact: true })
    .click();
  await expect(edit.getByLabel("Customer email", { exact: true })).toHaveValue(
    "",
  );
  await edit.getByRole("button", { name: "Save record", exact: true }).click();
  await expect(edit).toHaveCount(0);
  await row.getByRole("button", { name: /Delete record/ }).click();
  await page
    .getByRole("dialog")
    .getByRole("button", { name: "Delete record", exact: true })
    .click();
  await expect(row).toHaveCount(0);
  // Reopen configuration to prove virtual fields and independent list settings persisted.
  await page.getByRole("button", { name: "Object editor" }).click();
  await page
    .getByRole("combobox", { name: "Connection", exact: true })
    .selectOption(String(id));
  await page
    .getByRole("combobox", { name: "Table", exact: true })
    .selectOption("lookup_orders");
  await page.getByRole("button", { name: "Edit field joined_1" }).click();
  fieldDialog = page.getByRole("dialog", { name: "Edit database field" });
  await expect(fieldDialog.getByLabel("joined_1 joined value")).toHaveValue("email");
  await fieldDialog.getByRole("button", { name: "Cancel" }).click();
  await page.getByRole("button", { name: "Layout editor", exact: true }).click();
  await page
    .getByRole("combobox", { name: "Connection", exact: true })
    .selectOption(String(id));
  await page
    .getByRole("combobox", { name: "Table", exact: true })
    .selectOption("lookup_orders");
  await expect(page.getByLabel("joined_1 label", { exact: true })).toHaveValue("Customer email");
  await expect(
    page.getByLabel("joined_2 showInList", { exact: true }),
  ).not.toBeChecked();
  await expect(
    page.getByLabel("person_id section", { exact: true }),
  ).toHaveValue("Customer details");
  await page.getByLabel("title showInList", { exact: true }).uncheck();
  await page.getByLabel("joined_1 showInList", { exact: true }).uncheck();
  await page.getByRole("button", { name: "Save layout", exact: true }).click();
  await expect(page.getByText("Layout saved.", { exact: true })).toBeVisible();
  await page.getByRole("button", { name: "Data browser" }).click();
  await expect(
    page.getByText(
      "No visible list fields. Check the layout and field permissions.",
    ),
  ).toBeVisible();
});

test("data type rules block invalid creates and updates", async ({
  page,
}) => {
  test.setTimeout(60_000);
  await page.goto("/");
  await page.getByLabel("Username", { exact: true }).fill("admin");
  await page
    .getByLabel("Password", { exact: true })
    .fill("browser-test-only-password");
  await page.getByRole("button", { name: "Sign in →" }).click();
  await expect(
    page.getByRole("heading", { name: "Data browser", exact: true }),
  ).toBeVisible();
  const id = await page.evaluate(
    async ({ port, password }) => {
      const { token } = await (await fetch("/api/auth/csrf")).json();
      const r = await fetch("/api/admin/connections", {
        method: "POST",
        headers: { "Content-Type": "application/json", "X-CSRF-TOKEN": token },
        body: JSON.stringify({
          name: "Required test",
          host: "127.0.0.1",
          port,
          database: "dbweb_tests",
          username: "root",
          password,
          verifyTls: false,
        }),
      });
      if (!r.ok) throw new Error("Connection setup failed");
      return (await r.json()).id as number;
    },
    {
      port: process.env.CI ? 3306 : 33079,
      password: process.env.CI ? "ci-disposable-root" : "",
    },
  );
  await page.reload();
  await page.getByRole("button", { name: "Object editor" }).click();
  await page
    .getByRole("combobox", { name: "Connection", exact: true })
    .selectOption(String(id));
  await page
    .getByRole("combobox", { name: "Table", exact: true })
    .selectOption("z_required_records");
  await page.getByRole("button", { name: "Edit field id" }).click();
  let fieldDialog = page.getByRole("dialog", { name: "Edit database field" });
  await expect(fieldDialog.getByLabel("Required", { exact: true })).toHaveCount(0);
  await expect(fieldDialog.getByLabel("Input mask pattern")).toHaveCount(0);
  await fieldDialog.getByRole("button", { name: "Cancel" }).click();
  const dataTypes = page.getByLabel("Data types");
  await dataTypes.getByRole("button", { name: "Add data type" }).click();
  let typeDialog = page.getByRole("dialog", { name: "Add data type" });
  await typeDialog.getByLabel("Data type key").fill("secondary");
  await typeDialog.getByLabel("Data type label").fill("Secondary");
  await typeDialog.getByRole("button", { name: "Save data type" }).click();
  await expect(typeDialog).toHaveCount(0);
  const datatypeDefault = page.getByLabel("datatype default value");
  await expect(datatypeDefault).toBeVisible();
  await expect(datatypeDefault.locator("option")).toHaveText(["Default", "Secondary"]);
  await datatypeDefault.selectOption("secondary");
  await expect(datatypeDefault).toHaveValue("secondary");
  await datatypeDefault.selectOption("default");
  await expect(datatypeDefault).toHaveValue("default");

  // Saving the first data type provisions the managed datatype column. The
  // editor must use that new schema version without requiring a page reload.
  await page.getByRole("button", { name: "Edit field title" }).click();
  fieldDialog = page.getByRole("dialog", { name: "Edit database field" });
  await fieldDialog.getByLabel("Text length").fill("120");
  const resizeResponse = page.waitForResponse(
    (response) =>
      response.request().method() === "POST" &&
      response.url().endsWith("/schema/tables/z_required_records/modify-column"),
  );
  await fieldDialog.getByRole("button", { name: "Save field" }).click();
  expect((await resizeResponse).ok()).toBe(true);
  await expect(fieldDialog).toHaveCount(0);
  await page.getByRole("button", { name: "Edit field note" }).click();
  fieldDialog = page.getByRole("dialog", { name: "Edit database field" });
  await fieldDialog.getByLabel("Control / behavior").selectOption("text");
  await fieldDialog.getByRole("button", { name: "Save field" }).click();
  await expect(fieldDialog).toHaveCount(0);

  await dataTypes.getByRole("button", { name: "Default", exact: true }).click();
  await dataTypes.getByLabel("Configure title for Default").click();
  let typeFieldDialog = page.getByRole("dialog", {
    name: "Data type field settings",
  });
  await typeFieldDialog.getByLabel("Required", { exact: true }).check();
  await typeFieldDialog
    .getByRole("button", { name: "Save field settings" })
    .click();
  await expect(typeFieldDialog).toHaveCount(0);
  await dataTypes.getByLabel("Configure note for Default").click();
  typeFieldDialog = page.getByRole("dialog", {
    name: "Data type field settings",
  });
  await typeFieldDialog.getByLabel("Input mask pattern").fill("AA-##?");
  await typeFieldDialog
    .getByRole("button", { name: "Save field settings" })
    .click();
  await expect(typeFieldDialog).toHaveCount(0);
  await expect(page.getByText("Data types updated.", { exact: true })).toBeVisible();
  await page.reload();
  await page.getByRole("button", { name: "Object editor" }).click();
  await page
    .getByRole("combobox", { name: "Connection", exact: true })
    .selectOption(String(id));
  await page
    .getByRole("combobox", { name: "Table", exact: true })
    .selectOption("z_required_records");
  const reloadedTypes = page.getByLabel("Data types");
  await reloadedTypes.getByLabel("Configure title for Default").click();
  typeFieldDialog = page.getByRole("dialog", { name: "Data type field settings" });
  await expect(typeFieldDialog.getByLabel("Required", { exact: true })).toBeChecked();
  await typeFieldDialog.getByRole("button", { name: "Cancel" }).click();
  await reloadedTypes.getByLabel("Configure note for Default").click();
  typeFieldDialog = page.getByRole("dialog", { name: "Data type field settings" });
  await expect(typeFieldDialog.getByLabel("Input mask pattern")).toHaveValue("AA-##?");
  await typeFieldDialog.getByRole("button", { name: "Cancel" }).click();
  await page.getByRole("button", { name: "Data browser", exact: true }).click();
  await page
    .getByRole("combobox", { name: "Connection", exact: true })
    .selectOption(String(id));
  await page
    .getByRole("combobox", { name: "Table", exact: true })
    .selectOption("z_required_records");
  await page.getByRole("button", { name: "Add record", exact: true }).click();
  let dialog = page.getByRole("dialog");
  await expect(dialog.getByText(/Required format: Format: AA-##\?/)).toBeVisible();
  const noteInput = dialog.getByLabel("note", { exact: true });
  await expect(noteInput).toHaveAttribute(
    "pattern",
    "[A-Za-z][A-Za-z]\\x2d[0-9](?:[0-9])?",
  );
  await dialog.getByLabel("title", { exact: true }).fill("   ");
  await noteInput.fill("AB-1");
  await dialog.getByRole("button", { name: "Save record" }).click();
  await expect(dialog.getByRole("alert")).toContainText("title is required.");
  const title = "Required browser " + Date.now();
  await dialog.getByLabel("title", { exact: true }).fill(title);
  await noteInput.fill("AB_1");
  expect(await noteInput.evaluate((input: HTMLInputElement) => input.validity.patternMismatch)).toBe(true);
  await noteInput.fill("AB-1");
  expect(await noteInput.evaluate((input: HTMLInputElement) => input.validity.patternMismatch)).toBe(false);
  await dialog.getByRole("button", { name: "Save record" }).click();
  await expect(dialog).toHaveCount(0);
  const row = page
    .getByRole("row")
    .filter({ has: page.getByRole("cell", { name: title, exact: true }) });
  await row.getByRole("button", { name: /Edit record/ }).click();
  dialog = page.getByRole("dialog");
  await dialog.getByLabel("title", { exact: true }).fill(" ");
  await dialog.getByRole("button", { name: "Save record" }).click();
  await expect(dialog.getByRole("alert")).toHaveText("title is required.");
  await dialog.getByLabel("title", { exact: true }).fill(title);
  await dialog.getByLabel("note", { exact: true }).fill("CD-34");
  await dialog.getByRole("button", { name: "Save record" }).click();
  await expect(dialog).toHaveCount(0);
  await row.getByRole("button", { name: /Delete record/ }).click();
  await page
    .getByRole("dialog")
    .getByRole("button", { name: "Delete record", exact: true })
    .click();
  await expect(row).toHaveCount(0);
});

test("layout labels, default sorting and filters persist and constrain search", async ({
  page,
}) => {
  await page.goto("/");
  await page.getByLabel("Username", { exact: true }).fill("admin");
  await page
    .getByLabel("Password", { exact: true })
    .fill("browser-test-only-password");
  await page.getByRole("button", { name: "Sign in →" }).click();
  await expect(
    page.getByRole("heading", { name: "Data browser", exact: true }),
  ).toBeVisible();
  const id = await page.evaluate(
    async ({ port, password }) => {
      const { token } = await (await fetch("/api/auth/csrf")).json();
      const response = await fetch("/api/admin/connections", {
        method: "POST",
        headers: { "Content-Type": "application/json", "X-CSRF-TOKEN": token },
        body: JSON.stringify({
          name: "List views test",
          host: "127.0.0.1",
          port,
          database: "dbweb_tests",
          username: "root",
          password,
          verifyTls: false,
        }),
      });
      if (!response.ok) throw new Error("Connection setup failed");
      return (await response.json()).id as number;
    },
    {
      port: process.env.CI ? 3306 : 33079,
      password: process.env.CI ? "ci-disposable-root" : "",
    },
  );
  await page.reload();
  await page.getByRole("button", { name: "Object editor" }).click();
  await page
    .getByRole("combobox", { name: "Connection", exact: true })
    .selectOption(String(id));
  await page
    .getByRole("combobox", { name: "Table", exact: true })
    .selectOption("z_list_view_records");
  await page.getByLabel("List title", { exact: true }).fill("Open documents");
  await page
    .getByLabel("Default sort column", { exact: true })
    .selectOption("amount");
  await page.getByLabel("Sort direction", { exact: true }).selectOption("desc");
  await page.getByRole("button", { name: "Add filter", exact: true }).click();
  await page
    .getByLabel("Filter 1 field", { exact: true })
    .selectOption("status");
  await page.getByLabel("Filter 1 value", { exact: true }).fill("open");
  await page.getByRole("button", { name: "Add filter", exact: true }).click();
  await page
    .getByLabel("Filter 2 field", { exact: true })
    .selectOption("amount");
  await page
    .getByLabel("Filter 2 condition", { exact: true })
    .selectOption("gte");
  await page.getByLabel("Filter 2 value", { exact: true }).fill("10");
  await page.getByRole("button", { name: "Save object", exact: true }).click();
  await expect(
    page.getByText("Object saved. Application behavior updated.", {
      exact: true,
    }),
  ).toBeVisible();
  await page.getByRole("button", { name: "Layout editor", exact: true }).click();
  await page.getByRole("combobox", { name: "Connection", exact: true }).selectOption(String(id));
  await page.getByRole("combobox", { name: "Table", exact: true }).selectOption("z_list_view_records");
  await page.getByLabel("title label", { exact: true }).fill("Document");
  await page.getByLabel("amount label", { exact: true }).fill("Total");
  await page.getByRole("button", { name: "Save layout", exact: true }).click();
  await expect(page.getByText("Layout saved.", { exact: true })).toBeVisible();
  await page.reload();
  await page.getByRole("button", { name: "Layout editor", exact: true }).click();
  await page.getByRole("combobox", { name: "Connection", exact: true }).selectOption(String(id));
  await page.getByRole("combobox", { name: "Table", exact: true }).selectOption("z_list_view_records");
  await expect(page.getByLabel("title label", { exact: true })).toHaveValue("Document");
  await expect(page.getByLabel("amount label", { exact: true })).toHaveValue("Total");
  await page.getByRole("button", { name: "Object editor", exact: true }).click();
  await page.getByRole("combobox", { name: "Connection", exact: true }).selectOption(String(id));
  await page.getByRole("combobox", { name: "Table", exact: true }).selectOption("z_list_view_records");
  await expect(page.getByLabel("List title", { exact: true })).toHaveValue(
    "Open documents",
  );
  await expect(
    page.getByLabel("Default sort column", { exact: true }),
  ).toHaveValue("amount");
  await expect(page.getByLabel("Filter 2 value", { exact: true })).toHaveValue(
    "10",
  );
  await page.setViewportSize({ width: 390, height: 844 });
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= window.innerWidth,
    ),
  ).toBe(true);
  await page.screenshot({
    path: "../../artifacts/layout-filters-mobile.png",
    fullPage: true,
  });
  await page.setViewportSize({ width: 1280, height: 900 });
  await page.getByRole("button", { name: "Data browser", exact: true }).click();
  await page
    .getByRole("combobox", { name: "Connection", exact: true })
    .selectOption(String(id));
  await page
    .getByRole("combobox", { name: "Table", exact: true })
    .selectOption("z_list_view_records");
  await expect(
    page.getByRole("heading", { name: "Open documents", exact: true }),
  ).toBeVisible();
  await expect(
    page.getByRole("columnheader", { name: "Document", exact: true }),
  ).toBeVisible();
  await expect(
    page.getByRole("columnheader", { name: "Total", exact: true }),
  ).toHaveAttribute("aria-sort", "descending");
  const names = () => page.locator("tbody tr td:nth-child(2)");
  await expect(names()).toHaveText(["Beta", "Alpha"]);
  await expect(page.getByRole("note")).toContainText(
    "status Equals open AND Total At least 10",
  );
  await page
    .getByRole("columnheader", { name: "Total", exact: true })
    .getByRole("button")
    .click();
  await expect(names()).toHaveText(["Alpha", "Beta"]);
  await page
    .getByRole("button", { name: "Use default sorting", exact: true })
    .click();
  await expect(names()).toHaveText(["Beta", "Alpha"]);
  await page.getByLabel("Search records", { exact: true }).fill("closed");
  await expect(page.getByText("0 records", { exact: true })).toBeVisible();
  await page.getByLabel("Search records", { exact: true }).fill("Alpha");
  await expect(names()).toHaveText(["Alpha"]);
  await page
    .getByRole("button", { name: "Edit record 1", exact: true })
    .click();
  await expect(
    page.getByRole("dialog").getByLabel("Document", { exact: true }),
  ).toHaveValue("Alpha");
  await page.keyboard.press("Escape");
});

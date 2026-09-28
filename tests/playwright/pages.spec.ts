import { expectDebouncedSearch } from "./search-debounce";
import { test, expect } from "@playwright/test";

test("define pages and drill through related tabs with record keys and browser history", async ({
  page,
}) => {
  test.setTimeout(120_000);
  const errors: string[] = [];
  page.on("pageerror", (e) => errors.push(e.message));
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
          name: "Pages browser",
          host: "127.0.0.1",
          port,
          database: "dbweb_tests",
          username: "root",
          password,
          verifyTls: false,
        }),
      });
      if (!r.ok) throw new Error("Connection failed");
      const { id } = await r.json();
      const layout = await fetch(
        `/api/admin/connections/${id}/tables/z_page_customers/layout`,
        {
          method: "PUT",
          headers,
          body: JSON.stringify([
            {
              name: "id",
              label: "ID",
              section: "",
              order: 0,
              hidden: true,
              readOnly: true,
              widget: "auto",
              showInList: false,
            },
            {
              name: "name",
              label: "Customer name",
              section: "Contact",
              order: 1,
              hidden: false,
              readOnly: false,
              widget: "text",
            },
          ]),
        },
      );
      if (!layout.ok) throw new Error("Layout failed");
      for (const [table, fields] of [
        [
          "z_page_orders",
          [
            {
              name: "customer_id",
              label: "Customer",
              section: "",
              order: 0,
              hidden: false,
              readOnly: false,
              widget: "lookup",
              lookup: {
                table: "z_page_customers",
                keyColumn: "id",
                displayColumn: "name",
                searchColumns: [],
                copyMappings: [
                  { sourceColumn: "email", destinationColumn: "copied_email" },
                ],
              },
            },
            {
              name: "copied_email",
              label: "Customer email",
              section: "",
              order: 2,
              hidden: false,
              readOnly: false,
              widget: "text",
            },
            {
              name: "line_total",
              label: "Line total",
              section: "",
              order: 5,
              hidden: false,
              readOnly: true,
              widget: "formula",
              formula: "Coalesce([amount], 0) * 2",
            },
            {
              name: "summary",
              label: "Summary",
              section: "",
              order: 4,
              hidden: false,
              readOnly: true,
              widget: "formula",
              formula: "Concat([title], ' / ', [copied_email])",
            },
          ],
        ],
        [
          "z_page_lines",
          [
            {
              name: "order_id",
              label: "Order",
              section: "",
              order: 0,
              hidden: false,
              readOnly: false,
              widget: "lookup",
              lookup: {
                table: "z_page_orders",
                keyColumn: "id",
                displayColumn: "title",
                searchColumns: [],
              },
            },
          ],
        ],
      ] as const) {
        const saved = await fetch(
          `/api/admin/connections/${id}/tables/${table}/layout`,
          { method: "PUT", headers, body: JSON.stringify(fields) },
        );
        if (!saved.ok) throw new Error("Related lookup layout failed");
        if (table === "z_page_orders") {
          const objectResponse = await fetch(
            `/api/admin/connections/${id}/tables/${table}/object`,
          );
          if (!objectResponse.ok) throw new Error("Related object load failed");
          const definition = await objectResponse.json();
          const active = definition.dataTypes.find(
            (dataType: { key: string }) =>
              dataType.key === definition.defaultDataTypeKey,
          );
          active.fields = active.fields.map(
            (field: { name: string; required: boolean }) => ({
              ...field,
              required: ["customer_id", "copied_email"].includes(field.name),
            }),
          );
          const configured = await fetch(
            `/api/admin/connections/${id}/tables/${table}/object`,
            { method: "PUT", headers, body: JSON.stringify(definition) },
          );
          if (!configured.ok)
            throw new Error("Related data type configuration failed");
        }
      }
      return id as number;
    },
    {
      port: process.env.CI ? 3306 : 33079,
      password: process.env.CI ? "ci-disposable-root" : "",
    },
  );
  await page.reload();
  await page
    .getByRole("button", { name: "Object editor", exact: true })
    .click();
  await page
    .getByRole("combobox", { name: "Connection", exact: true })
    .selectOption(String(id));
  await page
    .getByRole("combobox", { name: "Table", exact: true })
    .selectOption("z_page_customers");
  for (const [field, operation] of [
    ["order_total", "sum"],
    ["order_count", "count"],
  ]) {
    await page.getByRole("button", { name: `Edit field ${field}` }).click();
    const fieldDialog = page.getByRole("dialog", { name: "Edit database field" });
    await fieldDialog.getByLabel("Control / behavior", { exact: true }).selectOption("sumup");
    await fieldDialog.getByLabel(`${field} sum-up operation`, { exact: true }).selectOption(operation);
    await fieldDialog
      .getByLabel(`${field} sum-up relation`, { exact: true })
      .selectOption(JSON.stringify(["z_page_orders", "customer_id"]));
    if (operation === "sum")
      await fieldDialog.getByLabel(`${field} sum-up source`, { exact: true }).selectOption("line_total");
    await fieldDialog.getByRole("button", { name: "Save field" }).click();
  }
  await page.getByRole("button", { name: "Save object", exact: true }).click();
  await expect(
    page.getByText("Object saved. Application behavior updated.", {
      exact: true,
    }),
  ).toBeVisible();
  const totals = async () =>
    page.evaluate(
      async ({ id }) => {
        const result = await (
          await fetch(`/api/connections/${id}/tables/z_page_customers/records`)
        ).json();
        return result.rows.find(
          (r: { values: { id: string } }) => r.values.id === "9007199254740993",
        ).values;
      },
      { id },
    );
  expect((await totals()).order_total).toBe("50.0000");
  expect((await totals()).order_count).toBe(1);
  await page
    .getByRole("combobox", { name: "Table", exact: true })
    .selectOption("z_page_orders");
  await page.getByRole("button", { name: "Edit field summary" }).click();
  const formulaDialog = page.getByRole("dialog", { name: "Edit database field" });
  const formula = formulaDialog.getByLabel("summary expression", { exact: true });
  await formula.fill("[missing] + 1");
  await page
    .getByRole("button", { name: "Validate summary formula", exact: true })
    .click();
  const validation = formula
    .locator("..")
    .locator("..")
    .locator(".formula-validation");
  await expect(validation.getByRole("alert")).toBeVisible();
  await formula.fill("Concat([title], ' / ', [copied_email])");
  await page
    .getByRole("button", { name: "Validate summary formula", exact: true })
    .click();
  await expect(validation.getByRole("status")).toContainText(
    "Formula is valid",
  );
  expect((await validation.boundingBox())!.y).toBeLessThan(
    (await formula.boundingBox())!.y,
  );
  await formulaDialog.getByRole("button", { name: "Save field" }).click();
  await page.getByRole("button", { name: "Page editor", exact: true }).click();
  async function start(name: string, table: string, link: string) {
    await page.getByRole("button", { name: "New page", exact: true }).click();
    await page.getByLabel("Page name", { exact: true }).fill(name);
    await page
      .getByLabel("Page connection", { exact: true })
      .selectOption(String(id));
    await page.getByLabel("Main table", { exact: true }).selectOption(table);
    await page
      .getByLabel("List drill-down column", { exact: true })
      .selectOption(link);
  }
  async function tab(
    n: number,
    label: string,
    table: string,
    parent: string,
    related: string,
    columns: string[],
    target?: number,
    link?: string,
  ) {
    await page
      .getByRole("button", { name: "Add related tab", exact: true })
      .click();
    await page.getByLabel(`Tab ${n} label`, { exact: true }).fill(label);
    await page
      .getByLabel(`Tab ${n} table`, { exact: true })
      .selectOption(table);
    await page
      .getByLabel(`Tab ${n} lookup relation`, { exact: true })
      .selectOption(related);
    for (const c of columns)
      await page.getByLabel(`Tab ${n} show ${c}`, { exact: true }).check();
    if (target) {
      await page
        .getByLabel(`Tab ${n} destination page`, { exact: true })
        .selectOption(String(target));
      await page
        .getByLabel(`Tab ${n} drill-down column`, { exact: true })
        .selectOption(link!);
    }
  }
  async function save() {
    const response = page.waitForResponse(
      (r) =>
        r.url().endsWith("/api/admin/pages") && r.request().method() === "POST",
    );
    await page.getByRole("button", { name: "Save page", exact: true }).click();
    const r = await response;
    expect(r.status()).toBe(200);
    await expect(page.getByText("Page saved.", { exact: true })).toBeVisible();
    return (await r.json()).id as number;
  }
  await start("Line page", "z_page_lines", "title");
  const linePage = await save();
  await start("Order page", "z_page_orders", "title");
  await tab(
    1,
    "Lines",
    "z_page_lines",
    "id",
    "order_id",
    ["title"],
    linePage,
    "title",
  );
  const orderPage = await save();
  await start("Customer page", "z_page_customers", "name");
  await tab(
    1,
    "Orders",
    "z_page_orders",
    "id",
    "customer_id",
    ["title", "amount"],
    orderPage,
    "title",
  );
  await tab(2, "Amounts", "z_page_orders", "id", "customer_id", ["amount"]);
  const customerPage = await save();
  await page.reload();
  await page.getByRole("button", { name: "Page editor", exact: true }).click();
  await page
    .getByLabel("Page", { exact: true })
    .selectOption(String(customerPage));
  await expect(
    page.getByLabel("Tab 1 destination page", { exact: true }),
  ).toHaveValue(String(orderPage));
  await expect(
    page.getByLabel("Tab 2 show amount", { exact: true }),
  ).toBeChecked();
  await page.getByRole("button", { name: "Data browser", exact: true }).click();
  await page
    .getByRole("combobox", { name: "Connection", exact: true })
    .selectOption(String(id));
  await page
    .getByRole("combobox", { name: "Table", exact: true })
    .selectOption("z_page_customers");
  const browserAddSize = await page
    .getByRole("button", { name: "Add record", exact: true })
    .boundingBox();
  await page.getByRole("link", { name: "Page Alice", exact: true }).click();
  await expect(
    page.getByRole("heading", { name: "Customer page", exact: true }),
  ).toBeVisible();
  expect(decodeURIComponent(page.url())).toContain("9007199254740993");
  await expect(page.getByRole("region", { name: "Main record" })).toContainText(
    "Customer name",
  );
  await expect(page.getByRole("region", { name: "Main record" })).toContainText(
    "alice.page@example.test",
  );
  let panel = page.getByRole("tabpanel");
  await expect(
    panel.getByRole("link", { name: "Alice order", exact: true }),
  ).toBeVisible();
  await expect(panel).not.toContainText("Bob private order");
  await expect(panel.getByRole("columnheader")).toHaveText([
    "title",
    "amount",
    "Data type",
    "Actions",
  ]);
  const relatedAddSize = await panel
    .getByRole("button", { name: "Add record", exact: true })
    .boundingBox();
  expect(relatedAddSize!.width).toBeCloseTo(browserAddSize!.width, 0);
  expect(relatedAddSize!.height).toBeCloseTo(browserAddSize!.height, 0);
  expect(
    (await panel.getByLabel("Search Orders", { exact: true }).boundingBox())!.x,
  ).toBeLessThan(relatedAddSize!.x);
  await expectDebouncedSearch(
    page,
    panel.getByLabel("Search Orders", { exact: true }),
    /\/tabs\/[^/]+\/records$/,
    "Alice",
  );
  // Search the configured lookup's friendly value, not the stored numeric parent key.
  await panel.getByLabel("Search Orders", { exact: true }).fill("Page Alice");
  await expect(
    panel.getByRole("link", { name: "Alice order", exact: true }),
  ).toBeVisible();
  await expect(panel.locator(".table-scroll")).toHaveAttribute(
    "aria-busy",
    "false",
  );
  await expect(panel.locator(".pagination")).toContainText("1 records");
  await panel.getByLabel("Search Orders", { exact: true }).fill("Page Bob");
  await expect(
    panel.getByText("No related records found.", { exact: true }),
  ).toBeVisible();
  await panel.getByLabel("Search Orders", { exact: true }).fill("");
  await expect(
    panel.getByRole("link", { name: "Alice order", exact: true }),
  ).toBeVisible();
  // Preserve the list, search focus and scroll position during delayed/in-flight searches.
  await page.setViewportSize({ width: 390, height: 844 });
  const search = panel.getByLabel("Search Orders", { exact: true });
  await search.scrollIntoViewIfNeeded();
  await search.focus();
  const searchY = (await search.boundingBox())!.y;
  await page.route("**/api/pages/*/tabs/*/records?*", async (route) => {
    await new Promise((resolve) =>
      setTimeout(
        resolve,
        new URL(route.request().url()).searchParams.get("search") === "A"
          ? 700
          : 300,
      ),
    );
    await route.continue();
  });
  await search.pressSequentially("Alice", { delay: 180 });
  await expect(search).toBeFocused();
  await expect(panel.locator(".table-scroll")).toHaveAttribute(
    "aria-busy",
    "false",
  );
  await expect(
    panel.getByRole("link", { name: "Alice order", exact: true }),
  ).toBeVisible();
  expect(Math.abs((await search.boundingBox())!.y - searchY)).toBeLessThan(3);
  const filtered = page.waitForResponse(
    (r) =>
      r.url().includes("/tabs/") &&
      new URL(r.url()).searchParams.get("search") === "Bob",
  );
  await search.fill("Bob");
  expect((await (await filtered).json()).data.total).toBe(0);
  await expect(
    panel.getByText("No related records found.", { exact: true }),
  ).toBeVisible();
  await expect(search).toBeFocused();
  expect(Math.abs((await search.boundingBox())!.y - searchY)).toBeLessThan(3);
  await search.fill("");
  await expect(
    panel.getByRole("link", { name: "Alice order", exact: true }),
  ).toBeVisible();
  await page.unroute("**/api/pages/*/tabs/*/records?*");
  await page.setViewportSize({ width: 1280, height: 720 });
  await page.getByRole("tab", { name: "Amounts", exact: true }).click();
  await expect(page.getByRole("tabpanel").getByRole("columnheader")).toHaveText(
    ["amount", "Data type", "Actions"],
  );
  await page
    .getByRole("tab", { name: "Amounts", exact: true })
    .press("ArrowLeft");
  await expect(
    page.getByRole("tab", { name: "Orders", exact: true }),
  ).toHaveAttribute("aria-selected", "true");
  await page
    .getByRole("tabpanel")
    .getByRole("link", { name: "Alice order", exact: true })
    .click();
  await expect(
    page.getByRole("heading", { name: "Order page", exact: true }),
  ).toBeVisible();
  await page
    .getByRole("tabpanel")
    .getByRole("link", { name: "Alice second line", exact: true })
    .click();
  await expect(
    page.getByRole("heading", { name: "Line page", exact: true }),
  ).toBeVisible();
  expect(decodeURIComponent(page.url())).toContain('"seq":2');
  const crumbs = page.getByRole("navigation", { name: "Page navigation" });
  await expect(crumbs.getByRole("link")).toHaveText([
    "Data browser",
    "Customer page: Page Alice",
    "Order page: Alice order",
  ]);
  await expect(crumbs.locator('[aria-current="page"]')).toHaveText(
    "Line page: Alice second line",
  );
  const bookmark = page.url();
  await page.reload();
  await expect(page.getByRole("region", { name: "Main record" })).toContainText(
    "Alice second line",
  );
  await expect(crumbs.getByRole("link")).toHaveCount(3);
  const shared = await page.context().newPage();
  await shared.goto(bookmark);
  await expect(
    shared.getByRole("region", { name: "Main record" }),
  ).toContainText("Alice second line");
  const sharedCrumbs = shared.getByRole("navigation", {
    name: "Page navigation",
  });
  await sharedCrumbs
    .getByRole("link", { name: "Customer page: Page Alice", exact: true })
    .click();
  await expect(
    shared.getByRole("heading", { name: "Customer page", exact: true }),
  ).toBeVisible();
  await expect(sharedCrumbs.getByRole("link")).toHaveText(["Data browser"]);
  await shared.close();
  await page.goBack();
  await expect(
    page.getByRole("heading", { name: "Order page", exact: true }),
  ).toBeVisible();
  await page.goBack();
  await expect(
    page.getByRole("heading", { name: "Customer page", exact: true }),
  ).toBeVisible();
  const mainRecord = page.getByRole("region", { name: "Main record", exact: true });
  await expect(mainRecord).toContainText("Customer name");
  await expect(mainRecord.getByText("Contact", { exact: true })).toHaveCount(0);
  await page.setViewportSize({ width: 390, height: 844 });
  await expect(
    page.getByRole("tab", { name: "Orders", exact: true }),
  ).toBeVisible();
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= window.innerWidth,
    ),
  ).toBe(true);
  await expect(
    page
      .getByRole("tabpanel")
      .getByRole("link", { name: "Alice order", exact: true }),
  ).toBeVisible();
  await page.screenshot({
    path: "../../artifacts/record-page-mobile.png",
    fullPage: true,
  });
  await page.getByRole("button", { name: "Edit record", exact: true }).click();
  const editor = page.getByRole("dialog", { name: "Edit record", exact: true });
  await expect(
    editor.getByRole("group", { name: "Contact", exact: true }),
  ).toContainText("Customer name");
  await expect(editor.getByLabel("Customer name", { exact: true })).toHaveValue(
    "Page Alice",
  );
  await editor.getByRole("button", { name: "Cancel", exact: true }).click();
  // Creation from the tab inherits the parent and layout copy mappings.
  await page.getByRole("button", { name: "Add record", exact: true }).click();
  let create = page.getByRole("dialog", { name: "Add a record", exact: true });
  await expect(
    create.getByRole("button", { name: "Choose Customer", exact: true }),
  ).toBeDisabled();
  await expect(
    create.getByLabel("Customer email", { exact: true }),
  ).toHaveValue("alice.page@example.test");
  await expect(
    create.getByLabel("Customer email", { exact: true }),
  ).toBeDisabled();
  await create
    .getByLabel("title", { exact: true })
    .fill("Created from parent tab");
  await create.getByLabel("amount", { exact: true }).fill("17.25");
  await expect(create.getByLabel("Summary", { exact: true })).toHaveValue(
    "Created from parent tab / alice.page@example.test",
  );
  await create
    .getByRole("button", { name: "Save record", exact: true })
    .click();
  await expect(create).not.toBeVisible();
  await expect(
    page.getByRole("link", { name: "Created from parent tab", exact: true }),
  ).toBeVisible();
  const stored = await page.evaluate(
    async ({ id }) =>
      (
        await (
          await fetch(
            `/api/connections/${id}/tables/z_page_orders/records?search=Created%20from%20parent%20tab`,
          )
        ).json()
      ).rows[0].values,
    { id },
  );
  expect(stored.customer_id).toBe("9007199254740993");
  expect(stored.copied_email).toBe("alice.page@example.test");
  expect((await totals()).order_total).toBe("84.5000");
  expect((await totals()).order_count).toBe(2);
  await expect(page.getByRole("region", { name: "Main record" })).toContainText(
    "84.5000",
  );
  await page.getByRole("button", { name: "Add record", exact: true }).click();
  create = page.getByRole("dialog", { name: "Add a record", exact: true });
  await create.getByRole("button", { name: "Cancel", exact: true }).click();
  await expect(create).not.toBeVisible();
  const relatedRow = page
    .getByRole("tabpanel")
    .getByRole("row")
    .filter({ hasText: "Created from parent tab" });
  await relatedRow.getByRole("button", { name: /Edit record/ }).click();
  let editRelated = page.getByRole("dialog", {
    name: "Edit record",
    exact: true,
  });
  await expect(
    editRelated.getByRole("button", { name: "Choose Customer", exact: true }),
  ).toBeDisabled();
  await editRelated.getByLabel("amount", { exact: true }).fill("20");
  await editRelated
    .getByRole("button", { name: "Save record", exact: true })
    .click();
  await expect(editRelated).not.toBeVisible();
  expect((await totals()).order_total).toBe("90.0000");
  expect((await totals()).order_count).toBe(2);
  await relatedRow.getByRole("button", { name: /Edit record/ }).click();
  editRelated = page.getByRole("dialog", { name: "Edit record", exact: true });
  await expect(editRelated.getByText("Set NULL", { exact: true })).toHaveCount(0);
  await editRelated.getByLabel("amount", { exact: true }).fill("");
  await editRelated
    .getByRole("button", { name: "Save record", exact: true })
    .click();
  await expect(editRelated).not.toBeVisible();
  await expect(relatedRow.getByRole("cell").nth(1)).toHaveText("");
  expect((await totals()).order_total).toBe("50.0000");
  // The UI independently honors the server's delete capability.
  await page.route("**/api/pages/*/tabs/*/records?*", async (route) => {
    const response = await route.fetch();
    const body = await response.json();
    await route.fulfill({ response, json: { ...body, canDelete: false } });
  });
  await page.getByRole("button", { name: "Refresh page", exact: true }).click();
  await expect(relatedRow).toBeVisible();
  await expect(
    relatedRow.getByRole("button", { name: /Delete record/ }),
  ).toHaveCount(0);
  await expect(
    relatedRow.getByRole("button", { name: /Edit record/ }),
  ).toBeVisible();
  await page.unroute("**/api/pages/*/tabs/*/records?*");
  await page.getByRole("button", { name: "Refresh page", exact: true }).click();
  await expect(
    relatedRow.getByRole("button", { name: /Delete record/ }),
  ).toBeVisible();
  page.once("dialog", (dialog) => dialog.dismiss());
  await relatedRow.getByRole("button", { name: /Delete record/ }).click();
  await expect(relatedRow).toBeVisible();
  expect((await totals()).order_count).toBe(2);
  page.once("dialog", (dialog) => dialog.accept());
  await relatedRow.getByRole("button", { name: /Delete record/ }).click();
  await expect(relatedRow).toHaveCount(0);
  await expect(page.getByRole("tabpanel").locator(".pagination")).toContainText(
    "1 records",
  );
  await page.goto(bookmark);
  await expect(
    page.getByRole("heading", { name: "Line page", exact: true }),
  ).toBeVisible();
  expect((await totals()).order_total).toBe("50.0000");
  expect((await totals()).order_count).toBe(1);
  await page
    .getByRole("button", { name: "Object editor", exact: true })
    .click();
  await page
    .getByRole("combobox", { name: "Connection", exact: true })
    .selectOption(String(id));
  await page
    .getByRole("combobox", { name: "Table", exact: true })
    .selectOption("z_page_customers");
  await page
    .getByRole("button", { name: "Recalculate saved sum-ups", exact: true })
    .click();
  await expect(
    page.getByText("Sum-ups recalculated from all existing child records.", {
      exact: true,
    }),
  ).toBeVisible();
  expect(errors).toEqual([]);
});

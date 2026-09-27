import { test, expect } from "@playwright/test";

test("scrolls Data Type tables horizontally on a mobile viewport", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto("/", { waitUntil: "domcontentloaded" });
  await page.getByLabel("Username", { exact: true }).fill("admin");
  await page.getByLabel("Password", { exact: true }).fill("browser-test-only-password");
  await page.getByRole("button", { name: "Sign in →" }).click();
  await expect(page.getByRole("heading", { name: "Data browser", exact: true })).toBeVisible();

  const id = await page.evaluate(async ({ port, password }) => {
    const { token } = await (await fetch("/api/auth/csrf")).json();
    const response = await fetch("/api/admin/connections", {
      method: "POST",
      headers: { "Content-Type": "application/json", "X-CSRF-TOKEN": token },
      body: JSON.stringify({
        name: `Mobile data types ${Date.now()}`,
        host: "127.0.0.1",
        port,
        database: "dbweb_tests",
        username: "root",
        password,
        verifyTls: false,
      }),
    });
    if (!response.ok) throw new Error(`Connection setup failed: ${response.status} ${await response.text()}`);
    return (await response.json()).id as number;
  }, {
    port: Number(process.env.MARIADB_BROWSER_PORT || (process.env.CI ? 3306 : 33079)),
    password: process.env.MARIADB_BROWSER_PASSWORD || (process.env.CI ? "ci-disposable-root" : ""),
  });

  await page.reload();
  await page.getByRole("button", { name: "Object editor" }).click();
  await page.getByRole("combobox", { name: "Connection", exact: true }).selectOption(String(id));
  await page.getByRole("combobox", { name: "Table", exact: true }).selectOption("z_required_records");

  const dataTypes = page.getByLabel("Data types");
  await dataTypes.getByRole("button", { name: "Default", exact: true }).click();
  for (const label of ["Data type list table", "Data type fields table"]) {
    const scroller = page.getByLabel(label);
    await expect(scroller).toBeVisible();
    const dimensions = await scroller.evaluate((element) => ({
      clientWidth: element.clientWidth,
      scrollWidth: element.scrollWidth,
    }));
    expect(dimensions.scrollWidth).toBeGreaterThan(dimensions.clientWidth);
    await scroller.evaluate((element) => { element.scrollLeft = element.scrollWidth; });
    expect(await scroller.evaluate((element) => element.scrollLeft)).toBeGreaterThan(0);
  }
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
});

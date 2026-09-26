# TableSpace — DbWebTools

A responsive MariaDB record-management workspace with a separate **ASP.NET Core 9 API** and **React 19 + TypeScript + Vite frontend**.

## Features

- Discover existing MariaDB tables without modifying their schema; browse, search, sort, and paginate records.
- Create, update, and delete with parameterized values, schema-validated identifiers, composite primary keys, and optimistic concurrency checks.
- Users, administrator/member roles, disable accounts, reset passwords, and revoke active sessions on account changes.
- Multiple database connections with encrypted passwords, connection testing, verified TLS by default.
- Deny-by-default table permissions plus per-user field-level No access / Read / Write, enforced by the API.
- Visual object and layout editing: database fields, controls and behavior in one object list; labels, editor sections, order and visibility in the layout.
- Configurable related-table lookups: searchable selection dialogs, friendly labels, and key-only storage.
- Recent record-change activity without recording sensitive field values.
- Responsive interface, keyboard-accessible dialogs, loading states, inline errors, and deletion confirmation.

## Quick start

Install .NET SDK 9 and Node.js 22.12+ (or Node 24).

```bash
cp .env.example .env
# Edit .env locally and set a unique BOOTSTRAP_PASSWORD (14+ characters).
# For localhost HTTP testing ONLY, set APP_ENVIRONMENT=Development.
docker compose up --build
```

Open **http://localhost:8080** for local development. Sign in as `admin` with your configured bootstrap password. Add a connection in **Administration → Connections**; choose the database server hostname reachable from the API container (not `localhost` unless the DB runs in that container). Then browse records or configure users, table access, and editor layouts.

For production, keep `APP_ENVIRONMENT=Production` and terminate HTTPS at a trusted reverse proxy in front of the loopback-bound web port. Production authentication cookies require HTTPS. Do not publish the API directly to the Internet. Back up the `app-data` volume, including encryption keys, and restrict access to it. Bootstrap settings are only used when there are no users; remove the bootstrap password after provisioning.

### Development without Docker

Supply `Bootstrap__Password` through your local environment or .NET user-secrets; do not commit it.

```bash
# Terminal 1 (after setting the bootstrap password locally)
ASPNETCORE_ENVIRONMENT=Development dotnet run --project backend/DbWeb.Api --no-launch-profile --urls http://localhost:5188
# Terminal 2
cd frontend
npm ci
npm run dev
```

Open the Vite URL. Vite proxies `/api` to port 5188, so browser API access stays same-origin. There is intentionally no permissive CORS policy.

## Table and field access

Open **Administration → Table access**, choose a connection, table and user, then configure table operations and **Field access**:

- **No access**: field values are omitted from API responses, schema, forms and lists. Searches and user-selected sorting cannot use the field.
- **Read**: the field can be viewed but not submitted in creates or updates.
- **Write**: the field can be viewed and submitted, subject to table create/update permissions and existing generated/read-only/computed constraints.

Use **All fields: No access**, **All fields: Read**, or **All fields: Write** to set every field in one click, adjust exceptions, then **Save permissions**. Administrators retain full access. Existing table grants without a field policy keep their previous behavior. Saving creates an explicit policy; newly added or unspecified fields then default to No access. Older grant API requests that omit `fields` preserve the existing field policy rather than removing it.

Rules are checked on every API request, including record pages and related tabs. Formula/dropdown-display, joined and sum-up fields are suppressed when their required source fields are unreadable. Lookup key/display fields require target read access; inaccessible extra search columns are excluded. Lookup copies require read access to source fields and write access to destination fields. Trusted server-managed defaults, parent context and aggregate maintenance remain automatic; they are not a way to submit forbidden field values. Fixed administrator-defined view/relationship filters may still constrain records internally.

Restricted primary keys are not exposed: record responses provide a user/connection/table-bound `keyToken`, used as `key: { "$record": token }` for updates/deletes and page links. Return the response's opaque `version` unchanged for concurrency checks. Restricted record links are specific to the authorized user, not shareable credentials. No-access fields are removed before serialization; this is separate from layout visibility.

Field policies are stored in SQLite **FieldPolicies**, keyed by user/connection/table. Startup adds this table/index idempotently without changing existing users, grants, layouts or target MariaDB schemas. Back up the full application database and encryption keys together.

## Related-table lookups

1. Open **Administration → Object editor**, select the connection and source table, click **Edit** on the relation column, and change its control to **Lookup / relation**.
2. In its relation configuration, select the **Related table**, **Stored key**, **Display value**, and optional **Additional search columns**. Save the object.
3. In the record editor, click **Choose…** to open the record-selection window. Search matches the key, display column, and selected extra columns. Select a row to populate the relation; only its key is saved to MariaDB.

Each lookup has **Lookup selection criteria** in its relation configuration. Add up to 20 typed conditions on the lookup table and choose **All criteria (AND)** or **Any criterion (OR)**. Text, numeric, date/time and NULL conditions reuse the list filter controls. Criteria are combined with key/display/extra-column search before both pagination and counting. New selections, copy previews and writes must match the criteria; writes recheck within their transaction. Existing selections still resolve their friendly label and unrelated edits do not require reselecting them. These are selection rules, not table-read permissions: they do not hide existing related records, change page-tab membership, or alter sum-up membership. Changing a lookup table clears its old criteria.

For example, configure `orders.customer_id` to look up `customers.id`, display `customers.name`, and search `customers.email`. The grid and editor show the customer's name; the editor also shows the stored key for disambiguation. Existing relations resolve automatically. Cancel leaves the original selection intact; **Clear** stores NULL for nullable columns. Results are paginated and searchable, and large integer keys retain their exact precision.

Lookups target tables in the **same configured database connection** and require a non-null **single-column primary or unique key** with a compatible source type. Composite-key lookups and multi-column relation mappings are not supported. Existing composite-key CRUD is unchanged. Lookup behavior is stored in the object layer; its editor/list placement is stored separately in the layout layer.

Users need **read permission on both source and related tables** to search or resolve related values, plus the usual source create/update permission to save. Without target read access, the grid retains the source key but never exposes related values. Invalid or missing selections are rejected by the API. Layouts do not create or alter database foreign-key constraints: keep actual foreign keys for transactional referential integrity, including changes made outside this application. Search treats `%` and `_` literally; it never executes user SQL.

## Creation defaults

Under **Administration → Object editor**, click a stored field's **Edit** button and use its **Creation default** control:

- **Database default**: no layout override; omitted values use the existing database behavior.
- **Value**: a literal text, number, checkbox, date/date-time, dropdown key, or lookup key. Dropdown controls offer friendly labels while storing their keys.
- **NULL**: explicit NULL for nullable, non-required fields.

Save the object to activate defaults for new records only. Defaults appear when opening **Add record**, including in related tabs. Backend creation also fills omitted values for API clients; explicit values (including NULL or empty text), locked parent context, and lookup-copy results take precedence. Lookup defaults populate their copy mappings and require access to a matching source record. Existing records and database schema/defaults are unchanged. Generated, formula, joined and sum-up fields cannot have creation defaults. Defaults are literal values, not SQL or formula expressions. Standard required/dropdown/database constraints still apply; if a default lookup stops qualifying, revise the layout default or provide another valid key.

## List fields and read-only joined fields

In **Administration → Layout editor**, set each field's **Label**, **Editor section**, **Show in editor**, and **Editor order** for forms/pages, plus **Show in list** and **List order** for record lists. Behavior is defined through each field's Object editor popup. Existing layouts without list settings keep all columns visible. Hidden primary keys remain available internally for correct editing/deletion. Selecting no list fields shows an explicit empty-column notice, without changing data access permissions.

Choose **Add joined field** to display a value from another table without adding a physical database column. Configure:

1. **Local key column**, for example `orders.customer_id`.
2. **Related table** and **Related unique key**, for example `customers.id`.
3. **Displayed value**, for example `customers.email`.
4. A label such as *Customer email* plus editor/list visibility and order in Layout editor.

Joined fields are always read-only. They are available in the record editor and optionally the list, refresh when the local key changes (including lookup selections), and are never sent as stored values. The API rejects attempts to write virtual joined fields. A missing match or NULL local key displays blank for numeric fields and NULL for other fields; without related-table read permission the value displays **Unavailable**, and no related data is returned.

Joins use existing tables in the **same connection**, a compatible local column and a non-null **single-column primary/unique target key**. Up to 20 virtual joined fields are supported per layout. Multiple fields may display different values from the same related row. One-to-many joins, composite join keys, join chains, custom SQL expressions, and joined-field sorting/searching are not supported. Source rows are paginated first; bounded LEFT JOIN queries resolve their related values without dropping or duplicating records. Source concurrency versions depend only on source data, not related display values.

Object behavior and presentation are persisted as separate layers in the application metadata JSON. Existing combined definitions are migrated in place on startup without changing MariaDB data. List/editor visibility is presentation configuration, **not column-level security**; existing table permissions still govern access.

## Dates, timestamps, and dropdowns

Expired or revoked sessions return to the sign-in screen on any protected API response, and are rechecked when the browser/tab is restored or focused. The exact record URL is kept so signing in resumes that page. Permission-denied and network errors do not sign the user out; idle sessions are not kept alive by background polling.

All record searches (data browser, related lists, and lookup windows) wait **500 ms after the last keystroke** before querying. Each input change resets the timer; clearing the input follows the same delay.

Record search matches configured dropdown display labels using case-insensitive partial text, as well as stored keys and other text columns. It also matches configured lookup key/display values and extra lookup search columns in both the data browser and related lists. Lookup matches are case-insensitive, treat `%`/`_` literally, and are computed on the backend before counts and pagination. Related table and field read access is required; inaccessible extra fields cannot influence results. Existing referenced records remain searchable even when they no longer satisfy lookup selection criteria. Parent constraints and saved list filters still apply. Label matches respect layout filters, sorting, and pagination; stored values are unchanged.

In **Administration → Object editor**, click **Edit** for a field and use these controls in its popup:

- **Auto** selects Date for MariaDB `DATE` columns and DateTime for `DATETIME`/`TIMESTAMP` columns.
- **Date** is available for all three temporal types. Changing a Date control on a `DATETIME`/`TIMESTAMP` stores the selected day at midnight. Simply opening or saving an unchanged field does not discard its original time.
- **DateTime** is available for `DATETIME` and `TIMESTAMP`, with date, time, seconds, and milliseconds. Times are displayed and submitted in the database session timezone, without browser timezone conversion. MariaDB continues to apply its normal TIMESTAMP timezone behavior. Untouched values keep their full database precision, including microseconds; editing with the native picker uses millisecond precision. Choose Text when exact manual microsecond input is needed.
- **Dropdown** is available for text columns. Add key/display pairs in the field popup (for example `draft` → `Draft document`, `ready` → `Ready to publish`). Each key and each display label must be unique within that dropdown, ignoring case. Define 1–200 options, with non-blank keys/labels of up to 256 characters and no surrounding whitespace. Save the object.

Dropdowns display labels in both the editor and record grid, while only keys are stored in MariaDB. Unknown submitted keys and duplicate configuration entries are rejected by the API. Existing values removed from the option list remain visible as unconfigured values and are not silently rewritten; choose a configured replacement to change them. Nullable fields can be cleared, and untouched fields on new records retain database defaults. Updating a record submits only changed fields, preserving unrelated timestamps and allowing database `ON UPDATE` behavior to operate normally.

These controls are stored in the object layer in `/var/lib/dbwebtools/app.db`. Existing combined definitions are migrated to the layered object/layout format automatically. Data Types additionally manage the reserved MariaDB `datatype VARCHAR(64) NOT NULL` column described below.

## Build and test

```bash
./scripts/build.sh
# Windows:
./scripts/build.ps1
```

The scripts restore locked dependencies, build with warnings as errors, run backend and frontend tests, and publish independent artifacts into `artifacts/api` and `artifacts/web`.

To exercise real MariaDB CRUD locally, set `MARIADB_TEST_CONNECTION` to a connection string for a **disposable test database**, then run the script. The integration tests create and drop only randomly named test tables. CI always requires and runs this test against a MariaDB service. Without this environment variable, local runs do not exercise the database integration scenario.

## Record pages and related tabs

Use **Administration → Page editor** to create pages:

1. Choose a name, connection, main table, and **List drill-down column**. Make that column visible in the table's list layout. It becomes a link to the selected record's page. Multiple pages may use the same table with different drill-down columns.
2. The main record uses the **Object definition** for controls, lookup/dropdown values, joined values, formulas and validation, then applies **Layout editor** labels, sections, order and visibility. Authorized users can open the existing record editor from the page.
3. Add **related tabs**. Choose a related table and an **Object lookup relation** from that table’s Object definition which points to the main table (for example, an `orders.customer_id` lookup targeting `customers.id`). The key columns are derived from the current lookup definition, not entered separately. Choose and reorder the visible related columns; stored, joined, and formula fields are supported.
4. Optionally choose a **destination page** for a related tab and the visible column that should link to it. Create destination pages first. Links can continue through further related tabs; composite primary keys and exact large numeric keys are supported.
5. Save the page. Page definitions are stored in SQLite `app.db` → `Pages`, separately from record layouts. Startup applies an additive, idempotent schema upgrade to existing metadata databases without recreating them.

Each drill-down extends the breadcrumb with the page and record label. The browser URL encodes the entire record path (including exact composite/large keys), so refreshes, bookmarks and shared links reopen the same page and breadcrumb. Ancestors remain clickable, and browser back/forward navigation works. Ancestor labels are loaded through normal permission checks; unavailable ancestors never disclose their values. The related lists support debounced search, sorting, pagination, and the related table's saved list filters. Search keeps the list and toolbar mounted, preserves focus and height while updating, and ignores stale responses. The server re-reads the parent record and adds an independent relationship constraint to every tab query, including counts; client search/filter parameters cannot remove it. A NULL parent relationship value yields an empty tab, not every related row with a NULL value.

A page requires a main table with a scalar primary key. Relationships currently use one compatible column pair within the same connection. Each page supports up to 12 tabs and each tab up to 100 selected columns. Table read access is required for the main record **and** each related table; unauthorized tabs are hidden and their endpoints return 403. Linked pages enforce their own table access. Object behavior and layout presentation are not column-level security; saved object filters are not row-level authorization.


Users with update access see an **Edit record** action on each related row. It uses the related table’s layout and retains concurrency checks. The parent relationship stays locked; the backend checks actual membership inside the write transaction, so another parent’s record cannot be edited through the tab. Saving refreshes the related list and parent totals. The related toolbar places search on the left and the standard-sized **Add record** button on the right. Numeric NULL values display as blank in lists, pages, editors, and lookup results; stored NULL values and zero values remain unchanged.

Users with delete access to the related table see a **Delete record** action on each related row. Confirmation is required. The API rechecks delete permission and parent membership inside the write transaction, applies concurrency checks and updates sum-ups. Deletion refreshes the related list and parent totals. Read-only users do not see the delete action; write access to individual fields is not required to delete a record when table delete permission is granted. Database foreign-key constraints still apply.

Users with create access to the related table see **Add record**. The form uses that table’s layout; the parent lookup is filled automatically and locked. Copy-mapped fields are prefilled and follow each mapping's **Allow editing** setting. Required fields, other lookups, dropdowns, and formulas work as in the normal editor. The backend rechecks permissions, locks and rereads the parent during insertion, and reapplies copy mappings before validation; submitted parent replacements cannot change the relationship, and locked copies cannot be overridden. Cancelling creates nothing. After saving, the related list refreshes; configured filters and sorting still apply.

Previously saved manual column mappings remain readable. To enable creation, edit the tab and choose an existing layout lookup, then save. Removing a referenced lookup or changing it to target another table makes that tab fail with a configuration error until repaired. Changes to the valid lookup key/copy mappings are used immediately. No existing layouts or page definitions are rewritten by deployment.

A saved page's connection/table cannot change, to keep inbound links stable. Remove inbound page links before deleting a page. Deleting a page never deletes MariaDB records. Existing layouts and list behavior remain unchanged until pages are configured.

## Stored sum-up fields

1. On the **child table's Object definition**, define a lookup pointing to the parent's unique key. Optionally define a numeric **Formula** on the child (for example, `[quantity] * [unit_price]`).
2. On the **parent Object definition**, choose **Sum-up (stored total)** for an existing, non-key integer or DECIMAL column. The field becomes read-only. For money, use sufficient precision such as `DECIMAL(18,4)`; no schema columns are created automatically.
3. Select **Sum** or **Count**, the child lookup relation, and the source field. Sum accepts a numeric stored child column or a numeric child formula. Count with **All child records** counts rows; Count with a source counts its non-NULL results. NULL sum contributions count as zero. Table list filters and user search do not change aggregate membership.
4. **Save object** validates the configuration and fills existing parent records. Adding/changing an aggregate, its relation key, or dependent child formula/dropdown definitions rebuilds affected totals. Presentation-only changes do not rebuild totals.
5. **Recalculate saved sum-ups** forces a complete rebuild for that parent object using the saved configuration, including zeroing parents with no children. It is administrator-only and ignores unsaved editor changes.

Normal TableSpace create/update/delete operations apply only the changed child's before/after contribution, including both parents when moving a relation. The child write and parent adjustments commit together. Parent rows are locked with MariaDB `SELECT … FOR UPDATE`; a database-scoped advisory gate coordinates writes, configuration changes, and rebuilds across app instances. Busy operations may return a retryable conflict rather than overlap. Parent creation initializes totals (including pre-existing orphan children); deleting a parent with contributing children or changing a relationship key is rejected until children are moved/removed. After related-record creation or editing the parent page refreshes its stored totals.

Rebuilds scan child contributions once per aggregate and stream them, rather than issuing a child scan for every parent. Regular child edits do **not** scan the full child set. Formula errors, overflow, invalid mappings, and insufficient destination scale fail the operation and roll back its data changes. Use `Round(...)` in the child formula when deliberate rounding is needed; implicit per-edit rounding is rejected to prevent total drift. Destinations support exact integers/DECIMAL with up to 28 digits, matching the backend decimal formula engine; FLOAT/DOUBLE destinations and aggregate-on-aggregate formula dependencies are not supported. Parent and child tables must use InnoDB (MariaDB 10.5+).

Automatic maintenance covers writes through **the same configured TableSpace connection**, including normal and page-related creation endpoints. Direct SQL, other applications, and database-trigger/cascade side effects are not change-captured; run full recalculation after those changes. Configure and write through a single connection for a given aggregate relationship. Stored totals follow parent-field read permissions and require access to their child relation/source dependencies; child writers do not need parent update permission to maintain backend-managed totals. They cannot submit aggregate values directly.

Definitions remain in the object layer; no MariaDB triggers or auxiliary tables are installed. A durable pending-rebuild marker protects the SQLite/MariaDB configuration handoff: an interrupted backfill is repaired before subsequent record reads or writes. Existing layout/page configurations are preserved until edited.

## Lookup copies and calculated fields

In **Administration → Object editor**, click **Edit** and choose **Lookup / relation** for a stored relation column. Under **Copy values to this table**, add source-column → destination-column mappings. Source and destination types must be compatible; destinations must be distinct, writable non-key columns and cannot themselves be lookups. Up to 20 mappings are supported per lookup, within the same connection.

Selecting or reselecting a lookup (including the same record) repopulates its copies in the editor. Each mapping has an **Allow editing** checkbox, unchecked by default. Unchecked copies stay locked both immediately and when reopening a record; the API rejects standalone edits and re-reads authoritative source values when the lookup is submitted. Checked copies may be changed before saving or in subsequent edits, subject to normal field permissions/read-only settings. Submitted overrides, including NULL, are preserved; omitted destinations are copied on the backend inside the record transaction. Both kinds still pass required/dropdown/type validation. Clearing the lookup copies NULL (required/non-nullable destinations can therefore prevent saving). Unrelated edits leave the stored copies unchanged: these are snapshots, unlike joined fields. Related-table read permission is required for selection, preview, and copying. Mappings do not alter database schema.

Use **Validate formula** above each formula expression to check syntax, column references, and function arguments on the backend without saving the layout. Feedback appears above the expression and clears when the draft changes. Validation uses draft dropdown definitions too; valid syntax does not guarantee every record avoids runtime errors such as division by zero.

Choose **Add formula field** to open its field popup and define a read-only calculation. Configure its label, editor section, editor/list order and visibility in Layout editor. Formulas use stored column names in square brackets and are evaluated **only by the backend**, including debounced editor previews. They are never submitted as stored columns or included in record versions. They cannot be used for list sorting/filtering and cannot reference other formula/joined fields.

Examples:

```text
Round([unit_price] * [quantity], 2)
Concat(Upper(Trim([name])), ' — ', [code])
Replace([description], 'old', 'new')
Substring([code], 0, 3)
Coalesce([discount], 0)
DropdownDisplay('status', [status])
Concat('Status: ', Coalesce(DropdownDisplay('status', [status]), 'Unknown'))
if([quantity] > 0, [amount] / [quantity], 0)
```

Supported operations: `+ - * / %`, comparisons, boolean expressions; `Round(value, places)`, `Abs`, `Floor`, `Ceiling`, `Min(a,b)`, `Max(a,b)`, `Concat`, `Upper`, `Lower`, `Trim`, `Length`, `Substring(text,start,length)`, `Replace(text,from,to)`, `Coalesce(value,fallback,...)`, `DropdownDisplay('field_name', key)`, and `if(condition,yes,no)`. Function names are case-sensitive, substring indexes start at zero, and Round uses midpoint-to-even. Arithmetic/math functions propagate NULL; text functions treat NULL as empty text. Decimal computations preserve database precision within .NET decimal limits; results travel as strings, not imprecise JavaScript numbers. Errors such as division by zero or out-of-range values display a per-field calculation error without breaking the record list.

`DropdownDisplay('status', [status])` returns the display label configured for the supplied key on the `status` dropdown in the same layout. The first argument must be a quoted **stored column name**, not a field label or dynamic expression; it is checked when saving the object. The key may be a column reference, literal, or expression. Keys match exactly (case-sensitive), consistent with normal dropdown display. Unknown/NULL keys return NULL; wrap the call in `Coalesce(..., 'Unknown')` for a fallback. Updated dropdown labels take effect on the next list load or editor calculation, without rewriting stored keys. This resolves configured dropdown options, not relational lookup tables.

[NCalc](https://github.com/ncalc/ncalc) supplies the expression parser/interpreter. Only documented functions/operators and existing scalar stored columns are accepted—no SQL or arbitrary code. Limits: 20 total joined/formula fields, 1,024 expression characters, 32 nested expression levels, 8,192 input/output text characters, and an evaluation deadline.

### Browser tests

All Playwright specs, configuration, and dependencies now live in `tests/playwright` (not the frontend). Build scripts install its locked dependencies. After starting a **disposable** MariaDB test server and seeding `tests/fixtures/browser.sql`:

```bash
npm --prefix tests/playwright ci
cd tests/playwright
npx playwright install --with-deps chromium
npm test
```

The local fixture uses `127.0.0.1:33079`, database `dbweb_tests`, root with an empty password; CI uses its isolated MariaDB service instead. Never point these tests at a real database. Playwright starts separate API/frontend processes on ports 5190/5173; use a fresh `RUNNER_TEMP` directory for isolated application metadata. Results and screenshots are written under `artifacts/`.

## Architecture

```text
frontend/                React/TypeScript SPA, editor, administration, nginx image
backend/DbWeb.Api/        HTTP API, authentication/authorization, application metadata
  Models.cs              EF Core SQLite application metadata (never target data)
  DatabaseService.cs     MariaDB schema inspection and transactional record operations
  Program.cs             Composition root and authenticated endpoint mappings
tests/DbWeb.Tests/        HTTP security, formula, and real MariaDB integration tests
tests/playwright/        Browser specs, runner configuration and locked dependencies
scripts/                 Bash and PowerShell build entrypoints
.github/workflows/       PR validation, release artifacts, container publication
```

Authentication uses PBKDF2 password hashing through ASP.NET Core PasswordHasher, HTTP-only SameSite cookies, antiforgery tokens on all API mutations (including login), and login rate limiting. Passwords for target databases use ASP.NET Core Data Protection. Only administrators may configure target hosts; deploy with network egress restricted to intended database servers. Use dedicated least-privilege database accounts. Runtime-only connections need SELECT/INSERT/UPDATE/DELETE; connections used by Object editor schema operations additionally need the documented CREATE/ALTER/REFERENCES privileges.

Table names and column identifiers are validated against `information_schema` and safely quoted. Values are parameters. Updates/deletes require the full primary key and a record version checked inside a transaction with `SELECT ... FOR UPDATE`. Tables without primary keys are read-only; views are not exposed. Large integer and decimal values are transported as strings to preserve precision; binary values use base64. Defaulted fields omitted from create requests retain their database default.

Object definitions drive application behavior; layouts supply labels, editor sections, editor/list order and visibility. Neither is column-level security. Use **Table access → Field access** for column-level security in addition to table grants. MariaDB transactional tables (InnoDB) are required for reliable concurrency guarantees. Foreign keys and database constraints are enforced by MariaDB. Object editor supports the documented safe subset of table/column schema changes; advanced migrations and file attachments remain outside this release. Application metadata currently uses a single SQLite instance; scale the application as a single replica. The Pages schema is installed with an additive idempotent upgrade; future schema changes must preserve existing metadata. The activity log and target database are separate stores, not a distributed atomic audit ledger.

## CI/CD and contributing

Every pull request runs release builds, backend authorization/CSRF tests, frontend editor tests, and real MariaDB CRUD/concurrency validation. Successful builds upload separate deployment artifacts. Every PR also validates both container builds. A version tag (`v*`) validates first, then builds and publishes commit-SHA-tagged API/web images to GitHub Container Registry. No external production deployment target is assumed.

Use feature branches and pull requests for subsequent changes; never commit credentials or application data. Configure branch protection to require the `validate` job before merging.

### Data Types

Every object has a **Data Types** list and exactly one default type. Existing objects are normalized to a `default` type without changing their effective required or mask behavior. Type keys are immutable stored identifiers; labels can be edited. New records receive the current default key in the backend-managed `datatype VARCHAR(64) NOT NULL` column. Existing rows keep the key assigned when they were created, and clients cannot submit or update the managed column directly.

Select a data type to drill into all object fields. Required and input-mask rules belong to those per-type field settings rather than the object field itself. Dropdown key/display pairs remain the object-level master catalog. By default every catalog value is available; enable **Override dropdown options** to choose the values available for that type. Existing records whose stored value is no longer enabled still display its master label and can be changed only to an enabled value.

The record UI currently applies the default type's settings to all records. Additional types can be authored now for later record-specific selection. The application provisions and backfills the managed column when an administrator saves an existing object. Deployments can run `DbWeb.Api --provision-data-types` while the service is stopped to migrate every configured table explicitly; ordinary read requests never run DDL. Object-editor connections therefore require ALTER and UPDATE permission during this one-time upgrade. A pre-existing incompatible `datatype` column or unconfigured stored type key is rejected rather than overwritten.

### Required fields

In **Administration → Object editor → Data Types**, select a data type, configure a field, check **Required**, and save the field settings. Required fields are marked in the record editor. NULL, missing values, empty strings, and whitespace-only strings are rejected; zero and false are valid. The API enforces the rule as well as the form. On updates, it checks submitted values together with the locked current record, so an unchanged empty required field must be repaired before other edits can be saved. Deletes are unaffected.

Required fields must be editable stored columns; generated, auto-increment, read-only, joined, formula, and sum-up fields cannot be marked required. Layout editor prevents hiding a field required by the active default data type. On creation, a required value must be supplied explicitly, even if the database defines a default. Database NOT NULL constraints still apply; data-type required rules do not modify column nullability.

### Text input masks

For a Text or Text area field, open **Object editor → Data Types**, select the data type, configure the field, and enter an exact **Pattern** or enable **Numbers only**. In a pattern, `#` accepts a number, `A` a letter, and `X` a letter or number; append `?` to make a position optional. Empty values remain governed by the field's Required/nullability settings.

The record editor shows the complete rule below the field and checks changed values before saving. The backend independently enforces the same rule for creates, updates, related-record writes, lookup-copied values, and configured creation defaults. Masks are structured rather than arbitrary regular expressions.

### Object sorting, filters, and presentation layout

In **Administration → Object editor**, choose a connection/table:

- Set **List title** to replace the table name above the record list (optional).
- Choose a **Default sort column** and **Ascending/Descending** direction. Primary keys are added as tie-breakers for stable pagination. Users can temporarily sort by clicking a list heading, then click **Use default sorting** to restore the saved order.
- Click **Add filter**, select a stored field, condition, and value. Use **All criteria (AND)** or **Any criterion (OR)**. Save the object. Up to 20 criteria are supported. Filter and sort columns can be hidden later in Layout editor, but cannot be virtual joined fields.
- Open **Layout editor** to configure the friendly label, editor section, and editor/list order and visibility for every stored or virtual object field. Empty labels fall back to field names and do not rename database columns.

Filters are enforced on the records API before counting and pagination; free-text search is combined with the saved criteria, never substituted for them. The list displays its active filter. Create/update operations can produce records outside the current view; those records will disappear from that list after saving. Filters are list configuration, **not row-level security**, and do not constrain lookup selectors or change existing table permissions.

Conditions include equals/not-equals, greater/less than (inclusive or exclusive), text contains/starts-with, and IS NULL/IS NOT NULL. Text matching follows MariaDB column collation; contains/starts-with values treat `%` and `_` literally. Empty text and NULL are distinct. Not-equals excludes NULL rows; use an OR with IS NULL to include them. Relation filters use stored keys; configured text dropdowns offer display labels while storing the option key. Dates use database-session values without timezone conversion. Numeric filters preserve integer/decimal precision (up to 65 digits and 30 decimal places).

Object definitions and presentation layouts remain in `RecordLayout.FieldsJson` in the application SQLite database, but use a layered `{ "Object": ..., "Layout": ... }` format. Startup migrates existing array/combined definitions in place. Runtime endpoints merge both layers for compatibility. The object endpoint owns semantic fields and list query behavior; the layout endpoint owns `label`, `section`, `editorOrder`, `showInEditor`, `listOrder`, and `showInList`. Legacy combined layout PUT requests remain accepted when they omit Data Type-owned `Required` and `Mask` properties; clients must save those settings through the object endpoint.

## Object and layout editors

Administrators can open **Object editor**, select a connection and choose **New table**. Each table is created as InnoDB/UTF-8 with a non-null auto-increment `BIGINT` primary key (`id` by default, with a configurable name). New tables become available in the data browser immediately. Non-admin users require the usual table/field grants; creating a table does not grant access to everyone.

The **Application object** table is the single read-only summary of physical database columns and virtual fields. **Add field**, **Add joined field**, and **Add formula field** are grouped below it. **Add field** uses the same **Control / behavior** choice as editing—Text (length), Text area, Number (precision and decimal places), Checkbox, Date, Date and time, Dropdown, or Lookup / relation—to derive the physical database type; there is no separate type dropdown. A relation selects another table's non-null single-column unique integer/text key and a display column. The designer copies the key's database type, signedness and character settings, creates an actual `FOREIGN KEY ... RESTRICT`, and adds matching lookup behavior without replacing other object or presentation settings. Lookup search, selection, copy mappings and related-page configuration then work as usual. Foreign keys prevent orphan values and deletion of referenced parent records; no cascading deletes are introduced.

**Edit** opens one field popup containing the control/behavior, validation, default, lookup/dropdown, formula, join, sum-up, and supported database attributes; the list itself is not directly editable. Database changes support NULL allowance, text length, or decimal precision/places. Existing types/names, defaults, comments, character settings and indexes are preserved. Primary, generated and foreign-key columns are protected; unsupported special types must be managed with a database migration tool. This designer does not rename columns or convert existing types. **Delete** requires explicit confirmation, removes the physical column when present, and removes that field plus dependent references from Object, Layout, list, page-tab, lookup, sum-up, and field-access configuration. Primary keys and active page link columns are protected; MariaDB still blocks unsafe referenced-column deletion. Decimal changes cannot reduce fractional digits or integer capacity. Text shortening is rejected if existing values do not fit, and NULL must be filled before making a column required. New columns on populated tables initially allow NULL; fill their values before making them required.

Schema changes are admin-only, CSRF protected and audited. The saved MariaDB account needs the corresponding CREATE/ALTER/REFERENCES privileges; TableSpace does not grant database privileges automatically. Changes use validated/quoted identifiers, strict SQL mode, schema-version checks and the same coordination gate as application writes. MariaDB DDL commits independently of the application's SQLite configuration, and large table changes can take time; maintain database backups. The storage engine remains the authority for constraints and concurrent external writers. See the [MariaDB ALTER TABLE documentation](https://mariadb.com/docs/server/reference/sql-statements/data-definition/alter/alter-table).

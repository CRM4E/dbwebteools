using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using static DbWeb.Api.TableAccess;

namespace DbWeb.Api;

public class PageConfiguration
{
    public int Id { get; set; }
    public int ConnectionId { get; set; }
    public string Table { get; set; } = "";
    public string Name { get; set; } = "";
    public string LinkColumn { get; set; } = "";
    public string TabsJson { get; set; } = "[]";
}

public record RelatedTab(
    string Id,
    string Label,
    string Table,
    string ParentColumn,
    string RelatedColumn,
    List<string> Columns,
    int? TargetPageId = null,
    string? LinkColumn = null,
    string? LookupField = null
);

public record PageDefinition(
    int Id,
    int ConnectionId,
    string Table,
    string Name,
    string LinkColumn,
    List<RelatedTab> Tabs
);

public static class PageSchema
{
    // Additive, idempotent upgrade for existing EnsureCreated databases. Never recreate metadata.
    public static void EnsureCreated(AppDb db) =>
        db.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS "Pages" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_Pages" PRIMARY KEY AUTOINCREMENT,
                "ConnectionId" INTEGER NOT NULL, "Table" TEXT NOT NULL, "Name" TEXT NOT NULL,
                "LinkColumn" TEXT NOT NULL, "TabsJson" TEXT NOT NULL);
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_Pages_ConnectionId_Table_LinkColumn"
                ON "Pages" ("ConnectionId", "Table", "LinkColumn");
            """
        );
}

public partial class DatabaseService
{
    public async Task<RecordPage> PageRecord(
        MySqlConnection db,
        string table,
        string keyJson,
        MySqlTransaction? transaction = null
    )
    {
        if (string.IsNullOrWhiteSpace(keyJson))
            throw new ApiError(400, "Complete primary key required.");
        var columns = await Columns(db, table, transaction);
        var keys = columns.Where(c => c.PrimaryKey).ToList();
        Dictionary<string, JsonElement>? key;
        try
        {
            key =
                keyJson.Length <= 8192
                    ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(keyJson)
                    : null;
        }
        catch (JsonException)
        {
            throw new ApiError(400, "Invalid record key.");
        }
        if (
            keys.Count == 0
            || key == null
            || key.Count != keys.Count
            || keys.Any(c => !key.ContainsKey(c.Name))
        )
            throw new ApiError(400, "Complete primary key required.");
        await using var cmd = db.CreateCommand();
        cmd.Transaction = transaction;
        var predicates = new List<string>();
        foreach (var column in keys)
        {
            var value = key[column.Name];
            if (value.ValueKind is not JsonValueKind.String and not JsonValueKind.Number)
                throw new ApiError(400, "Record key values must be strings or numbers.");
            var parameter = "@key" + predicates.Count;
            predicates.Add(
                $"{Quote(column.Name)} = {FilterOperand(cmd, parameter, column, value.ToString())}"
            );
        }
        cmd.CommandText =
            $"SELECT * FROM {Quote(table)} WHERE {string.Join(" AND ", predicates)} LIMIT 1"
            + (transaction != null ? " FOR UPDATE" : "");
        var records = await Read(cmd);
        if (records.Count == 0)
            throw new ApiError(404, "Record no longer exists.");
        return new(1, 1, 1, columns, records.Select(r => new RecordRow(r, Version(r))).ToList());
    }
}

public partial class DatabaseService
{
    public async Task RequireRelated(
        MySqlConnection c,
        MySqlTransaction tx,
        string table,
        List<ColumnInfo> columns,
        RecordRow child,
        string relation,
        object parentValue
    )
    {
        using var cmd = new MySqlCommand { Connection = c, Transaction = tx };
        var keys = columns.Where(col => col.PrimaryKey).ToList();
        var predicates = new List<string>();
        foreach (var key in keys)
        {
            var name = "@key" + predicates.Count;
            predicates.Add($"{Quote(key.Name)}={name}");
            cmd.Parameters.AddWithValue(name, child.Values[key.Name]);
        }
        cmd.Parameters.AddWithValue("@parent", parentValue);
        cmd.CommandText =
            $"SELECT 1 FROM {Quote(table)} WHERE {string.Join(" AND ", predicates)} AND {Quote(relation)}=@parent FOR UPDATE";
        if (await cmd.ExecuteScalarAsync() == null)
            throw new ApiError(404, "Record does not belong to this parent's related list.");
    }
}

public static class PageEndpoints
{
    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    static PageDefinition Definition(PageConfiguration page) =>
        new(
            page.Id,
            page.ConnectionId,
            page.Table,
            page.Name,
            page.LinkColumn,
            JsonSerializer.Deserialize<List<RelatedTab>>(page.TabsJson, Json) ?? []
        );

    static async Task<LayoutDefinition> Layout(AppDb db, int connection, string table) =>
        DatabaseService.Layout(
            (
                await db.Layouts.SingleOrDefaultAsync(l =>
                    l.ConnectionId == connection && l.Table == table
                )
            )?.FieldsJson
        );

    static async Task<bool> CanRead(AppDb db, HttpContext ctx, int connection, string table)
    {
        try
        {
            await Access(db, ctx, connection, table, "read");
            return true;
        }
        catch (ApiError e) when (e.Status == 403)
        {
            return false;
        }
    }

    static async Task<RelatedTab> ResolveTab(
        AppDb db,
        int connection,
        string parentTable,
        RelatedTab tab
    )
    {
        if (string.IsNullOrEmpty(tab.LookupField))
            return tab; // Legacy manually mapped tabs remain readable.
        var fields = (await Layout(db, connection, tab.Table)).Fields;
        var field =
            fields.Find(f =>
                f.Name == tab.LookupField && f.Widget == "lookup" && f.Lookup?.Table == parentTable
            )
            ?? throw new ApiError(
                400,
                "The related lookup no longer points to this page's main table. Update the tab in Page editor."
            );
        return tab with { ParentColumn = field.Lookup!.KeyColumn, RelatedColumn = field.Name };
    }

    static async Task<PageDefinition> ResolvePage(AppDb db, PageDefinition page)
    {
        if (page.Tabs == null || page.Tabs.Any(t => t == null))
            throw new ApiError(400, "Related tabs required.");
        var tabs = new List<RelatedTab>();
        foreach (var tab in page.Tabs)
            tabs.Add(await ResolveTab(db, page.ConnectionId, page.Table, tab));
        return page with { Tabs = tabs };
    }

    static async Task<bool> CanUpdate(AppDb db, HttpContext ctx, int connection, string table)
    {
        try
        {
            await Access(db, ctx, connection, table, "update");
            return true;
        }
        catch (ApiError e) when (e.Status == 403)
        {
            return false;
        }
    }

    static async Task<bool> CanDelete(AppDb db, HttpContext ctx, int connection, string table)
    {
        try
        {
            await Access(db, ctx, connection, table, "delete");
            return true;
        }
        catch (ApiError e) when (e.Status == 403)
        {
            return false;
        }
    }

    static async Task<bool> CanCreate(AppDb db, HttpContext ctx, int connection, string table)
    {
        try
        {
            await Access(db, ctx, connection, table, "create");
            return true;
        }
        catch (ApiError e) when (e.Status == 403)
        {
            return false;
        }
    }

    static string Family(ColumnInfo c) =>
        DatabaseService.TextFilterColumn(c)
            ? "text"
            : c.Type switch
            {
                "tinyint"
                or "smallint"
                or "mediumint"
                or "int"
                or "bigint"
                or "year"
                or "bit"
                or "decimal"
                or "float"
                or "double" => "number",
                "datetime" or "timestamp" => "datetime",
                "date" => "date",
                _ => "unsupported",
            };

    static void Label(string? label)
    {
        if (string.IsNullOrWhiteSpace(label) || label.Length > 150 || label != label.Trim())
            throw new ApiError(
                400,
                "Page and tab names need 1–150 characters without surrounding spaces."
            );
    }

    static async Task Validate(PageDefinition input, AppDb db, DatabaseService service)
    {
        Label(input.Name);
        var config =
            await db.Connections.FindAsync(input.ConnectionId)
            ?? throw new ApiError(404, "Connection not found.");
        await using var c = await service.Open(config);
        var main = await service.Columns(c, input.Table);
        if (
            !main.Any(col => col.PrimaryKey)
            || main.Where(col => col.PrimaryKey).Any(col => Family(col) == "unsupported")
        )
            throw new ApiError(400, "Pages require a table with scalar primary-key columns.");
        if (!main.Any(col => col.Name == input.LinkColumn))
            throw new ApiError(400, "Choose a stored drill-down column.");
        if (
            input.Tabs == null
            || input.Tabs.Count > 12
            || input.Tabs.Any(t => t == null)
            || input.Tabs.Select(t => t.Id).Distinct().Count() != input.Tabs.Count
        )
            throw new ApiError(400, "Define at most 12 uniquely identified related tabs.");
        foreach (var tab in input.Tabs)
        {
            Label(tab.Label);
            if (!Guid.TryParse(tab.Id, out _))
                throw new ApiError(400, "Invalid tab identifier.");
            var related = await service.Columns(c, tab.Table);
            var parentColumn = main.Find(col => col.Name == tab.ParentColumn);
            var childColumn = related.Find(col => col.Name == tab.RelatedColumn);
            if (
                parentColumn == null
                || childColumn == null
                || Family(parentColumn) == "unsupported"
                || Family(parentColumn) != Family(childColumn)
            )
                throw new ApiError(
                    400,
                    "Select compatible stored parent and related columns for each tab."
                );
            var layout = await Layout(db, input.ConnectionId, tab.Table);
            var available = related
                .Select(col => col.Name)
                .Concat(
                    layout.Fields.Where(f => f.Widget is "join" or "formula").Select(f => f.Name)
                )
                .ToHashSet();
            if (
                tab.Columns is not { Count: > 0 and <= 100 }
                || tab.Columns.Distinct().Count() != tab.Columns.Count
                || tab.Columns.Any(name => !available.Contains(name))
            )
                throw new ApiError(
                    400,
                    "Select 1–100 distinct existing columns for each related list."
                );
            if (tab.TargetPageId != null)
            {
                var target =
                    tab.TargetPageId == input.Id && input.Id > 0 ? input
                    : await db.Pages.FindAsync(tab.TargetPageId) is { } stored ? Definition(stored)
                    : null;
                if (
                    target == null
                    || target.ConnectionId != input.ConnectionId
                    || target.Table != tab.Table
                )
                    throw new ApiError(
                        400,
                        "The destination page must belong to the related table and connection."
                    );
                if (tab.LinkColumn == null || !tab.Columns.Contains(tab.LinkColumn))
                    throw new ApiError(
                        400,
                        "Select a visible drill-down column for the related list."
                    );
            }
            else if (!string.IsNullOrEmpty(tab.LinkColumn))
                throw new ApiError(
                    400,
                    "Select a destination page before a related drill-down column."
                );
        }
    }

    public static void Map(WebApplication app)
    {
        var admin = app.MapGroup("/api/admin/pages").RequireAuthorization("Admin");
        admin.MapGet(
            "",
            async (AppDb db) =>
                (await db.Pages.OrderBy(p => p.Name).ToListAsync()).Select(Definition)
        );
        admin.MapPost(
            "",
            async (PageDefinition input, AppDb db, DatabaseService service) =>
            {
                input = input with { Id = 0 };
                input = await ResolvePage(db, input);
                await Validate(input, db, service);
                var page = new PageConfiguration
                {
                    ConnectionId = input.ConnectionId,
                    Table = input.Table,
                    Name = input.Name,
                    LinkColumn = input.LinkColumn,
                    TabsJson = JsonSerializer.Serialize(input.Tabs),
                };
                db.Pages.Add(page);
                await db.SaveChangesAsync();
                return Results.Ok(Definition(page));
            }
        );
        admin.MapPut(
            "/{id:int}",
            async (int id, PageDefinition input, AppDb db, DatabaseService service) =>
            {
                var page =
                    await db.Pages.FindAsync(id) ?? throw new ApiError(404, "Page not found.");
                if (input.ConnectionId != page.ConnectionId || input.Table != page.Table)
                    throw new ApiError(
                        400,
                        "A saved page's connection and table cannot change. Create a new page instead."
                    );
                input = input with { Id = id };
                input = await ResolvePage(db, input);
                await Validate(input, db, service);
                page.Name = input.Name;
                page.LinkColumn = input.LinkColumn;
                page.TabsJson = JsonSerializer.Serialize(input.Tabs);
                await db.SaveChangesAsync();
                return Results.NoContent();
            }
        );
        admin.MapDelete(
            "/{id:int}",
            async (int id, AppDb db) =>
            {
                var page =
                    await db.Pages.FindAsync(id) ?? throw new ApiError(404, "Page not found.");
                if (
                    (await db.Pages.Where(p => p.Id != id).ToListAsync()).Any(p =>
                        Definition(p).Tabs.Any(t => t.TargetPageId == id)
                    )
                )
                    throw new ApiError(
                        409,
                        "Remove links to this page from other pages before deleting it."
                    );
                db.Pages.Remove(page);
                await db.SaveChangesAsync();
                return Results.NoContent();
            }
        );
        var api = app.MapGroup("/api/pages").RequireAuthorization();
        api.MapGet(
            "",
            async (AppDb db, HttpContext ctx) =>
            {
                var result = new List<object>();
                foreach (var page in await db.Pages.OrderBy(p => p.Name).ToListAsync())
                    if (
                        await CanRead(db, ctx, page.ConnectionId, page.Table)
                        && (await FieldAccess.For(db, ctx, page.ConnectionId, page.Table)).Read(
                            page.LinkColumn
                        )
                    )
                        result.Add(
                            new
                            {
                                page.Id,
                                page.ConnectionId,
                                page.Table,
                                page.Name,
                                page.LinkColumn,
                            }
                        );
                return result;
            }
        );
        api.MapGet(
            "/{id:int}/record",
            async (int id, string key, AppDb db, DatabaseService service, HttpContext ctx) =>
            {
                var page =
                    await db.Pages.FindAsync(id) ?? throw new ApiError(404, "Page not found.");
                var config = await Access(db, ctx, page.ConnectionId, page.Table, "read");
                await using var c = await service.Open(config);
                await Sumups.RepairIfPending(db, service, c, page.ConnectionId);
                key = await FieldAccess.DecodePageKey(db, ctx, page.ConnectionId, page.Table, key);
                var records = await service.PageRecord(c, page.Table, key);
                var layout = await Layout(db, page.ConnectionId, page.Table);
                await RecordPresentation.Decorate(
                    db,
                    ctx,
                    page.ConnectionId,
                    page.Table,
                    c,
                    service,
                    layout.Fields,
                    records
                );
                var tabs = new List<RelatedTab>();
                var mainAccess = await FieldAccess.For(db, ctx, page.ConnectionId, page.Table);
                foreach (var tab in Definition(page).Tabs)
                    if (await CanRead(db, ctx, page.ConnectionId, tab.Table))
                    {
                        var childAccess = await FieldAccess.For(
                            db,
                            ctx,
                            page.ConnectionId,
                            tab.Table
                        );
                        tabs.Add(
                            tab with
                            {
                                Columns = tab.Columns.Where(childAccess.Read).ToList(),
                                ParentColumn = mainAccess.Read(tab.ParentColumn)
                                    ? tab.ParentColumn
                                    : "",
                                RelatedColumn = childAccess.Read(tab.RelatedColumn)
                                    ? tab.RelatedColumn
                                    : "",
                                LookupField =
                                    tab.LookupField != null && childAccess.Read(tab.LookupField)
                                        ? tab.LookupField
                                        : null,
                                LinkColumn =
                                    tab.LinkColumn != null && childAccess.Read(tab.LinkColumn)
                                        ? tab.LinkColumn
                                        : null,
                            }
                        );
                    }
                var uid = int.Parse(ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
                var canUpdate =
                    ctx.User.IsInRole("Admin")
                    || await db.Grants.AnyAsync(g =>
                        g.UserId == uid
                        && g.ConnectionId == page.ConnectionId
                        && g.Table == page.Table
                        && g.Update
                        && g.Read
                    );
                return new
                {
                    page = Definition(page) with
                    {
                        Tabs = tabs,
                        LinkColumn = mainAccess.Read(page.LinkColumn) ? page.LinkColumn : "",
                    },
                    record = records.Rows[0],
                    columns = records.Columns.Concat(records.JoinedColumns),
                    fields = await FieldAccess.VisibleFields(
                        db,
                        ctx,
                        page.ConnectionId,
                        page.Table,
                        layout.Fields,
                        await service.Columns(c, page.Table)
                    ),
                    canUpdate = canUpdate
                        && records.Columns.Any(col =>
                            col.CanWrite != false
                            && !col.Generated
                            && !col.AutoIncrement
                            && !col.PrimaryKey
                        ),
                };
            }
        );
        api.MapGet(
            "/{id:int}/tabs/{tabId}/records",
            async (
                int id,
                string tabId,
                string key,
                int? page,
                int? size,
                string? search,
                string? sort,
                bool? descending,
                AppDb db,
                DatabaseService service,
                HttpContext ctx
            ) =>
            {
                var configPage =
                    await db.Pages.FindAsync(id) ?? throw new ApiError(404, "Page not found.");
                var config = await Access(
                    db,
                    ctx,
                    configPage.ConnectionId,
                    configPage.Table,
                    "read"
                );
                var tab =
                    Definition(configPage).Tabs.SingleOrDefault(t => t.Id == tabId)
                    ?? throw new ApiError(404, "Related tab not found.");
                await Access(db, ctx, configPage.ConnectionId, tab.Table, "read");
                tab = await ResolveTab(db, configPage.ConnectionId, configPage.Table, tab);
                if (search?.Length > 200)
                    throw new ApiError(400, "Search is limited to 200 characters.");
                await using var c = await service.Open(config);
                await Sumups.RepairIfPending(db, service, c, configPage.ConnectionId);
                key = await FieldAccess.DecodePageKey(
                    db,
                    ctx,
                    configPage.ConnectionId,
                    configPage.Table,
                    key
                );
                var parent = await service.PageRecord(c, configPage.Table, key);
                if (!parent.Columns.Any(col => col.Name == tab.ParentColumn))
                    throw new ApiError(400, "Parent relationship column no longer exists.");
                var value = parent.Rows[0].Values.GetValueOrDefault(tab.ParentColumn);
                var layout = await Layout(db, configPage.ConnectionId, tab.Table);
                var access = await FieldAccess.For(db, ctx, configPage.ConnectionId, tab.Table);
                var childColumns = await service.Columns(c, tab.Table);
                var safeFields = await FieldAccess.VisibleFields(
                    db,
                    ctx,
                    configPage.ConnectionId,
                    tab.Table,
                    layout.Fields,
                    childColumns
                );
                var blocked = layout
                    .Fields.Where(f => !safeFields.Any(v => v.Name == f.Name))
                    .Select(f => f.Name)
                    .ToHashSet();
                var readable = childColumns
                    .Where(col => access.Read(col.Name) && !blocked.Contains(col.Name))
                    .Select(col => col.Name)
                    .ToHashSet();
                var result = await service.List(
                    c,
                    tab.Table,
                    page ?? 1,
                    size ?? 25,
                    sort,
                    descending ?? false,
                    search,
                    layout.View,
                    layout.Fields,
                    new ListFilter(tab.RelatedColumn, "eq", DatabaseService.KeyText(value)),
                    value == null,
                    readable,
                    string.IsNullOrEmpty(search)
                        ? []
                        : await FieldAccess.SearchLookups(
                            db,
                            ctx,
                            configPage.ConnectionId,
                            safeFields
                        )
                );
                await RecordPresentation.Decorate(
                    db,
                    ctx,
                    configPage.ConnectionId,
                    tab.Table,
                    c,
                    service,
                    layout.Fields,
                    result
                );
                var available = result
                    .Columns.Concat(result.JoinedColumns)
                    .Select(col => col.Name)
                    .ToHashSet();
                return new
                {
                    connectionId = configPage.ConnectionId,
                    canUpdate = result.HasPrimaryKey
                        && result.Columns.Any(col =>
                            col.CanWrite != false
                            && !col.PrimaryKey
                            && !col.Generated
                            && !col.AutoIncrement
                        )
                        && await CanUpdate(db, ctx, configPage.ConnectionId, tab.Table),
                    canDelete = result.HasPrimaryKey
                        && await CanDelete(db, ctx, configPage.ConnectionId, tab.Table),
                    lockedFields = new[] { tab.RelatedColumn }.Where(access.Read),
                    canCreate = !string.IsNullOrEmpty(tab.LookupField)
                        && value != null
                        && result.HasPrimaryKey
                        && childColumns.Any(col =>
                            col.Name == tab.RelatedColumn && !col.AutoIncrement && !col.Generated
                        )
                        && await CanCreate(db, ctx, configPage.ConnectionId, tab.Table),
                    data = result,
                    fields = safeFields,
                    visibleColumns = tab.Columns.Where(available.Contains),
                    targetPageId = tab.TargetPageId,
                    linkColumn = tab.LinkColumn != null && available.Contains(tab.LinkColumn)
                        ? tab.LinkColumn
                        : null,
                };
            }
        );
        foreach (var operation in new[] { "update", "delete" })
        {
            api.MapPost(
                "/{id:int}/tabs/{tabId}/" + operation,
                async (
                    int id,
                    string tabId,
                    RelatedUpdateInput input,
                    AppDb db,
                    DatabaseService service,
                    HttpContext ctx
                ) =>
                {
                    var page =
                        await db.Pages.FindAsync(id) ?? throw new ApiError(404, "Page not found.");
                    var config = await Access(db, ctx, page.ConnectionId, page.Table, "read");
                    var tab =
                        Definition(page).Tabs.SingleOrDefault(t => t.Id == tabId)
                        ?? throw new ApiError(404, "Related tab not found.");
                    await Access(db, ctx, page.ConnectionId, tab.Table, operation);
                    await using var c = await service.Open(config);
                    await using var gate = await SumupGate.Enter(c);
                    var plans = await Sumups.Ready(db, service, c, page.ConnectionId);
                    tab = await ResolveTab(db, page.ConnectionId, page.Table, tab);
                    if (input.Mutation?.Values == null)
                        throw new ApiError(400, "Record values required.");
                    input = input with
                    {
                        ParentKey = await FieldAccess.DecodePageKey(
                            db,
                            ctx,
                            page.ConnectionId,
                            page.Table,
                            input.ParentKey
                        ),
                        Mutation = await FieldAccess.DecodeMutation(
                            db,
                            ctx,
                            page.ConnectionId,
                            tab.Table,
                            input.Mutation,
                            operation
                        ),
                    };
                    if (
                        operation == "update"
                        && input.Mutation.Values.ContainsKey(tab.RelatedColumn)
                    )
                        throw new ApiError(
                            400,
                            "The parent relation is locked in this related list."
                        );
                    var validated = await RecordWrites.Validate(
                        db,
                        ctx,
                        page.ConnectionId,
                        tab.Table,
                        input.Mutation,
                        operation,
                        service,
                        c
                    );
                    await service.Mutate(
                        c,
                        tab.Table,
                        input.Mutation,
                        operation,
                        validated.Fields,
                        async tx =>
                        {
                            var parent = await service.PageRecord(
                                c,
                                page.Table,
                                input.ParentKey,
                                tx
                            );
                            var parentValue = parent
                                .Rows[0]
                                .Values.GetValueOrDefault(tab.ParentColumn);
                            if (parentValue == null)
                                throw new ApiError(404, "This parent has no related record key.");
                            var child = await service.PageRecord(
                                c,
                                tab.Table,
                                JsonSerializer.Serialize(input.Mutation.Key),
                                tx
                            );
                            await service.RequireRelated(
                                c,
                                tx,
                                tab.Table,
                                child.Columns,
                                child.Rows[0],
                                tab.RelatedColumn,
                                parentValue
                            );
                        },
                        plans,
                        validated.FieldSetRules,
                        validated.DataTypeKeys
                    );
                    db.Audit.Add(
                        new()
                        {
                            Actor = ctx.User.Identity!.Name!,
                            Action = operation,
                            Resource = $"{page.ConnectionId}/{tab.Table}",
                        }
                    );
                    await db.SaveChangesAsync();
                    return Results.NoContent();
                }
            );
        }
        foreach (var preview in new[] { true, false })
        {
            var isPreview = preview;
            api.MapPost(
                "/{id:int}/tabs/{tabId}/" + (preview ? "create-preview" : "create"),
                async (
                    int id,
                    string tabId,
                    RelatedCreateInput input,
                    AppDb db,
                    DatabaseService service,
                    HttpContext ctx
                ) =>
                {
                    var page =
                        await db.Pages.FindAsync(id) ?? throw new ApiError(404, "Page not found.");
                    var config = await Access(db, ctx, page.ConnectionId, page.Table, "read");
                    var tab =
                        Definition(page).Tabs.SingleOrDefault(t => t.Id == tabId)
                        ?? throw new ApiError(404, "Related tab not found.");
                    await Access(db, ctx, page.ConnectionId, tab.Table, "create");
                    if (string.IsNullOrEmpty(tab.LookupField))
                        throw new ApiError(
                            400,
                            "Select a layout lookup relation for this tab before creating records."
                        );
                    tab = await ResolveTab(db, page.ConnectionId, page.Table, tab);
                    await using var c = await service.Open(config);
                    await using var gate = await SumupGate.Enter(c);
                    var plans = await Sumups.Ready(db, service, c, page.ConnectionId);
                    var layout = await Layout(db, page.ConnectionId, tab.Table);
                    var field = layout.Fields.Single(f => f.Name == tab.LookupField);
                    var columns = await service.Columns(c, tab.Table);
                    if (
                        !columns.Any(col => col.PrimaryKey)
                        || !columns.Any(col =>
                            col.Name == field.Name && !col.Generated && !col.AutoIncrement
                        )
                    )
                        throw new ApiError(
                            400,
                            "The related table and lookup column must allow record creation."
                        );
                    await service.ValidateLookup(
                        c,
                        field.Lookup!,
                        columns.Single(col => col.Name == field.Name)
                    );
                    await service.ValidateCopyMappings(c, layout.Fields, columns);
                    async Task<JsonElement> ParentKey(MySqlTransaction? tx = null)
                    {
                        var parent = await service.PageRecord(c, page.Table, input.Key, tx);
                        var value = parent.Rows[0].Values.GetValueOrDefault(tab.ParentColumn);
                        if (value == null)
                            throw new ApiError(400, "The parent's lookup key is empty.");
                        return JsonSerializer.SerializeToElement(value);
                    }
                    input = input with
                    {
                        Key = await FieldAccess.DecodePageKey(
                            db,
                            ctx,
                            page.ConnectionId,
                            page.Table,
                            input.Key
                        ),
                    };
                    var access = await FieldAccess.For(db, ctx, page.ConnectionId, tab.Table);
                    if (!isPreview)
                        access.RequireWrite((input.Values ?? []).Keys);
                    var key = await ParentKey();
                    var values = new Dictionary<string, JsonElement>(input.Values ?? []);
                    // Parent context is authoritative; ignore any submitted replacement key.
                    values[field.Name] = key;
                    if (isPreview)
                    {
                        var defaults = await RecordWrites.Preview(
                            db,
                            ctx,
                            page.ConnectionId,
                            tab.Table,
                            service,
                            c,
                            new() { [field.Name] = key }
                        );
                        var locked = (field.Lookup!.CopyMappings ?? [])
                            .Where(m => !m.Editable)
                            .Select(m => m.DestinationColumn)
                            .Append(field.Name)
                            .ToList();
                        var safeFields = await FieldAccess.VisibleFields(
                            db,
                            ctx,
                            page.ConnectionId,
                            tab.Table,
                            layout.Fields,
                            columns
                        );
                        var virtualColumns = await RecordPresentation.PopulateJoins(
                            db,
                            ctx,
                            page.ConnectionId,
                            c,
                            service,
                            safeFields,
                            columns,
                            []
                        );
                        return Results.Ok(
                            new
                            {
                                connectionId = page.ConnectionId,
                                table = tab.Table,
                                columns = columns
                                    .Where(col => access.Read(col.Name))
                                    .Select(access.Column)
                                    .Concat(virtualColumns),
                                fields = safeFields,
                                values = defaults.Where(v => access.Read(v.Key)).ToDictionary(),
                                lockedFields = locked.Where(access.Read),
                            }
                        );
                    }
                    var mutation = new RowMutation(values, null, null);
                    var validated = await RecordWrites.Validate(
                        db,
                        ctx,
                        page.ConnectionId,
                        tab.Table,
                        mutation,
                        "create",
                        service,
                        c
                    );
                    await service.Mutate(
                        c,
                        tab.Table,
                        mutation,
                        "create",
                        validated.Fields,
                        async tx =>
                        {
                            // Lock/reload the actual parent inside the insertion transaction; copy values are then re-read there too.
                            values[field.Name] = await ParentKey(tx);
                        },
                        sumups: plans,
                        fieldSetRules: validated.FieldSetRules,
                        dataTypeKeys: validated.DataTypeKeys
                    );
                    db.Audit.Add(
                        new()
                        {
                            Actor = ctx.User.Identity!.Name!,
                            Action = "create",
                            Resource = $"{page.ConnectionId}/{tab.Table}",
                        }
                    );
                    await db.SaveChangesAsync();
                    return Results.NoContent();
                }
            );
        }
    }
}

public record RelatedCreateInput(string Key, Dictionary<string, JsonElement>? Values = null);

public record RelatedUpdateInput(string ParentKey, RowMutation Mutation);

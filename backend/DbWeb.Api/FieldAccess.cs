using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace DbWeb.Api;

public class FieldPolicy
{
    public int Id
    {
        get; set;
    }
    public int UserId
    {
        get; set;
    }
    public int ConnectionId
    {
        get; set;
    }
    public string Table { get; set; } = "";
    public string FieldsJson { get; set; } = "{}";
}

public sealed class FieldAccess(Dictionary<string, string>? levels)
{
    public bool Explicit => levels != null;

    public bool Read(string name) =>
        !DataTypeColumn.Is(name)
        && (levels == null || levels.GetValueOrDefault(name) is "read" or "write");

    public bool Write(string name) =>
        !DataTypeColumn.Is(name) && (levels == null || levels.GetValueOrDefault(name) == "write");

    public void RequireRead(IEnumerable<string> names)
    {
        if (names.Any(n => !Read(n)))
            throw new ApiError(403, "You do not have read access to the requested fields.");
    }

    public void RequireWrite(IEnumerable<string> names)
    {
        if (names.Any(n => !Write(n)))
            throw new ApiError(403, "You do not have write access to the requested fields.");
    }

    public ColumnInfo Column(ColumnInfo c) =>
        c with
        {
            CanWrite = Write(c.Name),
            Default = Read(c.Name) ? c.Default : null,
        };

    public static void EnsureCreated(AppDb db) =>
        db.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS "FieldPolicies" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_FieldPolicies" PRIMARY KEY AUTOINCREMENT,
                "UserId" INTEGER NOT NULL, "ConnectionId" INTEGER NOT NULL,
                "Table" TEXT NOT NULL, "FieldsJson" TEXT NOT NULL);
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_FieldPolicies_UserId_ConnectionId_Table"
                ON "FieldPolicies" ("UserId", "ConnectionId", "Table");
            """
        );

    public static async Task<FieldAccess> For(
        AppDb db,
        HttpContext ctx,
        int connection,
        string table
    )
    {
        if (ctx.User.IsInRole("Admin"))
            return new(null);
        var cacheKey = (typeof(FieldAccess), connection, table);
        if (ctx.Items.TryGetValue(cacheKey, out var cached))
            return (FieldAccess)cached!;
        var uid = int.Parse(ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var policy = await db.FieldPolicies.SingleOrDefaultAsync(p =>
            p.UserId == uid && p.ConnectionId == connection && p.Table == table
        );
        var result = new FieldAccess(
            policy == null
                ? null
                : JsonSerializer.Deserialize<Dictionary<string, string>>(policy.FieldsJson) ?? []
        );
        ctx.Items[cacheKey] = result;
        return result;
    }

    public static async Task<FieldAccess> Related(
        AppDb db,
        HttpContext ctx,
        int connection,
        string table,
        IEnumerable<string> names
    )
    {
        await TableAccess.Access(db, ctx, connection, table, "read");
        var access = await For(db, ctx, connection, table);
        access.RequireRead(names);
        return access;
    }

    public static async Task<List<LayoutField>> SearchLookups(
        AppDb db,
        HttpContext ctx,
        int connection,
        List<LayoutField> fields
    )
    {
        var result = new List<LayoutField>();
        foreach (var field in fields.Where(f => f.Widget == "lookup" && f.Lookup != null))
        {
            var lookup = field.Lookup!;
            try
            {
                var access = await Related(
                    db,
                    ctx,
                    connection,
                    lookup.Table,
                    [lookup.KeyColumn, lookup.DisplayColumn]
                );
                result.Add(
                    field with
                    {
                        Lookup = lookup with
                        {
                            SearchColumns = lookup.SearchColumns.Where(access.Read).ToList(),
                        },
                    }
                );
            }
            catch (ApiError e) when (e.Status == 403) { }
        }
        return result;
    }

    public static async Task<LookupConfig> Lookup(
        AppDb db,
        HttpContext ctx,
        int connection,
        LookupConfig lookup,
        bool copy = false
    )
    {
        var names = new[] { lookup.KeyColumn, lookup.DisplayColumn }.Concat(
            lookup.Criteria?.Filters?.Select(f => f.Column) ?? []
        );
        if (copy)
            names = names.Concat(lookup.CopyMappings?.Select(m => m.SourceColumn) ?? []);
        var access = await Related(db, ctx, connection, lookup.Table, names);
        return lookup with
        {
            SearchColumns = lookup.SearchColumns.Where(access.Read).ToList()
        };
    }

    // Calculated fields cannot be used as a side channel for unreadable source values.
    public static async Task<List<LayoutField>> VisibleFields(
        AppDb db,
        HttpContext ctx,
        int connection,
        string table,
        List<LayoutField> fields,
        List<ColumnInfo> columns
    )
    {
        var access = await For(db, ctx, connection, table);
        var result = new List<LayoutField>();
        foreach (var field in fields.Where(f => access.Read(f.Name)))
        {
            try
            {
                if (field.Widget == "formula")
                    access.RequireRead(Formulas.Dependencies(field.Formula, columns, fields));
                if (field.Join is { } join)
                {
                    access.RequireRead([join.SourceColumn]);
                    await Related(
                        db,
                        ctx,
                        connection,
                        join.Table,
                        [join.KeyColumn, join.ValueColumn]
                    );
                }
                if (field.Sumup is { } sumup)
                {
                    var childAccess = await Related(
                        db,
                        ctx,
                        connection,
                        sumup.ChildTable,
                        [sumup.LookupField]
                    );
                    if (!string.IsNullOrEmpty(sumup.SourceField))
                    {
                        childAccess.RequireRead([sumup.SourceField]);
                        var childLayout = await db.Layouts.SingleOrDefaultAsync(l =>
                            l.ConnectionId == connection && l.Table == sumup.ChildTable
                        );
                        var childFields = DatabaseService.LayoutFields(childLayout?.FieldsJson);
                        if (
                            childFields.Find(f => f.Name == sumup.SourceField) is
                            { Widget: "formula" } formula
                        )
                        {
                            // Dependencies need only stored names for parsing; metadata is never returned here.
                            var refs = Formulas.Dependencies(formula.Formula, null, childFields);
                            childAccess.RequireRead(refs);
                        }
                    }
                }
                var safe = field with
                {
                    ReadOnly = field.ReadOnly || !access.Write(field.Name),
                    Required = field.Required && access.Write(field.Name),
                };
                if (field.Lookup is { } lookup)
                {
                    try
                    {
                        safe = safe with
                        {
                            Lookup = await Lookup(db, ctx, connection, lookup)
                        };
                        if (lookup.CopyMappings is { Count: > 0 })
                        {
                            try
                            {
                                await Lookup(db, ctx, connection, lookup, true);
                                access.RequireWrite(
                                    lookup.CopyMappings.Select(m => m.DestinationColumn)
                                );
                            }
                            catch (ApiError e) when (e.Status == 403)
                            {
                                safe = safe with
                                {
                                    ReadOnly = true,
                                    Required = false,
                                    Lookup = safe.Lookup! with
                                    {
                                        CopyMappings = []
                                    },
                                };
                            }
                        }
                    }
                    catch (ApiError e) when (e.Status == 403)
                    {
                        safe = safe with
                        {
                            Widget = "text",
                            Lookup = null,
                            ReadOnly = true,
                            Required = false,
                            CreationDefault = null,
                        };
                    }
                }
                result.Add(safe);
            }
            catch (ApiError e) when (e.Status == 403) { }
        }
        return result;
    }

    public static ListView? PublicView(ListView? view, FieldAccess access) =>
        view == null
            ? null
            : view with
            {
                Sort = view.Sort != null && access.Read(view.Sort) ? view.Sort : null,
                Filters = view.Filters?.Where(f => access.Read(f.Column)).ToList(),
            };

    static IDataProtector Protector(HttpContext ctx, int connection, string table, string kind) =>
        ctx
            .RequestServices.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector(
                "field-record-v1",
                ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)!,
                connection.ToString(),
                table,
                kind
            );

    public static void Present(
        HttpContext ctx,
        int connection,
        string table,
        FieldAccess access,
        HashSet<string> visible,
        RecordPage result
    )
    {
        var keys = result.Columns.Where(c => c.PrimaryKey).Select(c => c.Name).ToList();
        result.HasPrimaryKey = keys.Count > 0;
        var restricted = access.Explicit || result.Columns.Any(col => !visible.Contains(col.Name));
        for (var i = 0; i < result.Rows.Count; i++)
        {
            var row = result.Rows[i];
            if (restricted)
            {
                row = row with
                {
                    KeyToken = Protector(ctx, connection, table, "key")
                        .Protect(
                            JsonSerializer.Serialize(keys.ToDictionary(k => k, k => row.Values[k]))
                        ),
                    Version =
                        "protected:"
                        + Protector(ctx, connection, table, "version").Protect(row.Version),
                };
                result.Rows[i] = row;
            }
            foreach (var name in row.Values.Keys.Where(n => !visible.Contains(n)).ToList())
                row.Values.Remove(name);
            foreach (var name in row.DisplayValues.Keys.Where(n => !visible.Contains(n)).ToList())
                row.DisplayValues.Remove(name);
            foreach (var name in row.JoinedValues.Keys.Where(n => !visible.Contains(n)).ToList())
                row.JoinedValues.Remove(name);
            foreach (
                var name in row.CalculationErrors.Keys.Where(n => !visible.Contains(n)).ToList()
            )
                row.CalculationErrors.Remove(name);
        }
        result.Columns.RemoveAll(c => !visible.Contains(c.Name));
        for (var i = 0; i < result.Columns.Count; i++)
            result.Columns[i] = access.Column(result.Columns[i]);
        result.JoinedColumns.RemoveAll(c => !visible.Contains(c.Name));
    }

    public static async Task<string> DecodePageKey(
        AppDb db,
        HttpContext ctx,
        int connection,
        string table,
        string json
    )
    {
        Dictionary<string, JsonElement>? key;
        try
        {
            key = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
        }
        catch (JsonException)
        {
            throw new ApiError(400, "Invalid record key.");
        }
        return JsonSerializer.Serialize(await DecodeKey(db, ctx, connection, table, key));
    }

    public static async Task<Dictionary<string, JsonElement>?> DecodeKey(
        AppDb db,
        HttpContext ctx,
        int connection,
        string table,
        Dictionary<string, JsonElement>? key
    )
    {
        if (key == null)
            return null;
        if (key.TryGetValue("$record", out var token))
        {
            try
            {
                if (key.Count != 1 || token.ValueKind != JsonValueKind.String)
                    throw new CryptographicException();
                return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                    Protector(ctx, connection, table, "key").Unprotect(token.GetString()!)
                );
            }
            catch (Exception e) when (e is CryptographicException or JsonException)
            {
                throw new ApiError(400, "Invalid record key.");
            }
        }
        (await For(db, ctx, connection, table)).RequireRead(key.Keys);
        return key;
    }

    public static async Task<RowMutation> DecodeMutation(
        AppDb db,
        HttpContext ctx,
        int connection,
        string table,
        RowMutation input,
        string operation
    )
    {
        if (input.Values == null)
            throw new ApiError(400, "Values object required.");
        var access = await For(db, ctx, connection, table);
        if (operation != "delete")
            access.RequireWrite(input.Values.Keys);
        var version = input.Version;
        if (version?.StartsWith("protected:", StringComparison.Ordinal) == true)
        {
            try
            {
                version = Protector(ctx, connection, table, "version").Unprotect(version[10..]);
            }
            catch (CryptographicException)
            {
                throw new ApiError(409, "Invalid record version. Refresh before saving.");
            }
        }
        else if (operation != "create" && access.Explicit)
            throw new ApiError(409, "Permissions changed. Refresh before saving.");
        return input with
        {
            Key = await DecodeKey(db, ctx, connection, table, input.Key),
            Version = version,
        };
    }
}

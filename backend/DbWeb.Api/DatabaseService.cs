using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using MySqlConnector;

namespace DbWeb.Api;

public record ColumnInfo(
    string Name,
    string Type,
    bool Nullable,
    bool PrimaryKey,
    bool Generated,
    bool AutoIncrement,
    string? Default,
    long? Length = null,
    bool? CanWrite = null
);

public partial class DatabaseService(IDataProtectionProvider protection)
{
    public static string Quote(string name) => "`" + name.Replace("`", "``") + "`";

    public async Task<MySqlConnection> Open(DatabaseConnection c)
    {
        var cs = new MySqlConnectionStringBuilder
        {
            Server = c.Host,
            Port = c.Port,
            Database = c.Database,
            UserID = c.Username,
            Password = protection
                .CreateProtector("database-passwords")
                .Unprotect(c.ProtectedPassword),
            SslMode = c.VerifyTls ? MySqlSslMode.VerifyFull : MySqlSslMode.Disabled,
            AllowLoadLocalInfile = false,
            AllowUserVariables = false,
            ConnectionTimeout = 8,
            DefaultCommandTimeout = 20,
        };
        var db = new MySqlConnection(cs.ConnectionString);
        try
        {
            await db.OpenAsync();
            return db;
        }
        catch
        {
            await db.DisposeAsync();
            throw;
        }
    }

    public async Task<List<string>> Tables(MySqlConnection db, MySqlTransaction? transaction = null)
    {
        await using var cmd = new MySqlCommand(
            "SELECT TABLE_NAME FROM information_schema.TABLES WHERE TABLE_SCHEMA=DATABASE() AND TABLE_TYPE='BASE TABLE' ORDER BY TABLE_NAME",
            db
        );
        cmd.Transaction = transaction;
        await using var r = await cmd.ExecuteReaderAsync();
        var a = new List<string>();
        while (await r.ReadAsync())
            a.Add(r.GetString(0));
        return a;
    }

    public async Task<List<ColumnInfo>> Columns(
        MySqlConnection db,
        string table,
        MySqlTransaction? transaction = null
    )
    {
        if (!(await Tables(db, transaction)).Contains(table, StringComparer.Ordinal))
            throw new ApiError(404, "Table not found.");
        await using var cmd = new MySqlCommand(
            "SELECT COLUMN_NAME,DATA_TYPE,IS_NULLABLE,COLUMN_KEY,EXTRA,COLUMN_DEFAULT,CHARACTER_MAXIMUM_LENGTH FROM information_schema.COLUMNS WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME=@table ORDER BY ORDINAL_POSITION",
            db
        );
        cmd.Parameters.AddWithValue("@table", table);
        cmd.Transaction = transaction;
        await using var r = await cmd.ExecuteReaderAsync();
        var a = new List<ColumnInfo>();
        while (await r.ReadAsync())
            a.Add(
                new(
                    r.GetString(0),
                    r.GetString(1),
                    r.GetString(2) == "YES",
                    r.GetString(3) == "PRI",
                    r.GetString(4).Contains("GENERATED"),
                    r.GetString(4).Contains("auto_increment"),
                    r.IsDBNull(5) ? null : r.GetString(5),
                    r.IsDBNull(6) ? null : r.GetInt64(6)
                )
            );
        return a;
    }

    static object? Wire(object value) =>
        value switch
        {
            DBNull => null,
            byte[] bytes => Convert.ToBase64String(bytes),
            DateTime d => d.ToString("yyyy-MM-ddTHH:mm:ss.ffffff", CultureInfo.InvariantCulture),
            long or ulong or decimal => Convert.ToString(value, CultureInfo.InvariantCulture),
            _ => value,
        };

    public static string Version(Dictionary<string, object?> row) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(row))));

    static async Task<List<Dictionary<string, object?>>> Read(MySqlCommand cmd)
    {
        await using var r = await cmd.ExecuteReaderAsync();
        var rows = new List<Dictionary<string, object?>>();
        while (await r.ReadAsync())
        {
            var row = new Dictionary<string, object?>();
            for (int i = 0; i < r.FieldCount; i++)
                row[r.GetName(i)] =
                    r.GetValue(i) is DateTime date
                    && r.GetDataTypeName(i).Equals("DATE", StringComparison.OrdinalIgnoreCase)
                        ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                        : Wire(r.GetValue(i));
            rows.Add(row);
        }
        return rows;
    }

    public async Task<RecordPage> List(
        MySqlConnection db,
        string table,
        int page,
        int size,
        string? sort,
        bool descending,
        string? search,
        ListView? view = null,
        List<LayoutField>? fields = null,
        ListFilter? relation = null,
        bool emptyRelation = false,
        HashSet<string>? readable = null,
        List<LayoutField>? lookupSearchFields = null
    )
    {
        var cols = await Columns(db, table);
        if (!string.IsNullOrEmpty(sort) && readable != null && !readable.Contains(sort))
            throw new ApiError(403, "You cannot sort by a restricted field.");
        if (readable != null && !string.IsNullOrEmpty(view?.Sort) && !readable.Contains(view.Sort))
            view = view with
            {
                Sort = null
            };
        page = Math.Max(1, page);
        size = Math.Clamp(size, 1, 100);
        if (string.IsNullOrEmpty(sort))
        {
            sort = string.IsNullOrEmpty(view?.Sort) ? null : view.Sort;
            descending = view?.Descending ?? descending;
        }
        sort ??=
            cols.FirstOrDefault(x =>
                (readable == null || readable.Contains(x.Name)) && x.PrimaryKey
            )?.Name
            ?? cols.FirstOrDefault(x => readable == null || readable.Contains(x.Name))?.Name
            ?? cols[0].Name;
        if (!cols.Any(x => x.Name == sort))
            throw new ApiError(400, "Unknown sort column.");
        await using var cmd = db.CreateCommand();
        var searchable = cols.Where(x =>
                (readable == null || readable.Contains(x.Name))
                && new[]
                {
                    "varchar",
                    "char",
                    "tinytext",
                    "text",
                    "mediumtext",
                    "longtext",
                }.Contains(x.Type)
            )
            .ToList();
        var predicates = new List<string>();
        if (emptyRelation)
            predicates.Add("1=0");
        else if (relation != null)
        {
            var column =
                cols.Find(c => c.Name == relation.Column)
                ?? throw new ApiError(400, "Related column no longer exists.");
            predicates.Add(
                $"{Quote(column.Name)} = {FilterOperand(cmd, "@relation", column, relation.Value!)}"
            );
        }
        var viewPredicate = ViewPredicate(cmd, view, cols);
        if (viewPredicate != "")
            predicates.Add(viewPredicate);
        if (!string.IsNullOrEmpty(search))
        {
            var matches = searchable.Select(x => $"{Quote(x.Name)} LIKE @search").ToList();
            // Translate friendly labels to exact stored keys before counting/paging.
            // Labels are literal, case-insensitive substrings; SQL only receives key parameters.
            foreach (var field in fields ?? [])
            {
                if (
                    field.Widget != "dropdown"
                    || field.Options == null
                    || !searchable.Any(c => c.Name == field.Name)
                )
                    continue;
                var keys = new List<string>();
                foreach (
                    var option in field.Options.Where(o =>
                        o.Display.Contains(search, StringComparison.OrdinalIgnoreCase)
                    )
                )
                {
                    var parameter = "@dropdownSearch" + cmd.Parameters.Count;
                    cmd.Parameters.AddWithValue(parameter, option.Key);
                    keys.Add(parameter);
                }
                if (keys.Count > 0)
                    matches.Add($"BINARY {Quote(field.Name)} IN ({string.Join(", ", keys)})");
            }
            // Only permission-approved lookup definitions reach this query. EXISTS keeps
            // counts and pagination exact without materializing every matching related key.
            foreach (var field in lookupSearchFields ?? [])
            {
                if (
                    field.Lookup is not { } lookup
                    || (readable != null && !readable.Contains(field.Name))
                )
                    continue;
                await ValidateLookup(db, lookup, cols.Single(c => c.Name == field.Name));
                var aliasName = "lookup_search_" + matches.Count;
                while (aliasName.Equals(table, StringComparison.OrdinalIgnoreCase))
                    aliasName += "_";
                var alias = Quote(aliasName);
                var names = lookup
                    .SearchColumns.Append(lookup.KeyColumn)
                    .Append(lookup.DisplayColumn)
                    .Distinct();
                var terms = names.Select(n =>
                    $"CONVERT({alias}.{Quote(n)} USING utf8mb4) COLLATE utf8mb4_unicode_ci LIKE @lookupSearch ESCAPE '!'"
                );
                matches.Add(
                    $"EXISTS (SELECT 1 FROM {Quote(lookup.Table)} AS {alias} WHERE {alias}.{Quote(lookup.KeyColumn)} = {Quote(table)}.{Quote(field.Name)} AND ({string.Join(" OR ", terms)}))"
                );
            }
            cmd.Parameters.AddWithValue(
                "@lookupSearch",
                "%" + search.Replace("!", "!!").Replace("%", "!%").Replace("_", "!_") + "%"
            );
            predicates.Add(matches.Count == 0 ? "1=0" : "(" + string.Join(" OR ", matches) + ")");
            cmd.Parameters.AddWithValue("@search", "%" + search + "%");
        }
        var where = predicates.Count == 0 ? "" : " WHERE " + string.Join(" AND ", predicates);
        var order = $"{Quote(sort)} {(descending ? "DESC" : "ASC")}";
        foreach (var key in cols.Where(c => c.PrimaryKey && c.Name != sort))
            order += $", {Quote(key.Name)} ASC";
        cmd.CommandText = $"SELECT COUNT(*) FROM {Quote(table)}{where}";
        var total = Convert.ToInt64(await cmd.ExecuteScalarAsync());
        cmd.CommandText =
            $"SELECT * FROM {Quote(table)}{where} ORDER BY {order} LIMIT @size OFFSET @offset";
        cmd.Parameters.AddWithValue("@size", size);
        cmd.Parameters.AddWithValue("@offset", checked((long)(page - 1) * size));
        var rows = await Read(cmd);
        return new(total, page, size, cols, rows.Select(x => new RecordRow(x, Version(x))).ToList())
        {
            Sort = readable == null || readable.Contains(sort) ? sort : null,
            HasPrimaryKey = cols.Any(c => c.PrimaryKey),
            Descending = descending,
        };
    }

    static object Value(JsonElement e, ColumnInfo col)
    {
        var emptyTextOrNumber =
            e.ValueKind == JsonValueKind.String
            && e.GetString() == ""
            && col.Type
                is "char"
                    or "varchar"
                    or "tinytext"
                    or "text"
                    or "mediumtext"
                    or "longtext"
                    or "tinyint"
                    or "smallint"
                    or "mediumint"
                    or "int"
                    or "integer"
                    or "bigint"
                    or "decimal"
                    or "numeric"
                    or "dec"
                    or "fixed"
                    or "float"
                    or "double"
                    or "real"
                    or "bit"
                    or "year";
        if (emptyTextOrNumber)
        {
            if (!col.Nullable)
                throw new ApiError(400, $"{col.Name} cannot be null.");
            return DBNull.Value;
        }
        if (e.ValueKind == JsonValueKind.Null)
        {
            if (!col.Nullable)
                throw new ApiError(400, $"{col.Name} cannot be null.");
            return DBNull.Value;
        }
        if (e.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            throw new ApiError(400, "Field values must be scalar.");
        if (col.Type is "date" or "datetime" or "timestamp")
        {
            var formats =
                col.Type == "date"
                    ? new[] { "yyyy-MM-dd" }
                    : new[]
                    {
                        "yyyy-MM-dd",
                        "yyyy-MM-dd'T'HH:mm",
                        "yyyy-MM-dd'T'HH:mm:ss",
                        "yyyy-MM-dd'T'HH:mm:ss.FFFFFF",
                        "yyyy-MM-dd HH:mm:ss",
                        "yyyy-MM-dd HH:mm:ss.FFFFFF",
                    };
            if (
                e.ValueKind != JsonValueKind.String
                || !DateTime.TryParseExact(
                    e.GetString(),
                    formats,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var date
                )
                || date.Year < 1000
            )
                throw new ApiError(
                    400,
                    $"{col.Name} requires a valid {(col.Type == "date" ? "date (YYYY-MM-DD)" : "date/time without a timezone offset")}."
                );
            return DateTime.SpecifyKind(date, DateTimeKind.Unspecified);
        }
        if (col.Type.Contains("blob") || col.Type is "binary" or "varbinary")
            return Convert.FromBase64String(e.GetString() ?? "");
        return e.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => e.ToString(),
        };
    }

    public async Task Mutate(
        MySqlConnection db,
        string table,
        RowMutation input,
        string operation,
        List<LayoutField>? fields = null,
        Func<MySqlTransaction, Task>? prepare = null,
        List<SumupPlan>? sumups = null,
        List<FieldSetRule>? fieldSetRules = null
    )
    {
        if (input.Values == null)
            throw new ApiError(400, "Values object required.");
        var cols = await Columns(db, table);
        if (operation == "update" && input.Values.Keys.Any(DataTypeColumn.Is))
            throw new ApiError(400, "datatype is managed by the backend and cannot be edited.");
        if (operation == "create")
            ApplyCreationDefaults(fields ?? [], cols, input.Values);
        var plans = sumups ?? [];
        var managed = (fields ?? []).Where(f => f.Widget == "sumup").ToList();
        if (input.Values.Keys.Any(n => managed.Any(f => f.Name == n)))
            throw new ApiError(
                400,
                "Sum-up fields are managed by the backend and cannot be edited."
            );
        if (operation == "create")
            foreach (var field in managed)
                input.Values[field.Name] = JsonSerializer.SerializeToElement(0);
        if (!cols.Any(x => x.PrimaryKey))
            throw new ApiError(400, "Tables without a primary key are read-only.");
        foreach (var field in input.Values)
        {
            var col =
                cols.Find(x => x.Name == field.Key) ?? throw new ApiError(400, "Unknown column.");
            if (col.Generated || col.AutoIncrement || (operation == "update" && col.PrimaryKey))
                throw new ApiError(400, $"{col.Name} is read-only.");
        }
        if (operation == "update" && input.Values.Count == 0)
            throw new ApiError(400, "Provide at least one field.");
        if (operation != "delete")
            await ValidateCopyMappings(db, fields ?? [], cols);
        await using var tx = await db.BeginTransactionAsync();
        if (prepare != null)
            await prepare(tx);
        await using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        string where = "";
        Dictionary<string, object?>? current = null;
        if (operation != "create")
        {
            var keys = cols.Where(x => x.PrimaryKey).ToList();
            if (keys.Count == 0)
                throw new ApiError(400, "Tables without a primary key are read-only.");
            if (
                input.Key == null
                || input.Key.Count != keys.Count
                || keys.Any(x => !input.Key.ContainsKey(x.Name))
            )
                throw new ApiError(400, "Complete primary key required.");
            where = string.Join(" AND ", keys.Select((x, i) => $"{Quote(x.Name)}=@k{i}"));
            for (int i = 0; i < keys.Count; i++)
                cmd.Parameters.AddWithValue("@k" + i, Value(input.Key[keys[i].Name], keys[i]));
            cmd.CommandText = $"SELECT * FROM {Quote(table)} WHERE {where} FOR UPDATE";
            var existing = await Read(cmd);
            if (existing.Count == 0)
                throw new ApiError(404, "Record no longer exists.");
            current = existing[0];
            if (input.Version != Version(existing[0]))
                throw new ApiError(409, "This record changed. Refresh before saving.");
        }
        if (operation != "delete")
        {
            foreach (
                var field in (fields ?? []).Where(f => f.Widget == "lookup" && f.Lookup != null)
            )
            {
                var mappings = field.Lookup!.CopyMappings ?? [];
                if (input.Values.TryGetValue(field.Name, out var selectedKey))
                {
                    foreach (
                        var copied in await CopyLookupValues(db, field.Lookup, selectedKey, tx)
                    )
                        if (
                            !mappings.Single(m => m.DestinationColumn == copied.Key).Editable
                            || !input.Values.ContainsKey(copied.Key)
                        )
                            input.Values[copied.Key] = copied.Value;
                }
                else if (
                    mappings.Any(m => !m.Editable && input.Values.ContainsKey(m.DestinationColumn))
                )
                    throw new ApiError(400, "This copied field is locked by its lookup mapping.");
            }
            LayoutRules.ValidateDropdownValues(fields ?? [], input.Values);
            LayoutRules.ValidateEmailValues(fields ?? [], input.Values);
            LayoutRules.ValidateMaskValues(fields ?? [], input.Values);
            LayoutRules.ValidateRequiredValues(fields ?? [], input.Values, current);
            FieldSetRules.Apply(
                fieldSetRules ?? [],
                fields ?? [],
                cols,
                current,
                input.Values,
                (value, column) => _ = Value(value, column)
            );
            LayoutRules.ValidateDropdownValues(fields ?? [], input.Values);
            LayoutRules.ValidateEmailValues(fields ?? [], input.Values);
            LayoutRules.ValidateMaskValues(fields ?? [], input.Values);
            LayoutRules.ValidateRequiredValues(fields ?? [], input.Values, current);
        }
        await CheckSumupParent(db, tx, plans, table, current, input, operation);
        var names = input.Values.Keys.ToList();
        for (int i = 0; i < names.Count; i++)
            cmd.Parameters.AddWithValue(
                "@v" + i,
                Value(input.Values[names[i]], cols.First(x => x.Name == names[i]))
            );
        cmd.CommandText = operation switch
        {
            "create" =>
                $"INSERT INTO {Quote(table)} ({string.Join(",", names.Select(Quote))}) VALUES ({string.Join(",", names.Select((_, i) => "@v" + i))})",
            "update" =>
                $"UPDATE {Quote(table)} SET {string.Join(",", names.Select((n, i) => $"{Quote(n)}=@v{i}"))} WHERE {where}",
            _ => $"DELETE FROM {Quote(table)} WHERE {where}",
        };
        Dictionary<string, object?>? updated = null;
        if (plans.Count > 0 && operation == "create")
        {
            cmd.CommandText += " RETURNING *";
            updated = (await Read(cmd)).Single();
        }
        else
        {
            await cmd.ExecuteNonQueryAsync();
            if (plans.Count > 0 && operation == "update")
            {
                cmd.CommandText = $"SELECT * FROM {Quote(table)} WHERE {where}";
                updated = (await Read(cmd)).Single();
            }
        }
        if (plans.Count > 0)
            await ApplySumupDeltas(db, tx, plans, table, current, updated);
        await tx.CommitAsync();
    }
}

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using static DbWeb.Api.DatabaseService;

namespace DbWeb.Api;

public record NewTable(string Name, string PrimaryKey = "id");

public record SchemaColumnInput(
    string Name,
    string Type,
    bool Nullable = true,
    int Length = 255,
    int Precision = 18,
    int Scale = 2,
    string? Version = null,
    string? RelatedTable = null,
    string? RelatedKey = null,
    string? DisplayColumn = null
);

public record SchemaColumn(
    string Name,
    string Type,
    string SqlType,
    bool Nullable,
    bool PrimaryKey,
    bool Generated,
    bool AutoIncrement,
    long? Length,
    int? Precision,
    int? Scale,
    string? Default,
    string? Charset,
    string? Collation,
    string Extra,
    string Comment,
    string? RelatedTable,
    string? RelatedKey,
    bool UniqueKey,
    bool Referenced,
    string? EditBlocked
);

public record TableSchema(string Version, List<SchemaColumn> Columns);

public static class SchemaDesigner
{
    static readonly Regex NamePattern = new(
        "^[A-Za-z_][A-Za-z0-9_]{0,63}$",
        RegexOptions.CultureInvariant
    );

    static void Name(string name)
    {
        if (name == null || !NamePattern.IsMatch(name))
            throw new ApiError(
                400,
                "Use 1–64 letters, digits or underscores; start with a letter or underscore."
            );
    }

    static string Literal(string text) => "'" + text.Replace("\\", "\\\\").Replace("'", "''") + "'";

    static string ScalarType(SchemaColumnInput input) =>
        input.Type switch
        {
            "text" when input.Length is >= 1 and <= 16383 => $"VARCHAR({input.Length})",
            "decimal"
                when input.Precision is >= 1 and <= 65
                    && input.Scale >= 0
                    && input.Scale <= 30
                    && input.Scale <= input.Precision =>
                $"DECIMAL({input.Precision},{input.Scale})",
            "integer" => "BIGINT",
            "boolean" => "TINYINT(1)",
            "longtext" => "LONGTEXT",
            "date" => "DATE",
            "datetime" => "DATETIME(6)",
            _ => throw new ApiError(
                400,
                "Choose a supported type. Text length: 1–16383. Decimal precision: 1–65; decimal places: 0–30, no greater than precision."
            ),
        };

    static async Task Strict(MySqlConnection c)
    {
        using var cmd = new MySqlCommand(
            "SET SESSION sql_mode='STRICT_ALL_TABLES,NO_ZERO_DATE,NO_ZERO_IN_DATE,ERROR_FOR_DIVISION_BY_ZERO,NO_ENGINE_SUBSTITUTION', lock_wait_timeout=15",
            c
        );
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task<TableSchema> Inspect(
        MySqlConnection c,
        DatabaseService service,
        string table
    )
    {
        await service.Columns(c, table); // Exact base-table existence, not a view.
        using var cmd = new MySqlCommand(
            """
            SELECT c.COLUMN_NAME,c.DATA_TYPE,c.COLUMN_TYPE,c.IS_NULLABLE,c.COLUMN_KEY,c.EXTRA,
                c.CHARACTER_MAXIMUM_LENGTH,c.NUMERIC_PRECISION,c.NUMERIC_SCALE,c.COLUMN_DEFAULT,
                c.CHARACTER_SET_NAME,c.COLLATION_NAME,c.COLUMN_COMMENT,
                (SELECT k.REFERENCED_TABLE_NAME FROM information_schema.KEY_COLUMN_USAGE k WHERE k.TABLE_SCHEMA=c.TABLE_SCHEMA AND k.TABLE_NAME=c.TABLE_NAME AND k.COLUMN_NAME=c.COLUMN_NAME AND k.REFERENCED_TABLE_NAME IS NOT NULL LIMIT 1),
                (SELECT k.REFERENCED_COLUMN_NAME FROM information_schema.KEY_COLUMN_USAGE k WHERE k.TABLE_SCHEMA=c.TABLE_SCHEMA AND k.TABLE_NAME=c.TABLE_NAME AND k.COLUMN_NAME=c.COLUMN_NAME AND k.REFERENCED_TABLE_NAME IS NOT NULL LIMIT 1),
                EXISTS(SELECT 1 FROM information_schema.STATISTICS s WHERE s.TABLE_SCHEMA=c.TABLE_SCHEMA AND s.TABLE_NAME=c.TABLE_NAME AND s.COLUMN_NAME=c.COLUMN_NAME AND s.NON_UNIQUE=0 AND s.SUB_PART IS NULL AND (SELECT COUNT(*) FROM information_schema.STATISTICS z WHERE z.TABLE_SCHEMA=s.TABLE_SCHEMA AND z.TABLE_NAME=s.TABLE_NAME AND z.INDEX_NAME=s.INDEX_NAME)=1),
                EXISTS(SELECT 1 FROM information_schema.KEY_COLUMN_USAGE k WHERE k.REFERENCED_TABLE_SCHEMA=c.TABLE_SCHEMA AND k.REFERENCED_TABLE_NAME=c.TABLE_NAME AND k.REFERENCED_COLUMN_NAME=c.COLUMN_NAME)
            FROM information_schema.COLUMNS c WHERE c.TABLE_SCHEMA=DATABASE() AND c.TABLE_NAME=@table ORDER BY c.ORDINAL_POSITION
            """,
            c
        );
        cmd.Parameters.AddWithValue("@table", table);
        var columns = new List<SchemaColumn>();
        await using (var r = await cmd.ExecuteReaderAsync())
        {
            while (await r.ReadAsync())
            {
                string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
                int? I(int i) =>
                    r.IsDBNull(i)
                        ? null
                        : Convert.ToInt32(r.GetValue(i), CultureInfo.InvariantCulture);
                var extra = r.GetString(5);
                var type = r.GetString(1);
                var blocked =
                    DataTypeColumn.Is(r.GetString(0))
                        ? "The datatype column is managed by the backend."
                    : r.GetString(4) == "PRI" || extra.Contains("auto_increment")
                        ? "Primary keys are managed outside the designer."
                    : extra.Contains("GENERATED")
                        ? "Generated columns are managed outside the designer."
                    : S(13) != null || r.GetBoolean(16)
                        ? "Foreign-key columns are protected. Configure lookup labels in Editor layouts."
                    : !new[]
                    {
                        "varchar",
                        "decimal",
                        "bigint",
                        "int",
                        "smallint",
                        "mediumint",
                        "tinyint",
                        "longtext",
                        "text",
                        "date",
                        "datetime",
                        "timestamp",
                    }.Contains(type)
                        ? "This database type is not editable in the designer."
                    : extra != ""
                    && !extra.StartsWith(
                        "on update current_timestamp",
                        StringComparison.OrdinalIgnoreCase
                    )
                        ? "This column has special database attributes."
                    : null;
                columns.Add(
                    new(
                        r.GetString(0),
                        type,
                        r.GetString(2),
                        r.GetString(3) == "YES",
                        r.GetString(4) == "PRI",
                        extra.Contains("GENERATED"),
                        extra.Contains("auto_increment"),
                        r.IsDBNull(6) ? null : Convert.ToInt64(r.GetValue(6)),
                        I(7),
                        I(8),
                        S(9),
                        S(10),
                        S(11),
                        extra,
                        r.GetString(12),
                        S(13),
                        S(14),
                        r.GetBoolean(15),
                        r.GetBoolean(16),
                        blocked
                    )
                );
            }
        }
        using var show = new MySqlCommand($"SHOW CREATE TABLE {Quote(table)}", c);
        await using var reader = await show.ExecuteReaderAsync();
        await reader.ReadAsync();
        return new(
            Convert.ToHexString(
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(
                        Regex.Replace(
                            reader.GetString(1),
                            @"(\) ENGINE=\w+) AUTO_INCREMENT=\d+",
                            "$1"
                        )
                    )
                )
            ),
            columns
        );
    }

    static async Task<bool> Any(MySqlConnection c, string table, string predicate = "1=1")
    {
        using var cmd = new MySqlCommand(
            $"SELECT EXISTS(SELECT 1 FROM {Quote(table)} WHERE {predicate} LIMIT 1)",
            c
        );
        return Convert.ToInt32(await cmd.ExecuteScalarAsync()) != 0;
    }

    static async Task Execute(MySqlConnection c, string sql)
    {
        using var cmd = new MySqlCommand(sql, c) { CommandTimeout = 120 };
        try
        {
            await cmd.ExecuteNonQueryAsync();
        }
        catch (MySqlException e) when (e.Number is 1050 or 1060)
        {
            throw new ApiError(409, "That table or column already exists. Refresh the designer.");
        }
        catch (MySqlException e) when (e.Number is 1142 or 1143 or 1044)
        {
            throw new ApiError(
                403,
                "The saved database account needs CREATE / ALTER / REFERENCES privileges for schema changes."
            );
        }
        catch (MySqlException e) when (e.Number is 1265 or 1406 or 1264 or 1138 or 1067)
        {
            throw new ApiError(
                400,
                "The change conflicts with existing data or its default. No values were truncated; revise the column parameters."
            );
        }
        catch (MySqlException e) when (e.Number is 1553 or 1833)
        {
            throw new ApiError(
                409,
                "This field is referenced by a database constraint. Remove that reference first."
            );
        }
    }

    public static async Task DropColumn(MySqlConnection c, string table, string column)
    {
        Name(column);
        if (DataTypeColumn.Is(column))
            throw new ApiError(400, "The datatype column is managed by the backend.");
        var foreignKeys = new List<string>();
        using (var command = new MySqlCommand(
            "SELECT CONSTRAINT_NAME FROM information_schema.KEY_COLUMN_USAGE WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME=@table AND COLUMN_NAME=@column AND REFERENCED_TABLE_NAME IS NOT NULL",
            c
        ))
        {
            command.Parameters.AddWithValue("@table", table);
            command.Parameters.AddWithValue("@column", column);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                foreignKeys.Add(reader.GetString(0));
        }
        var clauses = foreignKeys.Select(key => $"DROP FOREIGN KEY {Quote(key)}").ToList();
        clauses.Add($"DROP COLUMN {Quote(column)}");
        await Execute(c, $"ALTER TABLE {Quote(table)} {string.Join(", ", clauses)}");
    }

    public static void Map(RouteGroupBuilder admin)
    {
        var group = admin.MapGroup("/connections/{id:int}/schema");
        group.MapGet(
            "/tables/{table}",
            async (int id, string table, AppDb db, DatabaseService service) =>
            {
                var config =
                    await db.Connections.FindAsync(id)
                    ?? throw new ApiError(404, "Connection not found.");
                await using var c = await service.Open(config);
                return await Inspect(c, service, table);
            }
        );
        group.MapPost(
            "/tables",
            async (int id, NewTable input, AppDb db, DatabaseService service, HttpContext ctx) =>
            {
                Name(input.Name);
                Name(input.PrimaryKey);
                if (DataTypeColumn.Is(input.PrimaryKey))
                    throw new ApiError(400, "The datatype column is managed by the backend.");
                var config =
                    await db.Connections.FindAsync(id)
                    ?? throw new ApiError(404, "Connection not found.");
                await using var c = await service.Open(config);
                await using var gate = await SumupGate.Enter(c);
                await Strict(c);
                await Execute(
                    c,
                    $"CREATE TABLE {Quote(input.Name)} ({Quote(input.PrimaryKey)} BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY, {Quote(DataTypeColumn.Name)} VARCHAR(64) NOT NULL DEFAULT 'default') ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci"
                );
                db.Audit.Add(
                    new()
                    {
                        Actor = ctx.User.Identity!.Name!,
                        Action = "create-table",
                        Resource = $"{id}/{input.Name}",
                    }
                );
                await db.SaveChangesAsync();
                return await Inspect(c, service, input.Name);
            }
        );
        foreach (var updating in new[] { false, true })
        {
            group.MapPost(
                "/tables/{table}/" + (updating ? "modify-column" : "columns"),
                async (
                    int id,
                    string table,
                    SchemaColumnInput input,
                    AppDb db,
                    DatabaseService service,
                    HttpContext ctx
                ) =>
                {
                    Name(input.Name);
                    if (DataTypeColumn.Is(input.Name))
                        throw new ApiError(400, "The datatype column is managed by the backend.");
                    var config =
                        await db.Connections.FindAsync(id)
                        ?? throw new ApiError(404, "Connection not found.");
                    await using var c = await service.Open(config);
                    await using var gate = await SumupGate.Enter(c);
                    await Strict(c);
                    var schema = await Inspect(c, service, table);
                    if (input.Version != schema.Version)
                        throw new ApiError(
                            409,
                            "Table structure changed. Refresh the designer before saving."
                        );
                    string definition;
                    LookupConfig? lookup = null;
                    if (updating)
                    {
                        var old =
                            schema.Columns.SingleOrDefault(x => x.Name == input.Name)
                            ?? throw new ApiError(404, "Column not found.");
                        if (old.EditBlocked != null)
                            throw new ApiError(400, old.EditBlocked);
                        var expected = old.Type switch
                        {
                            "varchar" => "text",
                            "decimal" => "decimal",
                            "bigint" or "int" or "smallint" or "mediumint" => "integer",
                            "tinyint" => "boolean",
                            "text" or "longtext" => "longtext",
                            "timestamp" => "datetime",
                            _ => old.Type,
                        };
                        if (input.Type != expected)
                            throw new ApiError(
                                400,
                                "Existing column types cannot be converted here. Edit their parameters without changing type."
                            );
                        definition = old.SqlType;
                        if (old.Type == "varchar")
                        {
                            definition = ScalarType(input);
                            if (
                                await Any(
                                    c,
                                    table,
                                    $"CHAR_LENGTH({Quote(old.Name)}) > {input.Length}"
                                )
                            )
                                throw new ApiError(
                                    400,
                                    "Existing text exceeds the requested length. No data was changed."
                                );
                        }
                        if (old.Type == "decimal")
                        {
                            definition = ScalarType(input);
                            if (
                                input.Scale < old.Scale
                                || input.Precision - input.Scale < old.Precision - old.Scale
                            )
                                throw new ApiError(
                                    400,
                                    "Decimal changes cannot reduce decimal places or integer digits; this could round or overflow values."
                                );
                            if (old.SqlType.Contains("unsigned"))
                                definition += " UNSIGNED";
                            if (old.SqlType.Contains("zerofill"))
                                definition += " ZEROFILL";
                        }
                        if (!input.Nullable && await Any(c, table, $"{Quote(old.Name)} IS NULL"))
                            throw new ApiError(
                                400,
                                "Fill existing NULL values before making this column required."
                            );
                        if (old.Charset != null)
                            definition +=
                                $" CHARACTER SET {Quote(old.Charset)} COLLATE {Quote(old.Collation!)}";
                        definition += input.Nullable ? " NULL" : " NOT NULL";
                        if (
                            old.Default != null
                            && !(
                                old.Default.Equals("NULL", StringComparison.OrdinalIgnoreCase)
                                && !input.Nullable
                            )
                        )
                            definition += " DEFAULT " + old.Default;
                        if (old.Extra != "")
                            definition += " " + old.Extra;
                        definition += " COMMENT " + Literal(old.Comment);
                    }
                    else
                    {
                        if (
                            schema.Columns.Any(x =>
                                x.Name.Equals(input.Name, StringComparison.OrdinalIgnoreCase)
                            )
                        )
                            throw new ApiError(409, "Column already exists.");
                        var layout = await db.Layouts.SingleOrDefaultAsync(x =>
                            x.ConnectionId == id && x.Table == table
                        );
                        if (
                            LayoutFields(layout?.FieldsJson)
                                .Any(x =>
                                    x.Name.Equals(input.Name, StringComparison.OrdinalIgnoreCase)
                                )
                        )
                            throw new ApiError(400, "A layout field already uses this name.");
                        if (!input.Nullable && await Any(c, table))
                            throw new ApiError(
                                400,
                                "New columns on populated tables must allow NULL. Fill the values, then make the column required."
                            );
                        if (input.Type == "relation")
                        {
                            if (
                                input.RelatedTable == null
                                || input.RelatedKey == null
                                || input.DisplayColumn == null
                            )
                                throw new ApiError(
                                    400,
                                    "Select the related table, unique key and display column."
                                );
                            lookup = new(
                                input.RelatedTable,
                                input.RelatedKey,
                                input.DisplayColumn,
                                []
                            );
                            await service.ValidateLookup(c, lookup);
                            var target = (
                                await Inspect(c, service, input.RelatedTable)
                            ).Columns.Single(x => x.Name == input.RelatedKey);
                            if (
                                !target.UniqueKey
                                || target.Nullable
                                || target.Generated
                                || !new[]
                                {
                                    "bigint",
                                    "int",
                                    "smallint",
                                    "mediumint",
                                    "tinyint",
                                    "varchar",
                                    "char",
                                }.Contains(target.Type)
                            )
                                throw new ApiError(
                                    400,
                                    "Relations require a non-null single-column unique integer or short text key."
                                );
                            definition = target.SqlType;
                            if (target.Charset != null)
                                definition +=
                                    $" CHARACTER SET {Quote(target.Charset)} COLLATE {Quote(target.Collation!)}";
                        }
                        else
                            definition = ScalarType(input);
                        definition += input.Nullable ? " NULL" : " NOT NULL";
                    }
                    var ddl =
                        $"ALTER TABLE {Quote(table)} {(updating ? "MODIFY" : "ADD")} COLUMN {Quote(input.Name)} {definition}";
                    if (lookup != null)
                        ddl +=
                            $", ADD CONSTRAINT {Quote("fk_" + Guid.NewGuid().ToString("N"))} FOREIGN KEY ({Quote(input.Name)}) REFERENCES {Quote(lookup.Table)} ({Quote(lookup.KeyColumn)}) ON DELETE RESTRICT ON UPDATE RESTRICT";
                    await Execute(c, ddl);
                    if (lookup != null)
                    {
                        var layout = await db.Layouts.SingleOrDefaultAsync(x =>
                            x.ConnectionId == id && x.Table == table
                        );
                        if (layout == null)
                        {
                            layout = new()
                            {
                                ConnectionId = id,
                                Table = table
                            };
                            db.Layouts.Add(layout);
                        }
                        var stored = ObjectModel.Stored(layout.FieldsJson);
                        stored.Object.Fields.Add(
                            new(input.Name, input.Name, false, "lookup", Lookup: lookup)
                        );
                        stored.Layout.Fields.Add(
                            new(
                                input.Name,
                                EditorOrder: schema.Columns.Count,
                                ListOrder: schema.Columns.Count,
                                Label: input.Name
                            )
                        );
                        stored = ObjectModel.Normalize(stored);
                        layout.FieldsJson = ObjectModel.Serialize(stored);
                    }
                    db.Audit.Add(
                        new()
                        {
                            Actor = ctx.User.Identity!.Name!,
                            Action = updating ? "modify-column" : "create-column",
                            Resource = $"{id}/{table}/{input.Name}",
                        }
                    );
                    await db.SaveChangesAsync();
                    return await Inspect(c, service, table);
                }
            );
        }
    }
}

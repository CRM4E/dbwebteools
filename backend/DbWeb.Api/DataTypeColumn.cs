using System.Text.Json;
using MySqlConnector;

namespace DbWeb.Api;

public static class DataTypeColumn
{
    public const string Name = "datatype";

    public static LayoutField Presentation(ObjectDefinition definition) =>
        new(
            Name,
            "Data type",
            "",
            int.MaxValue,
            false,
            false,
            "dropdown",
            Options: definition.DataTypes!
                .Select(type => new DropdownOption(type.Key, type.Label))
                .ToList(),
            ShowInList: true,
            ListOrder: int.MaxValue
        );

    public static bool Is(string? name) =>
        name != null && name.Equals(Name, StringComparison.OrdinalIgnoreCase);

    public static void ValidateValue(JsonElement value, IEnumerable<string> allowedKeys)
    {
        if (
            value.ValueKind != JsonValueKind.String
            || !allowedKeys.Contains(value.GetString()!, StringComparer.Ordinal)
        )
            throw new ApiError(400, "Data type must be one of the configured values.");
    }

    public static List<LayoutField> ApplyConstraints(
        IEnumerable<LayoutField> fields,
        string? dataTypeKey
    ) =>
        fields.Select(field =>
        {
            var constraint = field.DataTypeConstraints?.SingleOrDefault(candidate =>
                candidate.Key.Equals(dataTypeKey, StringComparison.Ordinal)
            );
            return constraint == null
                ? field
                : field with
                {
                    Required = constraint.Required,
                    Mask = constraint.Mask,
                    EnabledOptionKeys = constraint.EnabledOptionKeys,
                };
        }).ToList();

    public static async Task ValidateExisting(
        MySqlConnection connection,
        DatabaseService service,
        string table,
        IEnumerable<string> allowedKeys
    )
    {
        var existing = (await service.Columns(connection, table)).SingleOrDefault(column =>
            Is(column.Name)
        );
        if (existing == null)
            return;
        Compatible(existing);
        await ValidateValues(connection, table, allowedKeys);
    }

    public static async Task Prepare(
        MySqlConnection connection,
        DatabaseService service,
        string table
    )
    {
        var existing = (await service.Columns(connection, table)).SingleOrDefault(column =>
            Is(column.Name)
        );
        if (existing != null)
        {
            Compatible(existing);
            return;
        }
        await Execute(
            connection,
            $"ALTER TABLE {DatabaseService.Quote(table)} ADD COLUMN {DatabaseService.Quote(Name)} VARCHAR(64) NULL"
        );
    }

    public static async Task Finalize(
        MySqlConnection connection,
        DatabaseService service,
        string table,
        string defaultKey,
        IEnumerable<string> allowedKeys
    )
    {
        var existing = (await service.Columns(connection, table)).SingleOrDefault(column =>
            Is(column.Name)
        ) ?? throw new ApiError(409, "The managed datatype column is not provisioned.");
        Compatible(existing);
        if (existing.Nullable)
        {
            try
            {
                await using var backfill = connection.CreateCommand();
                backfill.CommandText =
                    $"UPDATE {DatabaseService.Quote(table)} SET {DatabaseService.Quote(Name)}=@default WHERE {DatabaseService.Quote(Name)} IS NULL OR {DatabaseService.Quote(Name)}=''";
                backfill.Parameters.AddWithValue("@default", defaultKey);
                await backfill.ExecuteNonQueryAsync();
            }
            catch (MySqlException exception) when (exception.Number is 1142 or 1143 or 1044)
            {
                throw new ApiError(
                    403,
                    "The saved database account needs UPDATE privileges to backfill the managed datatype column."
                );
            }
        }
        await ValidateValues(connection, table, allowedKeys);
        if (existing.Nullable || existing.Default != defaultKey)
            await Execute(
                connection,
                $"ALTER TABLE {DatabaseService.Quote(table)} MODIFY COLUMN {DatabaseService.Quote(Name)} VARCHAR(64) NOT NULL DEFAULT {Literal(defaultKey)}"
            );
    }

    public static async Task Ensure(
        MySqlConnection connection,
        DatabaseService service,
        string table,
        string defaultKey,
        IEnumerable<string> allowedKeys
    )
    {
        await ValidateExisting(connection, service, table, allowedKeys);
        await Prepare(connection, service, table);
        await Finalize(connection, service, table, defaultKey, allowedKeys);
    }

    public static void RequireReady(IEnumerable<ColumnInfo> columns)
    {
        var column = columns.SingleOrDefault(item => Is(item.Name));
        if (column == null || column.Nullable)
            throw new ApiError(
                409,
                "This object is waiting for datatype provisioning. Ask an administrator to save the object configuration."
            );
        Compatible(column);
    }

    static void Compatible(ColumnInfo existing)
    {
        if (
            existing.Name != Name
            || existing.Type != "varchar"
            || existing.Length != 64
            || existing.PrimaryKey
            || existing.Generated
            || existing.AutoIncrement
        )
            throw new ApiError(
                409,
                "The existing datatype column is incompatible. It must be exactly VARCHAR(64) and named datatype."
            );
    }

    static async Task ValidateValues(
        MySqlConnection connection,
        string table,
        IEnumerable<string> allowedKeys
    )
    {
        var allowed = allowedKeys.Distinct(StringComparer.Ordinal).ToList();
        if (allowed.Count == 0)
            throw new ApiError(400, "At least one data type is required.");
        await using var validate = connection.CreateCommand();
        var parameters = allowed.Select((key, index) =>
        {
            var name = "@type" + index;
            validate.Parameters.AddWithValue(name, key);
            return name;
        });
        validate.CommandText =
            $"SELECT EXISTS(SELECT 1 FROM {DatabaseService.Quote(table)} "
            + $"WHERE {DatabaseService.Quote(Name)} IS NOT NULL AND {DatabaseService.Quote(Name)}<>'' "
            + $"AND BINARY {DatabaseService.Quote(Name)} NOT IN ({string.Join(",", parameters)}) LIMIT 1)";
        if (Convert.ToInt32(await validate.ExecuteScalarAsync()) != 0)
            throw new ApiError(
                409,
                "The datatype column contains a value that is not configured. Restore that data type before saving."
            );
    }

    static string Literal(string text) =>
        "'" + text.Replace("\\", "\\\\").Replace("'", "''") + "'";

    static async Task Execute(MySqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 120;
        try
        {
            await command.ExecuteNonQueryAsync();
        }
        catch (MySqlException exception) when (exception.Number is 1142 or 1143 or 1044)
        {
            throw new ApiError(
                403,
                "The saved database account needs ALTER privileges to provision the managed datatype column."
            );
        }
        catch (MySqlException exception) when (exception.Number == 1060)
        {
            throw new ApiError(
                409,
                "The datatype column changed while it was being provisioned. Retry after refreshing."
            );
        }
        catch (MySqlException exception) when (exception.Number is 1265 or 1406 or 1264 or 1138)
        {
            throw new ApiError(
                409,
                "Existing datatype values are incompatible with the managed VARCHAR(64) column."
            );
        }
    }
}

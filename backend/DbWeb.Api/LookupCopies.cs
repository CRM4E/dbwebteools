using System.Text.Json;
using MySqlConnector;

namespace DbWeb.Api;

public partial class DatabaseService
{
    public async Task ValidateCopyMappings(
        MySqlConnection db,
        List<LayoutField> fields,
        List<ColumnInfo> destinations
    )
    {
        var assigned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in fields.Where(f => f.Widget == "lookup" && f.Lookup != null))
        {
            var lookup = field.Lookup!;
            if (lookup.CopyMappings is not { Count: > 0 })
                continue;
            if (lookup.CopyMappings.Count > 20)
                throw new ApiError(400, "Define at most 20 copy mappings per lookup.");
            var sources = await ValidateLookup(db, lookup);
            foreach (var mapping in lookup.CopyMappings)
            {
                var source = sources.Find(c => c.Name == mapping?.SourceColumn);
                var destination = destinations.Find(c => c.Name == mapping?.DestinationColumn);
                if (
                    source == null
                    || destination == null
                    || destination.Generated
                    || destination.AutoIncrement
                    || destination.PrimaryKey
                    || DataTypeColumn.Is(destination.Name)
                    || destination.Name == field.Name
                    || fields.Any(f =>
                        f.Name == destination.Name && f.Widget is "lookup" or "sumup"
                    )
                    || !assigned.Add(destination.Name)
                )
                    throw new ApiError(
                        400,
                        "Copy mappings need existing source columns and distinct writable destination columns (not keys or lookups)."
                    );
                if (
                    CopyFamily(source.Type) != CopyFamily(destination.Type)
                    || CopyFamily(source.Type) == "unsupported"
                )
                    throw new ApiError(
                        400,
                        $"Copy mapping to {destination.Name} has incompatible column types."
                    );
            }
        }
    }

    static string CopyFamily(string type) =>
        type switch
        {
            "tinyint"
            or "smallint"
            or "mediumint"
            or "int"
            or "bigint"
            or "decimal"
            or "float"
            or "double" => "number",
            "char"
            or "varchar"
            or "tinytext"
            or "text"
            or "mediumtext"
            or "longtext"
            or "enum"
            or "set" => "text",
            "date" => "date",
            "datetime" or "timestamp" => "datetime",
            _ => "unsupported",
        };

    // Used both for the editor preview and authoritative transactional writes. Never trust copied client values.
    public async Task<Dictionary<string, JsonElement>> CopyLookupValues(
        MySqlConnection db,
        LookupConfig lookup,
        JsonElement key,
        MySqlTransaction? transaction = null
    )
    {
        var mappings = lookup.CopyMappings ?? [];
        if (key.ValueKind == JsonValueKind.Null)
            return mappings.ToDictionary(
                m => m.DestinationColumn,
                _ => JsonSerializer.SerializeToElement<object?>(null)
            );
        if (key.ValueKind is not JsonValueKind.String and not JsonValueKind.Number)
            throw new ApiError(400, "Lookup keys must be strings, numbers, or NULL.");
        await using var cmd = db.CreateCommand();
        cmd.Transaction = transaction;
        var names = mappings.Select(m => m.SourceColumn).Append(lookup.KeyColumn).Distinct();
        var columns = await Columns(db, lookup.Table, transaction);
        var criteria = ViewPredicate(cmd, lookup.Criteria, columns);
        cmd.CommandText =
            $"SELECT {string.Join(",", names.Select(Quote))} FROM {Quote(lookup.Table)} WHERE {Quote(lookup.KeyColumn)}=@key"
            + (criteria.Length == 0 ? "" : " AND " + criteria)
            + (transaction == null ? "" : " LOCK IN SHARE MODE");
        cmd.Parameters.AddWithValue("@key", key.ToString());
        var rows = await Read(cmd);
        if (rows.Count != 1)
            throw new ApiError(400, "Select a related record that matches the lookup criteria.");
        return mappings.ToDictionary(
            m => m.DestinationColumn,
            m => JsonSerializer.SerializeToElement(rows[0][m.SourceColumn])
        );
    }
}

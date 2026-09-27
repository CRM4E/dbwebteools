using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using static DbWeb.Api.TableAccess;

namespace DbWeb.Api;

public record ValidatedRecordWrite(
    List<LayoutField> Fields,
    List<FieldSetRule> FieldSetRules
);

public static class RecordWrites
{
    public static async Task<Dictionary<string, JsonElement>> Preview(
        AppDb db,
        HttpContext ctx,
        int id,
        string table,
        DatabaseService service,
        MySqlConnection c,
        Dictionary<string, JsonElement>? initial = null
    )
    {
        var values = new Dictionary<string, JsonElement>(initial ?? []);
        var validated = await Validate(
            db,
            ctx,
            id,
            table,
            new RowMutation(values, null, null),
            "create",
            service,
            c
        );
        var fields = validated.Fields;
        await service.ValidateCopyMappings(c, fields, await service.Columns(c, table));
        foreach (
            var field in fields.Where(f =>
                f.Widget == "lookup" && f.Lookup != null && values.ContainsKey(f.Name)
            )
        )
            foreach (var copied in await service.CopyLookupValues(c, field.Lookup!, values[field.Name]))
                values[copied.Key] = copied.Value;
        LayoutRules.ValidateMaskValues(fields, values);
        return values;
    }

    public static async Task<ValidatedRecordWrite> Validate(
        AppDb db,
        HttpContext ctx,
        int id,
        string table,
        RowMutation input,
        string op,
        DatabaseService s,
        MySqlConnection c
    )
    {
        List<LayoutField> fields = [];
        List<FieldSetRule> fieldSetRules = [];
        if (op != "delete" && input.Values != null)
        {
            var layout = await db.Layouts.SingleOrDefaultAsync(x =>
                x.ConnectionId == id && x.Table == table
            );
            var stored = ObjectModel.Stored(layout?.FieldsJson);
            fields = ObjectModel.Merge(stored).Fields;
            fieldSetRules = stored.Object.FieldSetRules ?? [];
            var columns = await s.Columns(c, table);
            var hasManagedDataType = columns.Any(column => DataTypeColumn.Is(column.Name));
            if (
                op == "create"
                && (
                    hasManagedDataType
                    || ObjectModel.HasCanonicalDataTypes(layout?.FieldsJson)
                )
            )
                DataTypeColumn.RequireReady(columns);
            if (hasManagedDataType)
            {
                if (input.Values.Keys.Any(DataTypeColumn.Is))
                    throw new ApiError(400, "datatype is managed by the backend and cannot be edited.");
                if (op == "create")
                    input.Values[DataTypeColumn.Name] = JsonSerializer.SerializeToElement(
                        stored.Object.DefaultDataTypeKey!
                    );
            }
            if (op == "create")
                DatabaseService.ApplyCreationDefaults(
                    fields,
                    columns,
                    input.Values
                );
            if (
                fields.Any(f =>
                    (f.Widget is "join" or "formula") && input.Values.ContainsKey(f.Name)
                )
            )
                throw new ApiError(
                    400,
                    "Computed fields are read-only and cannot be submitted as stored values."
                );
            foreach (
                var field in DatabaseService
                    .LayoutFields(layout?.FieldsJson)
                    .Where(f => f.Widget == "lookup" && f.Lookup != null)
            )
            {
                if (!input.Values.TryGetValue(field.Name, out var key))
                    continue;
                if (
                    key.ValueKind == JsonValueKind.Null
                    && field.Lookup!.CopyMappings is not { Count: > 0 }
                )
                    continue;
                await FieldAccess.Lookup(db, ctx, id, field.Lookup!, true);
                (await FieldAccess.For(db, ctx, id, table)).RequireWrite(
                    field.Lookup!.CopyMappings?.Select(m => m.DestinationColumn) ?? []
                );
                if (key.ValueKind == JsonValueKind.Null)
                    continue;
                if (key.ValueKind is not JsonValueKind.Number and not JsonValueKind.String)
                    throw new ApiError(400, "Lookup keys must be strings or numbers.");
                await s.CopyLookupValues(c, field.Lookup, key);
            }
        }

        return new(fields, fieldSetRules);
    }
}

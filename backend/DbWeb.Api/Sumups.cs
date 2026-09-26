using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using NCalc;

namespace DbWeb.Api;

public record SumupConfig(
    string Operation,
    string ChildTable,
    string LookupField,
    string? SourceField = null
);

public record SumupPlan(
    string ParentTable,
    LayoutField Field,
    LookupConfig Lookup,
    List<ColumnInfo> ChildColumns,
    List<LayoutField> ChildFields,
    int Scale,
    Expression? Expression
);

// A database-scoped coordination gate prevents configuration/backfill races across API instances.
// Each affected parent is also locked FOR UPDATE and changed inside the child's transaction.
public sealed class SumupGate(MySqlConnection connection, string name) : IAsyncDisposable
{
    public static async Task<SumupGate> Enter(MySqlConnection connection)
    {
        var name =
            "dbweb-sumup-"
            + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(connection.Database)))[
                ..40
            ];
        using var cmd = new MySqlCommand("SELECT GET_LOCK(@name,15)", connection);
        cmd.Parameters.AddWithValue("@name", name);
        if (Convert.ToInt32(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture) != 1)
            throw new ApiError(
                409,
                "Another write or sum-up recalculation is running. Retry shortly."
            );
        return new(connection, name);
    }

    public async ValueTask DisposeAsync()
    {
        if (connection.State != System.Data.ConnectionState.Open)
            return;
        using var cmd = new MySqlCommand("SELECT RELEASE_LOCK(@name)", connection);
        cmd.Parameters.AddWithValue("@name", name);
        await cmd.ExecuteScalarAsync();
    }
}

public static class Sumups
{
    public static bool Numeric(string type) =>
        type
            is "tinyint"
                or "smallint"
                or "mediumint"
                or "int"
                or "bigint"
                or "decimal"
                or "float"
                or "double";

    public static async Task<List<SumupPlan>> Plans(
        AppDb db,
        DatabaseService service,
        MySqlConnection c,
        int connection,
        string? replacementTable = null,
        LayoutDefinition? replacement = null
    )
    {
        var layouts = (
            await db.Layouts.AsNoTracking().Where(l => l.ConnectionId == connection).ToListAsync()
        ).ToDictionary(l => l.Table, l => DatabaseService.Layout(l.FieldsJson));
        if (replacementTable != null)
            layouts[replacementTable] = replacement!;
        var plans = new List<SumupPlan>();
        foreach (var (parentTable, layout) in layouts.OrderBy(x => x.Key, StringComparer.Ordinal))
            foreach (
                var field in layout
                    .Fields.Where(f => f.Widget == "sumup")
                    .OrderBy(f => f.Name, StringComparer.Ordinal)
            )
            {
                var config = field.Sumup ?? throw new ApiError(400, "Sum-up configuration required.");
                var parentColumns = await service.Columns(c, parentTable);
                var target = parentColumns.Find(col => col.Name == field.Name);
                if (
                    target == null
                    || !Numeric(target.Type)
                    || target.Type is "float" or "double"
                    || target.PrimaryKey
                    || target.Generated
                    || target.AutoIncrement
                    || !field.ReadOnly
                    || field.Required
                )
                    throw new ApiError(
                        400,
                        "Sum-ups require a read-only, non-key integer or DECIMAL destination column."
                    );
                if (config.Operation is not "sum" and not "count")
                    throw new ApiError(400, "Choose count or sum.");
                var childFields = layouts.GetValueOrDefault(config.ChildTable)?.Fields ?? [];
                var relation =
                    childFields.Find(f =>
                        f.Name == config.LookupField
                        && f.Widget == "lookup"
                        && f.Lookup?.Table == parentTable
                    )
                    ?? throw new ApiError(
                        400,
                        $"Sum-up {parentTable}.{field.Name} requires an existing child lookup to its parent."
                    );
                var lookup = relation.Lookup!;
                if (lookup.KeyColumn == field.Name)
                    throw new ApiError(400, "A sum-up cannot be its own relationship key.");
                var childColumns = await service.Columns(c, config.ChildTable);
                await service.ValidateLookup(
                    c,
                    lookup,
                    childColumns.Single(col => col.Name == relation.Name)
                );
                using var engine = new MySqlCommand(
                    "SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME IN (@parent,@child) AND ENGINE <> 'InnoDB'",
                    c
                );
                engine.Parameters.AddWithValue("@parent", parentTable);
                engine.Parameters.AddWithValue("@child", config.ChildTable);
                if (Convert.ToInt32(await engine.ExecuteScalarAsync()) != 0)
                    throw new ApiError(
                        400,
                        "Sum-ups require InnoDB parent and child tables for transactional locking."
                    );
                using var precision = new MySqlCommand(
                    "SELECT NUMERIC_PRECISION,NUMERIC_SCALE FROM information_schema.COLUMNS WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME=@table AND COLUMN_NAME=@column",
                    c
                );
                precision.Parameters.AddWithValue("@table", parentTable);
                precision.Parameters.AddWithValue("@column", field.Name);
                int scale;
                await using (var reader = await precision.ExecuteReaderAsync())
                {
                    await reader.ReadAsync();
                    if (Convert.ToInt32(reader.GetValue(0)) > 28)
                        throw new ApiError(
                            400,
                            "Use a sum-up destination with at most 28 digits of precision."
                        );
                    scale = Convert.ToInt32(reader.GetValue(1));
                }
                Expression? expression = null;
                if (config.Operation == "sum" && string.IsNullOrEmpty(config.SourceField))
                    throw new ApiError(400, "Choose a numeric child column or formula for sum.");
                if (!string.IsNullOrEmpty(config.SourceField))
                {
                    var sourceField = childFields.Find(f => f.Name == config.SourceField);
                    if (sourceField?.Widget == "formula")
                    {
                        expression = Formulas.Compile(sourceField.Formula, childColumns, childFields);
                        if (
                            childFields.Any(f => f.Widget == "sumup")
                            && expression
                                .GetParameterNames()
                                .Any(n => childColumns.Any(col => col.Name == n && col.Generated))
                        )
                            throw new ApiError(
                                400,
                                "Formula sources on aggregate tables cannot reference database-generated columns with untracked dependencies."
                            );
                        if (
                            expression
                                .GetParameterNames()
                                .Any(n => childFields.Any(f => f.Name == n && f.Widget == "sumup"))
                        )
                            throw new ApiError(400, "Sum-up sources cannot depend on other sum-ups.");
                    }
                    else if (
                        (
                            childFields.Any(f => f.Widget == "sumup")
                            && childColumns.Any(col => col.Name == config.SourceField && col.Generated)
                        )
                        || !childColumns.Any(col => col.Name == config.SourceField && Numeric(col.Type))
                        || sourceField?.Widget == "sumup"
                    )
                        throw new ApiError(
                            400,
                            "Sum-up source must be a numeric child column or an independent child formula."
                        );
                }
                if (
                    layout.Fields.Any(f =>
                        f.Lookup?.CopyMappings?.Any(m => m.DestinationColumn == field.Name) == true
                    )
                )
                    throw new ApiError(400, "Lookup copy mappings cannot write sum-up destinations.");
                plans.Add(
                    new(parentTable, field, lookup, childColumns, childFields, scale, expression)
                );
            }
        return plans;
    }

    public static async Task RepairIfPending(
        AppDb db,
        DatabaseService service,
        MySqlConnection c,
        int connection
    )
    {
        var layouts = await db
            .Layouts.AsNoTracking()
            .Where(l => l.ConnectionId == connection)
            .Select(l => l.FieldsJson)
            .ToListAsync();
        if (!layouts.Any(json => DatabaseService.Layout(json).SumupsPending))
            return;
        await using var gate = await SumupGate.Enter(c);
        await Ready(db, service, c, connection);
    }

    public static async Task<List<SumupPlan>> Ready(
        AppDb db,
        DatabaseService service,
        MySqlConnection c,
        int connection
    )
    {
        var plans = await Plans(db, service, c, connection);
        var layouts = await db.Layouts.Where(l => l.ConnectionId == connection).ToListAsync();
        if (layouts.Any(l => DatabaseService.Layout(l.FieldsJson).SumupsPending))
        {
            await using var tx = await c.BeginTransactionAsync();
            await service.RebuildSumups(c, tx, plans);
            await tx.CommitAsync();
            foreach (var layout in layouts)
            {
                var stored = ObjectModel.Stored(layout.FieldsJson);
                layout.FieldsJson = ObjectModel.Serialize(
                    stored with
                    {
                        Object = stored.Object with
                        {
                            SumupsPending = false
                        },
                    }
                );
            }
            await db.SaveChangesAsync();
        }
        return plans;
    }

    // Prepare all data changes before committing metadata. A durable pending marker repairs an interrupted cross-database commit.
    public static async Task SaveLayout(
        AppDb db,
        DatabaseService service,
        MySqlConnection c,
        int connection,
        RecordLayout layout,
        StoredObjectDefinition definition
    )
    {
        var merged = ObjectModel.Merge(definition);
        List<SumupPlan> previous = [];
        var pending = (
            await db.Layouts.AsNoTracking().Where(l => l.ConnectionId == connection).ToListAsync()
        ).Any(l => DatabaseService.Layout(l.FieldsJson).SumupsPending);
        if (!pending)
            try
            {
                previous = await Plans(db, service, c, connection);
            }
            catch (ApiError)
            { /* An administrator must be able to repair stale schema/configuration. */
            }
        var plans = await Plans(db, service, c, connection, layout.Table, merged);
        static string Signature(SumupPlan p) =>
            JsonSerializer.Serialize(
                new
                {
                    p.Field.Sumup,
                    p.Lookup.KeyColumn,
                    p.Scale,
                    formulas = p
                        .ChildFields.Where(f => f.Widget is "formula" or "dropdown")
                        .Select(f => new
                        {
                            f.Name,
                            f.Formula,
                            Options = f.Options,
                        }),
                }
            );
        plans = plans
            .Where(p =>
                !previous.Any(old =>
                    old.ParentTable == p.ParentTable
                    && old.Field.Name == p.Field.Name
                    && Signature(old) == Signature(p)
                )
            )
            .ToList();
        await using var tx = await c.BeginTransactionAsync();
        await service.RebuildSumups(c, tx, plans);
        layout.FieldsJson = ObjectModel.Serialize(
            definition with
            {
                Object = definition.Object with
                {
                    SumupsPending = plans.Count > 0
                },
            }
        );
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        if (plans.Count > 0)
        {
            layout.FieldsJson = ObjectModel.Serialize(
                definition with
                {
                    Object = definition.Object with
                    {
                        SumupsPending = false
                    },
                }
            );
            await db.SaveChangesAsync();
        }
    }
}

public partial class DatabaseService
{
    static decimal Contribution(SumupPlan plan, Dictionary<string, object?> row)
    {
        var config = plan.Field.Sumup!;
        if (config.Operation == "count" && string.IsNullOrEmpty(config.SourceField))
            return 1;
        object? value;
        try
        {
            value =
                plan.Expression == null
                    ? row.GetValueOrDefault(config.SourceField!)
                    : Formulas.Evaluate(plan.Expression, plan.ChildColumns, row);
        }
        catch (Exception)
        {
            throw new ApiError(
                400,
                $"Cannot calculate child formula for sum-up {plan.Field.Label}."
            );
        }
        if (value == null)
            return 0;
        if (config.Operation == "count")
            return 1;
        if (
            value is bool
            || !decimal.TryParse(
                Convert.ToString(value, CultureInfo.InvariantCulture),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var number
            )
        )
            throw new ApiError(400, $"Sum-up {plan.Field.Label} needs a numeric formula result.");
        if (decimal.Round(number, plan.Scale) != number)
            throw new ApiError(
                400,
                $"Sum-up {plan.Field.Label} cannot store this precision. Use a higher-scale DECIMAL destination or Round in the child formula."
            );
        return number;
    }

    static async Task<object?> LockSumupParent(
        MySqlConnection c,
        MySqlTransaction tx,
        SumupPlan plan,
        object? key
    )
    {
        if (key == null)
            return null;
        using var cmd = new MySqlCommand(
            $"SELECT {Quote(plan.Lookup.KeyColumn)} FROM {Quote(plan.ParentTable)} WHERE {Quote(plan.Lookup.KeyColumn)}=@key FOR UPDATE",
            c,
            tx
        );
        cmd.Parameters.AddWithValue("@key", key);
        return await cmd.ExecuteScalarAsync();
    }

    static async Task SetSumup(
        MySqlConnection c,
        MySqlTransaction tx,
        SumupPlan plan,
        object key,
        decimal value,
        bool delta
    )
    {
        using var cmd = new MySqlCommand(
            $"UPDATE {Quote(plan.ParentTable)} SET {Quote(plan.Field.Name)}={(delta ? $"COALESCE({Quote(plan.Field.Name)},0)+@value" : "@value")} WHERE {Quote(plan.Lookup.KeyColumn)}=@key",
            c,
            tx
        );
        cmd.Parameters.AddWithValue("@key", key);
        cmd.Parameters.AddWithValue("@value", value);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task CheckSumupParent(
        MySqlConnection c,
        MySqlTransaction tx,
        List<SumupPlan> plans,
        string table,
        Dictionary<string, object?>? current,
        RowMutation input,
        string operation
    )
    {
        if (current == null)
            return;
        foreach (var plan in plans.Where(p => p.ParentTable == table))
        {
            var key = current.GetValueOrDefault(plan.Lookup.KeyColumn);
            if (
                operation == "update"
                && input.Values.TryGetValue(plan.Lookup.KeyColumn, out var next)
                && KeyText(key) != next.ToString()
            )
                throw new ApiError(
                    400,
                    "Relationship keys used by sum-ups cannot be changed. Move child relations instead."
                );
            if (operation == "delete")
            {
                using var cmd = new MySqlCommand(
                    $"SELECT 1 FROM {Quote(plan.Field.Sumup!.ChildTable)} WHERE {Quote(plan.Field.Sumup.LookupField)}=@key LIMIT 1",
                    c,
                    tx
                );
                cmd.Parameters.AddWithValue("@key", key);
                if (await cmd.ExecuteScalarAsync() != null)
                    throw new ApiError(
                        409,
                        "Remove or move child records before deleting a sum-up parent."
                    );
            }
        }
    }

    public async Task ApplySumupDeltas(
        MySqlConnection c,
        MySqlTransaction tx,
        List<SumupPlan> plans,
        string table,
        Dictionary<string, object?>? before,
        Dictionary<string, object?>? after
    )
    {
        foreach (var plan in plans.Where(p => p.Field.Sumup!.ChildTable == table))
        {
            var changes = new Dictionary<string, (object key, decimal delta)>(
                StringComparer.Ordinal
            );
            foreach (var (row, sign) in new[] { (before, -1), (after, 1) })
            {
                if (row == null)
                    continue;
                var key = await LockSumupParent(
                    c,
                    tx,
                    plan,
                    row.GetValueOrDefault(plan.Field.Sumup!.LookupField)
                );
                if (key == null)
                    continue; // Orphans contribute only once a matching parent exists.
                var text = KeyText(key);
                changes[text] = (
                    key,
                    checked(changes.GetValueOrDefault(text).delta + sign * Contribution(plan, row))
                );
            }
            foreach (var change in changes.Values.Where(v => v.delta != 0))
                await SetSumup(c, tx, plan, change.key, change.delta, true);
        }
        if (before == null && after != null)
            foreach (var plan in plans.Where(p => p.ParentTable == table))
                await RebuildOneSumup(c, tx, plan, after.GetValueOrDefault(plan.Lookup.KeyColumn));
    }

    static string SourceProjection(SumupPlan plan, string prefix = "")
    {
        var names =
            plan.Expression != null ? plan.Expression.GetParameterNames().Where(n => n != "null")
            : string.IsNullOrEmpty(plan.Field.Sumup!.SourceField) ? []
            : new[] { plan.Field.Sumup.SourceField! };
        return string.Join(",", names.Distinct().Select(n => prefix + Quote(n)));
    }

    static Dictionary<string, object?> SumupRow(MySqlDataReader reader) =>
        Enumerable
            .Range(0, reader.FieldCount)
            .ToDictionary(reader.GetName, i => Wire(reader.GetValue(i)));

    async Task RebuildOneSumup(MySqlConnection c, MySqlTransaction tx, SumupPlan plan, object? key)
    {
        key = await LockSumupParent(c, tx, plan, key);
        if (key == null)
            return;
        using var cmd = new MySqlCommand(
            $"SELECT {(SourceProjection(plan) is { Length: > 0 } projection ? projection : "1")} FROM {Quote(plan.Field.Sumup!.ChildTable)} WHERE {Quote(plan.Field.Sumup.LookupField)}=@key",
            c,
            tx
        );
        cmd.Parameters.AddWithValue("@key", key);
        decimal total = 0;
        await using (var reader = await cmd.ExecuteReaderAsync())
            while (await reader.ReadAsync())
                total = checked(total + Contribution(plan, SumupRow(reader)));
        await SetSumup(c, tx, plan, key, total, false);
    }

    public async Task RebuildSumups(MySqlConnection c, MySqlTransaction tx, List<SumupPlan> plans)
    {
        foreach (var plan in plans)
        {
            using var parents = new MySqlCommand(
                $"SELECT {Quote(plan.Lookup.KeyColumn)} FROM {Quote(plan.ParentTable)} ORDER BY {Quote(plan.Lookup.KeyColumn)} FOR UPDATE",
                c,
                tx
            );
            var keys = await Read(parents);
            // One child-table scan per configured aggregate during explicit/backfill rebuild, never during normal child edits.
            var alias = "__sumup_" + Guid.NewGuid().ToString("N");
            using var children = new MySqlCommand(
                $"SELECT {(SourceProjection(plan, "c.") is { Length: > 0 } projection ? projection + "," : "")}p.{Quote(plan.Lookup.KeyColumn)} AS {Quote(alias)} FROM {Quote(plan.Field.Sumup!.ChildTable)} c JOIN {Quote(plan.ParentTable)} p ON c.{Quote(plan.Field.Sumup.LookupField)}=p.{Quote(plan.Lookup.KeyColumn)}",
                c,
                tx
            );
            var totals = new Dictionary<string, decimal>(StringComparer.Ordinal);
            await using (var reader = await children.ExecuteReaderAsync())
                while (await reader.ReadAsync())
                {
                    var row = SumupRow(reader);
                    var key = KeyText(row[alias]);
                    totals[key] = checked(totals.GetValueOrDefault(key) + Contribution(plan, row));
                }
            foreach (var row in keys)
            {
                var key = row[plan.Lookup.KeyColumn]!;
                await SetSumup(c, tx, plan, key, totals.GetValueOrDefault(KeyText(key)), false);
            }
        }
    }
}

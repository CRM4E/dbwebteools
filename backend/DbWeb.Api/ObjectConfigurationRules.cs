using MySqlConnector;

namespace DbWeb.Api;

public static class ObjectConfigurationRules
{
    public static void ValidateFieldSetRule(
        FieldSetRule rule,
        List<ColumnInfo> columns,
        List<LayoutField> fields
    )
    {
        if (string.IsNullOrWhiteSpace(rule.Field) || string.IsNullOrWhiteSpace(rule.Condition) || string.IsNullOrWhiteSpace(rule.Value))
            throw new ApiError(400, "Every field set rule needs a field, condition, and value formula.");
        var target = columns.SingleOrDefault(column => column.Name.Equals(rule.Field, StringComparison.OrdinalIgnoreCase));
        var targetField = fields.SingleOrDefault(field => field.Name.Equals(rule.Field, StringComparison.OrdinalIgnoreCase));
        var managedDataTypeTarget = target != null && DataTypeColumn.Is(target.Name);
        if (
            target == null || target.Generated || target.AutoIncrement || target.PrimaryKey
            || target.Type.Contains("blob") || target.Type is "binary" or "varbinary" or "geometry"
            || !managedDataTypeTarget && (targetField == null || targetField.ReadOnly || targetField.Lookup != null || targetField.Widget is "join" or "formula" or "sumup")
        )
            throw new ApiError(400, "Field set rules require an editable scalar stored target field or the managed datatype field.");
        Formulas.Compile(rule.Condition, columns, fields);
        Formulas.Compile(rule.Value, columns, fields);
        var dependencies = Formulas.Dependencies(rule.Condition, columns, fields)
            .Concat(Formulas.Dependencies(rule.Value, columns, fields));
        if (dependencies.Any(name => columns.Any(column => column.Generated && column.Name.Equals(name, StringComparison.OrdinalIgnoreCase))))
            throw new ApiError(400, "Field set rules cannot reference generated database columns.");
    }

    static void ValidatePresentationCompleteness(
        ObjectDefinition definition,
        LayoutPresentation presentation
    )
    {
        if (presentation?.Fields == null || presentation.Fields.Any(f => f == null))
            throw new ApiError(400, "Layout fields are required.");
        var names = definition
            .Fields.Select(f => f.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (
            presentation.Fields.Count != definition.Fields.Count
            || presentation
                .Fields.Select(f => f.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() != presentation.Fields.Count
            || presentation.Fields.Any(f => !names.Contains(f.Name))
        )
            throw new ApiError(400, "Layout must configure every object field exactly once.");
    }

    public static void ValidatePresentation(
        ObjectDefinition definition,
        LayoutPresentation presentation
    )
    {
        ValidatePresentationCompleteness(definition, presentation);
        if (presentation.Fields.Any(f => f.Section == null || f.Section.Length > 150))
            throw new ApiError(400, "Editor section names must be at most 150 characters.");
        if (presentation.Fields.Any(f => f.Label == null || f.Label.Length > 150))
            throw new ApiError(400, "Field labels must be at most 150 characters.");
        var required = ObjectModel
            .Merge(definition, presentation)
            .Fields.Where(f => f.Required)
            .Select(f => f.Name)
            .ToHashSet();
        if (presentation.Fields.Any(f => !f.ShowInEditor && required.Contains(f.Name)))
            throw new ApiError(400, "Required fields must remain visible in the editor.");
    }

    public static async Task<StoredObjectDefinition> Validate(
        DatabaseService service,
        MySqlConnection connection,
        string table,
        ObjectDefinition definition,
        LayoutPresentation presentation
    )
    {
        if (definition.Fields == null || definition.Fields.Any(field => field == null))
            throw new ApiError(400, "Object fields are required.");
        // Normalize completes omitted presentation rows for stored legacy data. API writes
        // must not gain that repair behavior: a submitted layout is complete or rejected.
        ValidatePresentationCompleteness(definition, presentation);
        if (definition.DataTypes != null)
        {
            var submittedNames = definition.Fields
                .Select(field => field.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (
                definition.DataTypes.Count is < 1 or > 100
                || definition.DataTypes.Where(type => type != null).Select(type => type.Key)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count() != definition.DataTypes.Count
                || definition.DataTypes.Where(type => type != null).Select(type => type.Label)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count() != definition.DataTypes.Count
                || definition.DataTypes.Any(type =>
                    type == null
                    || type.Fields == null
                    || type.Fields.Any(field => field == null || !submittedNames.Contains(field.Name))
                    || type.Fields.Select(field => field.Name)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Count() != type.Fields.Count
                )
            )
                throw new ApiError(400, "Data type fields must reference object fields at most once.");
        }
        var normalized = ObjectModel.Normalize(new(definition, presentation));
        definition = normalized.Object;
        presentation = normalized.Layout;
        if (definition.Fields == null || definition.Fields.Any(f => f == null))
            throw new ApiError(400, "Object fields are required.");
        if (definition.Fields.Any(f => f.Label == null || f.Label.Length > 150))
            throw new ApiError(400, "Field labels must be at most 150 characters.");
        if (
            definition.Fields.Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count()
                != definition.Fields.Count
            || definition.Fields.Count(x => x.Widget is "join" or "formula") > 20
        )
            throw new ApiError(400, "Invalid object fields.");
        if (
            definition.Fields.Any(field =>
                field.Name.Equals("datatype", StringComparison.OrdinalIgnoreCase)
            )
        )
            throw new ApiError(400, "datatype is a backend-managed field.");

        var dataTypes = definition.DataTypes!;
        if (
            dataTypes.Count is < 1 or > 100
            || dataTypes.Any(type =>
                type == null
                || string.IsNullOrWhiteSpace(type.Key)
                || type.Key.Length > 64
                || type.Key != type.Key.Trim()
                || string.IsNullOrWhiteSpace(type.Label)
                || type.Label.Length > 150
                || type.Label != type.Label.Trim()
                || type.Fields == null
            )
            || dataTypes.Select(type => type.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count()
                != dataTypes.Count
            || dataTypes.Select(type => type.Label).Distinct(StringComparer.OrdinalIgnoreCase).Count()
                != dataTypes.Count
            || dataTypes.Count(type =>
                type.Key.Equals(definition.DefaultDataTypeKey, StringComparison.OrdinalIgnoreCase)
            ) != 1
        )
            throw new ApiError(
                400,
                "Define 1–100 data types with unique non-blank keys (up to 64 characters), unique labels, and exactly one default."
            );
        var objectFields = definition.Fields.ToDictionary(
            field => field.Name,
            StringComparer.OrdinalIgnoreCase
        );
        foreach (var type in dataTypes)
        {
            if (
                type.Fields.Count != definition.Fields.Count
                || type.Fields.Select(field => field.Name)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count() != type.Fields.Count
                || type.Fields.Any(field => !objectFields.ContainsKey(field.Name))
            )
                throw new ApiError(400, "Every data type must configure every object field exactly once.");
            foreach (var setting in type.Fields)
            {
                var field = objectFields[setting.Name];
                var enabled = setting.EnabledOptionKeys ?? [];
                if (
                    (field.ReadOnly || field.Widget is "join" or "formula" or "sumup")
                        && (setting.Required || setting.Mask != null)
                    ||
                    enabled.Count != enabled.Distinct(StringComparer.OrdinalIgnoreCase).Count()
                    || field.Widget != "dropdown"
                        && (setting.OverrideDropdownOptions || enabled.Count > 0)
                    || setting.OverrideDropdownOptions
                        && enabled.Any(key =>
                            field.Options?.Any(option =>
                                option.Key.Equals(key, StringComparison.Ordinal)
                            ) != true
                        )
                    || setting.Required
                        && setting.OverrideDropdownOptions
                        && enabled.Count == 0
                )
                    throw new ApiError(
                        400,
                        "Data type settings must be editable-field rules, and dropdown selections must use unique keys from the field's master option catalog."
                    );
            }
        }

        ValidatePresentation(definition, presentation);
        var fields = ObjectModel.Merge(definition, presentation).Fields;
        var columns = await service.Columns(connection, table);
        var ruleColumns = columns.Any(column => DataTypeColumn.Is(column.Name))
            ? columns
            :
            [
                .. columns,
                new ColumnInfo(
                    DataTypeColumn.Name,
                    "varchar",
                    false,
                    false,
                    false,
                    false,
                    definition.DefaultDataTypeKey,
                    64
                ),
            ];
        var fieldSetRules = definition.FieldSetRules ?? [];
        if (fieldSetRules.Count > 50 || fieldSetRules.Any(rule => rule == null))
            throw new ApiError(400, "Define at most 50 field set rules.");
        foreach (var rule in fieldSetRules)
            ValidateFieldSetRule(rule, ruleColumns, fields);
        using var validation = connection.CreateCommand();
        DatabaseService.ViewPredicate(validation, definition.View, columns);
        if (
            fields.Any(x =>
                (x.Widget is not "join" and not "formula" && !columns.Any(y => y.Name == x.Name))
                || !new[]
                {
                    "auto",
                    "text",
                    "email",
                    "textarea",
                    "number",
                    "date",
                    "datetime",
                    "dropdown",
                    "checkbox",
                    "lookup",
                    "join",
                    "formula",
                    "sumup",
                }.Contains(x.Widget)
            )
        )
            throw new ApiError(400, "Invalid object fields.");

        foreach (var field in fields)
        {
            if (field.Widget != "sumup" && field.Sumup != null)
                throw new ApiError(400, "Only sum-up fields can define sum-up configuration.");
            if (field.Widget is "join" or "formula")
            {
                if (
                    string.IsNullOrWhiteSpace(field.Name)
                    || field.Name.Length > 100
                    || field.Name != field.Name.Trim()
                    || columns.Any(col =>
                        col.Name.Equals(field.Name, StringComparison.OrdinalIgnoreCase)
                    )
                    || field.Required
                    || field.CreationDefault != null
                    || !field.ReadOnly
                    || field.Lookup != null
                    || field.Options is { Count: > 0 }
                    || field.Mask != null
                )
                    throw new ApiError(
                        400,
                        "Computed fields must have a unique virtual name, be read-only, and have no editable control configuration."
                    );
                if (field.Widget == "formula")
                {
                    if (field.Join != null)
                        throw new ApiError(400, "Formula fields cannot define a join.");
                    Formulas.Compile(field.Formula, columns, fields);
                }
                else
                {
                    if (field.Formula != null)
                        throw new ApiError(400, "Only formula fields may define a formula.");
                    await service.ValidateJoin(
                        connection,
                        field.Join ?? throw new ApiError(400, "Join configuration required."),
                        columns
                    );
                }
                continue;
            }
            if (field.Formula != null)
                throw new ApiError(400, "Only formula fields may define a formula.");
            if (field.Join != null)
                throw new ApiError(400, "Only joined fields may define a join.");
            var master = objectFields[field.Name];
            LayoutRules.Validate(
                field with
                {
                    Options = master.Options
                },
                columns.Single(x => x.Name == field.Name)
            );
            if (field.Widget == "lookup")
                await service.ValidateLookup(
                    connection,
                    field.Lookup ?? throw new ApiError(400, "Lookup configuration required."),
                    columns.Single(x => x.Name == field.Name)
                );
            else if (field.Lookup != null)
                throw new ApiError(400, "Only lookup controls may have lookup configuration.");
        }
        await service.ValidateCopyMappings(connection, fields, columns);
        await service.ValidateCreationDefaults(connection, fields, columns);
        // Validate non-default settings now even though runtime currently applies the default only.
        foreach (var type in dataTypes)
        {
            var typed = definition with
            {
                DefaultDataTypeKey = type.Key
            };
            foreach (
                var field in ObjectModel.Merge(typed, presentation).Fields.Where(field =>
                    field.Widget is not "join" and not "formula"
                )
            )
                LayoutRules.Validate(
                    field with
                    {
                        Options = objectFields[field.Name].Options
                    },
                    columns.Single(column => column.Name == field.Name)
                );
        }
        return new(definition, presentation);
    }
}

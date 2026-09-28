using System.Text.Json;

namespace DbWeb.Api;

// The object definition is the application-facing model: validation, controls,
// relationships and calculated behavior. Layout stores presentation only.
public record ObjectField(
    string Name,
    string Label,
    bool ReadOnly,
    string Widget,
    LookupConfig? Lookup = null,
    List<DropdownOption>? Options = null,
    JoinConfig? Join = null,
    bool Required = false,
    string? Formula = null,
    SumupConfig? Sumup = null,
    CreationDefault? CreationDefault = null,
    InputMask? Mask = null
);

public record ObjectDataTypeField(
    string Name,
    bool Required = false,
    InputMask? Mask = null,
    bool OverrideDropdownOptions = false,
    List<string>? EnabledOptionKeys = null
);

public record ObjectDataType(string Key, string Label, List<ObjectDataTypeField> Fields);

public record FieldSetRule(string Field, string Condition, string Value);

public record InputMask(
    string CharacterSet = "",
    int MinimumLength = 1,
    string RequiredCharacters = "",
    string? Pattern = null
);

public record ObjectDefinition(
    List<ObjectField> Fields,
    ListView? View = null,
    bool SumupsPending = false,
    List<ObjectDataType>? DataTypes = null,
    string? DefaultDataTypeKey = null,
    List<FieldSetRule>? FieldSetRules = null
);

public record FieldPresentation(
    string Name,
    string Section = "",
    int EditorOrder = 0,
    bool ShowInEditor = true,
    bool ShowInList = true,
    int? ListOrder = null,
    string? Label = null
);

public record LayoutPresentation(List<FieldPresentation> Fields);

public record StoredObjectDefinition(ObjectDefinition Object, LayoutPresentation Layout);

public static class ObjectModel
{
    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    static bool TryProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        value = default;
        return false;
    }

    public static StoredObjectDefinition Stored(string? json)
    {
        using var document = JsonDocument.Parse(json ?? "[]");
        try
        {
            if (
                document.RootElement.ValueKind == JsonValueKind.Object
                && TryProperty(document.RootElement, "object", out _)
            )
            {
                var stored = document.RootElement.Deserialize<StoredObjectDefinition>(Json);
                if (
                    stored?.Object?.Fields == null
                    || stored.Layout?.Fields == null
                    || stored.Object.Fields.Any(f => f == null)
                    || stored.Layout.Fields.Any(f => f == null)
                )
                    throw new ApiError(400, "Object and layout fields are required.");
                var legacySections = new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase
                );
                if (
                    TryProperty(document.RootElement, "object", out var objectElement)
                    && TryProperty(objectElement, "fields", out var objectFields)
                )
                    foreach (var field in objectFields.EnumerateArray())
                        if (
                            TryProperty(field, "name", out var name)
                            && TryProperty(field, "section", out var section)
                            && name.ValueKind == JsonValueKind.String
                            && section.ValueKind is JsonValueKind.String or JsonValueKind.Null
                        )
                            legacySections[name.GetString() ?? ""] = section.GetString() ?? "";
                var layoutHasSection = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (
                    TryProperty(document.RootElement, "layout", out var layoutElement)
                    && TryProperty(layoutElement, "fields", out var layoutFields)
                )
                    foreach (var field in layoutFields.EnumerateArray())
                        if (
                            TryProperty(field, "name", out var name)
                            && TryProperty(field, "section", out _)
                            && name.ValueKind == JsonValueKind.String
                        )
                            layoutHasSection.Add(name.GetString() ?? "");
                stored = stored with
                {
                    Layout = new(
                        stored
                            .Layout.Fields.Select(f =>
                                !layoutHasSection.Contains(f.Name)
                                && legacySections.TryGetValue(f.Name, out var section)
                                    ? f with
                                    {
                                        Section = section,
                                    }
                                    : f
                            )
                            .ToList()
                    ),
                };
                return Normalize(stored);
            }
            return Normalize(Split(DatabaseService.ParseLegacyLayout(document.RootElement)));
        }
        catch (JsonException)
        {
            throw new ApiError(400, "Invalid object configuration.");
        }
    }

    public static StoredObjectDefinition Normalize(StoredObjectDefinition stored)
    {
        var sourceFields = stored.Object.Fields.Where(field => !DataTypeColumn.Is(field.Name)).ToList();
        var legacyLabels = sourceFields
            .GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First().Label,
                StringComparer.OrdinalIgnoreCase
            );
        var names = sourceFields.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var layout = stored
            .Layout.Fields.Where(f => names.Contains(f.Name))
            .GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var field = g.First();
                return field with
                {
                    Section = field.Section ?? "",
                    Label = field.Label == null
                        ? legacyLabels.GetValueOrDefault(field.Name, field.Name)
                        : field.Label,
                };
            })
            .ToList();
        for (var i = 0; i < sourceFields.Count; i++)
        {
            var field = sourceFields[i];
            if (!layout.Any(f => f.Name.Equals(field.Name, StringComparison.OrdinalIgnoreCase)))
                layout.Add(
                    new(
                        field.Name,
                        EditorOrder: i,
                        ListOrder: i,
                        Label: field.Label
                    )
                );
        }
        var labels = layout.ToDictionary(f => f.Name, f => f.Label, StringComparer.OrdinalIgnoreCase);
        var dataTypes = stored.Object.DataTypes?
            .Where(type => type != null && !string.IsNullOrEmpty(type.Key))
            .GroupBy(type => type.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        var defaultKey = stored.Object.DefaultDataTypeKey;
        if (dataTypes is not { Count: > 0 })
        {
            defaultKey = "default";
            dataTypes =
            [
                new(
                    defaultKey,
                    "Default",
                    sourceFields
                        .Select(f => new ObjectDataTypeField(
                            f.Name,
                            f.Required,
                            f.Mask,
                            false,
                            []
                        ))
                        .ToList()
                ),
            ];
        }
        else
        {
            defaultKey = string.IsNullOrEmpty(defaultKey) ? dataTypes[0].Key : defaultKey;
            dataTypes = dataTypes
                .Where(type => type != null)
                .Select(type =>
                {
                    var configured = (type.Fields ?? [])
                        .Where(field => field != null && names.Contains(field.Name))
                        .GroupBy(field => field.Name, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
                    return type with
                    {
                        Fields = sourceFields.Select(field =>
                            {
                                var setting = configured.GetValueOrDefault(field.Name)
                                    ?? new ObjectDataTypeField(
                                        field.Name,
                                        Required: string.Equals(
                                            type.Key,
                                            defaultKey,
                                            StringComparison.OrdinalIgnoreCase
                                        ) && field.Required,
                                        Mask: string.Equals(
                                            type.Key,
                                            defaultKey,
                                            StringComparison.OrdinalIgnoreCase
                                        )
                                            ? field.Mask
                                            : null
                                    );
                                return setting with
                                {
                                    Name = field.Name,
                                    EnabledOptionKeys = setting.OverrideDropdownOptions
                                        ? (setting.EnabledOptionKeys ?? []).ToList()
                                        : [],
                                };
                            })
                            .ToList(),
                    };
                })
                .ToList();
            defaultKey = dataTypes
                .FirstOrDefault(type =>
                    type.Key != null
                    && type.Key.Equals(defaultKey, StringComparison.OrdinalIgnoreCase)
                )
                ?.Key ?? dataTypes[0].Key;
        }
        var definition = stored.Object with
        {
            // Keep the legacy projection populated while presentation owns labels.
            Fields = sourceFields.Select(f =>
                    f with
                    {
                        Label = labels.GetValueOrDefault(f.Name) ?? f.Label ?? f.Name,
                        Required = false,
                        Mask = null,
                    }
                )
                .ToList(),
            DataTypes = dataTypes,
            DefaultDataTypeKey = defaultKey,
            FieldSetRules = (stored.Object.FieldSetRules ?? []).Where(rule => rule != null).ToList(),
        };
        return new(definition, new(layout));
    }

    public static StoredObjectDefinition Split(LayoutDefinition definition)
    {
        var objects = definition
            .Fields.Select(f => new ObjectField(
                f.Name,
                f.Label,
                f.ReadOnly,
                f.Widget,
                f.Lookup,
                f.Options,
                f.Join,
                f.Required,
                f.Formula,
                f.Sumup,
                f.CreationDefault,
                f.Mask
            ))
            .ToList();
        var layout = definition
            .Fields.Select(
                (f, i) =>
                    new FieldPresentation(
                        f.Name,
                        Section: f.Section,
                        EditorOrder: f.Order,
                        ShowInEditor: !f.Hidden,
                        ShowInList: f.ShowInList,
                        ListOrder: f.ListOrder ?? f.Order,
                        Label: f.Label
                    )
            )
            .ToList();
        return new(new(objects, definition.View, definition.SumupsPending), new(layout));
    }

    public static LayoutPresentation CompleteLayout(
        ObjectDefinition definition,
        LayoutPresentation presentation
    ) => Normalize(new(definition, presentation)).Layout;

    public static ObjectDefinition WithColumns(
        ObjectDefinition definition,
        IEnumerable<ColumnInfo> columns
    )
    {
        var fields = definition.Fields.ToList();
        foreach (var column in columns)
            if (
                !column.Name.Equals("datatype", StringComparison.OrdinalIgnoreCase)
                &&
                !fields.Any(field =>
                    field.Name.Equals(column.Name, StringComparison.OrdinalIgnoreCase)
                )
            )
                fields.Add(
                    new(column.Name, column.Name, column.Generated || column.AutoIncrement, "auto")
                );
        return Normalize(new(definition with
        {
            Fields = fields
        }, new([]))).Object;
    }

    public static ObjectDefinition ParseObject(JsonElement input)
    {
        try
        {
            var result = input.Deserialize<ObjectDefinition>(Json)
                ?? throw new ApiError(400, "Object fields are required.");
            // CompleteLayout normalizes before the full database-aware validation.
            // Reject malformed field structures here so they cannot reach the
            // normalizer and surface as a non-API exception.
            if (result.Fields == null || result.Fields.Any(field => field == null))
                throw new ApiError(400, "Object fields are required.");
            if (result.Fields.Any(field => string.IsNullOrWhiteSpace(field.Name)))
                throw new ApiError(400, "Object field names are required.");
            return result;
        }
        catch (JsonException)
        {
            throw new ApiError(400, "Invalid object configuration.");
        }
    }

    public static bool HasProperty(JsonElement input, string name) =>
        input.ValueKind == JsonValueKind.Object && TryProperty(input, name, out _);

    public static bool HasLegacyFieldBehavior(JsonElement input)
    {
        var fields = input;
        if (
            input.ValueKind != JsonValueKind.Array
            && (
                input.ValueKind != JsonValueKind.Object
                || !TryProperty(input, "fields", out fields)
            )
        )
            return false;
        if (fields.ValueKind != JsonValueKind.Array)
            return false;
        return fields.EnumerateArray().Any(field =>
            field.ValueKind == JsonValueKind.Object
            && (TryProperty(field, "required", out _) || TryProperty(field, "mask", out _))
        );
    }

    public static void ValidateLegacyLayoutWrite(JsonElement input)
    {
        if (HasLegacyFieldBehavior(input))
            throw new ApiError(
                400,
                "Required and mask settings moved to Data Types. Save them through the object endpoint."
            );
    }

    public static bool HasCanonicalDataTypes(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return false;
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && TryProperty(document.RootElement, "object", out var definition)
                && TryProperty(definition, "dataTypes", out var types)
                && types.ValueKind == JsonValueKind.Array;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static LayoutDefinition Merge(ObjectDefinition definition, LayoutPresentation layout)
    {
        definition = Normalize(new(definition, layout)).Object;
        var presentation = layout.Fields.ToDictionary(
            f => f.Name,
            StringComparer.OrdinalIgnoreCase
        );
        return new(
            definition
                .Fields.Select(
                    (f, i) =>
                    {
                        var p =
                            presentation.GetValueOrDefault(f.Name)
                            ?? new FieldPresentation(f.Name, EditorOrder: i, ListOrder: i);
                        var setting = definition.DataTypes!
                            .Single(type => type.Key == definition.DefaultDataTypeKey)
                            .Fields.Single(candidate => candidate.Name.Equals(f.Name, StringComparison.OrdinalIgnoreCase));
                        return new LayoutField(
                            f.Name,
                            p.Label ?? f.Label,
                            p.Section,
                            p.EditorOrder,
                            !p.ShowInEditor,
                            f.ReadOnly,
                            f.Widget,
                            f.Lookup,
                            f.Options,
                            p.ShowInList,
                            p.ListOrder,
                            f.Join,
                            setting.Required,
                            f.Formula,
                            f.Sumup,
                            f.CreationDefault,
                            setting.Mask
                        )
                        {
                            EnabledOptionKeys = setting.OverrideDropdownOptions
                                ? setting.EnabledOptionKeys ?? []
                                : null,
                            DataTypeConstraints = definition.DataTypes!
                                .Select(type =>
                                {
                                    var typeSetting = type.Fields.Single(candidate =>
                                        candidate.Name.Equals(f.Name, StringComparison.OrdinalIgnoreCase)
                                    );
                                    return new DataTypeFieldConstraint(
                                        type.Key,
                                        typeSetting.Required,
                                        typeSetting.Mask,
                                        typeSetting.OverrideDropdownOptions
                                            ? typeSetting.EnabledOptionKeys ?? []
                                            : null
                                    );
                                })
                                .ToList(),
                        };
                    }
                )
                .ToList(),
            definition.View,
            definition.SumupsPending
        );
    }

    public static LayoutDefinition Merge(StoredObjectDefinition stored) =>
        Merge(stored.Object, stored.Layout);

    public static string Serialize(StoredObjectDefinition stored) =>
        JsonSerializer.Serialize(Normalize(stored));

    public static string Serialize(LayoutDefinition definition) => Serialize(Split(definition));

    public static string Serialize(ObjectDefinition definition, LayoutPresentation presentation) =>
        Serialize(new StoredObjectDefinition(definition, presentation));

    public static void Migrate(AppDb db)
    {
        var changed = false;
        foreach (var configuration in db.Layouts)
        {
            var normalized = Serialize(Stored(configuration.FieldsJson));
            if (configuration.FieldsJson == normalized)
                continue;
            configuration.FieldsJson = normalized;
            changed = true;
        }
        if (changed)
            db.SaveChanges();
    }
}

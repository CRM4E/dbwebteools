using System.Text.Json;

namespace DbWeb.Api;

public static class FieldSetRules
{
    static object? FormulaValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String => value.GetString(),
        _ => value.ToString(),
    };

    public static void Apply(
        List<FieldSetRule> rules,
        List<LayoutField> fields,
        List<ColumnInfo> columns,
        Dictionary<string, object?>? current,
        Dictionary<string, JsonElement> values,
        Action<JsonElement, ColumnInfo>? validate = null
    )
    {
        if (rules.Count == 0)
            return;
        var effective = columns.ToDictionary(
            column => column.Name,
            column => current?.GetValueOrDefault(column.Name),
            StringComparer.OrdinalIgnoreCase
        );
        foreach (var submitted in values)
            effective[submitted.Key] = FormulaValue(submitted.Value);
        foreach (var rule in rules)
        {
            var target = columns.Single(column =>
                column.Name.Equals(rule.Field, StringComparison.OrdinalIgnoreCase)
            );
            try
            {
                var condition = Formulas.Evaluate(
                    Formulas.Compile(rule.Condition, columns, fields),
                    columns,
                    effective
                );
                if (condition is not bool matches)
                    throw new ApiError(400, $"Field set rule for {target.Name} must return true or false.");
                if (!matches)
                    continue;
                var result = Formulas.Evaluate(
                    Formulas.Compile(rule.Value, columns, fields),
                    columns,
                    effective
                );
                var serialized = JsonSerializer.SerializeToElement(result);
                validate?.Invoke(serialized, target);
                values[target.Name] = serialized;
                effective[target.Name] = result;
            }
            catch (ApiError error) when (!error.Message.StartsWith("Field set rule"))
            {
                throw new ApiError(400, $"Cannot apply field set rule for {target.Name}.");
            }
            catch (ApiError)
            {
                throw;
            }
            catch (Exception)
            {
                throw new ApiError(400, $"Cannot apply field set rule for {target.Name}.");
            }
        }
    }
}

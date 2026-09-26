using System.Globalization;
using NCalc;
using NCalc.Handlers;

namespace DbWeb.Api;

// NCalc interprets expressions only: no SQL, reflection, scripting, filesystem, or network functions.
public static class Formulas
{
    const int TextLimit = 8192;
    static readonly HashSet<string> Functions = new(StringComparer.Ordinal)
    {
        "Concat",
        "Upper",
        "Lower",
        "Trim",
        "Length",
        "Substring",
        "Replace",
        "Coalesce",
        "DropdownDisplay",
        "Round",
        "Abs",
        "Floor",
        "Ceiling",
        "Min",
        "Max",
        "if",
    };

    public static Expression Compile(
        string? formula,
        List<ColumnInfo> columns,
        List<LayoutField>? fields = null
    )
    {
        if (string.IsNullOrWhiteSpace(formula) || formula.Length > 1024)
            throw new ApiError(400, "Formula must contain 1–1024 characters.");
        // Bound parenthesis nesting before entering the parser (quoted text/identifiers are ignored).
        var nesting = 0;
        char quote = '\0';
        for (var i = 0; i < formula.Length; i++)
        {
            var ch = formula[i];
            if (quote != '\0')
            {
                if (ch == '\\' && quote != ']')
                {
                    i++;
                    continue;
                }
                if (ch == quote)
                    quote = '\0';
                continue;
            }
            if (ch is '\'' or '"' or '[')
            {
                quote = ch == '[' ? ']' : ch;
                continue;
            }
            if (ch == '(' && ++nesting > 32)
                throw new ApiError(400, "Formula nesting is limited to 32 levels.");
            if (ch == ')')
                nesting--;
        }
        try
        {
            var expression = new Expression(formula, cultureInfo: CultureInfo.InvariantCulture);
            expression.Options =
                ExpressionOptions.NoCache
                | ExpressionOptions.DecimalAsDefault
                | ExpressionOptions.AllowNullParameter
                | ExpressionOptions.OverflowProtection;
            if (
                expression
                    .GetParameterNames()
                    .Any(n => n != "null" && !columns.Any(c => c.Name == n && Scalar(c)))
            )
                throw new ApiError(
                    400,
                    "Formula references must be existing scalar stored columns; use [column_name]."
                );
            if (expression.GetFunctionNames().Any(n => !Functions.Contains(n)))
                throw new ApiError(400, "Formula uses an unsupported function.");
            ValidateTree(expression.LogicalExpression!, 0, columns, fields ?? []);
            expression.Functions["DropdownDisplay"] = a =>
            {
                var fieldName = Text(a.Evaluate(0));
                var key = a.Evaluate(1);
                if (key == null)
                    return null;
                var field = fields!
                    .Single(f => f.Name == fieldName && f.Widget == "dropdown");
                return field.Options
                    ?.FirstOrDefault(o => o.Key == Text(key))
                    ?.Display;
            };
            foreach (var name in new[] { "Round", "Abs", "Floor", "Ceiling", "Min", "Max" })
                expression.Functions[name] = a =>
                {
                    var args = Enumerable.Range(0, a.Count).Select(i => a.Evaluate(i)).ToArray();
                    if (args.Any(v => v == null))
                        return null;
                    var numbers = args.Select(v =>
                            Convert.ToDecimal(v, CultureInfo.InvariantCulture)
                        )
                        .ToArray();
                    return name switch
                    {
                        "Round" => decimal.Round(
                            numbers[0],
                            checked((int)numbers[1]),
                            MidpointRounding.ToEven
                        ),
                        "Abs" => decimal.Abs(numbers[0]),
                        "Floor" => decimal.Floor(numbers[0]),
                        "Ceiling" => decimal.Ceiling(numbers[0]),
                        "Min" => decimal.Min(numbers[0], numbers[1]),
                        _ => decimal.Max(numbers[0], numbers[1]),
                    };
                };
            expression.Functions["Concat"] = a =>
            {
                if (a.Count is < 1 or > 20)
                    throw new ArgumentException();
                return Bound(
                    string.Concat(Enumerable.Range(0, a.Count).Select(i => Text(a.Evaluate(i))))
                );
            };
            expression.Functions["Upper"] = a =>
            {
                Arity(a, 1);
                return Text(a.Evaluate(0)).ToUpperInvariant();
            };
            expression.Functions["Lower"] = a =>
            {
                Arity(a, 1);
                return Text(a.Evaluate(0)).ToLowerInvariant();
            };
            expression.Functions["Trim"] = a =>
            {
                Arity(a, 1);
                return Text(a.Evaluate(0)).Trim();
            };
            expression.Functions["Length"] = a =>
            {
                Arity(a, 1);
                return Text(a.Evaluate(0)).Length;
            };
            expression.Functions["Substring"] = a =>
            {
                Arity(a, 3);
                return Text(a.Evaluate(0))
                    .Substring(Convert.ToInt32(a.Evaluate(1)), Convert.ToInt32(a.Evaluate(2)));
            };
            expression.Functions["Replace"] = a =>
            {
                Arity(a, 3);
                var text = Text(a.Evaluate(0));
                var from = Text(a.Evaluate(1));
                var to = Text(a.Evaluate(2));
                if (from.Length == 0 || (long)text.Length * Math.Max(1, to.Length) > TextLimit)
                    throw new ArgumentException();
                return Bound(text.Replace(from, to, StringComparison.Ordinal));
            };
            expression.Functions["Coalesce"] = a =>
            {
                if (a.Count is < 2 or > 20)
                    throw new ArgumentException();
                for (var i = 0; i < a.Count; i++)
                {
                    var value = a.Evaluate(i);
                    if (value != null)
                        return value;
                }
                return null;
            };
            return expression;
        }
        catch (ApiError)
        {
            throw;
        }
        catch (Exception)
        {
            throw new ApiError(
                400,
                "Invalid formula syntax. Use [column_name], literals, operators, and supported functions."
            );
        }
    }

    public static HashSet<string> Dependencies(
        string? formula,
        List<ColumnInfo>? columns,
        List<LayoutField> fields
    )
    {
        var expression =
            columns != null
                ? Compile(formula, columns, fields)
                : new Expression(formula ?? "", ExpressionOptions.NoCache);
        var names = expression.GetParameterNames().Where(n => n != "null").ToHashSet();
        void Walk(LogicalExpression node)
        {
            if (
                node is Function f
                && f.Identifier.Name == "DropdownDisplay"
                && f.Parameters[0] is ValueExpression { Value: string name }
            )
                names.Add(name);
            IEnumerable<LogicalExpression> children = node switch
            {
                Function fn => fn.Parameters,
                BinaryExpression b => [b.LeftExpression, b.RightExpression],
                UnaryExpression u => [u.Expression],
                TernaryExpression t => [t.LeftExpression, t.MiddleExpression, t.RightExpression],
                LogicalExpressionList l => l,
                _ => [],
            };
            foreach (var child in children)
                Walk(child);
        }
        Walk(expression.LogicalExpression!);
        return names;
    }

    static void ValidateTree(
        LogicalExpression node,
        int depth,
        List<ColumnInfo> columns,
        List<LayoutField> fields
    )
    {
        if (depth > 32)
            throw new ApiError(400, "Formula nesting is limited to 32 levels.");
        IEnumerable<LogicalExpression> children = node switch
        {
            Function f => f.Parameters,
            BinaryExpression b => [b.LeftExpression, b.RightExpression],
            UnaryExpression u => [u.Expression],
            TernaryExpression t => [t.LeftExpression, t.MiddleExpression, t.RightExpression],
            LogicalExpressionList list => list,
            _ => [],
        };
        if (node is Function function)
        {
            var count = function.Parameters.Count;
            var valid = function.Identifier.Name switch
            {
                "Concat" => count is >= 1 and <= 20,
                "Coalesce" => count is >= 2 and <= 20,
                "Replace" or "Substring" or "if" => count == 3,
                "Round" or "Min" or "Max" or "DropdownDisplay" => count == 2,
                _ => count == 1,
            };
            if (!valid)
                throw new ApiError(
                    400,
                    $"Incorrect number of arguments for {function.Identifier.Name}."
                );
            if (
                function.Identifier.Name == "DropdownDisplay"
                && (
                    function.Parameters[0] is not ValueExpression { Value: string fieldName }
                    || !columns.Any(c => c.Name == fieldName)
                    || !fields.Any(f => f.Name == fieldName && f.Widget == "dropdown")
                )
            )
                throw new ApiError(
                    400,
                    "DropdownDisplay requires a quoted dropdown column name from this layout, for example DropdownDisplay('status', [status])."
                );
        }
        if (
            node is BinaryExpression binary
            && binary.Type
                is not (
                    BinaryExpressionType.And
                    or BinaryExpressionType.Or
                    or BinaryExpressionType.NotEqual
                    or BinaryExpressionType.LesserOrEqual
                    or BinaryExpressionType.GreaterOrEqual
                    or BinaryExpressionType.Lesser
                    or BinaryExpressionType.Greater
                    or BinaryExpressionType.Equal
                    or BinaryExpressionType.Minus
                    or BinaryExpressionType.Plus
                    or BinaryExpressionType.Modulo
                    or BinaryExpressionType.Div
                    or BinaryExpressionType.Times
                    or BinaryExpressionType.Coalesce
                )
        )
            throw new ApiError(
                400,
                "Formula operator is not supported. Use arithmetic, comparisons, and boolean operators."
            );
        foreach (var child in children)
            ValidateTree(child, depth + 1, columns, fields);
    }

    static bool Scalar(ColumnInfo c) =>
        !c.Type.Contains("blob") && c.Type is not "binary" and not "varbinary" and not "geometry";

    static void Arity(FunctionData args, int count)
    {
        if (args.Count != count)
            throw new ArgumentException();
    }

    static string Bound(string value) =>
        value.Length <= TextLimit ? value : throw new ArgumentException();

    static string Text(object? value) =>
        Bound(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "");

    static object? Typed(object? value, ColumnInfo column)
    {
        if (value == null || value is DBNull)
            return null;
        if (
            column.Type
            is "tinyint"
                or "smallint"
                or "mediumint"
                or "int"
                or "bigint"
                or "decimal"
                or "float"
                or "double"
        )
            return decimal.Parse(Text(value), NumberStyles.Float, CultureInfo.InvariantCulture);
        return Text(value);
    }

    public static object? Evaluate(
        Expression expression,
        List<ColumnInfo> columns,
        Dictionary<string, object?> values
    )
    {
        foreach (var name in expression.GetParameterNames().Where(n => n != "null"))
            expression.Parameters[name] = Typed(
                values.GetValueOrDefault(name),
                columns.Single(c => c.Name == name)
            );
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        return expression.Evaluate(deadline.Token);
    }

    public static List<ColumnInfo> Populate(
        List<LayoutField> fields,
        List<ColumnInfo> columns,
        List<RecordRow> rows,
        HashSet<string>? changedFields = null
    )
    {
        var result = new List<ColumnInfo>();
        foreach (var field in fields.Where(f => f.Widget == "formula"))
        {
            var expression = Compile(field.Formula, columns, fields);
            var references = expression.GetParameterNames().Where(n => n != "null").ToList();
            // Runtime recalculation follows row-value parameters only. Dependencies() also
            // includes structural references such as DropdownDisplay's configured field.
            if (changedFields != null && !references.Any(changedFields.Contains))
                continue;
            result.Add(new(field.Name, "text", true, false, true, false, null));
            foreach (var row in rows)
            {
                try
                {
                    row.CalculationErrors.Remove(field.Name);
                    foreach (var name in references)
                        expression.Parameters[name] = Typed(
                            row.Values.GetValueOrDefault(name),
                            columns.Single(c => c.Name == name)
                        );
                    using var deadline = new CancellationTokenSource(
                        TimeSpan.FromMilliseconds(100)
                    );
                    var value = expression.Evaluate(deadline.Token);
                    if (
                        value is double d && !double.IsFinite(d)
                        || value is float f && !float.IsFinite(f)
                    )
                        throw new ArithmeticException();
                    row.JoinedValues[field.Name] = value == null ? null : Text(value);
                }
                catch (Exception)
                {
                    row.JoinedValues[field.Name] = null;
                    row.CalculationErrors[field.Name] =
                        "Cannot calculate: check missing values, numeric inputs, and division by zero.";
                }
            }
        }
        return result;
    }
}

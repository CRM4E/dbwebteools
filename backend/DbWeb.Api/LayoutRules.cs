using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace DbWeb.Api;

public static class LayoutRules
{
    enum MaskTokenKind
    {
        Literal,
        Letter,
        Digit,
        Alphanumeric,
    }

    readonly record struct MaskToken(MaskTokenKind Kind, bool Required, char Literal = '\0');

    public static void Validate(LayoutField field, ColumnInfo column)
    {
        if (
            field.Required
            && (
                field.Hidden
                || field.ReadOnly
                || column.Generated
                || column.AutoIncrement
                || field.Widget == "join"
            )
        )
            throw new ApiError(400, "Required fields must be visible, editable stored columns.");
        if (field.Widget == "datetime" && column.Type is not "datetime" and not "timestamp")
            throw new ApiError(400, "DateTime controls require a DATETIME or TIMESTAMP column.");
        if (
            field.Widget == "date"
            && column.Type is not "date" and not "datetime" and not "timestamp"
        )
            throw new ApiError(400, "Date controls require a DATE, DATETIME, or TIMESTAMP column.");
        if (field.Widget == "email" && (column.Type != "varchar" || column.Length != 255))
            throw new ApiError(400, "Email controls require a VARCHAR(255) column.");
        ValidateMaskConfiguration(field, column);
        if (field.Widget != "dropdown")
        {
            if (field.Options is { Count: > 0 })
                throw new ApiError(400, "Only dropdown controls may define options.");
            return;
        }
        if (
            column.Type
            is not "char"
                and not "varchar"
                and not "tinytext"
                and not "text"
                and not "mediumtext"
                and not "longtext"
        )
            throw new ApiError(400, "Dropdown controls require a text column.");
        if (
            field.Options is not { Count: > 0 and <= 200 }
            || field.Options.Any(o =>
                o == null
                || string.IsNullOrWhiteSpace(o.Key)
                || string.IsNullOrWhiteSpace(o.Display)
                || o.Key.Length > 256
                || o.Display.Length > 256
                || o.Key != o.Key.Trim()
                || o.Display != o.Display.Trim()
            )
        )
            throw new ApiError(
                400,
                "Define 1–200 dropdown options with non-blank keys and labels (up to 256 characters, no surrounding spaces)."
            );
        if (
            field.Options.Select(o => o.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count()
                != field.Options.Count
            || field
                .Options.Select(o => o.Display)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() != field.Options.Count
        )
            throw new ApiError(
                400,
                "Dropdown keys and display labels must each be unique (ignoring case)."
            );
    }

    public static void ValidateMaskConfiguration(LayoutField field, ColumnInfo column)
    {
        var mask = field.Mask;
        if (mask == null)
            return;
        if (field.Widget is not "auto" and not "text" and not "textarea")
            throw new ApiError(400, "Input masks are supported only for text controls.");
        if (field.ReadOnly || column.Generated || column.AutoIncrement)
            throw new ApiError(400, "Input masks require an editable stored field.");
        if (
            column.Type
            is not "char"
                and not "varchar"
                and not "tinytext"
                and not "text"
                and not "mediumtext"
                and not "longtext"
        )
            throw new ApiError(400, "Input masks require a text database field.");
        if (mask.Pattern != null)
        {
            if (
                mask.CharacterSet != ""
                || mask.MinimumLength != 1
                || !string.IsNullOrEmpty(mask.RequiredCharacters)
            )
                throw new ApiError(400, "Exact masks cannot be combined with character rules.");
            var tokens = ParseExactMask(mask.Pattern);
            if (column.Length is > 0 && tokens.Count > column.Length)
                throw new ApiError(
                    400,
                    "The exact mask cannot exceed the database field length."
                );
            return;
        }
        if (mask.CharacterSet is not "letters" and not "digits" and not "alphanumeric")
            throw new ApiError(
                400,
                "Choose letters, numbers, or letters and numbers for the input mask."
            );
        if (mask.MinimumLength is < 1 or > 4000)
            throw new ApiError(400, "Input mask minimum length must be between 1 and 4000.");
        if (column.Length is > 0 && mask.MinimumLength > column.Length)
            throw new ApiError(
                400,
                "Input mask minimum length cannot exceed the database field length."
            );
        var required = mask.RequiredCharacters ?? "";
        if (
            required.Length > 16
            || required.Any(c => c is < '!' or > '~' || char.IsLetterOrDigit(c))
            || required.Distinct().Count() != required.Length
        )
            throw new ApiError(
                400,
                "Input mask required characters must be up to 16 unique punctuation characters without spaces."
            );
        if (mask.MinimumLength < required.Length)
            throw new ApiError(
                400,
                "Input mask minimum length must allow all required characters."
            );
    }

    public static string DescribeMask(InputMask mask)
    {
        if (mask.Pattern != null)
            return $"Format: {mask.Pattern}. # is a number, A a letter, and X a letter or number. Add ? after any position to make it optional. Other characters are required literals; use \\ to escape #, A, X, ?, or \\.";
        var allowed = mask.CharacterSet switch
        {
            "letters" => "letters",
            "digits" => "numbers",
            _ => "letters and numbers",
        };
        var required = string.IsNullOrEmpty(mask.RequiredCharacters)
            ? ""
            : $" Include each of these characters: {string.Join(" ", mask.RequiredCharacters.Select(c => $"'{c}'"))}.";
        return $"Use at least {mask.MinimumLength} characters. Allowed: {allowed}{(required.Length > 0 ? " plus the required characters." : ".")}{required}";
    }

    public static void ValidateMaskValues(
        IEnumerable<LayoutField> fields,
        Dictionary<string, JsonElement> values
    )
    {
        foreach (var field in fields.Where(f => f.Mask != null))
        {
            if (
                !values.TryGetValue(field.Name, out var value)
                || value.ValueKind == JsonValueKind.Null
            )
                continue;
            if (value.ValueKind != JsonValueKind.String)
                throw new ApiError(
                    400,
                    $"{field.Label} must match the input mask. {DescribeMask(field.Mask!)}"
                );
            var text = value.GetString() ?? "";
            if (text.Length == 0)
                continue;
            var mask = field.Mask!;
            if (mask.Pattern != null)
            {
                if (!MatchesExactMask(ParseExactMask(mask.Pattern), text))
                    throw new ApiError(
                        400,
                        $"{field.Label} must match the input mask. {DescribeMask(mask)}"
                    );
                continue;
            }
            var required = mask.RequiredCharacters ?? "";
            bool Allowed(char c) =>
                required.Contains(c)
                || mask.CharacterSet switch
                {
                    "letters" => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z',
                    "digits" => c is >= '0' and <= '9',
                    _ =>
                        c
                        is >= 'A' and <= 'Z'
                            or >= 'a' and <= 'z'
                            or >= '0' and <= '9',
                };
            if (
                text.Length < mask.MinimumLength
                || text.Any(c => !Allowed(c))
                || required.Any(c => !text.Contains(c))
            )
                throw new ApiError(
                    400,
                    $"{field.Label} must match the input mask. {DescribeMask(mask)}"
                );
        }
    }

    static List<MaskToken> ParseExactMask(string pattern)
    {
        if (pattern.Length is < 1 or > 1024)
            throw new ApiError(400, "Exact mask patterns must be between 1 and 1024 characters.");
        var tokens = new List<MaskToken>();
        var hasPlaceholder = false;
        for (var index = 0; index < pattern.Length; index++)
        {
            var symbol = pattern[index];
            if (symbol is < ' ' or > '~')
                throw new ApiError(400, "Exact mask patterns may contain printable characters only.");
            MaskToken token;
            if (symbol == '\\')
            {
                if (++index >= pattern.Length)
                    throw new ApiError(400, "An exact mask cannot end with an escape character.");
                var literal = pattern[index];
                if (literal is not ('#' or 'A' or 'X' or '?' or '\\'))
                    throw new ApiError(400, "Only mask symbols need an escape character.");
                token = new(MaskTokenKind.Literal, true, literal);
            }
            else
            {
                if (symbol == '?')
                    throw new ApiError(400, "An optional marker must follow a mask position.");
                token = symbol switch
                {
                    '#' => new MaskToken(MaskTokenKind.Digit, true),
                    'A' => new MaskToken(MaskTokenKind.Letter, true),
                    'X' => new MaskToken(MaskTokenKind.Alphanumeric, true),
                    _ => new MaskToken(MaskTokenKind.Literal, true, symbol),
                };
            }
            if (index + 1 < pattern.Length && pattern[index + 1] == '?')
            {
                token = token with
                {
                    Required = false
                };
                index++;
            }
            hasPlaceholder |= token.Kind != MaskTokenKind.Literal;
            tokens.Add(token);
            if (tokens.Count > 256)
                throw new ApiError(400, "Exact masks may contain at most 256 positions.");
        }
        if (!hasPlaceholder)
            throw new ApiError(400, "An exact mask must contain at least one character placeholder.");
        return tokens;
    }

    static bool MatchesExactMask(IReadOnlyList<MaskToken> tokens, string value)
    {
        var positions = new HashSet<int> { 0 };
        foreach (var token in tokens)
        {
            var next = new HashSet<int>();
            foreach (var position in positions)
            {
                if (!token.Required)
                    next.Add(position);
                if (position >= value.Length)
                    continue;
                var matches = token.Kind switch
                {
                    MaskTokenKind.Literal => value[position] == token.Literal,
                    MaskTokenKind.Letter =>
                        value[position] is >= 'A' and <= 'Z' or >= 'a' and <= 'z',
                    MaskTokenKind.Digit => value[position] is >= '0' and <= '9',
                    _ =>
                        value[position]
                        is >= 'A' and <= 'Z'
                            or >= 'a' and <= 'z'
                            or >= '0' and <= '9',
                };
                if (matches)
                    next.Add(position + 1);
            }
            positions = next;
            if (positions.Count == 0)
                return false;
        }
        return positions.Contains(value.Length);
    }

    public static void ValidateRequiredValues(
        IEnumerable<LayoutField> fields,
        Dictionary<string, JsonElement> values,
        Dictionary<string, object?>? existing
    )
    {
        foreach (var field in fields.Where(f => f.Required))
        {
            var empty = values.TryGetValue(field.Name, out var value)
                ? value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
                    || (
                        value.ValueKind == JsonValueKind.String
                        && string.IsNullOrWhiteSpace(value.GetString())
                    )
                : existing == null
                    || !existing.TryGetValue(field.Name, out var stored)
                    || stored == null
                    || stored is DBNull
                    || (stored is string text && string.IsNullOrWhiteSpace(text));
            if (empty)
                throw new ApiError(
                    400,
                    $"{(string.IsNullOrWhiteSpace(field.Label) ? field.Name : field.Label)} is required."
                );
        }
    }

    public static void ValidateDropdownValues(
        List<LayoutField> fields,
        Dictionary<string, JsonElement> values
    )
    {
        foreach (var field in fields.Where(f => f.Widget == "dropdown"))
            if (
                values.TryGetValue(field.Name, out var value)
                && value.ValueKind != JsonValueKind.Null
                && (
                    value.ValueKind != JsonValueKind.String
                    || field.Options?.Any(o =>
                        o.Key == value.GetString()
                        && (
                            field.EnabledOptionKeys == null
                            || field.EnabledOptionKeys.Contains(
                                o.Key,
                                StringComparer.OrdinalIgnoreCase
                            )
                        )
                    ) != true
                )
            )
                throw new ApiError(400, $"Choose a configured dropdown value for {field.Name}.");
    }

    public static void ValidateEmailValues(
        IEnumerable<LayoutField> fields,
        Dictionary<string, JsonElement> values
    )
    {
        var validator = new EmailAddressAttribute();
        foreach (var field in fields.Where(f => f.Widget == "email"))
        {
            if (
                !values.TryGetValue(field.Name, out var value)
                || value.ValueKind == JsonValueKind.Null
            )
                continue;
            if (value.ValueKind != JsonValueKind.String)
                throw new ApiError(400, $"Enter a valid email address for {field.Label}.");
            var text = value.GetString() ?? "";
            if (text.Length == 0)
                continue;
            if (text.Length > 255 || text != text.Trim() || !validator.IsValid(text))
                throw new ApiError(400, $"Enter a valid email address for {field.Label}.");
        }
    }

    public static void AddDropdownLabels(List<LayoutField> fields, List<RecordRow> rows)
    {
        foreach (
            var field in fields.Where(f =>
                f.Widget == "dropdown" && f.Options != null
            )
        )
        {
            var labels = field.Options!.ToDictionary(
                o => o.Key,
                o => o.Display,
                StringComparer.Ordinal
            );
            foreach (var row in rows)
                if (
                    row.Values.GetValueOrDefault(field.Name) is string key
                    && labels.TryGetValue(key, out var display)
                )
                    row.DisplayValues[field.Name] = display;
        }
    }
}

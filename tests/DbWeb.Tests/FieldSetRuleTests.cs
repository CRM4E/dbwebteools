using System.Text.Json;
using DbWeb.Api;
using Xunit;

namespace DbWeb.Tests;

public class FieldSetRuleTests
{
    static readonly List<ColumnInfo> Columns =
    [
        new("id", "int", false, true, false, true, null),
        new("qty", "int", true, false, false, false, null),
        new("price", "decimal", true, false, false, false, null),
        new("status", "varchar", true, false, false, false, null),
        new("total", "decimal", true, false, false, false, null),
    ];

    static Dictionary<string, JsonElement> Values(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    [Fact]
    public void AppliesMatchingRulesAgainstCompleteUpdateRecordAndPreservesFalseTargets()
    {
        var values = Values("{\"price\":\"4.25\",\"status\":\"submitted\"}");
        var current = new Dictionary<string, object?>
        {
            ["id"] = 1,
            ["qty"] = 3,
            ["price"] = "2.00",
            ["status"] = "old",
            ["total"] = null,
        };
        FieldSetRules.Apply(
            [
                new("total", "[qty] > 1", "[qty] * [price]"),
                new("status", "[qty] < 0", "'never'"),
            ],
            [],
            Columns,
            current,
            values
        );
        Assert.Equal("12.75", values["total"].ToString());
        Assert.Equal("submitted", values["status"].GetString());
    }

    [Fact]
    public void AppliesRulesSequentiallyAndSupportsNull()
    {
        var values = Values("{\"qty\":2}");
        FieldSetRules.Apply(
            [
                new("qty", "true", "[qty] + 1"),
                new("status", "[qty] = 3", "Concat('ready-', [qty])"),
                new("total", "[status] = 'ready-3'", "null"),
            ],
            [],
            Columns,
            null,
            values
        );
        Assert.Equal("3", values["qty"].ToString());
        Assert.Equal("ready-3", values["status"].GetString());
        Assert.Equal(JsonValueKind.Null, values["total"].ValueKind);
    }

    [Theory]
    [InlineData("1", "Field set rule for status must return true or false.")]
    [InlineData("[qty] / 0 > 1", "Cannot apply field set rule for status.")]
    public void ReportsControlledErrors(string condition, string message)
    {
        var error = Assert.Throws<ApiError>(() =>
            FieldSetRules.Apply(
                [new("status", condition, "'set'")],
                [],
                Columns,
                null,
                Values("{\"qty\":2}")
            )
        );
        Assert.Equal(400, error.Status);
        Assert.Equal(message, error.Message);
    }
}

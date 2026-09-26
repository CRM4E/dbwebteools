using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DbWeb.Api;
using MySqlConnector;
using Xunit;

namespace DbWeb.Tests;

public partial class ApiTests
{
    [Fact]
    public void FieldMaskRulesEnforceSafeConfigurationAndValues()
    {
        var column = new ColumnInfo("code", "varchar", true, false, false, false, null, 20);
        var field = new LayoutField(
            "code",
            "Reference code",
            "",
            0,
            false,
            false,
            "text",
            Mask: new("alphanumeric", 6, "-/")
        );
        LayoutRules.ValidateMaskConfiguration(field, column);
        LayoutRules.ValidateMaskValues(
            [field],
            new()
            {
                ["code"] = JsonSerializer.SerializeToElement("AB-/12")
            }
        );
        foreach (var invalid in new object[] { "A-/1", "AB-123", "AB_/12", 123456 })
        {
            var error = Assert.Throws<ApiError>(() =>
                LayoutRules.ValidateMaskValues(
                    [field],
                    new()
                    {
                        ["code"] = JsonSerializer.SerializeToElement(invalid)
                    }
                )
            );
            Assert.Contains("input mask", error.Message);
        }
        Assert.Throws<ApiError>(() =>
            LayoutRules.ValidateMaskConfiguration(field with
            {
                Mask = new("letters", 21)
            }, column)
        );
        Assert.Throws<ApiError>(() =>
            LayoutRules.ValidateMaskConfiguration(field with
            {
                Widget = "number"
            }, column)
        );
        Assert.Throws<ApiError>(() =>
            LayoutRules.ValidateMaskConfiguration(
                field,
                column with { Type = "decimal", Length = null }
            )
        );
        Assert.Throws<ApiError>(() =>
            LayoutRules.ValidateMaskConfiguration(field with { ReadOnly = true }, column)
        );
        Assert.Throws<ApiError>(() =>
            LayoutRules.ValidateMaskConfiguration(field with
            {
                Mask = new("letters", 6, "--")
            }, column)
        );

        var numbersOnly = field with { Mask = new("digits") };
        LayoutRules.ValidateMaskValues(
            [numbersOnly],
            new() { ["code"] = JsonSerializer.SerializeToElement("123456") }
        );
        Assert.Throws<ApiError>(() =>
            LayoutRules.ValidateMaskValues(
                [numbersOnly],
                new() { ["code"] = JsonSerializer.SerializeToElement("123A56") }
            )
        );
    }

    [Fact]
    public void ExactFieldMasksEnforceRequiredOptionalAndEscapedPositions()
    {
        var column = new ColumnInfo("code", "varchar", true, false, false, false, null, 20);
        var field = new LayoutField(
            "code",
            "Reference code",
            "",
            0,
            false,
            false,
            "text",
            Mask: new(Pattern: "AA-##?")
        );
        LayoutRules.ValidateMaskConfiguration(field, column);
        LayoutRules.ValidateMaskConfiguration(field with { Widget = "auto" }, column);
        foreach (var valid in new[] { "AB-1", "AB-12" })
            LayoutRules.ValidateMaskValues(
                [field],
                new() { ["code"] = JsonSerializer.SerializeToElement(valid) }
            );
        foreach (var invalid in new[] { "A1-12", "AB-", "AB_12", "AB-123", "AB-１2", "AB-١2" })
            Assert.Throws<ApiError>(() =>
                LayoutRules.ValidateMaskValues(
                    [field],
                    new() { ["code"] = JsonSerializer.SerializeToElement(invalid) }
                )
            );

        var optionalLiteral = field with { Mask = new(Pattern: "##-?#") };
        LayoutRules.ValidateMaskConfiguration(optionalLiteral, column);
        foreach (var valid in new[] { "123", "12-3" })
            LayoutRules.ValidateMaskValues(
                [optionalLiteral],
                new() { ["code"] = JsonSerializer.SerializeToElement(valid) }
            );

        var escaped = field with { Mask = new(Pattern: @"\#-###") };
        LayoutRules.ValidateMaskConfiguration(escaped, column);
        LayoutRules.ValidateMaskValues(
            [escaped],
            new() { ["code"] = JsonSerializer.SerializeToElement("#-123") }
        );
        Assert.Throws<ApiError>(() =>
            LayoutRules.ValidateMaskConfiguration(field with { Mask = new(Pattern: "") }, column)
        );
        Assert.Throws<ApiError>(() =>
            LayoutRules.ValidateMaskConfiguration(field with { Mask = new(Pattern: "---") }, column)
        );
        Assert.Throws<ApiError>(() =>
            LayoutRules.ValidateMaskConfiguration(
                field with { Mask = new("digits", Pattern: "###") },
                column
            )
        );
        Assert.Throws<ApiError>(() =>
            LayoutRules.ValidateMaskConfiguration(field with { Mask = new(Pattern: "##??") }, column)
        );
        Assert.Throws<ApiError>(() =>
            LayoutRules.ValidateMaskConfiguration(field with { Mask = new(Pattern: @"\q#") }, column)
        );
        Assert.Throws<ApiError>(() =>
            LayoutRules.ValidateMaskConfiguration(field with { Mask = new(Pattern: "###\\") }, column)
        );
        Assert.Throws<ApiError>(() =>
            LayoutRules.ValidateMaskConfiguration(
                field with { Mask = new(Pattern: "#####################") },
                column
            )
        );
    }

    [Fact]
    public async Task FieldMasksValidateConfigurationDefaultsCreateAndUpdate()
    {
        var cs = Environment.GetEnvironmentVariable("MARIADB_TEST_CONNECTION");
        if (string.IsNullOrEmpty(cs))
        {
            Assert.False(Environment.GetEnvironmentVariable("CI") == "true");
            return;
        }
        await using var connection = new MySqlConnection(cs);
        await connection.OpenAsync();
        var table = "mask_" + Guid.NewGuid().ToString("N");
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"CREATE TABLE `{table}` (id INT AUTO_INCREMENT PRIMARY KEY, code VARCHAR(20) NULL)";
        await cmd.ExecuteNonQueryAsync();
        try
        {
            using var factory = new Factory();
            using var client = factory.CreateClient();
            await Login(client);
            var builder = new MySqlConnectionStringBuilder(cs);
            var response = await client.PostAsJsonAsync(
                "/api/admin/connections",
                new ConnectionInput(
                    "Mask test",
                    builder.Server,
                    builder.Port,
                    builder.Database,
                    builder.UserID,
                    builder.Password,
                    false
                )
            );
            response.EnsureSuccessStatusCode();
            var connectionId = (await response.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("id")
                .GetInt32();
            var root = $"/api/admin/connections/{connectionId}/tables/{table}";
            var layoutPath = root + "/layout";
            var field = new LayoutField(
                "code",
                "Reference code",
                "",
                0,
                false,
                false,
                "text",
                Mask: new(Pattern: "###-###")
            );
            await SaveConfiguration(client, layoutPath, new[] { field });

            var settings = await client.GetFromJsonAsync<JsonElement>(
                $"/api/connections/{connectionId}/tables/{table}/settings"
            );
            var mask = settings
                .GetProperty("fields")
                .EnumerateArray()
                .Single(item => item.GetProperty("name").GetString() == "code")
                .GetProperty("mask");
            Assert.Equal("###-###", mask.GetProperty("pattern").GetString());

            var objectDefinition = (await client.GetFromJsonAsync<ObjectDefinition>(root + "/object"))!;
            var computedMask = objectDefinition with
            {
                Fields =
                [
                    .. objectDefinition.Fields,
                    new ObjectField(
                        "computed",
                        "Computed",
                        true,
                        "formula",
                        Formula: "[id]",
                        Mask: new("letters", 1)
                    ),
                ],
            };
            Assert.Equal(
                HttpStatusCode.BadRequest,
                (await client.PutAsJsonAsync(root + "/object", computedMask)).StatusCode
            );

            var path = $"/api/connections/{connectionId}/tables/{table}";
            foreach (var value in new object[] { "12-345", "ABC-123", "123_456", 123456 })
            {
                var bad = await client.PostAsJsonAsync(
                    path + "/create",
                    new
                    {
                        values = new Dictionary<string, object> { ["code"] = value }
                    }
                );
                Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
                Assert.Contains("input mask", await bad.Content.ReadAsStringAsync());
            }
            (
                await client.PostAsJsonAsync(
                    path + "/create",
                    new
                    {
                        values = new
                        {
                            code = "123-456"
                        }
                    }
                )
            ).EnsureSuccessStatusCode();

            var rows = (await client.GetFromJsonAsync<JsonElement>(path + "/records"))
                .GetProperty("rows");
            var row = rows[0];
            var badUpdate = await client.PostAsJsonAsync(
                path + "/update",
                new
                {
                    values = new
                    {
                        code = "123-45"
                    },
                    key = new
                    {
                        id = row.GetProperty("values").GetProperty("id").GetInt32()
                    },
                    version = row.GetProperty("version").GetString(),
                }
            );
            Assert.Equal(HttpStatusCode.BadRequest, badUpdate.StatusCode);

            var invalidLength = field with
            {
                Mask = new(Pattern: "#####################")
            };
            Assert.Equal(
                HttpStatusCode.BadRequest,
                (await PutObjectConfiguration(client, layoutPath, new[] { invalidLength })).StatusCode
            );
            var invalidControl = field with
            {
                Widget = "number"
            };
            Assert.Equal(
                HttpStatusCode.BadRequest,
                (await PutObjectConfiguration(client, layoutPath, new[] { invalidControl })).StatusCode
            );
            var invalidDefault = field with
            {
                CreationDefault = new("ABC123")
            };
            Assert.Equal(
                HttpStatusCode.BadRequest,
                (await PutObjectConfiguration(client, layoutPath, new[] { invalidDefault })).StatusCode
            );
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS `{table}`";
            await cmd.ExecuteNonQueryAsync();
        }
    }
}

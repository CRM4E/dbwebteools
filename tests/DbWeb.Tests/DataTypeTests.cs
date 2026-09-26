using System.Text.Json;
using System.Net;
using System.Net.Http.Json;
using DbWeb.Api;
using MySqlConnector;
using Xunit;

namespace DbWeb.Tests;

public partial class ApiTests
{
    [Theory]
    [InlineData("{\"fields\":null}")]
    [InlineData("{\"fields\":[null]}")]
    public void ObjectParserRejectsNullFieldsBeforeNormalization(string json)
    {
        using var document = JsonDocument.Parse(json);

        var error = Assert.Throws<ApiError>(() =>
            ObjectModel.ParseObject(document.RootElement)
        );

        Assert.Equal(400, error.Status);
        Assert.Equal("Object fields are required.", error.Message);
    }

    [Theory]
    [InlineData("[{\"name\":\"title\",\"widget\":\"text\",\"required\":false}]")]
    [InlineData("{\"fields\":[{\"name\":\"title\",\"widget\":\"text\",\"mask\":null}]}")]
    public void LegacyLayoutWritesRejectDataTypeOwnedProperties(string json)
    {
        using var document = JsonDocument.Parse(json);

        var error = Assert.Throws<ApiError>(() =>
            ObjectModel.ValidateLegacyLayoutWrite(document.RootElement)
        );

        Assert.Equal(400, error.Status);
        Assert.Contains("moved to Data Types", error.Message);
    }

    [Fact]
    public void LegacyLayoutWritesWithoutDataTypeOwnedPropertiesRemainSupported()
    {
        using var document = JsonDocument.Parse(
            "[{\"name\":\"title\",\"label\":\"Title\",\"widget\":\"text\",\"section\":\"Main\"}]"
        );

        ObjectModel.ValidateLegacyLayoutWrite(document.RootElement);
    }

    [Fact]
    public void ManagedDatatypeIsNeverUserReadableOrWritable()
    {
        Assert.False(new FieldAccess(null).Read("datatype"));
        Assert.False(new FieldAccess(null).Write("datatype"));
        Assert.False(
            new FieldAccess(new()
            {
                ["datatype"] = "write"
            }).Read("datatype")
        );
    }

    [Fact]
    public void LegacyRequiredAndMaskNormalizeIntoCanonicalDefaultTypeIdempotently()
    {
        const string json = """
            {
              "Object": {
                "Fields": [
                  {
                    "Name":"code", "Label":"Code", "ReadOnly":false, "Widget":"text",
                    "Required":true, "Mask":{"Pattern":"##-##"}
                  }
                ]
              },
              "Layout": {"Fields":[{"Name":"code","Label":"Code"}]}
            }
            """;

        var stored = ObjectModel.Stored(json);
        var field = Assert.Single(stored.Object.Fields);
        Assert.False(field.Required);
        Assert.Null(field.Mask);
        Assert.Equal("default", stored.Object.DefaultDataTypeKey);
        var type = Assert.Single(stored.Object.DataTypes!);
        var setting = Assert.Single(type.Fields);
        Assert.True(setting.Required);
        Assert.Equal("##-##", setting.Mask!.Pattern);
        Assert.False(setting.OverrideDropdownOptions);
        Assert.Empty(setting.EnabledOptionKeys!);

        var once = ObjectModel.Serialize(stored);
        Assert.Equal(once, ObjectModel.Serialize(ObjectModel.Stored(once)));
        using var document = JsonDocument.Parse(once);
        var canonicalField = document.RootElement.GetProperty("Object").GetProperty("Fields")[0];
        Assert.False(canonicalField.GetProperty("Required").GetBoolean());
        Assert.Equal(JsonValueKind.Null, canonicalField.GetProperty("Mask").ValueKind);
    }

    [Fact]
    public void DefaultTypeAppliesRequiredMaskAndEnabledDropdownsButKeepsMasterLabels()
    {
        var definition = new ObjectDefinition(
            [
                new("status", "Status", false, "dropdown", Options: [new("a", "Active"), new("b", "Blocked")]),
                new("code", "Code", false, "text"),
            ],
            DataTypes:
            [
                new(
                    "standard",
                    "Standard",
                    [
                        new("status", OverrideDropdownOptions: true, EnabledOptionKeys: ["a"]),
                        new("code", Required: true, Mask: new(Pattern: "##-##")),
                    ]
                ),
            ],
            DefaultDataTypeKey: "standard"
        );
        var presentation = new LayoutPresentation(
            [new("status", Label: "Status"), new("code", Label: "Code")]
        );
        var merged = ObjectModel.Merge(definition, presentation);
        var status = merged.Fields.Single(field => field.Name == "status");
        var code = merged.Fields.Single(field => field.Name == "code");
        Assert.Equal(["a", "b"], status.Options!.Select(option => option.Key));
        Assert.Equal(["a"], status.EnabledOptionKeys);
        Assert.True(code.Required);
        Assert.Equal("##-##", code.Mask!.Pattern);

        Assert.Throws<ApiError>(() =>
            LayoutRules.ValidateDropdownValues(
                merged.Fields,
                new()
                {
                    ["status"] = JsonSerializer.SerializeToElement("b")
                }
            )
        );
        Assert.Throws<ApiError>(() =>
            LayoutRules.ValidateMaskValues(
                merged.Fields,
                new()
                {
                    ["code"] = JsonSerializer.SerializeToElement("1234")
                }
            )
        );
        var row = new RecordRow(new()
        {
            ["status"] = "b"
        }, "version");
        LayoutRules.AddDropdownLabels(merged.Fields, [row]);
        Assert.Equal("Blocked", row.DisplayValues["status"]);
    }

    [Fact]
    public void NormalizationCleansDeletedFieldsAndPreservesExplicitEmptyOverride()
    {
        var stored = ObjectModel.Normalize(
            new(
                new(
                    [new("status", "Status", false, "dropdown", Options: [new("a", "Active")])],
                    DataTypes:
                    [
                        new(
                            "default",
                            "Default",
                            [
                                new("status", OverrideDropdownOptions: true, EnabledOptionKeys: []),
                                new("deleted", Required: true),
                            ]
                        ),
                    ],
                    DefaultDataTypeKey: "default"
                ),
                new([new("status", Label: "Status")])
            )
        );
        var setting = Assert.Single(Assert.Single(stored.Object.DataTypes!).Fields);
        Assert.Equal("status", setting.Name);
        Assert.True(setting.OverrideDropdownOptions);
        Assert.Empty(setting.EnabledOptionKeys!);
        Assert.Empty(ObjectModel.Merge(stored).Fields.Single().EnabledOptionKeys!);
    }

    [Fact]
    public async Task ProvisioningBackfillsAndApiCreateOwnsDatatype()
    {
        var cs = Environment.GetEnvironmentVariable("MARIADB_TEST_CONNECTION");
        if (string.IsNullOrEmpty(cs))
        {
            Assert.False(Environment.GetEnvironmentVariable("CI") == "true");
            return;
        }
        await using var connection = new MySqlConnection(cs);
        await connection.OpenAsync();
        var table = "datatype_" + Guid.NewGuid().ToString("N");
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"CREATE TABLE `{table}`(id INT AUTO_INCREMENT PRIMARY KEY,title VARCHAR(80));" +
            $"INSERT INTO `{table}`(title) VALUES('Existing')";
        await command.ExecuteNonQueryAsync();
        try
        {
            using var factory = new Factory();
            using var admin = factory.CreateClient();
            await Login(admin);
            var builder = new MySqlConnectionStringBuilder(cs);
            var response = await admin.PostAsJsonAsync(
                "/api/admin/connections",
                new ConnectionInput(
                    "Data types",
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
            var adminRoot = $"/api/admin/connections/{connectionId}/tables/{table}";
            var definition = (await admin.GetFromJsonAsync<ObjectDefinition>(adminRoot + "/object"))!;
            command.CommandText =
                "SELECT COUNT(*) FROM information_schema.COLUMNS "
                + $"WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='{table}' AND COLUMN_NAME='datatype'";
            Assert.Equal(0, Convert.ToInt32(await command.ExecuteScalarAsync()));

            // Existing tables remain writable until their object configuration is
            // explicitly saved and the managed datatype column is provisioned.
            // Ordinary reads and create previews must not turn a rollout into a
            // write outage for every as-yet-unconfigured table.
            var apiRoot = $"/api/connections/{connectionId}/tables/{table}";
            (await admin.PostAsJsonAsync(apiRoot + "/create", new
            {
                values = new
                {
                    title = "Before provisioning"
                }
            }))
                .EnsureSuccessStatusCode();
            definition = definition with
            {
                DataTypes =
                [
                    new(
                        "primary",
                        "Primary label",
                        definition.Fields.Select(field =>
                                new ObjectDataTypeField(field.Name, Required: field.Name == "title")
                            )
                            .ToList()
                    ),
                ],
                DefaultDataTypeKey = "primary",
            };
            (await admin.PutAsJsonAsync(adminRoot + "/object", definition)).EnsureSuccessStatusCode();

            command.CommandText =
                "SELECT DATA_TYPE,CHARACTER_MAXIMUM_LENGTH,IS_NULLABLE FROM information_schema.COLUMNS " +
                $"WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='{table}' AND COLUMN_NAME='datatype'";
            await using (var reader = await command.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal("varchar", reader.GetString(0));
                Assert.Equal(64, reader.GetInt64(1));
                Assert.Equal("NO", reader.GetString(2));
            }
            command.CommandText = $"SELECT datatype FROM `{table}` WHERE title='Existing'";
            Assert.Equal("primary", (string?)await command.ExecuteScalarAsync());
            command.CommandText =
                $"SELECT datatype FROM `{table}` WHERE title='Before provisioning'";
            Assert.Equal("primary", (string?)await command.ExecuteScalarAsync());

            Assert.Equal(
                HttpStatusCode.Forbidden,
                (
                    await admin.PostAsJsonAsync(
                        apiRoot + "/create",
                        new
                        {
                            values = new
                            {
                                title = "Bad",
                                datatype = "other"
                            }
                        }
                    )
                ).StatusCode
            );
            (await admin.PostAsJsonAsync(apiRoot + "/create", new
            {
                values = new
                {
                    title = "New"
                }
            }))
                .EnsureSuccessStatusCode();
            command.CommandText = $"SELECT datatype FROM `{table}` WHERE title='New'";
            Assert.Equal("primary", (string?)await command.ExecuteScalarAsync());

            var page = await admin.GetFromJsonAsync<JsonElement>(apiRoot + "/records?sort=id");
            var row = page.GetProperty("rows")[1];
            Assert.False(row.GetProperty("values").TryGetProperty("datatype", out _));
            Assert.Equal(
                HttpStatusCode.Forbidden,
                (
                    await admin.PostAsJsonAsync(
                        apiRoot + "/update",
                        new
                        {
                            values = new
                            {
                                datatype = "other"
                            },
                            key = new
                            {
                                id = row.GetProperty("values").GetProperty("id").GetInt32()
                            },
                            version = row.GetProperty("version").GetString(),
                        }
                    )
                ).StatusCode
            );

            var current = (await admin.GetFromJsonAsync<ObjectDefinition>(adminRoot + "/object"))!;
            var secondary = new ObjectDataType(
                "secondary",
                "Secondary",
                current.Fields.Select(field => new ObjectDataTypeField(field.Name)).ToList()
            );
            current = current with
            {
                DataTypes = [.. current.DataTypes!, secondary],
                DefaultDataTypeKey = "secondary",
            };
            (await admin.PutAsJsonAsync(adminRoot + "/object", current)).EnsureSuccessStatusCode();
            (await admin.PostAsJsonAsync(apiRoot + "/create", new
            {
                values = new
                {
                    title = "Secondary row"
                }
            })).EnsureSuccessStatusCode();
            command.CommandText = $"SELECT datatype FROM `{table}` WHERE title='Secondary row'";
            Assert.Equal("secondary", (string?)await command.ExecuteScalarAsync());
            command.CommandText = $"SELECT datatype FROM `{table}` WHERE title='Existing'";
            Assert.Equal("primary", (string?)await command.ExecuteScalarAsync());

            var removeUsed = current with
            {
                DataTypes = [secondary],
                DefaultDataTypeKey = "secondary",
            };
            Assert.Equal(
                HttpStatusCode.Conflict,
                (await admin.PutAsJsonAsync(adminRoot + "/object", removeUsed)).StatusCode
            );
        }
        finally
        {
            command.CommandText = $"DROP TABLE IF EXISTS `{table}`";
            await command.ExecuteNonQueryAsync();
        }
    }
}

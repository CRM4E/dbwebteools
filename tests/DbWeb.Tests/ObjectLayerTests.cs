using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DbWeb.Api;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using Xunit;

namespace DbWeb.Tests;

public partial class ApiTests
{
    [Theory]
    [InlineData("{\"fields\":[{}]}")]
    [InlineData("{\"fields\":[{\"name\":null}]}")]
    [InlineData("{\"fields\":[{\"name\":\"\"}]}")]
    [InlineData("{\"fields\":[{\"name\":\"   \"}]}")]
    public void ObjectParserRejectsMissingNullAndBlankFieldNames(string json)
    {
        using var document = JsonDocument.Parse(json);

        var error = Assert.Throws<ApiError>(() => ObjectModel.ParseObject(document.RootElement));

        Assert.Equal(400, error.Status);
    }

    [Theory]
    [InlineData("required", "true")]
    [InlineData("mask", "{\"pattern\":\"##-##\"}")]
    public void LayoutParserRejectsDataTypeBehaviorWithoutLegacyShapeMarkers(
        string property,
        string value
    )
    {
        using var document = JsonDocument.Parse(
            $"{{\"fields\":[{{\"name\":\"title\",\"editorOrder\":0,\"{property}\":{value}}}]}}"
        );

        var error = Assert.Throws<ApiError>(() =>
            ObjectModel.ValidateLegacyLayoutWrite(document.RootElement)
        );

        Assert.Equal(400, error.Status);
    }

    [Fact]
    public async Task ObjectAndLayoutEndpointsRejectMalformedOwnershipPayloads()
    {
        var cs = Environment.GetEnvironmentVariable("MARIADB_TEST_CONNECTION");
        if (string.IsNullOrEmpty(cs))
        {
            Assert.False(Environment.GetEnvironmentVariable("CI") == "true");
            return;
        }
        await using var connection = new MySqlConnection(cs);
        await connection.OpenAsync();
        var table = "object_payloads_" + Guid.NewGuid().ToString("N");
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"CREATE TABLE `{table}`(id INT AUTO_INCREMENT PRIMARY KEY,title VARCHAR(80))";
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
                    "Object payloads",
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

            foreach (var malformed in new object[]
            {
                new
                {
                    fields = new[]
                    {
                        new
                        {
                            label = "Missing name",
                            readOnly = false,
                            widget = "text"
                        }
                    }
                },
                new
                {
                    fields = new[]
                    {
                        new
                        {
                            name = (string?)null,
                            label = "Null name",
                            readOnly = false,
                            widget = "text"
                        }
                    }
                },
                new
                {
                    fields = new[]
                    {
                        new
                        {
                            name = "   ",
                            label = "Blank name",
                            readOnly = false,
                            widget = "text"
                        }
                    }
                },
            })
                Assert.Equal(
                    HttpStatusCode.BadRequest,
                    (await admin.PutAsJsonAsync(root + "/object", malformed)).StatusCode
                );

            var presentation = new LayoutPresentation(
                [
                    new("id", Label: "ID"),
                    new("title", EditorOrder: 1, ListOrder: 1, Label: "Title"),
                ]
            );
            (await admin.PutAsJsonAsync(root + "/layout", presentation)).EnsureSuccessStatusCode();

            foreach (var dataTypeSetting in new object[]
            {
                new
                {
                    fields = presentation.Fields.Select(field => new
                    {
                        field.Name,
                        field.Section,
                        field.EditorOrder,
                        field.ShowInEditor,
                        field.ShowInList,
                        field.ListOrder,
                        field.Label,
                        required = field.Name == "title",
                    })
                },
                new
                {
                    fields = presentation.Fields.Select(field => new
                    {
                        field.Name,
                        field.Section,
                        field.EditorOrder,
                        field.ShowInEditor,
                        field.ShowInList,
                        field.ListOrder,
                        field.Label,
                        mask = field.Name == "title"
                            ? new
                            {
                                pattern = "##-##"
                            }
                            : null,
                    })
                },
            })
            {
                var rejected = await admin.PutAsJsonAsync(root + "/layout", dataTypeSetting);
                Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
                var problem = await rejected.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Contains(
                    "moved to Data Types",
                    problem.GetProperty("title").GetString(),
                    StringComparison.Ordinal
                );
            }
        }
        finally
        {
            command.CommandText = $"DROP TABLE `{table}`";
            await command.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public void FormerSplitMetadataMovesSectionFromObjectToLayout()
    {
        const string json = """
            {
              "Object": {
                "Fields": [
                  {
                    "Name": "title", "Label": "Title", "Section": "Details",
                    "ReadOnly": false, "Widget": "text"
                  }
                ]
              },
              "Layout": {
                "Fields": [
                  {
                    "Name": "title", "EditorOrder": 2, "ShowInEditor": true,
                    "ShowInList": true, "ListOrder": 4
                  }
                ]
              }
            }
            """;

        var stored = ObjectModel.Stored(json);
        Assert.Equal("Details", Assert.Single(stored.Layout.Fields).Section);
        var normalized = ObjectModel.Serialize(stored);
        using var document = JsonDocument.Parse(normalized);
        Assert.False(
            document
                .RootElement.GetProperty("Object")
                .GetProperty("Fields")[0]
                .TryGetProperty("Section", out _)
        );
        Assert.Equal(
            "Details",
            document
                .RootElement.GetProperty("Layout")
                .GetProperty("Fields")[0]
                .GetProperty("Section")
                .GetString()
        );
    }

    [Fact]
    public void LegacyCombinedMetadataMigratesInPlaceAndRemainsEquivalent()
    {
        var options = new DbContextOptionsBuilder<AppDb>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        using var db = new AppDb(options);
        db.Database.OpenConnection();
        db.Database.EnsureCreated();
        var legacy = new LayoutDefinition(
            [
                new(
                    "title",
                    "Friendly title",
                    "Details",
                    7,
                    true,
                    false,
                    "dropdown",
                    Options: [new("a", "Active")],
                    ShowInList: false,
                    ListOrder: 3,
                    Required: false,
                    CreationDefault: new("a")
                ),
            ],
            new(Label: "Filtered", Sort: "title", Filters: [new("title", "eq", "a")])
        );
        db.Layouts.Add(
            new()
            {
                ConnectionId = 1,
                Table = "records",
                FieldsJson = JsonSerializer.Serialize(legacy),
            }
        );
        db.SaveChanges();

        ObjectModel.Migrate(db);
        var stored = db.Layouts.Single().FieldsJson;
        using var document = JsonDocument.Parse(stored);
        Assert.True(document.RootElement.TryGetProperty("Object", out _));
        Assert.True(document.RootElement.TryGetProperty("Layout", out _));
        Assert.False(
            document
                .RootElement.GetProperty("Object")
                .GetProperty("Fields")[0]
                .TryGetProperty("Section", out _)
        );
        Assert.Equal(
            "Details",
            document
                .RootElement.GetProperty("Layout")
                .GetProperty("Fields")[0]
                .GetProperty("Section")
                .GetString()
        );
        var merged = DatabaseService.Layout(stored);
        var field = Assert.Single(merged.Fields);
        Assert.Equal("Friendly title", field.Label);
        Assert.Equal("Details", field.Section);
        Assert.Equal(7, field.Order);
        Assert.True(field.Hidden);
        Assert.False(field.ShowInList);
        Assert.Equal(3, field.ListOrder);
        Assert.Equal("dropdown", field.Widget);
        Assert.Equal("Filtered", merged.View!.Label);

        ObjectModel.Migrate(db);
        Assert.Equal(stored, db.Layouts.Single().FieldsJson);
    }

    [Fact]
    public async Task ObjectAndPresentationLayersMigrateAndSaveIndependently()
    {
        var cs = Environment.GetEnvironmentVariable("MARIADB_TEST_CONNECTION");
        if (string.IsNullOrEmpty(cs))
        {
            Assert.False(Environment.GetEnvironmentVariable("CI") == "true");
            return;
        }
        await using var connection = new MySqlConnection(cs);
        await connection.OpenAsync();
        var table = "object_layer_" + Guid.NewGuid().ToString("N");
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"CREATE TABLE `{table}`(id INT AUTO_INCREMENT PRIMARY KEY,title VARCHAR(80),status VARCHAR(20),amount DECIMAL(12,2));INSERT INTO `{table}`(title,status,amount) VALUES('Original','a',12.50)";
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
                    "Objects",
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
            foreach (var malformed in new object[]
            {
                new
                {
                    fields = (object?)null
                },
                new
                {
                    fields = new object?[]
                    {
                        null
                    }
                },
            })
                Assert.Equal(
                    HttpStatusCode.BadRequest,
                    (await admin.PutAsJsonAsync(root + "/object", malformed)).StatusCode
                );
            var legacy = new LayoutDefinition(
                [
                    new("id", "ID", "Keys", 3, true, true, "auto", ShowInList: false),
                    new(
                        "title",
                        "Title label",
                        "Content",
                        2,
                        false,
                        false,
                        "text",
                        ShowInList: true,
                        ListOrder: 4,
                        Required: true,
                        CreationDefault: new("New title")
                    ),
                    new(
                        "status",
                        "State",
                        "Content",
                        1,
                        false,
                        false,
                        "dropdown",
                        Options: [new("a", "Active"), new("b", "Blocked")],
                        ShowInList: true,
                        ListOrder: 1
                    ),
                    new(
                        "computed",
                        "Computed",
                        "Metrics",
                        5,
                        false,
                        true,
                        "formula",
                        ShowInList: true,
                        ListOrder: 2,
                        Formula: "Round([amount] * 2, 2)"
                    ),
                ],
                new(Label: "Object list", Sort: "title", Filters: [new("status", "eq", "a")])
            );
            // Former combined clients remain supported for settings not owned by Data Types.
            var legacyWithoutDataTypeSettings = new
            {
                fields = legacy.Fields.Select(field => new
                {
                    field.Name,
                    field.Label,
                    field.Section,
                    field.Order,
                    field.Hidden,
                    field.ReadOnly,
                    field.Widget,
                    field.Lookup,
                    field.Options,
                    field.ShowInList,
                    field.ListOrder,
                    field.Join,
                    field.Formula,
                    field.Sumup,
                    field.CreationDefault,
                }),
                legacy.View,
                legacy.SumupsPending,
            };
            (
                await admin.PutAsJsonAsync(root + "/layout", legacyWithoutDataTypeSettings)
            ).EnsureSuccessStatusCode();

            var initialObject = (
                await admin.GetFromJsonAsync<ObjectDefinition>(root + "/object")
            )!;
            initialObject = initialObject with
            {
                DataTypes = initialObject.DataTypes!
                    .Select(type => type with
                    {
                        Fields = type.Fields.Select(field =>
                                field.Name == "title"
                                    ? field with
                                    {
                                        Required = true
                                    }
                                    : field
                            )
                            .ToList(),
                    })
                    .ToList(),
            };
            (
                await admin.PutAsJsonAsync(root + "/object", initialObject)
            ).EnsureSuccessStatusCode();

            foreach (var dataTypeSetting in new object[]
            {
                new
                {
                    fields = new[]
                    {
                        new
                        {
                            name = "title",
                            label = "Title",
                            widget = "text",
                            required = true,
                        }
                    }
                },
                new
                {
                    fields = new[]
                    {
                        new
                        {
                            name = "title",
                            label = "Title",
                            widget = "text",
                            mask = new
                            {
                                pattern = "##-##"
                            },
                        }
                    }
                },
            })
                Assert.Equal(
                    HttpStatusCode.BadRequest,
                    (await admin.PutAsJsonAsync(root + "/layout", dataTypeSetting)).StatusCode
                );

            var objectDefinition = (
                await admin.GetFromJsonAsync<ObjectDefinition>(root + "/object")
            )!;
            Assert.Equal(
                "Title label",
                objectDefinition.Fields.Single(f => f.Name == "title").Label
            );
            Assert.False(objectDefinition.Fields.Single(f => f.Name == "title").Required);
            Assert.True(
                objectDefinition.DataTypes!
                    .Single(type => type.Key == objectDefinition.DefaultDataTypeKey)
                    .Fields.Single(field => field.Name == "title")
                    .Required
            );
            Assert.Equal("Object list", objectDefinition.View!.Label);
            var presentation = (
                await admin.GetFromJsonAsync<LayoutPresentation>(root + "/layout")
            )!;
            var titleLayout = presentation.Fields.Single(f => f.Name == "title");
            Assert.Equal("Content", titleLayout.Section);
            Assert.Equal(2, titleLayout.EditorOrder);
            Assert.True(titleLayout.ShowInEditor);
            Assert.True(titleLayout.ShowInList);
            Assert.Equal(4, titleLayout.ListOrder);

            var objectJson = await admin.GetStringAsync(root + "/object");
            Assert.DoesNotContain("editorOrder", objectJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("showInList", objectJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("section", objectJson, StringComparison.OrdinalIgnoreCase);
            var layoutJson = await admin.GetStringAsync(root + "/layout");
            Assert.DoesNotContain("widget", layoutJson, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("label", layoutJson, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("section", layoutJson, StringComparison.OrdinalIgnoreCase);

            objectDefinition = objectDefinition with
            {
                Fields = objectDefinition
                    .Fields.Select(f =>
                        f.Name == "title"
                            ? f with
                            {
                                Label = "Object title",
                                Widget = "textarea",
                            }
                            : f
                    )
                    .ToList(),
            };
            (
                await admin.PutAsJsonAsync(root + "/object", objectDefinition)
            ).EnsureSuccessStatusCode();
            var settings = await admin.GetFromJsonAsync<JsonElement>(
                $"/api/connections/{connectionId}/tables/{table}/settings"
            );
            var title = settings
                .GetProperty("fields")
                .EnumerateArray()
                .Single(f => f.GetProperty("name").GetString() == "title");
            Assert.Equal("Title label", title.GetProperty("label").GetString());
            Assert.Equal("textarea", title.GetProperty("widget").GetString());
            Assert.Equal("Content", title.GetProperty("section").GetString());
            Assert.Equal(2, title.GetProperty("order").GetInt32());
            Assert.Equal(4, title.GetProperty("listOrder").GetInt32());

            presentation = (await admin.GetFromJsonAsync<LayoutPresentation>(root + "/layout"))!;
            presentation = presentation with
            {
                Fields = presentation
                    .Fields.Select(f =>
                        f.Name == "title"
                            ? f with
                            {
                                Label = "Layout title",
                                Section = "Summary",
                                EditorOrder = 8,
                                ShowInEditor = true,
                                ShowInList = false,
                                ListOrder = 9,
                            }
                            : f
                    )
                    .ToList(),
            };
            (await admin.PutAsJsonAsync(root + "/layout", presentation)).EnsureSuccessStatusCode();
            var unchangedObject = (
                await admin.GetFromJsonAsync<ObjectDefinition>(root + "/object")
            )!;
            Assert.Equal("textarea", unchangedObject.Fields.Single(f => f.Name == "title").Widget);
            settings = await admin.GetFromJsonAsync<JsonElement>(
                $"/api/connections/{connectionId}/tables/{table}/settings"
            );
            title = settings
                .GetProperty("fields")
                .EnumerateArray()
                .Single(f => f.GetProperty("name").GetString() == "title");
            Assert.Equal("Layout title", title.GetProperty("label").GetString());
            Assert.Equal(8, title.GetProperty("order").GetInt32());
            Assert.Equal("Summary", title.GetProperty("section").GetString());
            Assert.False(title.GetProperty("showInList").GetBoolean());
            Assert.Equal(9, title.GetProperty("listOrder").GetInt32());

            // Presentation-only clients from the previous release omitted section.
            // Their saves must retain the section instead of silently clearing it.
            (
                await admin.PutAsJsonAsync(
                    root + "/layout",
                    new
                    {
                        fields = presentation.Fields.Select(f => new
                        {
                            name = f.Name,
                            editorOrder = f.EditorOrder,
                            showInEditor = f.ShowInEditor,
                            showInList = f.ShowInList,
                            listOrder = f.ListOrder,
                        }),
                    }
                )
            ).EnsureSuccessStatusCode();
            settings = await admin.GetFromJsonAsync<JsonElement>(
                $"/api/connections/{connectionId}/tables/{table}/settings"
            );
            title = settings
                .GetProperty("fields")
                .EnumerateArray()
                .Single(f => f.GetProperty("name").GetString() == "title");
            Assert.Equal("Summary", title.GetProperty("section").GetString());
            Assert.Equal("Layout title", title.GetProperty("label").GetString());

            // Empty is an intentional Layout value, while omitted labels from older
            // presentation clients preserve the existing value. Object saves cannot
            // overwrite the Layout-owned label with a stale compatibility projection.
            var clearedPresentation = presentation with
            {
                Fields = presentation.Fields.Select(f =>
                        f.Name == "title" ? f with
                        {
                            Label = ""
                        } : f
                    )
                    .ToList(),
            };
            (
                await admin.PutAsJsonAsync(root + "/layout", clearedPresentation)
            ).EnsureSuccessStatusCode();
            objectDefinition = (
                await admin.GetFromJsonAsync<ObjectDefinition>(root + "/object")
            )! with
            {
                Fields = (
                    await admin.GetFromJsonAsync<ObjectDefinition>(root + "/object")
                )!.Fields.Select(f =>
                        f.Name == "title" ? f with
                        {
                            Label = "Stale object label"
                        } : f
                    )
                    .ToList(),
            };
            (
                await admin.PutAsJsonAsync(root + "/object", objectDefinition)
            ).EnsureSuccessStatusCode();
            settings = await admin.GetFromJsonAsync<JsonElement>(
                $"/api/connections/{connectionId}/tables/{table}/settings"
            );
            title = settings
                .GetProperty("fields")
                .EnumerateArray()
                .Single(f => f.GetProperty("name").GetString() == "title");
            Assert.Equal("", title.GetProperty("label").GetString());

            Assert.Equal(
                HttpStatusCode.BadRequest,
                (
                    await admin.PutAsJsonAsync(
                        root + "/layout",
                        presentation with
                        {
                            Fields = presentation.Fields.Skip(1).ToList(),
                        }
                    )
                ).StatusCode
            );
            var hiddenRequired = presentation with
            {
                Fields = presentation
                    .Fields.Select(f => f.Name == "title" ? f with { ShowInEditor = false } : f)
                    .ToList(),
            };
            Assert.Equal(
                HttpStatusCode.BadRequest,
                (await admin.PutAsJsonAsync(root + "/layout", hiddenRequired)).StatusCode
            );

            var records = await admin.GetFromJsonAsync<JsonElement>(
                $"/api/connections/{connectionId}/tables/{table}/records"
            );
            Assert.Equal(
                "Active",
                records
                    .GetProperty("rows")[0]
                    .GetProperty("displayValues")
                    .GetProperty("status")
                    .GetString()
            );
            Assert.Equal(
                "25.00",
                records
                    .GetProperty("rows")[0]
                    .GetProperty("joinedValues")
                    .GetProperty("computed")
                    .GetString()
            );

            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            var stored = await db.Layouts.SingleAsync(l =>
                l.ConnectionId == connectionId && l.Table == table
            );
            using var document = JsonDocument.Parse(stored.FieldsJson);
            Assert.True(document.RootElement.TryGetProperty("Object", out _));
            Assert.True(document.RootElement.TryGetProperty("Layout", out _));
            Assert.DoesNotContain(
                "\"Widget\"",
                document.RootElement.GetProperty("Layout").ToString()
            );
        }
        finally
        {
            command.CommandText = $"DROP TABLE `{table}`";
            await command.ExecuteNonQueryAsync();
        }
    }
}

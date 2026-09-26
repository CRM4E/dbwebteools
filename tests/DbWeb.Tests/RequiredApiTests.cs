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
    public async Task RequiredLayoutsValidateFinalRecordsIncludingOmittedValues()
    {
        var cs = Environment.GetEnvironmentVariable("MARIADB_TEST_CONNECTION");
        if (string.IsNullOrEmpty(cs))
        {
            Assert.False(Environment.GetEnvironmentVariable("CI") == "true");
            return;
        }
        await using var connection = new MySqlConnection(cs);
        await connection.OpenAsync();
        var table = "required_" + Guid.NewGuid().ToString("N");
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            $"CREATE TABLE `{table}` (id INT AUTO_INCREMENT PRIMARY KEY, title VARCHAR(100) NULL DEFAULT 'default title', note TEXT, flag BOOLEAN NULL); INSERT INTO `{table}`(title,flag) VALUES(NULL,0),('Existing',0)";
        await cmd.ExecuteNonQueryAsync();
        try
        {
            using var f = new Factory();
            using var client = f.CreateClient();
            await Login(client);
            var b = new MySqlConnectionStringBuilder(cs);
            var response = await client.PostAsJsonAsync(
                "/api/admin/connections",
                new ConnectionInput(
                    "Required test",
                    b.Server,
                    b.Port,
                    b.Database,
                    b.UserID,
                    b.Password,
                    false
                )
            );
            response.EnsureSuccessStatusCode();
            var id = (await response.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("id")
                .GetInt32();
            var path = $"/api/connections/{id}/tables/{table}";
            var layoutPath = $"/api/admin/connections/{id}/tables/{table}/layout";
            var title = new LayoutField(
                "title",
                "Record title",
                "",
                0,
                false,
                false,
                "text",
                Required: true
            );
            var flag = new LayoutField(
                "flag",
                "Enabled",
                "",
                1,
                false,
                false,
                "checkbox",
                Required: true
            );
            foreach (
                var invalid in new[]
                {
                    title with
                    {
                        ReadOnly = true,
                    },
                    title with
                    {
                        Name = "id",
                    },
                }
            )
                Assert.Equal(
                    HttpStatusCode.BadRequest,
                    (await PutObjectConfiguration(client, layoutPath, new[] { invalid })).StatusCode
                );
            await SaveConfiguration(client, layoutPath, new[] { title, flag });
            var presentation = (
                await client.GetFromJsonAsync<LayoutPresentation>(layoutPath)
            )!;
            var hiddenRequired = presentation with
            {
                Fields = presentation
                    .Fields.Select(field =>
                        field.Name == title.Name
                            ? field with
                            {
                                ShowInEditor = false,
                            }
                            : field
                    )
                    .ToList(),
            };
            Assert.Equal(
                HttpStatusCode.BadRequest,
                (await client.PutAsJsonAsync(layoutPath, hiddenRequired)).StatusCode
            );
            var settings = await client.GetFromJsonAsync<JsonElement>(path + "/settings");
            Assert.True(settings.GetProperty("fields")[0].GetProperty("required").GetBoolean());
            foreach (var value in new object?[] { null, "", "   ", "\t\n" })
            {
                var bad = await client.PostAsJsonAsync(
                    path + "/create",
                    new { values = new { title = value, flag = false } }
                );
                Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
                Assert.Contains("Record title is required", await bad.Content.ReadAsStringAsync());
            }
            Assert.Equal(
                HttpStatusCode.BadRequest,
                (
                    await client.PostAsJsonAsync(
                        path + "/create",
                        new { values = new { flag = false } }
                    )
                ).StatusCode
            );
            (
                await client.PostAsJsonAsync(
                    path + "/create",
                    new { values = new { title = "New", flag = false } }
                )
            ).EnsureSuccessStatusCode();
            var rows = (await client.GetFromJsonAsync<JsonElement>(path + "/records")).GetProperty(
                "rows"
            );
            var empty = rows[0];
            var existing = rows[1];
            object Update(JsonElement row, object values) =>
                new
                {
                    values,
                    key = new { id = row.GetProperty("values").GetProperty("id").GetInt32() },
                    version = row.GetProperty("version").GetString(),
                };
            Assert.Equal(
                HttpStatusCode.BadRequest,
                (
                    await client.PostAsJsonAsync(
                        path + "/update",
                        Update(empty, new { note = "bypass" })
                    )
                ).StatusCode
            );
            Assert.Equal(
                HttpStatusCode.BadRequest,
                (
                    await client.PostAsJsonAsync(
                        path + "/update",
                        Update(existing, new { title = "  " })
                    )
                ).StatusCode
            );
            (
                await client.PostAsJsonAsync(
                    path + "/update",
                    Update(existing, new { note = "changed" })
                )
            ).EnsureSuccessStatusCode();
            (
                await client.PostAsJsonAsync(
                    path + "/update",
                    Update(empty, new { title = "Repaired", flag = 0 })
                )
            ).EnsureSuccessStatusCode();
            var after = (await client.GetFromJsonAsync<JsonElement>(path + "/records")).GetProperty(
                "rows"
            );
            Assert.Equal(
                "Repaired",
                after[0].GetProperty("values").GetProperty("title").GetString()
            );
            Assert.Equal("changed", after[1].GetProperty("values").GetProperty("note").GetString());
            (
                await client.PostAsJsonAsync(path + "/delete", Update(after[0], new { }))
            ).EnsureSuccessStatusCode();
            // Required is Data Type-owned and is cleared through the object endpoint.
            await SaveConfiguration(
                client,
                layoutPath,
                new[] { title with { Required = false } }
            );
            (
                await client.PostAsJsonAsync(
                    path + "/create",
                    new { values = new { title = (string?)null } }
                )
            ).EnsureSuccessStatusCode();
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE `{table}`";
            await cmd.ExecuteNonQueryAsync();
        }
    }
}

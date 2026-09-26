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
    public async Task LookupCopiesAndFormulasAreAuthoritativeTypedAndPermissionChecked()
    {
        var cs = Environment.GetEnvironmentVariable("MARIADB_TEST_CONNECTION");
        if (string.IsNullOrEmpty(cs))
        {
            Assert.False(Environment.GetEnvironmentVariable("CI") == "true");
            return;
        }
        await using var conn = new MySqlConnection(cs);
        await conn.OpenAsync();
        var source = "copy_source_" + Guid.NewGuid().ToString("N");
        var destination = "copy_dest_" + Guid.NewGuid().ToString("N");
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"CREATE TABLE `{source}` (id BIGINT PRIMARY KEY, name VARCHAR(100), price DECIMAL(24,4)); CREATE TABLE `{destination}` (id INT AUTO_INCREMENT PRIMARY KEY, source_id BIGINT NULL, copied_name VARCHAR(100), copied_price DECIMAL(24,4), qty INT); INSERT INTO `{source}` VALUES (9007199254740993,'Alice',12.3450),(42,'Bob',9007199254740993.1234)";
        await cmd.ExecuteNonQueryAsync();
        try
        {
            using var factory = new Factory();
            using var admin = factory.CreateClient();
            await Login(admin);
            var b = new MySqlConnectionStringBuilder(cs);
            var created = await admin.PostAsJsonAsync(
                "/api/admin/connections",
                new ConnectionInput(
                    "Copy",
                    b.Server,
                    b.Port,
                    b.Database,
                    b.UserID,
                    b.Password,
                    false
                )
            );
            created.EnsureSuccessStatusCode();
            var id = (await created.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("id")
                .GetInt32();
            var path = $"/api/connections/{id}/tables/{destination}";
            var layoutPath = $"/api/admin/connections/{id}/tables/{destination}/layout";
            var lookup = new LookupConfig(
                source,
                "id",
                "name",
                [],
                [new("name", "copied_name"), new("price", "copied_price")]
            );
            var lookupField = new LayoutField(
                "source_id",
                "Source",
                "",
                0,
                false,
                false,
                "lookup",
                Lookup: lookup
            );
            var formula = new LayoutField(
                "total",
                "Total",
                "",
                3,
                false,
                true,
                "formula",
                Formula: "Round([copied_price] * [qty], 2)"
            );
            LayoutField[] fields =
            [
                lookupField,
                new("copied_name", "Name", "", 1, false, false, "text", Required: true),
                formula,
                formula with
                {
                    Name = "greeting",
                    Formula = "Concat(Upper([copied_name]), ' x', [qty])",
                },
            ];
            await SaveConfiguration(admin, layoutPath, fields);
            var preview = await admin.PostAsJsonAsync(
                path + "/lookups/source_id/copy",
                new { key = "42" }
            );
            preview.EnsureSuccessStatusCode();
            Assert.Equal(
                "9007199254740993.1234",
                (await preview.Content.ReadFromJsonAsync<JsonElement>())
                    .GetProperty("values")
                    .GetProperty("copied_price")
                    .GetString()
            );
            // API clients need send only the lookup key: copies populate required fields before validation.
            (
                await admin.PostAsJsonAsync(
                    path + "/create",
                    new
                    {
                        values = new
                        {
                            source_id = "9007199254740993",
                            qty = 2,
                            copied_price = "999",
                        },
                    }
                )
            ).EnsureSuccessStatusCode();
            async Task<JsonElement> Row() =>
                (await admin.GetFromJsonAsync<JsonElement>(path + "/records")).GetProperty("rows")[
                    0
                ];
            var row = await Row();
            Assert.Equal("Alice", row.GetProperty("values").GetProperty("copied_name").GetString());
            Assert.Equal(
                "12.3450",
                row.GetProperty("values").GetProperty("copied_price").GetString()
            );
            Assert.Equal("24.69", row.GetProperty("joinedValues").GetProperty("total").GetString());
            Assert.Equal(
                "ALICE x2",
                row.GetProperty("joinedValues").GetProperty("greeting").GetString()
            );
            Assert.False(row.GetProperty("values").TryGetProperty("total", out _));
            var key = new { id = row.GetProperty("values").GetProperty("id").GetInt32() };
            var version = row.GetProperty("version").GetString();
            cmd.CommandText =
                $"UPDATE `{source}` SET name='Changed source',price=50 WHERE id=9007199254740993";
            await cmd.ExecuteNonQueryAsync();
            (
                await admin.PostAsJsonAsync(
                    path + "/update",
                    new
                    {
                        key,
                        version,
                        values = new { qty = 3 },
                    }
                )
            ).EnsureSuccessStatusCode();
            row = await Row();
            Assert.Equal("Alice", row.GetProperty("values").GetProperty("copied_name").GetString());
            version = row.GetProperty("version").GetString();
            (
                await admin.PostAsJsonAsync(
                    path + "/update",
                    new
                    {
                        key,
                        version,
                        values = new { source_id = "9007199254740993", copied_name = "Forged" },
                    }
                )
            ).EnsureSuccessStatusCode();
            row = await Row();
            Assert.Equal(
                "Changed source",
                row.GetProperty("values").GetProperty("copied_name").GetString()
            );
            Assert.Equal(
                "150.00",
                row.GetProperty("joinedValues").GetProperty("total").GetString()
            );
            Assert.Equal(
                HttpStatusCode.Conflict,
                (
                    await admin.PostAsJsonAsync(
                        path + "/update",
                        new
                        {
                            key,
                            version,
                            values = new { source_id = "42" },
                        }
                    )
                ).StatusCode
            );
            version = row.GetProperty("version").GetString();
            // Clearing copies NULL; required-field failure rolls back the whole update.
            Assert.Equal(
                HttpStatusCode.BadRequest,
                (
                    await admin.PostAsJsonAsync(
                        path + "/update",
                        new
                        {
                            key,
                            version,
                            values = new { source_id = (string?)null },
                        }
                    )
                ).StatusCode
            );
            var unchanged = await Row();
            Assert.Equal(
                "Changed source",
                unchanged.GetProperty("values").GetProperty("copied_name").GetString()
            );
            Assert.Equal(
                "50.0000",
                unchanged.GetProperty("values").GetProperty("copied_price").GetString()
            );
            version = unchanged.GetProperty("version").GetString();
            fields[1] = fields[1] with { Required = false };
            await SaveConfiguration(admin, layoutPath, fields);
            (
                await admin.PostAsJsonAsync(
                    path + "/update",
                    new
                    {
                        key,
                        version,
                        values = new { source_id = (string?)null },
                    }
                )
            ).EnsureSuccessStatusCode();
            Assert.Equal(
                JsonValueKind.Null,
                (await Row()).GetProperty("values").GetProperty("copied_price").ValueKind
            );
            // Locked copies stay locked after reopening, including direct API updates.
            row = await Row();
            Assert.Equal(
                HttpStatusCode.BadRequest,
                (
                    await admin.PostAsJsonAsync(
                        path + "/update",
                        new
                        {
                            key,
                            version = row.GetProperty("version").GetString(),
                            values = new { copied_name = "Locked override" },
                        }
                    )
                ).StatusCode
            );
            fields[0] = lookupField with
            {
                Lookup = lookup with
                {
                    CopyMappings = [new("name", "copied_name", true), new("price", "copied_price")],
                },
            };
            await SaveConfiguration(admin, layoutPath, fields);
            // Editable override survives same-request lookup selection, including explicit NULL.
            (
                await admin.PostAsJsonAsync(
                    path + "/update",
                    new
                    {
                        key,
                        version = row.GetProperty("version").GetString(),
                        values = new
                        {
                            source_id = "42",
                            copied_name = "My description",
                            copied_price = "123",
                        },
                    }
                )
            ).EnsureSuccessStatusCode();
            row = await Row();
            Assert.Equal(
                "My description",
                row.GetProperty("values").GetProperty("copied_name").GetString()
            );
            Assert.Equal(
                "9007199254740993.1234",
                row.GetProperty("values").GetProperty("copied_price").GetString()
            );
            (
                await admin.PostAsJsonAsync(
                    path + "/update",
                    new
                    {
                        key,
                        version = row.GetProperty("version").GetString(),
                        values = new { copied_name = (string?)null },
                    }
                )
            ).EnsureSuccessStatusCode();
            row = await Row();
            Assert.Equal(
                JsonValueKind.Null,
                row.GetProperty("values").GetProperty("copied_name").ValueKind
            );
            // Reselecting without an override repopulates even the same lookup key.
            (
                await admin.PostAsJsonAsync(
                    path + "/update",
                    new
                    {
                        key,
                        version = row.GetProperty("version").GetString(),
                        values = new { source_id = "42" },
                    }
                )
            ).EnsureSuccessStatusCode();
            row = await Row();
            Assert.Equal("Bob", row.GetProperty("values").GetProperty("copied_name").GetString());
            var resolve = await admin.PostAsJsonAsync(
                path + "/joins/resolve",
                new
                {
                    values = new
                    {
                        copied_price = "4.25",
                        qty = 3,
                        copied_name = "Preview",
                    },
                }
            );
            resolve.EnsureSuccessStatusCode();
            Assert.Equal(
                "12.75",
                (await resolve.Content.ReadFromJsonAsync<JsonElement>())
                    .GetProperty("values")
                    .GetProperty("total")
                    .GetString()
            );
            var selective = await admin.PostAsJsonAsync(
                path + "/joins/resolve",
                new
                {
                    values = new
                    {
                        copied_price = "4.25",
                        qty = 3,
                        copied_name = "Preview",
                    },
                    changedFields = new[] { "copied_price" },
                }
            );
            selective.EnsureSuccessStatusCode();
            var selectiveValues = (await selective.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("values");
            Assert.Equal("12.75", selectiveValues.GetProperty("total").GetString());
            Assert.False(selectiveValues.TryGetProperty("greeting", out _));
            var unrelated = await admin.PostAsJsonAsync(
                path + "/joins/resolve",
                new
                {
                    values = new
                    {
                        copied_price = "4.25",
                        qty = 3,
                        copied_name = "Preview",
                    },
                    changedFields = new[] { "source_id" },
                }
            );
            unrelated.EnsureSuccessStatusCode();
            Assert.Empty(
                (await unrelated.Content.ReadFromJsonAsync<JsonElement>())
                    .GetProperty("values")
                    .EnumerateObject()
            );
            foreach (
                var invalid in new[]
                {
                    formula with
                    {
                        Formula = "Unknown([qty])",
                    },
                    formula with
                    {
                        Formula = "[total] + 1",
                    },
                    formula with
                    {
                        ReadOnly = false,
                    },
                    formula with
                    {
                        Name = "qty",
                    },
                }
            )
                Assert.Equal(
                    HttpStatusCode.BadRequest,
                    (await PutObjectConfiguration(admin, layoutPath, new[] { invalid })).StatusCode
                );
            foreach (
                var mappings in new List<LookupCopyMapping>[]
                {
                    [new("name", "id")],
                    [new("name", "copied_price")],
                    [new("price", "source_id")],
                    [new("name", "copied_name"), new("name", "copied_name")],
                    [new("missing", "copied_name")],
                }
            )
                Assert.Equal(
                    HttpStatusCode.BadRequest,
                    (
                        await PutObjectConfiguration(
                            admin,
                            layoutPath,
                            new[]
                            {
                                lookupField with
                                {
                                    Lookup = lookup with { CopyMappings = mappings },
                                },
                            }
                        )
                    ).StatusCode
                );
            Assert.Equal(
                HttpStatusCode.BadRequest,
                (
                    await admin.PostAsJsonAsync(
                        path + "/create",
                        new { values = new { total = "123" } }
                    )
                ).StatusCode
            );
            Assert.Equal(
                HttpStatusCode.BadRequest,
                (
                    await admin.PostAsJsonAsync(
                        path + "/lookups/source_id/copy",
                        new { key = "999" }
                    )
                ).StatusCode
            );
            var user = await admin.PostAsJsonAsync(
                "/api/admin/users",
                new UserInput("copy_member", "member-password-12345", false, true)
            );
            var uid = (await user.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("id")
                .GetInt32();
            (
                await admin.PutAsJsonAsync(
                    "/api/admin/grants",
                    new TableGrant
                    {
                        UserId = uid,
                        ConnectionId = id,
                        Table = destination,
                        Read = true,
                        Create = true,
                    }
                )
            ).EnsureSuccessStatusCode();
            using var member = factory.CreateClient();
            await Login(member, "copy_member", "member-password-12345");
            Assert.Equal(
                HttpStatusCode.Forbidden,
                (
                    await member.PostAsJsonAsync(
                        path + "/lookups/source_id/copy",
                        new { key = "42" }
                    )
                ).StatusCode
            );
            Assert.Equal(
                HttpStatusCode.Forbidden,
                (
                    await member.PostAsJsonAsync(
                        path + "/create",
                        new { values = new { source_id = "42", qty = 1 } }
                    )
                ).StatusCode
            );
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE `{destination}`; DROP TABLE `{source}`";
            await cmd.ExecuteNonQueryAsync();
        }
    }
}

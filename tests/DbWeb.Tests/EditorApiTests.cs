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
    public async Task DatesAndDropdownsRoundTripAndRejectInvalidConfigurationAndValues()
    {
        var cs = Environment.GetEnvironmentVariable("MARIADB_TEST_CONNECTION");
        if (string.IsNullOrEmpty(cs))
        {
            Assert.False(Environment.GetEnvironmentVariable("CI") == "true");
            return;
        }
        await using var conn = new MySqlConnection(cs);
        await conn.OpenAsync();
        var table = "editor_" + Guid.NewGuid().ToString("N");
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"CREATE TABLE `{table}` (id INT AUTO_INCREMENT PRIMARY KEY, title VARCHAR(100), status VARCHAR(30) NOT NULL DEFAULT 'draft', day DATE, happened DATETIME(6), stamped TIMESTAMP(6) NULL)";
        await cmd.ExecuteNonQueryAsync();
        try
        {
            using var f = new Factory();
            using var admin = f.CreateClient();
            await Login(admin);
            var builder = new MySqlConnectionStringBuilder(cs);
            var response = await admin.PostAsJsonAsync(
                "/api/admin/connections",
                new ConnectionInput(
                    "Editor test",
                    builder.Server,
                    builder.Port,
                    builder.Database,
                    builder.UserID,
                    builder.Password,
                    false
                )
            );
            response.EnsureSuccessStatusCode();
            var id = (await response.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("id")
                .GetInt32();
            var path = $"/api/connections/{id}/tables/{table}";
            var layoutPath = $"/api/admin/connections/{id}/tables/{table}/layout";
            var field = new LayoutField(
                "status",
                "Status",
                "",
                0,
                false,
                false,
                "dropdown",
                Options: [new("draft", "Draft document"), new("ready", "Ready to publish")]
            );
            var day = new LayoutField("day", "Day", "", 1, false, false, "date");
            var time = new LayoutField("happened", "Happened", "", 2, false, false, "datetime");
            var stamp = new LayoutField("stamped", "Timestamp", "", 3, false, false, "date");
            await SaveConfiguration(admin, layoutPath, new[] { field, day, time, stamp });
            foreach (
                var options in new List<DropdownOption>[]
                {
                    [],
                    [new("a", "Same"), new("b", "same")],
                    [new("a", "One"), new("A", "Two")],
                    [new("", "Blank")],
                    [new(" a", "Space")],
                }
            )
                Assert.Equal(
                    HttpStatusCode.BadRequest,
                    (
                        await PutObjectConfiguration(
                            admin,
                            layoutPath,
                            new[] { field with { Options = options } }
                        )
                    ).StatusCode
                );
            Assert.Equal(
                HttpStatusCode.BadRequest,
                (
                    await PutObjectConfiguration(
                        admin,
                        layoutPath,
                        new[] { field with { Name = "id" } }
                    )
                ).StatusCode
            );
            Assert.Equal(
                HttpStatusCode.BadRequest,
                (
                    await PutObjectConfiguration(
                        admin,
                        layoutPath,
                        new[] { time with { Name = "status" } }
                    )
                ).StatusCode
            );
            Assert.Equal(
                HttpStatusCode.BadRequest,
                (
                    await admin.PostAsJsonAsync(
                        path + "/create",
                        new { values = new { status = "missing" } }
                    )
                ).StatusCode
            );
            Assert.Equal(
                HttpStatusCode.BadRequest,
                (
                    await admin.PostAsJsonAsync(
                        path + "/create",
                        new { values = new { status = 12 } }
                    )
                ).StatusCode
            );
            foreach (
                var invalidDate in new[]
                {
                    "2026-02-30",
                    "2026-09-15T12:00:00Z",
                    "2026-09-15T12:00:00+02:00",
                    "invalid",
                }
            )
                Assert.Equal(
                    HttpStatusCode.BadRequest,
                    (
                        await admin.PostAsJsonAsync(
                            path + "/create",
                            new { values = new { happened = invalidDate } }
                        )
                    ).StatusCode
                );
            (
                await admin.PostAsJsonAsync(
                    path + "/create",
                    new
                    {
                        values = new
                        {
                            title = "Temporal test",
                            status = "ready",
                            day = "2026-09-15",
                            happened = "2026-09-15T13:14:15.123456",
                            stamped = "2026-09-16",
                        },
                    }
                )
            ).EnsureSuccessStatusCode();
            var result = await admin.GetFromJsonAsync<JsonElement>(path + "/records");
            var row = result.GetProperty("rows")[0];
            var values = row.GetProperty("values");
            Assert.Equal("2026-09-15", values.GetProperty("day").GetString());
            Assert.Equal("2026-09-15T13:14:15.123456", values.GetProperty("happened").GetString());
            Assert.Equal("2026-09-16T00:00:00.000000", values.GetProperty("stamped").GetString());
            Assert.Equal("ready", values.GetProperty("status").GetString());
            Assert.Equal(
                "Ready to publish",
                row.GetProperty("displayValues").GetProperty("status").GetString()
            );
            var key = new { id = values.GetProperty("id").GetInt32() };
            (
                await admin.PostAsJsonAsync(
                    path + "/update",
                    new
                    {
                        values = new { title = "Unrelated edit" },
                        key,
                        version = row.GetProperty("version").GetString(),
                    }
                )
            ).EnsureSuccessStatusCode();
            result = await admin.GetFromJsonAsync<JsonElement>(path + "/records");
            row = result.GetProperty("rows")[0];
            Assert.Equal(
                "2026-09-15T13:14:15.123456",
                row.GetProperty("values").GetProperty("happened").GetString()
            );
            (
                await admin.PostAsJsonAsync(
                    path + "/update",
                    new
                    {
                        values = new
                        {
                            happened = "2026-10-20T17:18",
                            stamped = (string?)null,
                            day = (string?)null,
                            status = "draft",
                        },
                        key,
                        version = row.GetProperty("version").GetString(),
                    }
                )
            ).EnsureSuccessStatusCode();
            result = await admin.GetFromJsonAsync<JsonElement>(path + "/records");
            row = result.GetProperty("rows")[0];
            Assert.Equal(
                "2026-10-20T17:18:00.000000",
                row.GetProperty("values").GetProperty("happened").GetString()
            );
            Assert.Equal(
                JsonValueKind.Null,
                row.GetProperty("values").GetProperty("stamped").ValueKind
            );
            Assert.Equal(
                "Draft document",
                row.GetProperty("displayValues").GetProperty("status").GetString()
            );
            (
                await admin.PostAsJsonAsync(
                    path + "/create",
                    new { values = new { title = "Defaults" } }
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

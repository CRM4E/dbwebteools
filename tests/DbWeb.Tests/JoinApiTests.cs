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
    public async Task JoinedFieldsAreReadOnlyPermissionCheckedAndDoNotChangeSourceVersions()
    {
        var cs = Environment.GetEnvironmentVariable("MARIADB_TEST_CONNECTION");
        if (string.IsNullOrEmpty(cs))
        {
            Assert.False(Environment.GetEnvironmentVariable("CI") == "true");
            return;
        }
        await using var conn = new MySqlConnection(cs);
        await conn.OpenAsync();
        var target = "joined_" + Guid.NewGuid().ToString("N");
        var source = "source_" + Guid.NewGuid().ToString("N");
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"CREATE TABLE `{target}` (id BIGINT PRIMARY KEY, email VARCHAR(100), amount DECIMAL(20,4)); CREATE TABLE `{source}` (id INT AUTO_INCREMENT PRIMARY KEY, title VARCHAR(100), target_id BIGINT); INSERT INTO `{target}` VALUES (9007199254740993,'private@example.test',9007199254740993.1234),(42,'second@example.test',1.25),(1,'checked@example.test',1),(0,'unchecked@example.test',0),(9007199254740992,'neighbor-low@example.test',2),(9007199254740994,'neighbor-high@example.test',3); INSERT INTO `{source}` (title,target_id) VALUES ('Matched',9007199254740993),('Missing',999),('Empty',NULL)";
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
                    "Join test",
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
            var path = $"/api/connections/{id}/tables/{source}";
            var layoutPath = $"/api/admin/connections/{id}/tables/{source}/layout";
            var baseline = await admin.GetFromJsonAsync<JsonElement>(path + "/records");
            var version = baseline.GetProperty("rows")[0].GetProperty("version").GetString();
            var join = new JoinConfig("target_id", target, "id", "email");
            var virtualField = new LayoutField(
                "customer_email",
                "Customer email",
                "Customer",
                3,
                false,
                true,
                "join",
                Join: join,
                ListOrder: 0
            );
            var fields = new[]
            {
                new LayoutField("id", "ID", "", 0, false, true, "auto", ShowInList: false),
                new LayoutField("title", "Title", "", 1, true, false, "text", ListOrder: 1),
                virtualField,
                virtualField with
                {
                    Name = "customer_amount",
                    Label = "Amount",
                    Join = join with { ValueColumn = "amount" },
                    ShowInList = false,
                },
            };
            await SaveConfiguration(admin, layoutPath, fields);
            version = (
                await admin.GetFromJsonAsync<JsonElement>(path + "/records")
            ).GetProperty("rows")[0].GetProperty("version").GetString();
            var settings = await admin.GetFromJsonAsync<JsonElement>(path + "/settings");
            Assert.False(settings.GetProperty("fields")[0].GetProperty("showInList").GetBoolean());
            Assert.Equal(0, settings.GetProperty("fields")[2].GetProperty("listOrder").GetInt32());
            var result = await admin.GetFromJsonAsync<JsonElement>(path + "/records");
            Assert.Equal(3, result.GetProperty("total").GetInt32());
            Assert.Equal(4, result.GetProperty("columns").GetArrayLength());
            Assert.Contains(
                "datatype",
                result.GetProperty("columns").EnumerateArray().Select(column =>
                    column.GetProperty("name").GetString()
                )
            );
            Assert.Equal(2, result.GetProperty("joinedColumns").GetArrayLength());
            var rows = result.GetProperty("rows");
            Assert.False(rows[0].GetProperty("values").TryGetProperty("customer_email", out _));
            Assert.Equal(1, rows[0].GetProperty("values").GetProperty("id").GetInt32());
            Assert.Equal(
                "private@example.test",
                rows[0].GetProperty("joinedValues").GetProperty("customer_email").GetString()
            );
            Assert.Equal(
                "9007199254740993.1234",
                rows[0].GetProperty("joinedValues").GetProperty("customer_amount").GetString()
            );
            Assert.Equal(
                JsonValueKind.Null,
                rows[1].GetProperty("joinedValues").GetProperty("customer_email").ValueKind
            );
            Assert.Equal(
                JsonValueKind.Null,
                rows[2].GetProperty("joinedValues").GetProperty("customer_email").ValueKind
            );
            foreach (var flag in new[] { true, false })
            {
                var checkbox = await admin.PostAsJsonAsync(
                    path + "/joins/resolve",
                    new { values = new { target_id = flag } }
                );
                checkbox.EnsureSuccessStatusCode();
                Assert.Equal(
                    flag ? "checked@example.test" : "unchecked@example.test",
                    (await checkbox.Content.ReadFromJsonAsync<JsonElement>())
                        .GetProperty("values")
                        .GetProperty("customer_email")
                        .GetString()
                );
            }
            var resolved = await admin.PostAsJsonAsync(
                path + "/joins/resolve",
                new { values = new { target_id = 42 } }
            );
            resolved.EnsureSuccessStatusCode();
            Assert.Equal(
                "second@example.test",
                (await resolved.Content.ReadFromJsonAsync<JsonElement>())
                    .GetProperty("values")
                    .GetProperty("customer_email")
                    .GetString()
            );
            Assert.Equal(
                HttpStatusCode.BadRequest,
                (
                    await admin.PostAsJsonAsync(
                        path + "/create",
                        new { values = new { customer_email = "overwrite" } }
                    )
                ).StatusCode
            );
            Assert.Equal(
                HttpStatusCode.BadRequest,
                (
                    await admin.PostAsJsonAsync(
                        path + "/update",
                        new
                        {
                            values = new { customer_email = "overwrite" },
                            key = new { id = 1 },
                            version,
                        }
                    )
                ).StatusCode
            );
            foreach (
                var invalid in new[]
                {
                    virtualField with
                    {
                        Name = "id",
                    },
                    virtualField with
                    {
                        ReadOnly = false,
                    },
                    virtualField with
                    {
                        Join = join with { SourceColumn = "customer_email" },
                    },
                    virtualField with
                    {
                        Join = join with { SourceColumn = "title" },
                    },
                    virtualField with
                    {
                        Join = join with { KeyColumn = "email" },
                    },
                    virtualField with
                    {
                        Join = join with { ValueColumn = "x`; DROP TABLE y;--" },
                    },
                }
            )
                Assert.Equal(
                    HttpStatusCode.BadRequest,
                    (await PutObjectConfiguration(admin, layoutPath, new[] { invalid })).StatusCode
                );
            Assert.Equal(
                HttpStatusCode.BadRequest,
                (
                    await PutObjectConfiguration(
                        admin,
                        layoutPath,
                        new[] { virtualField, virtualField with { Name = "CUSTOMER_EMAIL" } }
                    )
                ).StatusCode
            );
            cmd.CommandText =
                $"UPDATE `{target}` SET email='changed@example.test' WHERE id=9007199254740993";
            await cmd.ExecuteNonQueryAsync();
            result = await admin.GetFromJsonAsync<JsonElement>(path + "/records");
            Assert.Equal(
                "changed@example.test",
                result
                    .GetProperty("rows")[0]
                    .GetProperty("joinedValues")
                    .GetProperty("customer_email")
                    .GetString()
            );
            (
                await admin.PostAsJsonAsync(
                    path + "/update",
                    new
                    {
                        values = new { title = "Updated" },
                        key = new { id = 1 },
                        version,
                    }
                )
            ).EnsureSuccessStatusCode();
            var memberResult = await admin.PostAsJsonAsync(
                "/api/admin/users",
                new UserInput("joinmember", "member-password-12345", false, true)
            );
            var uid = (await memberResult.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("id")
                .GetInt32();
            (
                await admin.PutAsJsonAsync(
                    "/api/admin/grants",
                    new TableGrant
                    {
                        UserId = uid,
                        ConnectionId = id,
                        Table = source,
                        Read = true,
                    }
                )
            ).EnsureSuccessStatusCode();
            using var member = f.CreateClient();
            await Login(member, "joinmember", "member-password-12345");
            var restricted = await member.GetStringAsync(path + "/records");
            Assert.DoesNotContain("changed@example.test", restricted);
            Assert.DoesNotContain("second@example.test", restricted);
            var deniedResolution = await member.PostAsJsonAsync(
                path + "/joins/resolve",
                new { values = new { target_id = 42 } }
            );
            deniedResolution.EnsureSuccessStatusCode();
            Assert.Empty(
                (await deniedResolution.Content.ReadFromJsonAsync<JsonElement>())
                    .GetProperty("values")
                    .EnumerateObject()
            );
            (
                await admin.PutAsJsonAsync(
                    "/api/admin/grants",
                    new TableGrant
                    {
                        UserId = uid,
                        ConnectionId = id,
                        Table = target,
                        Read = true,
                    }
                )
            ).EnsureSuccessStatusCode();
            var granted = await member.PostAsJsonAsync(
                path + "/joins/resolve",
                new { values = new { target_id = 42 } }
            );
            Assert.Contains("second@example.test", await granted.Content.ReadAsStringAsync());
            // Older layout requests omit list settings: all fields remain visible by default.
            (
                await admin.PutAsJsonAsync(
                    layoutPath,
                    new[]
                    {
                        new
                        {
                            name = "title",
                            label = "Title",
                            section = "",
                            order = 0,
                            hidden = true,
                            readOnly = false,
                            widget = "text",
                        },
                    }
                )
            ).EnsureSuccessStatusCode();
            settings = await admin.GetFromJsonAsync<JsonElement>(path + "/settings");
            Assert.True(settings.GetProperty("fields")[0].GetProperty("showInList").GetBoolean());
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE `{source}`; DROP TABLE `{target}`";
            await cmd.ExecuteNonQueryAsync();
        }
    }
}

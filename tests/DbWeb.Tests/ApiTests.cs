using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DbWeb.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using MySqlConnector;
using Xunit;

namespace DbWeb.Tests;

public partial class ApiTests
{
    private class Factory : WebApplicationFactory<Program>
    {
        readonly string path = Path.Combine(Path.GetTempPath(), "dbweb-test-" + Guid.NewGuid());

        protected override void ConfigureWebHost(IWebHostBuilder b)
        {
            b.UseEnvironment("Development");
            b.ConfigureAppConfiguration(
                (_, c) =>
                    c.AddInMemoryCollection(
                        new Dictionary<string, string?>
                        {
                            { "DataDirectory", path },
                            { "Bootstrap:Password", "test-only-password-12345" },
                        }
                    )
            );
        }

        protected override void Dispose(bool d)
        {
            base.Dispose(d);
            if (d && Directory.Exists(path))
                Directory.Delete(path, true);
        }
    }

    static async Task Csrf(HttpClient c)
    {
        var token = await c.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        c.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        c.DefaultRequestHeaders.Add("X-CSRF-TOKEN", token.GetProperty("token").GetString());
    }

    static async Task Login(
        HttpClient c,
        string username = "admin",
        string password = "test-only-password-12345"
    )
    {
        await Csrf(c);
        var r = await c.PostAsJsonAsync("/api/auth/login", new { username, password });
        r.EnsureSuccessStatusCode();
        await Csrf(c);
    }

    static string ObjectPath(string layoutPath) =>
        layoutPath.EndsWith("/layout", StringComparison.Ordinal)
            ? layoutPath[..^"/layout".Length] + "/object"
            : throw new ArgumentException("Expected a layout endpoint.", nameof(layoutPath));

    static async Task<StoredObjectDefinition> CanonicalConfiguration(
        HttpClient client,
        string layoutPath,
        IEnumerable<LayoutField> fields,
        ListView? view = null
    )
    {
        var objectPath = ObjectPath(layoutPath);
        var current = await client.GetFromJsonAsync<ObjectDefinition>(objectPath);
        return ObjectModel.Normalize(
            ObjectModel.Split(
                new LayoutDefinition(fields.ToList(), view ?? current?.View)
            )
        );
    }

    static async Task<HttpResponseMessage> PutObjectConfiguration(
        HttpClient client,
        string layoutPath,
        IEnumerable<LayoutField> fields,
        ListView? view = null
    )
    {
        var configuration = await CanonicalConfiguration(client, layoutPath, fields, view);
        return await client.PutAsJsonAsync(ObjectPath(layoutPath), configuration.Object);
    }

    static async Task SaveConfiguration(
        HttpClient client,
        string layoutPath,
        IEnumerable<LayoutField> fields,
        ListView? view = null
    )
    {
        var configuration = await CanonicalConfiguration(client, layoutPath, fields, view);
        (
            await client.PutAsJsonAsync(ObjectPath(layoutPath), configuration.Object)
        ).EnsureSuccessStatusCode();
        var completedObject = (
            await client.GetFromJsonAsync<ObjectDefinition>(ObjectPath(layoutPath))
        )!;
        var completedLayout = ObjectModel.CompleteLayout(completedObject, configuration.Layout);
        (
            await client.PutAsJsonAsync(layoutPath, completedLayout)
        ).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task AnonymousCannotReadAdministration()
    {
        using var f = new Factory();
        using var c = f.CreateClient();
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await c.GetAsync("/api/admin/users")).StatusCode
        );
    }

    [Fact]
    public async Task LoginRequiresCsrf()
    {
        using var f = new Factory();
        using var c = f.CreateClient();
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (
                await c.PostAsJsonAsync(
                    "/api/auth/login",
                    new { username = "admin", password = "test-only-password-12345" }
                )
            ).StatusCode
        );
    }

    [Fact]
    public async Task PermissionsAndRevocationAreEnforced()
    {
        using var f = new Factory();
        using var admin = f.CreateClient();
        await Login(admin);
        var r = await admin.PostAsJsonAsync(
            "/api/admin/users",
            new
            {
                username = "member",
                password = "member-password-12345",
                isAdmin = false,
                enabled = true,
            }
        );
        r.EnsureSuccessStatusCode();
        var id = (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        using var member = f.CreateClient();
        await Login(member, "member", "member-password-12345");
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await member.GetAsync("/api/admin/users")).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await member.GetAsync("/api/connections/999/tables/private/records")).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (
                await member.PostAsJsonAsync(
                    "/api/connections/999/tables/private/delete",
                    new
                    {
                        values = new { },
                        key = new { id = 1 },
                        version = "x",
                    }
                )
            ).StatusCode
        );
        (
            await admin.PutAsJsonAsync(
                $"/api/admin/users/{id}",
                new
                {
                    username = "member",
                    password = (string?)null,
                    isAdmin = false,
                    enabled = false,
                }
            )
        ).EnsureSuccessStatusCode();
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await member.GetAsync("/api/auth/me")).StatusCode
        );
    }

    [Fact]
    public async Task ConnectionsNeverReturnPasswords()
    {
        using var f = new Factory();
        using var c = f.CreateClient();
        await Login(c);
        (
            await c.PostAsJsonAsync(
                "/api/admin/connections",
                new
                {
                    name = "Demo",
                    host = "localhost",
                    port = 3306,
                    database = "demo",
                    username = "demo",
                    password = "must-not-appear",
                    verifyTls = true,
                }
            )
        ).EnsureSuccessStatusCode();
        var json = await c.GetStringAsync("/api/admin/connections");
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("must-not-appear", json);
    }

    [Fact]
    public async Task LookupsValidateSearchResolveAndEnforceTargetPermissions()
    {
        var cs = Environment.GetEnvironmentVariable("MARIADB_TEST_CONNECTION");
        if (string.IsNullOrEmpty(cs))
        {
            Assert.False(Environment.GetEnvironmentVariable("CI") == "true");
            return;
        }
        await using var conn = new MySqlConnection(cs);
        await conn.OpenAsync();
        var target = "lookup_" + Guid.NewGuid().ToString("N");
        var source = "source_" + Guid.NewGuid().ToString("N");
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"CREATE TABLE `{target}` (id BIGINT PRIMARY KEY, name VARCHAR(100), email VARCHAR(100)); CREATE TABLE `{source}` (id INT AUTO_INCREMENT PRIMARY KEY, target_id BIGINT NULL); INSERT INTO `{target}` VALUES (9007199254740993,'Alice 100%','special@example.test'),(42,'Bob','other@example.test'); INSERT INTO `{source}` (target_id) VALUES (9007199254740993)";
        await cmd.ExecuteNonQueryAsync();
        try
        {
            using var f = new Factory();
            using var admin = f.CreateClient();
            await Login(admin);
            var builder = new MySqlConnectionStringBuilder(cs);
            var created = await admin.PostAsJsonAsync(
                "/api/admin/connections",
                new ConnectionInput(
                    "Lookup test",
                    builder.Server,
                    builder.Port,
                    builder.Database,
                    builder.UserID,
                    builder.Password,
                    false
                )
            );
            created.EnsureSuccessStatusCode();
            var id = (await created.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("id")
                .GetInt32();
            var path = $"/api/connections/{id}/tables/{source}";
            var layoutPath = $"/api/admin/connections/{id}/tables/{source}/layout";
            var lookup = new LookupConfig(target, "id", "name", ["email"]);
            var field = new LayoutField(
                "target_id",
                "Person",
                "",
                1,
                false,
                false,
                "lookup",
                lookup
            );
            await SaveConfiguration(admin, layoutPath, new[] { field });
            foreach (var search in new[] { "9007199254740993", "Alice", "special@", "%" })
            {
                var page = await admin.GetFromJsonAsync<JsonElement>(
                    $"{path}/lookups/target_id?search={Uri.EscapeDataString(search)}"
                );
                Assert.Equal(1, page.GetProperty("total").GetInt32());
                Assert.Equal(
                    "9007199254740993",
                    page.GetProperty("rows")[0].GetProperty("id").GetString()
                );
            }
            var noMatch = await admin.GetFromJsonAsync<JsonElement>(
                $"{path}/lookups/target_id?search=%27%20OR%201=1--"
            );
            Assert.Equal(0, noMatch.GetProperty("total").GetInt32());
            var second = await admin.GetFromJsonAsync<JsonElement>(
                $"{path}/lookups/target_id?page=2&size=1"
            );
            Assert.Equal(2, second.GetProperty("total").GetInt32());
            Assert.Single(second.GetProperty("rows").EnumerateArray());
            var resolved = await admin.GetFromJsonAsync<JsonElement>(
                $"{path}/lookups/target_id?key=9007199254740993"
            );
            Assert.Equal("Alice 100%", resolved.GetProperty("label").GetString());
            var missing = await admin.GetFromJsonAsync<JsonElement>(
                $"{path}/lookups/target_id?key=123"
            );
            Assert.False(missing.GetProperty("found").GetBoolean());
            var records = await admin.GetFromJsonAsync<JsonElement>($"{path}/records");
            Assert.Equal(
                "Alice 100%",
                records
                    .GetProperty("rows")[0]
                    .GetProperty("displayValues")
                    .GetProperty("target_id")
                    .GetString()
            );
            Assert.Equal(
                "9007199254740993",
                records
                    .GetProperty("rows")[0]
                    .GetProperty("values")
                    .GetProperty("target_id")
                    .GetString()
            );
            Assert.Equal(
                HttpStatusCode.BadRequest,
                (
                    await admin.PostAsJsonAsync(
                        $"{path}/create",
                        new { values = new { target_id = "999" } }
                    )
                ).StatusCode
            );
            (
                await admin.PostAsJsonAsync(
                    $"{path}/create",
                    new { values = new { target_id = "42" } }
                )
            ).EnsureSuccessStatusCode();
            (
                await admin.PostAsJsonAsync(
                    $"{path}/create",
                    new { values = new { target_id = (string?)null } }
                )
            ).EnsureSuccessStatusCode();
            foreach (
                var invalid in new[]
                {
                    lookup with
                    {
                        KeyColumn = "name",
                    },
                    lookup with
                    {
                        DisplayColumn = "missing",
                    },
                    lookup with
                    {
                        SearchColumns = ["id`; DROP TABLE x;--"],
                    },
                }
            )
                Assert.Equal(
                    HttpStatusCode.BadRequest,
                    (
                        await PutObjectConfiguration(
                            admin,
                            layoutPath,
                            new[] { field with { Lookup = invalid } }
                        )
                    ).StatusCode
                );
            var memberResult = await admin.PostAsJsonAsync(
                "/api/admin/users",
                new UserInput("lookupmember", "member-password-12345", false, true)
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
                        Create = true,
                    }
                )
            ).EnsureSuccessStatusCode();
            using var member = f.CreateClient();
            await Login(member, "lookupmember", "member-password-12345");
            Assert.Equal(
                HttpStatusCode.Forbidden,
                (await member.GetAsync($"{path}/lookups/target_id")).StatusCode
            );
            Assert.Equal(
                HttpStatusCode.Forbidden,
                (await member.GetAsync($"{path}/lookups/target_id?key=42")).StatusCode
            );
            Assert.Equal(
                HttpStatusCode.Forbidden,
                (
                    await member.PostAsJsonAsync(
                        $"{path}/create",
                        new { values = new { target_id = 42 } }
                    )
                ).StatusCode
            );
            var restricted = await member.GetStringAsync($"{path}/records");
            Assert.DoesNotContain("Alice", restricted);
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
            (await member.GetAsync($"{path}/lookups/target_id")).EnsureSuccessStatusCode();
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE `{source}`; DROP TABLE `{target}`";
            await cmd.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public void IdentifierQuotingEscapesBackticks()
    {
        Assert.Equal(
            "`users``; DROP TABLE x;--`",
            DatabaseService.Quote("users`; DROP TABLE x;--")
        );
    }
}

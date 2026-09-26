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
    public async Task CreationDefaultsAndLookupCriteriaAreEnforcedWithoutChangingExistingRelations()
    {
        var cs = Environment.GetEnvironmentVariable("MARIADB_TEST_CONNECTION");
        if (string.IsNullOrEmpty(cs))
        {
            Assert.False(Environment.GetEnvironmentVariable("CI") == "true");
            return;
        }
        await using var c = new MySqlConnection(cs);
        await c.OpenAsync();
        var suffix = Guid.NewGuid().ToString("N");
        var parent = "defaults_p_" + suffix;
        var child = "defaults_c_" + suffix;
        await using var cmd = c.CreateCommand();
        cmd.CommandText =
            $"CREATE TABLE `{parent}` (id BIGINT PRIMARY KEY, name VARCHAR(100), active INT, price DECIMAL(24,4)); INSERT INTO `{parent}` VALUES(9007199254740993,'First allowed',1,12.3456),(42,'Second allowed',1,25),(43,'Excluded',0,10); CREATE TABLE `{child}` (id INT AUTO_INCREMENT PRIMARY KEY,parent_id BIGINT,title VARCHAR(100),status VARCHAR(30),qty INT,amount DECIMAL(24,4),enabled TINYINT,note VARCHAR(100) DEFAULT 'database note',day DATE,stamp DATETIME(6),copied VARCHAR(100));";
        await cmd.ExecuteNonQueryAsync();
        try
        {
            using var factory = new Factory();
            using var admin = factory.CreateClient();
            await Login(admin);
            var b = new MySqlConnectionStringBuilder(cs);
            var r = await admin.PostAsJsonAsync(
                "/api/admin/connections",
                new ConnectionInput(
                    "Defaults",
                    b.Server,
                    b.Port,
                    b.Database,
                    b.UserID,
                    b.Password,
                    false
                )
            );
            r.EnsureSuccessStatusCode();
            var id = (await r.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("id")
                .GetInt32();
            var path = $"/api/connections/{id}/tables/{child}";
            var layoutPath = $"/api/admin/connections/{id}/tables/{child}/layout";
            var lookup = new LookupConfig(
                parent,
                "id",
                "name",
                [],
                [new("name", "copied")],
                new(Filters: [new("active", "eq", "1")])
            );
            List<LayoutField> fields =
            [
                new(
                    "parent_id",
                    "Parent",
                    "",
                    0,
                    false,
                    false,
                    "lookup",
                    Lookup: lookup,
                    CreationDefault: new("9007199254740993")
                ),
                new(
                    "title",
                    "Title",
                    "",
                    1,
                    false,
                    false,
                    "text",
                    Required: true,
                    CreationDefault: new("New title")
                ),
                new(
                    "status",
                    "Status",
                    "",
                    2,
                    false,
                    false,
                    "dropdown",
                    Options: [new("open", "Open"), new("closed", "Closed")],
                    CreationDefault: new("open")
                ),
                new("qty", "Quantity", "", 3, false, false, "number", CreationDefault: new("0")),
                new(
                    "amount",
                    "Amount",
                    "",
                    4,
                    false,
                    false,
                    "number",
                    CreationDefault: new("9007199254740993.1234")
                ),
                new(
                    "enabled",
                    "Enabled",
                    "",
                    5,
                    false,
                    false,
                    "checkbox",
                    CreationDefault: new("false")
                ),
                new("day", "Day", "", 6, false, false, "date", CreationDefault: new("2026-09-16")),
                new(
                    "stamp",
                    "Stamp",
                    "",
                    7,
                    false,
                    false,
                    "datetime",
                    CreationDefault: new("2026-09-16T12:34:56.123456")
                ),
                new(
                    "copied",
                    "Copied",
                    "",
                    8,
                    false,
                    false,
                    "text",
                    CreationDefault: new("Copy must win")
                ),
            ];
            async Task Save() =>
                await SaveConfiguration(admin, layoutPath, fields);
            await Save();
            var preview = await admin.PostAsync(path + "/create-preview", null);
            preview.EnsureSuccessStatusCode();
            var defaults = (await preview.Content.ReadFromJsonAsync<JsonElement>()).GetProperty(
                "values"
            );
            Assert.Equal("First allowed", defaults.GetProperty("copied").GetString());
            Assert.Equal("9007199254740993", defaults.GetProperty("parent_id").GetString());
            Assert.False(defaults.GetProperty("enabled").GetBoolean());
            Assert.False(defaults.TryGetProperty("note", out _));
            (
                await admin.PostAsJsonAsync(path + "/create", new { values = new { } })
            ).EnsureSuccessStatusCode();
            cmd.CommandText =
                $"SELECT CONCAT(title,'|',status,'|',qty,'|',amount,'|',enabled,'|',note,'|',DATE_FORMAT(stamp,'%Y-%m-%d %H:%i:%s.%f'),'|',copied) FROM `{child}`";
            Assert.Equal(
                "New title|open|0|9007199254740993.1234|0|database note|2026-09-16 12:34:56.123456|First allowed",
                await cmd.ExecuteScalarAsync()
            );
            (
                await admin.PostAsJsonAsync(
                    path + "/create",
                    new
                    {
                        values = new
                        {
                            title = "Override",
                            parent_id = 42,
                            qty = 7,
                            amount = (string?)null,
                            note = "",
                            enabled = true,
                        },
                    }
                )
            ).EnsureSuccessStatusCode();
            cmd.CommandText =
                $"SELECT CONCAT(title,'|',parent_id,'|',qty,'|',amount IS NULL,'|',note IS NULL,'|',enabled,'|',copied) FROM `{child}` WHERE title='Override'";
            Assert.Equal("Override|42|7|1|1|1|Second allowed", await cmd.ExecuteScalarAsync());
            Assert.Equal(
                HttpStatusCode.BadRequest,
                (
                    await admin.PostAsJsonAsync(
                        path + "/create",
                        new { values = new { title = (string?)null } }
                    )
                ).StatusCode
            );
            var results = await admin.GetFromJsonAsync<JsonElement>(
                path + "/lookups/parent_id?size=1"
            );
            Assert.Equal(2, results.GetProperty("total").GetInt32());
            Assert.Equal(1, results.GetProperty("rows").GetArrayLength());
            results = await admin.GetFromJsonAsync<JsonElement>(
                path + "/lookups/parent_id?search=Excluded"
            );
            Assert.Equal(0, results.GetProperty("total").GetInt32());
            Assert.Equal(
                HttpStatusCode.BadRequest,
                (
                    await admin.PostAsJsonAsync(
                        path + "/create",
                        new { values = new { parent_id = 43 } }
                    )
                ).StatusCode
            );
            Assert.Equal(
                HttpStatusCode.BadRequest,
                (
                    await admin.PostAsJsonAsync(path + "/lookups/parent_id/copy", new { key = 43 })
                ).StatusCode
            );
            var oldRow = (
                await admin.GetFromJsonAsync<JsonElement>(path + "/records?search=Override")
            ).GetProperty("rows")[0];
            cmd.CommandText = $"UPDATE `{parent}` SET active=0 WHERE id=42";
            await cmd.ExecuteNonQueryAsync();
            var label = await admin.GetFromJsonAsync<JsonElement>(
                path + "/lookups/parent_id?key=42"
            );
            Assert.Equal("Second allowed", label.GetProperty("label").GetString());
            (
                await admin.PostAsJsonAsync(
                    path + "/update",
                    new
                    {
                        values = new { title = "Still editable" },
                        key = new { id = oldRow.GetProperty("values").GetProperty("id") },
                        version = oldRow.GetProperty("version").GetString(),
                    }
                )
            ).EnsureSuccessStatusCode();
            cmd.CommandText = $"SELECT qty FROM `{child}` WHERE title='Still editable'";
            Assert.Equal(7, await cmd.ExecuteScalarAsync());
            // OR criteria stay grouped with the independent search clause.
            fields[0] = fields[0] with
            {
                Lookup = lookup with
                {
                    Criteria = new(
                        Match: "any",
                        Filters: [new("active", "eq", "1"), new("id", "eq", "42")]
                    ),
                },
            };
            await Save();
            results = await admin.GetFromJsonAsync<JsonElement>(
                path + "/lookups/parent_id?search=Excluded"
            );
            Assert.Equal(0, results.GetProperty("total").GetInt32());
            results = await admin.GetFromJsonAsync<JsonElement>(
                path + "/lookups/parent_id?search=Second"
            );
            Assert.Equal(1, results.GetProperty("total").GetInt32());
            // Parent context outranks the configured default and copies, but normal defaults stay editable.
            var tab = new RelatedTab(
                Guid.NewGuid().ToString(),
                "Children",
                child,
                "id",
                "parent_id",
                ["title"],
                LookupField: "parent_id"
            );
            var pageResponse = await admin.PostAsJsonAsync(
                "/api/admin/pages",
                new PageDefinition(0, id, parent, "Parents", "name", [tab])
            );
            pageResponse.EnsureSuccessStatusCode();
            var page = (await pageResponse.Content.ReadFromJsonAsync<PageDefinition>())!;
            var related = $"/api/pages/{page.Id}/tabs/{tab.Id}";
            var pre = await admin.PostAsJsonAsync(
                related + "/create-preview",
                new RelatedCreateInput("{\"id\":42}")
            );
            pre.EnsureSuccessStatusCode();
            var data = await pre.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("42", data.GetProperty("values").GetProperty("parent_id").ToString());
            Assert.Equal(
                "Second allowed",
                data.GetProperty("values").GetProperty("copied").GetString()
            );
            Assert.Equal("New title", data.GetProperty("values").GetProperty("title").GetString());
            Assert.Equal(2, data.GetProperty("lockedFields").GetArrayLength());
            (
                await admin.PostAsJsonAsync(
                    related + "/create",
                    new RelatedCreateInput("{\"id\":42}")
                )
            ).EnsureSuccessStatusCode();
            // Invalid defaults/criteria must fail before configuration is saved.
            foreach (
                var invalid in new[]
                {
                    fields[3] with
                    {
                        CreationDefault = new("not a number"),
                    },
                    fields[2] with
                    {
                        CreationDefault = new("unknown"),
                    },
                    fields[1] with
                    {
                        CreationDefault = new(IsNull: true),
                    },
                    fields[6] with
                    {
                        CreationDefault = new("2026-02-30"),
                    },
                    new LayoutField(
                        "id",
                        "ID",
                        "",
                        0,
                        false,
                        false,
                        "auto",
                        CreationDefault: new("1")
                    ),
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
                        new[]
                        {
                            fields[0] with
                            {
                                Lookup = lookup with
                                {
                                    Criteria = new(Filters: [new("missing", "eq", "1")]),
                                },
                            },
                        }
                    )
                ).StatusCode
            );
            // Explicit NULL default is separate from no default and overrides the database default.
            fields.Add(
                new("note", "Note", "", 9, false, false, "text", CreationDefault: new(IsNull: true))
            );
            await Save();
            (
                await admin.PostAsJsonAsync(
                    path + "/create",
                    new { values = new { title = "NULL default" } }
                )
            ).EnsureSuccessStatusCode();
            cmd.CommandText = $"SELECT note IS NULL FROM `{child}` WHERE title='NULL default'";
            Assert.Equal(1, Convert.ToInt32(await cmd.ExecuteScalarAsync()));
            var u = await admin.PostAsJsonAsync(
                "/api/admin/users",
                new UserInput("default_member", "member-password-12345", false, true)
            );
            var uid = (await u.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("id")
                .GetInt32();
            (
                await admin.PutAsJsonAsync(
                    "/api/admin/grants",
                    new TableGrant
                    {
                        UserId = uid,
                        ConnectionId = id,
                        Table = child,
                        Read = true,
                        Create = true,
                    }
                )
            ).EnsureSuccessStatusCode();
            using var member = factory.CreateClient();
            await Login(member, "default_member", "member-password-12345");
            Assert.Equal(
                HttpStatusCode.Forbidden,
                (await member.PostAsync(path + "/create-preview", null)).StatusCode
            );
            Assert.Equal(
                HttpStatusCode.Forbidden,
                (
                    await member.PostAsJsonAsync(path + "/create", new { values = new { } })
                ).StatusCode
            );
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE `{child}`,`{parent}`";
            await cmd.ExecuteNonQueryAsync();
        }
    }
}

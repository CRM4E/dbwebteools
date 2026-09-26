using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DbWeb.Api;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Xunit;

namespace DbWeb.Tests;

public partial class ApiTests
{
    [Fact]
    public void PageSchemaUpgradeIsAdditiveAndIdempotent()
    {
        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        using var db = new AppDb(new DbContextOptionsBuilder<AppDb>().UseSqlite(conn).Options);
        db.Database.ExecuteSqlRaw(
            "CREATE TABLE ExistingSettings (Name TEXT); INSERT INTO ExistingSettings VALUES ('preserve me')"
        );
        PageSchema.EnsureCreated(db);
        PageSchema.EnsureCreated(db);
        db.Pages.Add(
            new PageConfiguration
            {
                ConnectionId = 1,
                Table = "customers",
                Name = "Customer",
                LinkColumn = "name",
            }
        );
        db.SaveChanges();
        Assert.Single(db.Pages);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Name FROM ExistingSettings";
        Assert.Equal("preserve me", cmd.ExecuteScalar());
    }

    [Fact]
    public async Task PagesEnforceParentScopePermissionsKeysAndConfiguration()
    {
        var cs = Environment.GetEnvironmentVariable("MARIADB_TEST_CONNECTION");
        if (string.IsNullOrEmpty(cs))
        {
            Assert.False(Environment.GetEnvironmentVariable("CI") == "true");
            return;
        }
        await using var conn = new MySqlConnection(cs);
        await conn.OpenAsync();
        var suffix = Guid.NewGuid().ToString("N");
        var parents = "page_parents_" + suffix;
        var children = "page_children_" + suffix;
        var lines = "page_lines_" + suffix;
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"CREATE TABLE `{parents}` (id BIGINT PRIMARY KEY,name VARCHAR(50),group_code VARCHAR(20)); CREATE TABLE `{children}` (id INT PRIMARY KEY,parent_id BIGINT,title VARCHAR(50),status VARCHAR(20),group_code VARCHAR(20)); CREATE TABLE `{lines}` (order_id INT,seq INT,body VARCHAR(50),PRIMARY KEY(order_id,seq)); INSERT INTO `{parents}` VALUES(9007199254740993,'First','A'),(42,'Other','B'),(43,'Empty',NULL); INSERT INTO `{children}` VALUES(1,9007199254740993,'Own first','a','A'),(2,42,'Foreign sentinel','a','B'),(3,NULL,'Orphan sentinel','a',NULL),(4,9007199254740993,'Filtered','b','A'); INSERT INTO `{lines}` VALUES(1,1,'First line'),(1,2,'Second line'),(2,1,'Foreign line');";
        await cmd.ExecuteNonQueryAsync();
        for (var i = 5; i <= 33; i++)
        {
            cmd.CommandText =
                $"INSERT INTO `{children}` VALUES({i},9007199254740993,'Own {i}','a','A')";
            await cmd.ExecuteNonQueryAsync();
        }
        try
        {
            using var factory = new Factory();
            using var admin = factory.CreateClient();
            await Login(admin);
            var b = new MySqlConnectionStringBuilder(cs);
            var added = await admin.PostAsJsonAsync(
                "/api/admin/connections",
                new ConnectionInput(
                    "Pages",
                    b.Server,
                    b.Port,
                    b.Database,
                    b.UserID,
                    b.Password,
                    false
                )
            );
            added.EnsureSuccessStatusCode();
            var connection = (await added.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("id")
                .GetInt32();
            async Task<PageDefinition> Create(
                string table,
                string name,
                string link,
                List<RelatedTab> tabs
            )
            {
                var r = await admin.PostAsJsonAsync(
                    "/api/admin/pages",
                    new PageDefinition(0, connection, table, name, link, tabs)
                );
                r.EnsureSuccessStatusCode();
                return (await r.Content.ReadFromJsonAsync<PageDefinition>())!;
            }
            var linePage = await Create(lines, "Line", "body", []);
            var childPage = await Create(
                children,
                "Order",
                "title",
                [
                    new(
                        Guid.NewGuid().ToString(),
                        "Lines",
                        lines,
                        "id",
                        "order_id",
                        ["body"],
                        linePage.Id,
                        "body"
                    ),
                ]
            );
            var ordersTab = new RelatedTab(
                Guid.NewGuid().ToString(),
                "Orders",
                children,
                "id",
                "parent_id",
                ["title", "status", "summary"],
                childPage.Id,
                "title"
            );
            var childFields = new List<LayoutField>
            {
                new(
                    "status",
                    "Status",
                    "",
                    0,
                    false,
                    false,
                    "dropdown",
                    Options: [new("a", "Active"), new("b", "Closed")]
                ),
                new(
                    "summary",
                    "Summary",
                    "",
                    1,
                    false,
                    true,
                    "formula",
                    Formula: "Concat([title], ': ', DropdownDisplay('status',[status]))"
                ),
            };
            await SaveConfiguration(
                admin,
                $"/api/admin/connections/{connection}/tables/{children}/layout",
                childFields,
                new ListView(
                    Match: "any",
                    Filters:
                    [
                        new("status", "eq", "a"),
                        new("title", "eq", "Foreign sentinel"),
                    ]
                )
            );
            var parentPage = await Create(
                parents,
                "Customer",
                "name",
                [
                    ordersTab,
                    new(
                        Guid.NewGuid().ToString(),
                        "By group",
                        children,
                        "group_code",
                        "group_code",
                        ["title"]
                    ),
                ]
            );
            string Key(object value) => Uri.EscapeDataString(JsonSerializer.Serialize(value));
            var parentKey = Key(new { id = "9007199254740993" });
            var parentUrl = $"/api/pages/{parentPage.Id}/record?key={parentKey}";
            var related = $"/api/pages/{parentPage.Id}/tabs/{ordersTab.Id}/records?key={parentKey}";
            var record = await admin.GetFromJsonAsync<JsonElement>(parentUrl);
            Assert.Equal(
                "9007199254740993",
                record.GetProperty("record").GetProperty("values").GetProperty("id").GetString()
            );
            Assert.Equal(2, record.GetProperty("page").GetProperty("tabs").GetArrayLength());
            var tab = await admin.GetFromJsonAsync<JsonElement>(related + "&size=2");
            Assert.Equal(30, tab.GetProperty("data").GetProperty("total").GetInt32());
            Assert.Equal(2, tab.GetProperty("data").GetProperty("rows").GetArrayLength());
            Assert.Equal(
                "Active",
                tab.GetProperty("data")
                    .GetProperty("rows")[0]
                    .GetProperty("displayValues")
                    .GetProperty("status")
                    .GetString()
            );
            Assert.Equal(
                "Own first: Active",
                tab.GetProperty("data")
                    .GetProperty("rows")[0]
                    .GetProperty("joinedValues")
                    .GetProperty("summary")
                    .GetString()
            );
            Assert.Equal(childPage.Id, tab.GetProperty("targetPageId").GetInt32());
            Assert.Equal(
                new[] { "title", "status", "summary" },
                tab.GetProperty("visibleColumns").EnumerateArray().Select(x => x.GetString())
            );
            tab = await admin.GetFromJsonAsync<JsonElement>(related + "&search=sentinel");
            Assert.Equal(0, tab.GetProperty("data").GetProperty("total").GetInt32());
            tab = await admin.GetFromJsonAsync<JsonElement>(
                related + "&page=2&size=25&sort=id&descending=true"
            );
            Assert.Equal(5, tab.GetProperty("data").GetProperty("rows").GetArrayLength());
            var empty = await admin.GetFromJsonAsync<JsonElement>(
                $"/api/pages/{parentPage.Id}/tabs/{parentPage.Tabs[1].Id}/records?key={Key(new { id = 43 })}"
            );
            Assert.Equal(0, empty.GetProperty("data").GetProperty("total").GetInt32());
            var nested = await admin.GetFromJsonAsync<JsonElement>(
                $"/api/pages/{childPage.Id}/tabs/{childPage.Tabs[0].Id}/records?key={Key(new { id = 1 })}"
            );
            Assert.Equal(2, nested.GetProperty("data").GetProperty("total").GetInt32());
            var composite = await admin.GetFromJsonAsync<JsonElement>(
                $"/api/pages/{linePage.Id}/record?key={Key(new { order_id = 1, seq = 2 })}"
            );
            Assert.Equal(
                "Second line",
                composite
                    .GetProperty("record")
                    .GetProperty("values")
                    .GetProperty("body")
                    .GetString()
            );
            foreach (
                var badKey in new[]
                {
                    "{}",
                    "not json",
                    JsonSerializer.Serialize(new { id = "1 OR 1=1" }),
                    JsonSerializer.Serialize(new { id = 42, extra = 1 }),
                }
            )
                Assert.Equal(
                    HttpStatusCode.BadRequest,
                    (
                        await admin.GetAsync(
                            $"/api/pages/{parentPage.Id}/record?key={Uri.EscapeDataString(badKey)}"
                        )
                    ).StatusCode
                );
            Assert.Equal(
                HttpStatusCode.NotFound,
                (
                    await admin.GetAsync(
                        $"/api/pages/{parentPage.Id}/record?key={Key(new { id = 999 })}"
                    )
                ).StatusCode
            );
            Assert.Equal(
                HttpStatusCode.BadRequest,
                (
                    await admin.GetAsync(
                        $"/api/pages/{linePage.Id}/record?key={Key(new { order_id = 1 })}"
                    )
                ).StatusCode
            );
            Assert.Equal(
                HttpStatusCode.Conflict,
                (
                    await admin.PostAsJsonAsync("/api/admin/pages", parentPage with { Id = 0 })
                ).StatusCode
            );
            foreach (
                var invalid in new[]
                {
                    parentPage with
                    {
                        LinkColumn = "missing",
                    },
                    parentPage with
                    {
                        Tabs = [ordersTab with { RelatedColumn = "missing" }],
                    },
                    parentPage with
                    {
                        Tabs = [ordersTab with { RelatedColumn = "title" }],
                    },
                    parentPage with
                    {
                        Tabs = [ordersTab with { Columns = ["bad"] }],
                    },
                    parentPage with
                    {
                        Tabs = [ordersTab with { TargetPageId = linePage.Id }],
                    },
                    parentPage with
                    {
                        Tabs = [ordersTab with { LinkColumn = "id" }],
                    },
                    parentPage with
                    {
                        Tabs = [ordersTab, ordersTab],
                    },
                }
            )
                Assert.Equal(
                    HttpStatusCode.BadRequest,
                    (
                        await admin.PutAsJsonAsync($"/api/admin/pages/{parentPage.Id}", invalid)
                    ).StatusCode
                );
            Assert.Equal(
                HttpStatusCode.Conflict,
                (await admin.DeleteAsync($"/api/admin/pages/{childPage.Id}")).StatusCode
            );
            using var anon = factory.CreateClient();
            Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync(parentUrl)).StatusCode);
            var u = await admin.PostAsJsonAsync(
                "/api/admin/users",
                new UserInput("page_member", "member-password-12345", false, true)
            );
            var uid = (await u.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("id")
                .GetInt32();
            using var member = factory.CreateClient();
            await Login(member, "page_member", "member-password-12345");
            Assert.Equal(
                HttpStatusCode.Forbidden,
                (await member.GetAsync("/api/admin/pages")).StatusCode
            );
            Assert.Equal(
                HttpStatusCode.Forbidden,
                (await member.PostAsJsonAsync("/api/admin/pages", parentPage)).StatusCode
            );
            Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync(parentUrl)).StatusCode);
            Assert.Empty(
                (await member.GetFromJsonAsync<JsonElement>("/api/pages")).EnumerateArray()
            );
            async Task Grant(string table, bool read, bool delete = false) =>
                (
                    await admin.PutAsJsonAsync(
                        "/api/admin/grants",
                        new TableGrant
                        {
                            UserId = uid,
                            ConnectionId = connection,
                            Table = table,
                            Read = read,
                            Delete = delete,
                        }
                    )
                ).EnsureSuccessStatusCode();
            await Grant(parents, true);
            record = await member.GetFromJsonAsync<JsonElement>(parentUrl);
            Assert.Empty(record.GetProperty("page").GetProperty("tabs").EnumerateArray());
            Assert.False(record.GetProperty("canUpdate").GetBoolean());
            Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync(related)).StatusCode);
            await Grant(children, true);
            tab = await member.GetFromJsonAsync<JsonElement>(related);
            Assert.Equal(30, tab.GetProperty("data").GetProperty("total").GetInt32());
            Assert.False(tab.GetProperty("canDelete").GetBoolean());
            var deleteUrl = $"/api/pages/{parentPage.Id}/tabs/{ordersTab.Id}/delete";
            async Task<HttpResponseMessage> Delete(HttpClient client, int child, string version) =>
                await client.PostAsJsonAsync(
                    deleteUrl,
                    new
                    {
                        parentKey = Uri.UnescapeDataString(parentKey),
                        mutation = new
                        {
                            values = new { },
                            key = new { id = child },
                            version,
                        },
                    }
                );
            Assert.Equal(HttpStatusCode.Forbidden, (await Delete(member, 5, "stale")).StatusCode);
            await Grant(children, true, true);
            tab = await member.GetFromJsonAsync<JsonElement>(related);
            Assert.True(tab.GetProperty("canDelete").GetBoolean());
            var victim = tab.GetProperty("data")
                .GetProperty("rows")
                .EnumerateArray()
                .Single(r => r.GetProperty("values").GetProperty("id").GetInt32() == 5);
            Assert.Equal(HttpStatusCode.NotFound, (await Delete(member, 2, "stale")).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await Delete(member, 5, "stale")).StatusCode);
            (
                await Delete(member, 5, victim.GetProperty("version").GetString()!)
            ).EnsureSuccessStatusCode();
            tab = await member.GetFromJsonAsync<JsonElement>(related);
            Assert.Equal(29, tab.GetProperty("data").GetProperty("total").GetInt32());
            await Grant(children, true);
            Assert.Equal(HttpStatusCode.Forbidden, (await Delete(member, 6, "stale")).StatusCode);
            await Grant(parents, false);
            Assert.Equal(HttpStatusCode.Forbidden, (await Delete(member, 6, "stale")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync(related)).StatusCode); // Child permission alone cannot bypass parent access.
            (
                await admin.DeleteAsync($"/api/admin/pages/{parentPage.Id}")
            ).EnsureSuccessStatusCode();
            (await admin.DeleteAsync($"/api/admin/pages/{childPage.Id}")).EnsureSuccessStatusCode();
            (await admin.DeleteAsync($"/api/admin/pages/{linePage.Id}")).EnsureSuccessStatusCode();
        }
        finally
        {
            cmd.CommandText =
                $"DROP TABLE `{lines}`;DROP TABLE `{children}`;DROP TABLE `{parents}`";
            await cmd.ExecuteNonQueryAsync();
        }
    }
}

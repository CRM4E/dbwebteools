using System.Net.Http.Json;
using System.Text.Json;
using DbWeb.Api;
using MySqlConnector;
using Xunit;

namespace DbWeb.Tests;

public partial class ApiTests
{
    [Fact]
    public async Task LookupSearchMatchesLabelsKeysAndAllowedColumnsBeforePagingAndFilters()
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
        var source = "ls_" + suffix;
        var dest = "ld_" + suffix;
        await using var cmd = c.CreateCommand();
        cmd.CommandText =
            $"CREATE TABLE `{source}`(id BIGINT PRIMARY KEY,name VARCHAR(100),email VARCHAR(100));INSERT INTO `{source}` VALUES(9007199254740993,'Żółć 100%_Team','private-match'),(42,'Other person','other-mail');CREATE TABLE `{dest}`(id INT PRIMARY KEY,person_id BIGINT,active INT);INSERT INTO `{dest}` VALUES(1,9007199254740993,1),(2,9007199254740993,0),(3,42,1),(4,NULL,1),(5,999,1)";
        await cmd.ExecuteNonQueryAsync();
        try
        {
            using var factory = new Factory();
            using var admin = factory.CreateClient();
            await Login(admin);
            var b = new MySqlConnectionStringBuilder(cs);
            var response = await admin.PostAsJsonAsync(
                "/api/admin/connections",
                new ConnectionInput(
                    "Lookup search",
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
            var lookup = new LookupConfig(source, "id", "name", ["email"]);
            LayoutField[] fields =
            [
                new("person_id", "Person", "", 0, false, false, "lookup", Lookup: lookup),
            ];
            var layoutPath = $"/api/admin/connections/{id}/tables/{dest}/layout";
            await SaveConfiguration(admin, layoutPath, fields);
            var path = $"/api/connections/{id}/tables/{dest}/records";
            async Task<JsonElement> Search(HttpClient client, string term, string more = "") =>
                await client.GetFromJsonAsync<JsonElement>(
                    path + "?search=" + Uri.EscapeDataString(term) + more
                );
            foreach (var term in new[] { "żółć", "100%_", "private-match", "9007199254740993" })
                Assert.Equal(2, (await Search(admin, term)).GetProperty("total").GetInt32());
            Assert.Equal(0, (await Search(admin, "100X_")).GetProperty("total").GetInt32());
            var paged = await Search(admin, "Team", "&size=1&page=2");
            Assert.Equal(2, paged.GetProperty("total").GetInt32());
            Assert.Equal(
                2,
                paged.GetProperty("rows")[0].GetProperty("values").GetProperty("id").GetInt32()
            );
            // Lookup selector criteria do not hide an already stored relation's friendly value.
            fields[0] = fields[0] with
            {
                Lookup = lookup with { Criteria = new ListView(Filters: [new("id", "eq", "42")]) },
            };
            await SaveConfiguration(admin, layoutPath, fields);
            Assert.Equal(2, (await Search(admin, "Team")).GetProperty("total").GetInt32());
            await SaveConfiguration(
                admin,
                layoutPath,
                fields,
                new ListView(Filters: [new("active", "eq", "1")])
            );
            Assert.Equal(1, (await Search(admin, "Team")).GetProperty("total").GetInt32());
            var added = await admin.PostAsJsonAsync(
                "/api/admin/users",
                new UserInput("lookup_search_member", "member-password-12345", false, true)
            );
            var uid = (await added.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("id")
                .GetInt32();
            async Task Grant(string table, Dictionary<string, string>? policy) =>
                (
                    await admin.PutAsJsonAsync(
                        "/api/admin/grants",
                        new TableGrant
                        {
                            UserId = uid,
                            ConnectionId = id,
                            Table = table,
                            Read = true,
                            Fields = policy,
                        }
                    )
                ).EnsureSuccessStatusCode();
            await Grant(
                dest,
                new()
                {
                    ["id"] = "read",
                    ["person_id"] = "read",
                    ["active"] = "read",
                }
            );
            using var member = factory.CreateClient();
            await Login(member, "lookup_search_member", "member-password-12345");
            Assert.Equal(0, (await Search(member, "Team")).GetProperty("total").GetInt32());
            await Grant(source, new() { ["id"] = "read", ["name"] = "read" });
            Assert.Equal(1, (await Search(member, "Team")).GetProperty("total").GetInt32());
            Assert.Equal(
                0,
                (await Search(member, "private-match")).GetProperty("total").GetInt32()
            );
            await Grant(source, new() { ["id"] = "read", ["email"] = "read" });
            Assert.Equal(0, (await Search(member, "Team")).GetProperty("total").GetInt32());
            await Grant(
                source,
                new()
                {
                    ["id"] = "read",
                    ["name"] = "read",
                    ["email"] = "read",
                }
            );
            Assert.Equal(
                1,
                (await Search(member, "private-match")).GetProperty("total").GetInt32()
            );
            await Grant(dest, new() { ["id"] = "read", ["active"] = "read" });
            Assert.Equal(0, (await Search(member, "Team")).GetProperty("total").GetInt32());
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE `{dest}`,`{source}`";
            await cmd.ExecuteNonQueryAsync();
        }
    }
}

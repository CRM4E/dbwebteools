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
    public async Task ListViewsApplyTypedFiltersSortAndPaginationWithoutLosingFields()
    {
        var cs = Environment.GetEnvironmentVariable("MARIADB_TEST_CONNECTION");
        if (string.IsNullOrEmpty(cs))
        {
            Assert.False(Environment.GetEnvironmentVariable("CI") == "true");
            return;
        }
        await using var connection = new MySqlConnection(cs);
        await connection.OpenAsync();
        var table = "views_" + Guid.NewGuid().ToString("N");
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            $"CREATE TABLE `{table}` (id INT PRIMARY KEY, title VARCHAR(100), status VARCHAR(20), amount DECIMAL(20,4), ref BIGINT, day DATE); INSERT INTO `{table}` VALUES (1,'Alpha 100%','open',10.50,9007199254740992,'2026-01-01'),(2,'Beta','open',20.50,9007199254740993,'2026-02-01'),(3,'Gamma','closed',99.25,9007199254740994,'2026-03-01'),(4,'Delta',NULL,20.50,42,NULL),(5,'','open',0,0,'2026-01-01')";
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
                    "List views",
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
            var fields = new[]
            {
                new LayoutField(
                    "title",
                    "Display title",
                    "",
                    0,
                    false,
                    false,
                    "text",
                    Required: true
                ),
            };
            async Task Save(ListView view) =>
                await SaveConfiguration(client, layoutPath, fields, view);
            async Task<JsonElement> Rows(string query = "") =>
                await client.GetFromJsonAsync<JsonElement>(path + "/records" + query);
            int[] Ids(JsonElement page) =>
                page.GetProperty("rows")
                    .EnumerateArray()
                    .Select(r => r.GetProperty("values").GetProperty("id").GetInt32())
                    .ToArray();
            var view = new ListView(
                "Open items",
                "amount",
                true,
                "all",
                [new("status", "eq", "open"), new("amount", "gte", "10")]
            );
            await Save(view);
            var settings = await client.GetFromJsonAsync<JsonElement>(path + "/settings");
            Assert.Equal(
                "Open items",
                settings.GetProperty("view").GetProperty("label").GetString()
            );
            Assert.Equal(
                "Display title",
                settings.GetProperty("fields")[0].GetProperty("label").GetString()
            );
            Assert.True(settings.GetProperty("fields")[0].GetProperty("required").GetBoolean());
            var first = await Rows("?size=1");
            Assert.Equal(2, first.GetProperty("total").GetInt32());
            Assert.Equal<int[]>([2], Ids(first));
            Assert.Equal("amount", first.GetProperty("sort").GetString());
            Assert.True(first.GetProperty("descending").GetBoolean());
            Assert.Equal<int[]>([1], Ids(await Rows("?size=1&page=2")));
            Assert.Equal<int[]>([1, 2], Ids(await Rows("?sort=id&descending=false")));
            Assert.Empty(Ids(await Rows("?search=closed"))); // Search OR clauses cannot escape the layout AND predicate.
            Assert.Equal<int[]>([1], Ids(await Rows("?search=Alpha")));
            // Presentation-only updates preserve list configuration, as do all settings readers.
            var presentation = (
                await client.GetFromJsonAsync<LayoutPresentation>(layoutPath)
            )!;
            (await client.PutAsJsonAsync(layoutPath, presentation)).EnsureSuccessStatusCode();
            Assert.Equal<int[]>([2, 1], Ids(await Rows()));
            Assert.Equal(
                HttpStatusCode.BadRequest,
                (
                    await client.PostAsJsonAsync(
                        path + "/create",
                        new { values = new { id = 7, title = " " } }
                    )
                ).StatusCode
            );
            await Save(
                view with
                {
                    Match = "any",
                    Filters = [new("status", "eq", "closed"), new("status", "isNull")],
                }
            );
            Assert.Equal<int[]>([3, 4], Ids(await Rows()));
            await Save(view with { Filters = [new("ref", "eq", "9007199254740993")] });
            Assert.Equal<int[]>([2], Ids(await Rows()));
            await Save(view with { Filters = [new("amount", "gt", "20.5000")] });
            Assert.Equal<int[]>([3], Ids(await Rows()));
            await Save(view with { Filters = [new("title", "contains", "%")] });
            Assert.Equal<int[]>([1], Ids(await Rows()));
            await Save(view with { Filters = [new("title", "eq", "' OR 1=1 --")] });
            Assert.Empty(Ids(await Rows()));
            await Save(view with { Filters = [new("title", "eq", "")] });
            Assert.Equal<int[]>([5], Ids(await Rows()));
            await Save(
                view with
                {
                    Filters = [new("day", "gte", "2026-02-01"), new("day", "lt", "2026-03-01")],
                }
            );
            Assert.Equal<int[]>([2], Ids(await Rows()));
            await Save(view with { Filters = [new("amount", "eq", "20.5")] });
            Assert.Equal<int[]>([2, 4], Ids(await Rows())); // Ties use PK ascending.
            foreach (
                var invalid in new[]
                {
                    view with
                    {
                        Sort = "bad` column",
                    },
                    view with
                    {
                        Match = "sql",
                    },
                    view with
                    {
                        Filters = [new("bad", "eq", "x")],
                    },
                    view with
                    {
                        Filters = [new("title", "injected operator", "x")],
                    },
                    view with
                    {
                        Filters = [new("amount", "gte", "not a number")],
                    },
                    view with
                    {
                        Filters = [new("day", "eq", "2026-02-31")],
                    },
                    view with
                    {
                        Filters = [new("amount", "contains", "1")],
                    },
                    view with
                    {
                        Filters = Enumerable
                            .Repeat(new ListFilter("title", "eq", "a"), 21)
                            .ToList(),
                    },
                }
            )
                Assert.Equal(
                    HttpStatusCode.BadRequest,
                    (
                        await PutObjectConfiguration(client, layoutPath, fields, invalid)
                    ).StatusCode
                );
            await Save(new ListView());
            Assert.Equal<int[]>([1, 2, 3, 4, 5], Ids(await Rows()));
            Assert.Empty(DatabaseService.Layout("[]").Fields);
            Assert.Null(DatabaseService.Layout("[]").View);
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE `{table}`";
            await cmd.ExecuteNonQueryAsync();
        }
    }
}

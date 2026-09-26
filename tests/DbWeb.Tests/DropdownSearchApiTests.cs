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
    public async Task DropdownLabelsSearchBeforePaginationAndRespectFiltersAndStoredKeys()
    {
        var cs = Environment.GetEnvironmentVariable("MARIADB_TEST_CONNECTION");
        if (string.IsNullOrEmpty(cs))
        {
            Assert.False(Environment.GetEnvironmentVariable("CI") == "true");
            return;
        }
        await using var connection = new MySqlConnection(cs);
        await connection.OpenAsync();
        var table = "dropdown_search_" + Guid.NewGuid().ToString("N");
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            $"CREATE TABLE `{table}` (id INT PRIMARY KEY, title VARCHAR(100), status VARCHAR(30) COLLATE utf8mb4_bin, category TINYTEXT, active INT); INSERT INTO `{table}` VALUES (1,'Alpha','a','x',1),(2,'Beta','b','y',1),(3,'Gamma','a','y',0),(4,'Delta','legacy',NULL,1),(5,'Epsilon',NULL,'x',1),(6,'Zeta','A',NULL,1)";
        await cmd.ExecuteNonQueryAsync();
        try
        {
            using var factory = new Factory();
            using var client = factory.CreateClient();
            await Login(client);
            var b = new MySqlConnectionStringBuilder(cs);
            var response = await client.PostAsJsonAsync(
                "/api/admin/connections",
                new ConnectionInput(
                    "Dropdown search",
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
            var path = $"/api/connections/{id}/tables/{table}/records";
            var layoutPath = $"/api/admin/connections/{id}/tables/{table}/layout";
            var fields = new List<LayoutField>
            {
                new(
                    "status",
                    "Status",
                    "",
                    0,
                    false,
                    false,
                    "dropdown",
                    Options:
                    [
                        new("a", "Awaiting review"),
                        new("b", "Review complete"),
                        new("' OR 1=1 --", "Injection sentinel"),
                    ]
                ),
                new(
                    "category",
                    "Category",
                    "",
                    1,
                    false,
                    false,
                    "dropdown",
                    Options: [new("x", "Łódź office"), new("y", "Manager's queue")]
                ),
            };
            fields.Add(
                new(
                    "status_formula",
                    "Status formula",
                    "",
                    2,
                    false,
                    true,
                    "formula",
                    Formula: "Concat('Status: ', Coalesce(DropdownDisplay('status', [status]), 'Unknown'))"
                )
            );
            var view = new ListView(
                Sort: "id",
                Descending: true,
                Filters: [new("active", "eq", "1")]
            );
            async Task Save(List<LayoutField> nextFields) =>
                await SaveConfiguration(client, layoutPath, nextFields, view);
            async Task<JsonElement> Rows(string search, string query = "") =>
                await client.GetFromJsonAsync<JsonElement>(
                    path + "?search=" + Uri.EscapeDataString(search) + query
                );
            int[] Ids(JsonElement page) =>
                page.GetProperty("rows")
                    .EnumerateArray()
                    .Select(r => r.GetProperty("values").GetProperty("id").GetInt32())
                    .ToArray();
            await Save(fields);
            var first = await Rows("rEvIeW", "&size=1");
            Assert.Equal(2, first.GetProperty("total").GetInt32());
            Assert.Equal<int[]>([2], Ids(first));
            Assert.Equal<int[]>([1], Ids(await Rows("REVIEW", "&size=1&page=2")));
            Assert.Equal<int[]>([1, 2], Ids(await Rows("review", "&sort=id&descending=false")));
            Assert.Equal(
                "b",
                first.GetProperty("rows")[0].GetProperty("values").GetProperty("status").GetString()
            );
            Assert.Equal(
                "Review complete",
                first
                    .GetProperty("rows")[0]
                    .GetProperty("displayValues")
                    .GetProperty("status")
                    .GetString()
            );
            Assert.Equal(
                "Status: Review complete",
                first
                    .GetProperty("rows")[0]
                    .GetProperty("joinedValues")
                    .GetProperty("status_formula")
                    .GetString()
            );
            Assert.False(
                first
                    .GetProperty("rows")[0]
                    .GetProperty("values")
                    .TryGetProperty("status_formula", out _)
            );
            var resolvePath = $"/api/connections/{id}/tables/{table}/joins/resolve";
            async Task<string?> Preview(string? status)
            {
                var preview = await client.PostAsJsonAsync(
                    resolvePath,
                    new { values = new { status } }
                );
                preview.EnsureSuccessStatusCode();
                return (await preview.Content.ReadFromJsonAsync<JsonElement>())
                    .GetProperty("values")
                    .GetProperty("status_formula")
                    .GetString();
            }
            Assert.Equal("Status: Awaiting review", await Preview("a"));
            Assert.Equal("Status: Unknown", await Preview(null));
            Assert.Equal("Status: Unknown", await Preview("legacy"));
            Assert.Equal("Status: Unknown", await Preview("A"));
            // Removing a referenced dropdown must be rejected, not break existing lists.
            Assert.Equal(
                HttpStatusCode.BadRequest,
                (await PutObjectConfiguration(client, layoutPath, new[] { fields[2] }, view)).StatusCode
            );
            Assert.Equal<int[]>([1], Ids(await Rows("awaiting"))); // Excludes inactive and differently cased legacy keys.
            Assert.Equal<int[]>([2], Ids(await Rows("Manager's"))); // TINYTEXT dropdown, apostrophe, and filter.
            Assert.Equal<int[]>([5, 1], Ids(await Rows("ŁÓDŹ"))); // Unicode case-insensitive labels and NULL status.
            Assert.Equal<int[]>([4], Ids(await Rows("legacy"))); // Unconfigured stored values remain searchable.
            Assert.Equal<int[]>([4], Ids(await Rows("Delta"))); // Other text columns remain searchable.
            Assert.Equal<int[]>([2], Ids(await Rows("b"))); // Stored dropdown key still searchable.
            Assert.Empty(Ids(await Rows("no such label")));
            Assert.Empty(Ids(await Rows("Injection sentinel"))); // Key is a parameter, never SQL.
            Assert.Empty(Ids(await Rows("' OR 1=1 --")));
            fields[0] = fields[0] with
            {
                Options = [new("a", "Pending approval"), new("b", "Done")],
            };
            await Save(fields);
            Assert.Equal("Status: Pending approval", await Preview("a"));
            Assert.Equal(
                "Status: Pending approval",
                (await Rows("approval"))
                    .GetProperty("rows")[0]
                    .GetProperty("joinedValues")
                    .GetProperty("status_formula")
                    .GetString()
            );
            Assert.Empty(Ids(await Rows("review"))); // No stale cached labels after layout changes.
            Assert.Equal<int[]>([1], Ids(await Rows("approval")));
            await Save([]);
            Assert.Empty(Ids(await Rows("approval")));
            Assert.Equal<int[]>([4], Ids(await Rows("legacy")));
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE `{table}`";
            await cmd.ExecuteNonQueryAsync();
        }
    }
}

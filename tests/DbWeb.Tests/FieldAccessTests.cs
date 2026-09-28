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
    public async Task FieldPoliciesProtectReadsWritesComputedValuesLookupsAndOpaqueRecordKeys()
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
        var table = "fa_" + suffix;
        var target = "ft_" + suffix;
        var child = "fc_" + suffix;
        await using var cmd = c.CreateCommand();
        cmd.CommandText =
            $"CREATE TABLE `{target}`(id INT PRIMARY KEY,name VARCHAR(100),email VARCHAR(100));INSERT INTO `{target}` VALUES(1,'Public person','hidden-mail@example.test');CREATE TABLE `{table}`(id BIGINT PRIMARY KEY AUTO_INCREMENT,name VARCHAR(100),note VARCHAR(100),secret VARCHAR(100),target_id INT,copied VARCHAR(100),internal VARCHAR(100));INSERT INTO `{table}` VALUES(9007199254740993,'Visible name','Read only note','SENSITIVE_TOKEN',1,'old copy','internal');CREATE TABLE `{child}`(id INT PRIMARY KEY AUTO_INCREMENT,parent_id BIGINT,title VARCHAR(100),secret VARCHAR(100));INSERT INTO `{child}` VALUES(1,9007199254740993,'Related title','CHILD_SECRET');";
        await cmd.ExecuteNonQueryAsync();
        try
        {
            using var factory = new Factory();
            using var admin = factory.CreateClient();
            await Login(admin);
            var b = new MySqlConnectionStringBuilder(cs);
            var added = await admin.PostAsJsonAsync(
                "/api/admin/connections",
                new ConnectionInput(
                    "Fields",
                    b.Server,
                    b.Port,
                    b.Database,
                    b.UserID,
                    b.Password,
                    false
                )
            );
            added.EnsureSuccessStatusCode();
            var id = (await added.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("id")
                .GetInt32();
            var user = await admin.PostAsJsonAsync(
                "/api/admin/users",
                new UserInput("fieldmember", "member-password-12345", false, true)
            );
            var uid = (await user.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("id")
                .GetInt32();
            var path = $"/api/connections/{id}/tables/{table}";
            List<LayoutField> fields =
            [
                new("name", "Name", "", 0, false, false, "text"),
                new("note", "Note", "", 1, false, false, "text"),
                new(
                    "secret",
                    "Secret",
                    "",
                    2,
                    false,
                    false,
                    "dropdown",
                    Options: [new("SENSITIVE_TOKEN", "Sensitive label")]
                ),
                new(
                    "internal",
                    "Internal",
                    "",
                    3,
                    true,
                    true,
                    "text",
                    CreationDefault: new("TRUSTED_DEFAULT")
                ),
                new(
                    "target_id",
                    "Person",
                    "",
                    4,
                    false,
                    false,
                    "lookup",
                    Lookup: new(target, "id", "name", ["email"], [new("email", "copied")])
                ),
                new(
                    "safe_formula",
                    "Safe",
                    "",
                    5,
                    false,
                    true,
                    "formula",
                    Formula: "Upper([name])"
                ),
                new(
                    "leak_formula",
                    "Leak",
                    "",
                    6,
                    false,
                    true,
                    "formula",
                    Formula: "Concat([secret], '!')"
                ),
                new(
                    "leak_dropdown",
                    "Dropdown leak",
                    "",
                    7,
                    false,
                    true,
                    "formula",
                    Formula: "DropdownDisplay('secret', 'SENSITIVE_TOKEN')"
                ),
                new(
                    "leak_join",
                    "Join leak",
                    "",
                    8,
                    false,
                    true,
                    "join",
                    Join: new("target_id", target, "id", "email")
                ),
            ];
            await SaveConfiguration(
                admin,
                $"/api/admin/connections/{id}/tables/{table}/layout",
                fields
            );
            fields.Add(
                new(
                    "type_formula",
                    "Record type",
                    "",
                    9,
                    false,
                    true,
                    "formula",
                    Formula: "[datatype]"
                )
            );
            await SaveConfiguration(
                admin,
                $"/api/admin/connections/{id}/tables/{table}/layout",
                fields
            );
            await SaveConfiguration(
                admin,
                $"/api/admin/connections/{id}/tables/{child}/layout",
                new[]
                {
                    new LayoutField(
                        "parent_id",
                        "Parent",
                        "",
                        0,
                        false,
                        false,
                        "lookup",
                        Lookup: new(table, "id", "name", [])
                    ),
                }
            );
            async Task Grant(string t, Dictionary<string, string>? policy, bool update = true) =>
                (
                    await admin.PutAsJsonAsync(
                        "/api/admin/grants",
                        new TableGrant
                        {
                            UserId = uid,
                            ConnectionId = id,
                            Table = t,
                            Read = true,
                            Create = true,
                            Update = update,
                            Delete = true,
                            Fields = policy,
                        }
                    )
                ).EnsureSuccessStatusCode();
            await Grant(table, null);
            await Grant(target, null);
            await Grant(child, null);
            using var member = factory.CreateClient();
            await Login(member, "fieldmember", "member-password-12345");
            Assert.Contains("SENSITIVE_TOKEN", await member.GetStringAsync(path + "/records"));
            var unrestrictedSettings = await member.GetStringAsync(path + "/settings");
            Assert.Contains("type_formula", unrestrictedSettings);
            Assert.Contains("\"name\":\"datatype\"", unrestrictedSettings);
            Assert.Contains("\"display\":\"Default\"", unrestrictedSettings);
            Dictionary<string, string> policy = new()
            {
                ["name"] = "write",
                ["note"] = "read",
                ["target_id"] = "write",
                ["copied"] = "write",
                ["safe_formula"] = "read",
                ["leak_formula"] = "read",
                ["leak_dropdown"] = "read",
                ["leak_join"] = "read",
                ["type_formula"] = "read",
            };
            await Grant(table, policy);
            await Grant(target, new() { ["id"] = "read", ["name"] = "read" });
            await Grant(child, new() { ["title"] = "write" });
            var records = await member.GetFromJsonAsync<JsonElement>(path + "/records");
            var text = records.ToString();
            foreach (
                var secret in new[]
                {
                    "SENSITIVE_TOKEN",
                    "Sensitive label",
                    "TRUSTED_DEFAULT",
                    "hidden-mail@example.test",
                    "9007199254740993",
                    "leak_formula",
                    "leak_join",
                    "leak_dropdown",
                }
            )
                Assert.DoesNotContain(secret, text);
            var row = records.GetProperty("rows")[0];
            Assert.Equal(
                "VISIBLE NAME",
                row.GetProperty("joinedValues").GetProperty("safe_formula").GetString()
            );
            Assert.Equal(
                "default",
                row.GetProperty("joinedValues").GetProperty("type_formula").GetString()
            );
            Assert.Equal(
                "default",
                row.GetProperty("values").GetProperty("datatype").GetString()
            );
            Assert.Equal(
                "Default",
                row.GetProperty("displayValues").GetProperty("datatype").GetString()
            );
            Assert.False(row.GetProperty("values").TryGetProperty("id", out _));
            Assert.True(records.GetProperty("hasPrimaryKey").GetBoolean());
            var key = new Dictionary<string, string>
            {
                { "$record", row.GetProperty("keyToken").GetString()! },
            };
            var version = row.GetProperty("version").GetString();
            Assert.StartsWith("protected:", version);
            Assert.Equal(
                HttpStatusCode.Forbidden,
                (await member.GetAsync(path + "/records?sort=secret")).StatusCode
            );
            Assert.Equal(
                0,
                (
                    await member.GetFromJsonAsync<JsonElement>(
                        path + "/records?search=SENSITIVE_TOKEN"
                    )
                )
                    .GetProperty("total")
                    .GetInt32()
            );
            Assert.Equal(
                0,
                (
                    await member.GetFromJsonAsync<JsonElement>(
                        path + "/records?search=Sensitive%20label"
                    )
                )
                    .GetProperty("total")
                    .GetInt32()
            );
            var schema = await member.GetStringAsync(path + "/schema");
            Assert.DoesNotContain("\"secret\"", schema);
            Assert.DoesNotContain("\"id\"", schema);
            var settings = await member.GetStringAsync(path + "/settings");
            Assert.DoesNotContain("SENSITIVE_TOKEN", settings);
            Assert.DoesNotContain("TRUSTED_DEFAULT", settings);
            Assert.Equal(
                HttpStatusCode.Forbidden,
                (
                    await member.PostAsJsonAsync(
                        path + "/update",
                        new
                        {
                            values = new { note = "forged" },
                            key,
                            version,
                        }
                    )
                ).StatusCode
            );
            Assert.Equal(
                HttpStatusCode.Forbidden,
                (
                    await member.PostAsJsonAsync(
                        path + "/create",
                        new { values = new { secret = "forged" } }
                    )
                ).StatusCode
            );
            (
                await member.PostAsJsonAsync(
                    path + "/update",
                    new
                    {
                        values = new { name = "Edited name" },
                        key,
                        version,
                    }
                )
            ).EnsureSuccessStatusCode();
            Assert.Equal(
                HttpStatusCode.Conflict,
                (
                    await member.PostAsJsonAsync(
                        path + "/update",
                        new
                        {
                            values = new { name = "Stale" },
                            key,
                            version,
                        }
                    )
                ).StatusCode
            );
            cmd.CommandText = $"SELECT CONCAT(name,'|',note,'|',secret) FROM `{table}`";
            Assert.Equal(
                "Edited name|Read only note|SENSITIVE_TOKEN",
                await cmd.ExecuteScalarAsync()
            );
            var preview = await member.PostAsync(path + "/create-preview", null);
            preview.EnsureSuccessStatusCode();
            Assert.DoesNotContain("TRUSTED_DEFAULT", await preview.Content.ReadAsStringAsync());
            (
                await member.PostAsJsonAsync(
                    path + "/create",
                    new { values = new { name = "Created" } }
                )
            ).EnsureSuccessStatusCode();
            cmd.CommandText = $"SELECT internal FROM `{table}` WHERE name='Created'";
            Assert.Equal("TRUSTED_DEFAULT", await cmd.ExecuteScalarAsync());
            var lookup = await member.GetStringAsync(path + "/lookups/target_id");
            Assert.Contains("Public person", lookup);
            Assert.DoesNotContain("email", lookup);
            Assert.Equal(
                0,
                (
                    await member.GetFromJsonAsync<JsonElement>(
                        path + "/lookups/target_id?search=hidden-mail"
                    )
                )
                    .GetProperty("total")
                    .GetInt32()
            );
            Assert.Equal(
                HttpStatusCode.Forbidden,
                (
                    await member.PostAsJsonAsync(path + "/lookups/target_id/copy", new { key = 1 })
                ).StatusCode
            );
            Assert.Equal(
                HttpStatusCode.Forbidden,
                (
                    await member.PostAsJsonAsync(
                        path + "/joins/resolve",
                        new { values = new { secret = "guess" } }
                    )
                ).StatusCode
            );
            var calculation = await member.PostAsJsonAsync(
                path + "/joins/resolve",
                new { values = new { name = "Preview", target_id = 1 } }
            );
            calculation.EnsureSuccessStatusCode();
            var calculationText = await calculation.Content.ReadAsStringAsync();
            Assert.Contains("PREVIEW", calculationText);
            Assert.DoesNotContain("leak", calculationText);
            // Related pages retain opaque keys and independently redact the child table.
            var tab = new RelatedTab(
                Guid.NewGuid().ToString(),
                "Children",
                child,
                "id",
                "parent_id",
                ["title", "secret"],
                LookupField: "parent_id"
            );
            var createdPage = await admin.PostAsJsonAsync(
                "/api/admin/pages",
                new PageDefinition(0, id, table, "Field page", "name", [tab])
            );
            createdPage.EnsureSuccessStatusCode();
            var page = (await createdPage.Content.ReadFromJsonAsync<PageDefinition>())!;
            var pagePath = $"/api/pages/{page.Id}";
            var encoded = Uri.EscapeDataString(JsonSerializer.Serialize(key));
            var pageRecord = await member.GetStringAsync(pagePath + "/record?key=" + encoded);
            Assert.DoesNotContain("SENSITIVE_TOKEN", pageRecord);
            Assert.DoesNotContain("9007199254740993", pageRecord);
            var related = await member.GetFromJsonAsync<JsonElement>(
                pagePath + $"/tabs/{tab.Id}/records?key=" + encoded
            );
            Assert.DoesNotContain("CHILD_SECRET", related.ToString());
            Assert.DoesNotContain("9007199254740993", related.ToString());
            var relatedRow = related.GetProperty("data").GetProperty("rows")[0];
            var mutation = new
            {
                values = new { title = "Edited related" },
                key = new Dictionary<string, string>
                {
                    { "$record", relatedRow.GetProperty("keyToken").GetString()! },
                },
                version = relatedRow.GetProperty("version").GetString(),
            };
            (
                await member.PostAsJsonAsync(
                    pagePath + $"/tabs/{tab.Id}/update",
                    new { parentKey = JsonSerializer.Serialize(key), mutation }
                )
            ).EnsureSuccessStatusCode();
            // Changing grants applies immediately; old clients cannot erase field restrictions by omitting Fields.
            await Grant(table, null, false);
            Assert.DoesNotContain(
                "SENSITIVE_TOKEN",
                await member.GetStringAsync(path + "/records")
            );
            Assert.Equal(
                HttpStatusCode.Forbidden,
                (
                    await member.PostAsJsonAsync(
                        path + "/update",
                        new
                        {
                            values = new { name = "Denied" },
                            key,
                            version,
                        }
                    )
                ).StatusCode
            );
            await Grant(table, policy, true);
            cmd.CommandText =
                $"ALTER TABLE `{table}` ADD new_secret VARCHAR(100) DEFAULT 'NEW_SECRET'";
            await cmd.ExecuteNonQueryAsync();
            Assert.DoesNotContain("NEW_SECRET", await member.GetStringAsync(path + "/records"));
            // Context-bound tokens reject reuse for another table or a tampered key.
            Assert.Equal(
                HttpStatusCode.BadRequest,
                (
                    await member.PostAsJsonAsync(
                        $"/api/connections/{id}/tables/{child}/delete",
                        new
                        {
                            values = new { },
                            key,
                            version = relatedRow.GetProperty("version").GetString(),
                        }
                    )
                ).StatusCode
            );
            Assert.Equal(
                HttpStatusCode.BadRequest,
                (
                    await member.GetAsync(
                        pagePath
                            + "/record?key="
                            + Uri.EscapeDataString("{\"$record\":\"tampered\"}")
                    )
                ).StatusCode
            );
            var adminRecords = await admin.GetStringAsync(path + "/records");
            Assert.Contains("SENSITIVE_TOKEN", adminRecords);
            Assert.Contains("NEW_SECRET", adminRecords);
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE `{child}`,`{table}`,`{target}`";
            await cmd.ExecuteNonQueryAsync();
        }
    }
}

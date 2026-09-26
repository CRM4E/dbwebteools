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
    public async Task RelatedCreationUsesCurrentLayoutLocksParentAndCopiesAuthoritatively()
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
        var parent = "rc_parent_" + suffix;
        var child = "rc_child_" + suffix;
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"CREATE TABLE `{parent}` (id BIGINT PRIMARY KEY,code BIGINT NOT NULL UNIQUE,name VARCHAR(100),price DECIMAL(24,4)); CREATE TABLE `{child}` (id INT AUTO_INCREMENT PRIMARY KEY,parent_id BIGINT,title VARCHAR(100),copied_name VARCHAR(100),copied_price DECIMAL(24,4),qty INT); INSERT INTO `{parent}` VALUES(9007199254740993,101,'Alice',12.3456),(42,102,'Bob',40),(43,103,'No key',50);";
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
                    "Related create",
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
            var field = new LayoutField(
                "parent_id",
                "Parent",
                "",
                0,
                false,
                false,
                "lookup",
                new LookupConfig(
                    parent,
                    "id",
                    "name",
                    [],
                    [new("name", "copied_name"), new("price", "copied_price")]
                ),
                Required: true
            );
            List<LayoutField> fields =
            [
                field,
                new("title", "Title", "", 1, false, false, "text", Required: true),
                new("copied_name", "Copied name", "", 2, false, false, "text", Required: true),
                new(
                    "total",
                    "Total",
                    "",
                    5,
                    false,
                    true,
                    "formula",
                    Formula: "[qty] * [copied_price]"
                ),
            ];
            var layoutPath = $"/api/admin/connections/{connection}/tables/{child}/layout";
            await SaveConfiguration(admin, layoutPath, fields);
            var tab = new RelatedTab(
                Guid.NewGuid().ToString(),
                "Children",
                child,
                "tampered",
                "tampered",
                ["title", "copied_name", "total"],
                LookupField: "parent_id"
            );
            var result = await admin.PostAsJsonAsync(
                "/api/admin/pages",
                new PageDefinition(0, connection, parent, "Parent", "name", [tab])
            );
            result.EnsureSuccessStatusCode();
            var page = (await result.Content.ReadFromJsonAsync<PageDefinition>())!;
            Assert.Equal("id", page.Tabs[0].ParentColumn);
            Assert.Equal("parent_id", page.Tabs[0].RelatedColumn);
            var root = $"/api/pages/{page.Id}/tabs/{tab.Id}";
            var key = "{\"id\":\"9007199254740993\"}";
            async Task<JsonElement> Preview(string selectedKey)
            {
                var r = await admin.PostAsJsonAsync(
                    root + "/create-preview",
                    new RelatedCreateInput(selectedKey)
                );
                r.EnsureSuccessStatusCode();
                return await r.Content.ReadFromJsonAsync<JsonElement>();
            }
            var preview = await Preview(key);
            Assert.Equal(
                "9007199254740993",
                preview.GetProperty("values").GetProperty("parent_id").GetString()
            );
            Assert.Equal(
                "Alice",
                preview.GetProperty("values").GetProperty("copied_name").GetString()
            );
            Assert.Contains(
                preview.GetProperty("columns").EnumerateArray(),
                c => c.GetProperty("name").GetString() == "total"
            );
            Assert.Equal(3, preview.GetProperty("lockedFields").GetArrayLength());
            Dictionary<string, JsonElement> Values(string title) =>
                new()
                {
                    ["title"] = JsonSerializer.SerializeToElement(title),
                    ["parent_id"] = JsonSerializer.SerializeToElement(42),
                    ["copied_name"] = JsonSerializer.SerializeToElement("forged"),
                    ["copied_price"] = JsonSerializer.SerializeToElement(999),
                    ["qty"] = JsonSerializer.SerializeToElement(2),
                };
            cmd.CommandText =
                $"UPDATE `{parent}` SET name='Alice refreshed',price=14.5678 WHERE id=9007199254740993";
            await cmd.ExecuteNonQueryAsync();
            Assert.Equal(
                HttpStatusCode.NoContent,
                (
                    await admin.PostAsJsonAsync(
                        root + "/create",
                        new RelatedCreateInput(key, Values("Created child"))
                    )
                ).StatusCode
            );
            cmd.CommandText =
                $"SELECT CONCAT(parent_id,'|',copied_name,'|',copied_price) FROM `{child}`";
            Assert.Equal(
                "9007199254740993|Alice refreshed|14.5678",
                await cmd.ExecuteScalarAsync()
            );
            Assert.Equal(
                HttpStatusCode.BadRequest,
                (
                    await admin.PostAsJsonAsync(
                        root + "/create",
                        new RelatedCreateInput(key, Values(" "))
                    )
                ).StatusCode
            );
            var computed = Values("No computed writes");
            computed["total"] = JsonSerializer.SerializeToElement(10);
            Assert.Equal(
                HttpStatusCode.BadRequest,
                (
                    await admin.PostAsJsonAsync(
                        root + "/create",
                        new RelatedCreateInput(key, computed)
                    )
                ).StatusCode
            );
            Assert.Equal(
                HttpStatusCode.NotFound,
                (
                    await admin.PostAsJsonAsync(
                        root + "/create",
                        new RelatedCreateInput("{\"id\":999}", Values("Missing parent"))
                    )
                ).StatusCode
            );
            var listed = await admin.GetFromJsonAsync<JsonElement>(
                root + "/records?key=" + Uri.EscapeDataString(key)
            );
            Assert.True(listed.GetProperty("canCreate").GetBoolean());
            Assert.Equal(1, listed.GetProperty("data").GetProperty("total").GetInt32());
            Assert.True(listed.GetProperty("canUpdate").GetBoolean());
            var original = listed.GetProperty("data").GetProperty("rows")[0];
            var childKey = new Dictionary<string, JsonElement>
            {
                ["id"] = original.GetProperty("values").GetProperty("id").Clone(),
            };
            var mutation = new RowMutation(
                new Dictionary<string, JsonElement>
                {
                    ["title"] = JsonSerializer.SerializeToElement("Edited child"),
                },
                childKey,
                original.GetProperty("version").GetString()
            );
            Assert.Equal(
                HttpStatusCode.NotFound,
                (
                    await admin.PostAsJsonAsync(
                        root + "/update",
                        new RelatedUpdateInput("{\"id\":42}", mutation)
                    )
                ).StatusCode
            );
            var locked = mutation with
            {
                Values = new Dictionary<string, JsonElement>
                {
                    ["parent_id"] = JsonSerializer.SerializeToElement(42),
                },
            };
            Assert.Equal(
                HttpStatusCode.BadRequest,
                (
                    await admin.PostAsJsonAsync(
                        root + "/update",
                        new RelatedUpdateInput(key, locked)
                    )
                ).StatusCode
            );
            Assert.Equal(
                HttpStatusCode.NoContent,
                (
                    await admin.PostAsJsonAsync(
                        root + "/update",
                        new RelatedUpdateInput(key, mutation)
                    )
                ).StatusCode
            );
            Assert.Equal(
                HttpStatusCode.Conflict,
                (
                    await admin.PostAsJsonAsync(
                        root + "/update",
                        new RelatedUpdateInput(key, mutation)
                    )
                ).StatusCode
            );
            cmd.CommandText = $"SELECT title FROM `{child}` LIMIT 1";
            Assert.Equal("Edited child", await cmd.ExecuteScalarAsync());
            var validatePath =
                $"/api/admin/connections/{connection}/tables/{child}/formulas/validate";
            (
                await admin.PostAsJsonAsync(
                    validatePath,
                    new FormulaValidationInput("[qty] * [copied_price]", fields)
                )
            ).EnsureSuccessStatusCode();
            foreach (var invalid in new[] { "[missing] + 1", "[qty] +", "Abs(1,2)", "Unknown(1)" })
                Assert.Equal(
                    HttpStatusCode.BadRequest,
                    (
                        await admin.PostAsJsonAsync(
                            validatePath,
                            new FormulaValidationInput(invalid, fields)
                        )
                    ).StatusCode
                );
            // A changed layout key is resolved live rather than using stale stored mapping columns.
            field = field with
            {
                Lookup = field.Lookup! with { KeyColumn = "code" },
            };
            fields[0] = field;
            await SaveConfiguration(admin, layoutPath, fields);
            preview = await Preview(key);
            Assert.Equal("101", preview.GetProperty("values").GetProperty("parent_id").ToString());
            Assert.Equal(
                HttpStatusCode.NoContent,
                (
                    await admin.PostAsJsonAsync(
                        root + "/create",
                        new RelatedCreateInput(key, Values("New mapping"))
                    )
                ).StatusCode
            );
            cmd.CommandText = $"SELECT parent_id FROM `{child}` WHERE title='New mapping'";
            Assert.Equal(101L, await cmd.ExecuteScalarAsync());
            cmd.CommandText =
                $"ALTER TABLE `{parent}` MODIFY code BIGINT NULL; UPDATE `{parent}` SET code=NULL WHERE id=43";
            await cmd.ExecuteNonQueryAsync();
            Assert.Equal(
                HttpStatusCode.BadRequest,
                (
                    await admin.PostAsJsonAsync(
                        root + "/create",
                        new RelatedCreateInput("{\"id\":43}", Values("Null parent key"))
                    )
                ).StatusCode
            );
            Assert.Equal(
                HttpStatusCode.BadRequest,
                (
                    await admin.GetAsync(
                        root + "/records?key=" + Uri.EscapeDataString("{\"id\":43}")
                    )
                ).StatusCode
            );
            cmd.CommandText =
                $"UPDATE `{parent}` SET code=103 WHERE id=43; ALTER TABLE `{parent}` MODIFY code BIGINT NOT NULL";
            await cmd.ExecuteNonQueryAsync();
            // Permissions checked independently on preview and write, including revocation after opening the form.
            var u = await admin.PostAsJsonAsync(
                "/api/admin/users",
                new UserInput("related_member", "member-password-12345", false, true)
            );
            var uid = (await u.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("id")
                .GetInt32();
            using var member = factory.CreateClient();
            await Login(member, "related_member", "member-password-12345");
            async Task Grant(string table, bool read, bool create = false) =>
                (
                    await admin.PutAsJsonAsync(
                        "/api/admin/grants",
                        new TableGrant
                        {
                            UserId = uid,
                            ConnectionId = connection,
                            Table = table,
                            Read = read,
                            Create = create,
                        }
                    )
                ).EnsureSuccessStatusCode();
            await Grant(parent, true);
            await Grant(child, true);
            Assert.False(
                (
                    await member.GetFromJsonAsync<JsonElement>(
                        root + "/records?key=" + Uri.EscapeDataString(key)
                    )
                )
                    .GetProperty("canCreate")
                    .GetBoolean()
            );
            Assert.Equal(
                HttpStatusCode.Forbidden,
                (
                    await member.PostAsJsonAsync(
                        root + "/create-preview",
                        new RelatedCreateInput(key)
                    )
                ).StatusCode
            );
            Assert.Equal(
                HttpStatusCode.Forbidden,
                (
                    await member.PostAsJsonAsync(
                        root + "/create",
                        new RelatedCreateInput(key, Values("Denied"))
                    )
                ).StatusCode
            );
            Assert.Equal(
                HttpStatusCode.Forbidden,
                (
                    await member.PostAsJsonAsync(
                        root + "/update",
                        new RelatedUpdateInput(key, mutation)
                    )
                ).StatusCode
            );
            Assert.False(
                (
                    await member.GetFromJsonAsync<JsonElement>(
                        root + "/records?key=" + Uri.EscapeDataString(key)
                    )
                )
                    .GetProperty("canUpdate")
                    .GetBoolean()
            );
            Assert.Equal(
                HttpStatusCode.Forbidden,
                (
                    await member.PostAsJsonAsync(
                        validatePath,
                        new FormulaValidationInput("1+2", fields)
                    )
                ).StatusCode
            );
            await Grant(child, true, true);
            (
                await member.PostAsJsonAsync(root + "/create-preview", new RelatedCreateInput(key))
            ).EnsureSuccessStatusCode();
            await Grant(parent, false);
            Assert.Equal(
                HttpStatusCode.Forbidden,
                (
                    await member.PostAsJsonAsync(
                        root + "/create",
                        new RelatedCreateInput(key, Values("Revoked"))
                    )
                ).StatusCode
            );
            // Removing the layout relation fails closed for lists and creates.
            fields.RemoveAt(0);
            await SaveConfiguration(admin, layoutPath, fields);
            Assert.Equal(
                HttpStatusCode.BadRequest,
                (
                    await admin.GetAsync(root + "/records?key=" + Uri.EscapeDataString(key))
                ).StatusCode
            );
            Assert.Equal(
                HttpStatusCode.BadRequest,
                (
                    await admin.PostAsJsonAsync(
                        root + "/create",
                        new RelatedCreateInput(key, Values("Removed lookup"))
                    )
                ).StatusCode
            );
            cmd.CommandText = $"SELECT COUNT(*) FROM `{child}`";
            Assert.Equal(2L, await cmd.ExecuteScalarAsync());
            // Editable parent copies are not locked in related creation and preserve overrides.
            fields.Insert(
                0,
                field with
                {
                    Lookup = field.Lookup! with
                    {
                        KeyColumn = "id",
                        CopyMappings =
                        [
                            new("name", "copied_name", true),
                            new("price", "copied_price"),
                        ],
                    },
                }
            );
            await SaveConfiguration(admin, layoutPath, fields);
            var editablePreview = await Preview(key);
            Assert.DoesNotContain(
                editablePreview.GetProperty("lockedFields").EnumerateArray(),
                x => x.GetString() == "copied_name"
            );
            Assert.Contains(
                editablePreview.GetProperty("lockedFields").EnumerateArray(),
                x => x.GetString() == "parent_id"
            );
            var editableValues = Values("Editable parent copy");
            editableValues["copied_name"] = JsonSerializer.SerializeToElement(
                "Override from related form"
            );
            (
                await admin.PostAsJsonAsync(
                    root + "/create",
                    new RelatedCreateInput(key, editableValues)
                )
            ).EnsureSuccessStatusCode();
            cmd.CommandText =
                $"SELECT copied_name FROM `{child}` WHERE title='Editable parent copy'";
            Assert.Equal("Override from related form", await cmd.ExecuteScalarAsync());
            (await admin.DeleteAsync($"/api/admin/pages/{page.Id}")).EnsureSuccessStatusCode();
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE `{child}`;DROP TABLE `{parent}`";
            await cmd.ExecuteNonQueryAsync();
        }
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using DbWeb.Api;
using MySqlConnector;
using Xunit;

namespace DbWeb.Tests;

public partial class ApiTests
{
    [Fact]
    public async Task FieldSetRulesPersistAndRunForCreatesAndPartialUpdates()
    {
        var cs = Environment.GetEnvironmentVariable("MARIADB_TEST_CONNECTION");
        if (string.IsNullOrEmpty(cs))
        {
            Assert.False(Environment.GetEnvironmentVariable("CI") == "true");
            return;
        }
        await using var connection = new MySqlConnection(cs);
        await connection.OpenAsync();
        var table = "set_rules_" + Guid.NewGuid().ToString("N");
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE TABLE `{table}` (id INT AUTO_INCREMENT PRIMARY KEY, qty INT, price DECIMAL(24,4), status VARCHAR(100), total DECIMAL(24,4), generated_total DECIMAL(24,4) AS (qty * price) STORED)";
        await command.ExecuteNonQueryAsync();
        try
        {
            using var factory = new Factory();
            using var admin = factory.CreateClient();
            await Login(admin);
            var builder = new MySqlConnectionStringBuilder(cs);
            var response = await admin.PostAsJsonAsync(
                "/api/admin/connections",
                new ConnectionInput("Set rules", builder.Server, builder.Port, builder.Database, builder.UserID, builder.Password, false)
            );
            response.EnsureSuccessStatusCode();
            var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
            var objectPath = $"/api/admin/connections/{id}/tables/{table}/object";
            var apiPath = $"/api/connections/{id}/tables/{table}";
            var definition = (await admin.GetFromJsonAsync<ObjectDefinition>(objectPath))! with
            {
                FieldSetRules =
                [
                    new("status", "[qty] > 1", "Concat('qty-', [qty])"),
                    new("total", "[status] = Concat('qty-', [qty])", "[qty] * [price]"),
                ],
            };
            (await admin.PutAsJsonAsync(objectPath, definition)).EnsureSuccessStatusCode();
            var saved = (await admin.GetFromJsonAsync<ObjectDefinition>(objectPath))!;
            Assert.Equal(2, saved.FieldSetRules!.Count);
            var legacySave = (await admin.GetFromJsonAsync<JsonObject>(objectPath))!;
            Assert.True(legacySave.Remove("fieldSetRules"));
            (await admin.PutAsJsonAsync(objectPath, legacySave)).EnsureSuccessStatusCode();
            Assert.Equal(
                2,
                (await admin.GetFromJsonAsync<ObjectDefinition>(objectPath))!.FieldSetRules!.Count
            );

            (await admin.PostAsJsonAsync(apiPath + "/create", new { values = new { qty = 3, price = "4.2500", status = "manual" } })).EnsureSuccessStatusCode();
            command.CommandText = $"SELECT CONCAT(status,'|',total) FROM `{table}` WHERE id=1";
            Assert.Equal("qty-3|12.7500", await command.ExecuteScalarAsync());

            var row = (await admin.GetFromJsonAsync<JsonElement>(apiPath + "/records")).GetProperty("rows")[0];
            (await admin.PostAsJsonAsync(apiPath + "/update", new
            {
                values = new { price = "5.0000" },
                key = new { id = 1 },
                version = row.GetProperty("version").GetString(),
            })).EnsureSuccessStatusCode();
            command.CommandText = $"SELECT CONCAT(status,'|',total) FROM `{table}` WHERE id=1";
            Assert.Equal("qty-3|15.0000", await command.ExecuteScalarAsync());

            var invalid = definition with { FieldSetRules = [new("id", "true", "2")] };
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync(objectPath, invalid)).StatusCode);
            var generatedDependency = definition with
            {
                FieldSetRules = [new("total", "true", "[generated_total]")],
            };
            Assert.Equal(
                HttpStatusCode.BadRequest,
                (await admin.PutAsJsonAsync(objectPath, generatedDependency)).StatusCode
            );
            var lookupTarget = definition with
            {
                Fields = definition.Fields.Select(field =>
                    field.Name == "qty"
                        ? field with
                        {
                            Widget = "lookup",
                            Lookup = new(table, "id", "status", ["status"]),
                        }
                        : field
                ).ToList(),
                FieldSetRules = [new("qty", "true", "1")],
            };
            Assert.Equal(
                HttpStatusCode.BadRequest,
                (await admin.PutAsJsonAsync(objectPath, lookupTarget)).StatusCode
            );
            var runtimeInvalid = definition with { FieldSetRules = [new("status", "1", "'bad'")] };
            (await admin.PutAsJsonAsync(objectPath, runtimeInvalid)).EnsureSuccessStatusCode();
            Assert.Equal(
                HttpStatusCode.BadRequest,
                (await admin.PostAsJsonAsync(apiPath + "/create", new { values = new { qty = 1 } })).StatusCode
            );
            command.CommandText = $"SELECT COUNT(*) FROM `{table}`";
            Assert.Equal(1L, Convert.ToInt64(await command.ExecuteScalarAsync()));

            var deletionDefinition = definition with
            {
                FieldSetRules =
                [
                    .. definition.FieldSetRules!,
                    new("status", "[total] > 0", "'uses-total'"),
                ],
            };
            (await admin.PutAsJsonAsync(objectPath, deletionDefinition)).EnsureSuccessStatusCode();
            (await admin.DeleteAsync(
                $"/api/admin/connections/{id}/tables/{table}/fields/total"
            )).EnsureSuccessStatusCode();
            var afterDelete = (await admin.GetFromJsonAsync<ObjectDefinition>(objectPath))!;
            Assert.Collection(
                afterDelete.FieldSetRules!,
                rule => Assert.Equal("status", rule.Field)
            );
        }
        finally
        {
            command.CommandText = $"DROP TABLE IF EXISTS `{table}`";
            await command.ExecuteNonQueryAsync();
        }
    }
}

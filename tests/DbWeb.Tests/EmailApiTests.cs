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
    public async Task EmailFieldsValidateCreateUpdateAndConfiguration()
    {
        var cs = Environment.GetEnvironmentVariable("MARIADB_TEST_CONNECTION");
        if (string.IsNullOrEmpty(cs))
        {
            Assert.False(Environment.GetEnvironmentVariable("CI") == "true");
            return;
        }
        await using var connection = new MySqlConnection(cs);
        await connection.OpenAsync();
        var table = "email_" + Guid.NewGuid().ToString("N");
        var textTable = "email_text_" + Guid.NewGuid().ToString("N");
        var shortTable = "email_short_" + Guid.NewGuid().ToString("N");
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            $"CREATE TABLE `{table}` (id INT AUTO_INCREMENT PRIMARY KEY, email VARCHAR(255) NULL);"
            + $"CREATE TABLE `{textTable}` (id INT AUTO_INCREMENT PRIMARY KEY, email TEXT NULL);"
            + $"CREATE TABLE `{shortTable}` (id INT AUTO_INCREMENT PRIMARY KEY, email VARCHAR(100) NULL);";
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
                    "Email test",
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
            var emailField = new LayoutField(
                "email",
                "Email address",
                "",
                0,
                false,
                false,
                "email"
            );
            var layoutPath = $"/api/admin/connections/{id}/tables/{table}/layout";
            await SaveConfiguration(client, layoutPath, new[] { emailField });
            Assert.Equal(
                HttpStatusCode.BadRequest,
                (
                    await PutObjectConfiguration(
                        client,
                        $"/api/admin/connections/{id}/tables/{textTable}/layout",
                        new[] { emailField }
                    )
                ).StatusCode
            );
            Assert.Equal(
                HttpStatusCode.BadRequest,
                (
                    await PutObjectConfiguration(
                        client,
                        $"/api/admin/connections/{id}/tables/{shortTable}/layout",
                        new[] { emailField }
                    )
                ).StatusCode
            );

            var path = $"/api/connections/{id}/tables/{table}";
            foreach (var value in new object[] { "not-an-email", " person@example.com ", 12 })
            {
                var bad = await client.PostAsJsonAsync(
                    path + "/create",
                    new { values = new Dictionary<string, object> { ["email"] = value } }
                );
                Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
                Assert.Contains("valid email address", await bad.Content.ReadAsStringAsync());
            }
            (await client.PostAsJsonAsync(path + "/create", new { values = new { email = "person@example.com" } })).EnsureSuccessStatusCode();
            (await client.PostAsJsonAsync(path + "/create", new { values = new { email = "" } })).EnsureSuccessStatusCode();

            var rows = (await client.GetFromJsonAsync<JsonElement>(path + "/records")).GetProperty("rows");
            var row = rows[0];
            var badUpdate = await client.PostAsJsonAsync(
                path + "/update",
                new
                {
                    values = new { email = "broken" },
                    key = new { id = row.GetProperty("values").GetProperty("id").GetInt32() },
                    version = row.GetProperty("version").GetString(),
                }
            );
            Assert.Equal(HttpStatusCode.BadRequest, badUpdate.StatusCode);
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS `{table}`; DROP TABLE IF EXISTS `{textTable}`; DROP TABLE IF EXISTS `{shortTable}`";
            await cmd.ExecuteNonQueryAsync();
        }
    }
}

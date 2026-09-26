using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DbWeb.Api;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using Xunit;

namespace DbWeb.Tests;

public partial class ApiTests
{
    [Fact]
    public async Task SumupsBackfillApplyDeltasLockParentsAndRebuildSafely()
    {
        var cs = Environment.GetEnvironmentVariable("MARIADB_TEST_CONNECTION");
        if (string.IsNullOrEmpty(cs))
        {
            Assert.False(Environment.GetEnvironmentVariable("CI") == "true");
            return;
        }
        await using var conn = new MySqlConnection(cs);
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        var suffix = Guid.NewGuid().ToString("N");
        var parent = "su_parent_" + suffix;
        var child = "su_child_" + suffix;
        cmd.CommandText =
            $"CREATE TABLE `{parent}` (id BIGINT PRIMARY KEY,name VARCHAR(100),total DECIMAL(18,4) DEFAULT 99,item_count INT DEFAULT 99,non_null_count INT); CREATE TABLE `{child}` (id INT AUTO_INCREMENT PRIMARY KEY,parent_id BIGINT NULL,price DECIMAL(12,4),qty INT,title VARCHAR(100),INDEX(parent_id)); INSERT INTO `{parent}` VALUES (9007199254740993,'First',99,99,99),(42,'Second',99,99,99); INSERT INTO `{child}` (parent_id,price,qty,title) VALUES (9007199254740993,10.25,2,'Existing'),(9007199254740993,NULL,1,'Null'),(42,3,1,'Other'),(NULL,100,1,'Orphan');";
        await cmd.ExecuteNonQueryAsync();
        try
        {
            using var factory = new Factory();
            using var admin = factory.CreateClient();
            await Login(admin);
            var b = new MySqlConnectionStringBuilder(cs);
            var create = await admin.PostAsJsonAsync(
                "/api/admin/connections",
                new ConnectionInput(
                    "Sumup",
                    b.Server,
                    b.Port,
                    b.Database,
                    b.UserID,
                    b.Password,
                    false
                )
            );
            create.EnsureSuccessStatusCode();
            var id = (await create.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("id")
                .GetInt32();
            var childLayout = $"/api/admin/connections/{id}/tables/{child}/layout";
            var parentLayout = $"/api/admin/connections/{id}/tables/{parent}/layout";
            var childApi = $"/api/connections/{id}/tables/{child}";
            var parentApi = $"/api/connections/{id}/tables/{parent}";
            List<LayoutField> childFields =
            [
                new(
                    "parent_id",
                    "Parent",
                    "",
                    0,
                    false,
                    false,
                    "lookup",
                    new(parent, "id", "name", [])
                ),
                new(
                    "line_total",
                    "Line total",
                    "",
                    5,
                    false,
                    true,
                    "formula",
                    Formula: "[price] * [qty]"
                ),
            ];
            await SaveConfiguration(admin, childLayout, childFields);
            List<LayoutField> fields =
            [
                new(
                    "total",
                    "Total",
                    "",
                    2,
                    false,
                    true,
                    "sumup",
                    Sumup: new("sum", child, "parent_id", "line_total")
                ),
                new(
                    "item_count",
                    "Items",
                    "",
                    3,
                    false,
                    true,
                    "sumup",
                    Sumup: new("count", child, "parent_id")
                ),
                new(
                    "non_null_count",
                    "Priced items",
                    "",
                    4,
                    false,
                    true,
                    "sumup",
                    Sumup: new("count", child, "parent_id", "price")
                ),
            ];
            async Task AssertTotals(long key, decimal total, int count, int nonNull)
            {
                cmd.CommandText =
                    $"SELECT total,item_count,non_null_count FROM `{parent}` WHERE id={key}";
                await using var r = await cmd.ExecuteReaderAsync();
                Assert.True(await r.ReadAsync());
                Assert.Equal(total, r.GetDecimal(0));
                Assert.Equal(count, r.GetInt32(1));
                Assert.Equal(nonNull, r.GetInt32(2));
            }
            await SaveConfiguration(admin, parentLayout, fields);
            await AssertTotals(9007199254740993, 20.5m, 2, 1);
            await AssertTotals(42, 3, 1, 1);
            async Task<JsonElement> Row(string tableApi, int key)
            {
                var page = await admin.GetFromJsonAsync<JsonElement>(
                    tableApi + "/records?size=100"
                );
                return page.GetProperty("rows")
                    .EnumerateArray()
                    .Single(r =>
                        r.GetProperty("values").GetProperty("id").ToString() == key.ToString()
                    );
            }
            async Task<HttpResponseMessage> Update(int key, object values)
            {
                var row = await Row(childApi, key);
                return await admin.PostAsJsonAsync(
                    childApi + "/update",
                    new
                    {
                        key = new
                        {
                            id = key
                        },
                        values,
                        version = row.GetProperty("version").GetString(),
                    }
                );
            }
            (await Update(1, new
            {
                qty = 3
            })).EnsureSuccessStatusCode();
            await AssertTotals(9007199254740993, 30.75m, 2, 1);
            (await Update(1, new
            {
                parent_id = 42
            })).EnsureSuccessStatusCode();
            await AssertTotals(9007199254740993, 0, 1, 0);
            await AssertTotals(42, 33.75m, 2, 2);
            (await Update(2, new
            {
                price = 2m,
                qty = 4
            })).EnsureSuccessStatusCode();
            await AssertTotals(9007199254740993, 8, 1, 1);
            (await Update(1, new
            {
                parent_id = (int?)null
            })).EnsureSuccessStatusCode();
            await AssertTotals(42, 3, 1, 1);
            var deleted = await Row(childApi, 3);
            (
                await admin.PostAsJsonAsync(
                    childApi + "/delete",
                    new
                    {
                        key = new
                        {
                            id = 3
                        },
                        values = new
                        {
                        },
                        version = deleted.GetProperty("version").GetString(),
                    }
                )
            ).EnsureSuccessStatusCode();
            await AssertTotals(42, 0, 0, 0);
            // Concurrent inserts must not lose increments, even across requests/app DB contexts.
            var inserts = Enumerable
                .Range(0, 12)
                .Select(i =>
                    admin.PostAsJsonAsync(
                        childApi + "/create",
                        new
                        {
                            values = new
                            {
                                parent_id = "9007199254740993",
                                price = 1.25m,
                                qty = 2,
                                title = "Concurrent " + i,
                            },
                        }
                    )
                );
            foreach (var response in await Task.WhenAll(inserts))
                response.EnsureSuccessStatusCode();
            await AssertTotals(9007199254740993, 38, 13, 13);
            // A real MariaDB parent row lock must block the child commit.
            await using (var tx = await conn.BeginTransactionAsync())
            {
                cmd.Transaction = tx;
                cmd.CommandText = $"SELECT id FROM `{parent}` WHERE id=42 FOR UPDATE";
                await cmd.ExecuteScalarAsync();
                var waiting = admin.PostAsJsonAsync(
                    childApi + "/create",
                    new
                    {
                        values = new
                        {
                            parent_id = 42,
                            price = 7,
                            qty = 2,
                            title = "Locked insert",
                        },
                    }
                );
                await Task.Delay(250);
                Assert.False(waiting.IsCompleted);
                await tx.CommitAsync();
                cmd.Transaction = null;
                (await waiting).EnsureSuccessStatusCode();
            }
            await AssertTotals(42, 14, 1, 1);
            // A poisoned unrelated row cannot be read/evaluated by an incremental edit.
            childFields[1] = childFields[1] with
            {
                Formula = "[price] / [qty]",
            };
            await SaveConfiguration(admin, childLayout, childFields);
            cmd.CommandText = $"UPDATE `{child}` SET qty=0 WHERE title='Locked insert'";
            await cmd.ExecuteNonQueryAsync();
            (await Update(2, new
            {
                title = "No aggregate changes"
            })).EnsureSuccessStatusCode();
            // Full rebuild detects the external invalid formula and rolls back every changed parent total.
            var recalc = $"/api/admin/connections/{id}/tables/{parent}/sumups/recalculate";
            Assert.Equal(
                HttpStatusCode.BadRequest,
                (await admin.PostAsync(recalc, null)).StatusCode
            );
            Assert.Equal(HttpStatusCode.BadRequest, (await Update(2, new
            {
                qty = 0
            })).StatusCode);
            Assert.Equal(
                "4",
                (await Row(childApi, 2)).GetProperty("values").GetProperty("qty").ToString()
            );
            cmd.CommandText =
                $"UPDATE `{child}` SET qty=2 WHERE title='Locked insert'; UPDATE `{parent}` SET total=999,item_count=999,non_null_count=999";
            await cmd.ExecuteNonQueryAsync();
            (await admin.PostAsync(recalc, null)).EnsureSuccessStatusCode();
            await AssertTotals(9007199254740993, 8, 13, 13);
            await AssertTotals(42, 3.5m, 1, 1);
            // Presentation-only save is not a hidden full rebuild: external drift remains until explicitly repaired.
            cmd.CommandText = $"UPDATE `{parent}` SET total=100 WHERE id=42";
            await cmd.ExecuteNonQueryAsync();
            fields[0] = fields[0] with
            {
                Label = "Renamed total"
            };
            await SaveConfiguration(admin, parentLayout, fields);
            await AssertTotals(42, 100, 1, 1);
            (await admin.PostAsync(recalc, null)).EnsureSuccessStatusCode();
            await AssertTotals(42, 3.5m, 1, 1);
            // Stored aggregates cannot be forged through either create or update.
            Assert.Equal(
                HttpStatusCode.BadRequest,
                (
                    await admin.PostAsJsonAsync(
                        parentApi + "/create",
                        new
                        {
                            values = new
                            {
                                id = 50,
                                name = "Forged",
                                total = 99,
                            },
                        }
                    )
                ).StatusCode
            );
            var p = await Row(parentApi, 42);
            Assert.Equal(
                HttpStatusCode.BadRequest,
                (
                    await admin.PostAsJsonAsync(
                        parentApi + "/update",
                        new
                        {
                            key = new
                            {
                                id = 42
                            },
                            values = new
                            {
                                total = 99
                            },
                            version = p.GetProperty("version").GetString(),
                        }
                    )
                ).StatusCode
            );
            Assert.Equal(
                HttpStatusCode.Conflict,
                (
                    await admin.PostAsJsonAsync(
                        parentApi + "/delete",
                        new
                        {
                            key = new
                            {
                                id = 42
                            },
                            values = new
                            {
                            },
                            version = p.GetProperty("version").GetString(),
                        }
                    )
                ).StatusCode
            );
            (
                await admin.PostAsJsonAsync(
                    parentApi + "/create",
                    new
                    {
                        values = new
                        {
                            id = 50,
                            name = "Empty parent"
                        }
                    }
                )
            ).EnsureSuccessStatusCode();
            await AssertTotals(50, 0, 0, 0);
            // Rebuild also recovers children entered before their parent existed.
            cmd.CommandText =
                $"INSERT INTO `{child}`(parent_id,price,qty,datatype) VALUES(51,6,2,'default')";
            await cmd.ExecuteNonQueryAsync();
            (
                await admin.PostAsJsonAsync(
                    parentApi + "/create",
                    new
                    {
                        values = new
                        {
                            id = 51,
                            name = "Late parent"
                        }
                    }
                )
            ).EnsureSuccessStatusCode();
            await AssertTotals(51, 3, 1, 1);
            // Failed aggregate definitions preserve both stored totals and the previous layout.
            var invalid = fields
                .Select(f =>
                    f.Name == "total"
                        ? f with
                        {
                            Sumup = f.Sumup! with
                            {
                                SourceField = "title"
                            },
                        }
                        : f
                )
                .ToList();
            Assert.Equal(
                HttpStatusCode.BadRequest,
                (await PutObjectConfiguration(admin, parentLayout, invalid)).StatusCode
            );
            await AssertTotals(42, 3.5m, 1, 1);
            Assert.Equal(
                HttpStatusCode.BadRequest,
                (
                    await PutObjectConfiguration(
                        admin,
                        childLayout,
                        childFields.Where(f => f.Name != "parent_id").ToList()
                    )
                ).StatusCode
            );
            // Durable pending marker models interruption after metadata commit. Next write repairs before applying its delta.
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDb>();
                var l = await db.Layouts.SingleAsync(l =>
                    l.ConnectionId == id && l.Table == parent
                );
                l.FieldsJson = JsonSerializer.Serialize(
                    DatabaseService.Layout(l.FieldsJson) with
                    {
                        SumupsPending = true,
                    }
                );
                await db.SaveChangesAsync();
            }
            cmd.CommandText = $"UPDATE `{parent}` SET total=777 WHERE id=42";
            await cmd.ExecuteNonQueryAsync();
            (await admin.GetAsync(parentApi + "/records")).EnsureSuccessStatusCode();
            await AssertTotals(42, 3.5m, 1, 1);
            var user = await admin.PostAsJsonAsync(
                "/api/admin/users",
                new UserInput("sumup_member", "member-password-12345", false, true)
            );
            var uid = (await user.Content.ReadFromJsonAsync<JsonElement>())
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
            (
                await admin.PutAsJsonAsync(
                    "/api/admin/grants",
                    new TableGrant
                    {
                        UserId = uid,
                        ConnectionId = id,
                        Table = parent,
                        Read = true,
                    }
                )
            ).EnsureSuccessStatusCode();
            using var member = factory.CreateClient();
            await Login(member, "sumup_member", "member-password-12345");
            Assert.Equal(
                HttpStatusCode.Forbidden,
                (await member.PostAsync(recalc, null)).StatusCode
            );
            // Updating a managed total is backend work: child create permission does not require parent update permission.
            (
                await member.PostAsJsonAsync(
                    childApi + "/create",
                    new
                    {
                        values = new
                        {
                            parent_id = 42,
                            price = 4,
                            qty = 2,
                        },
                    }
                )
            ).EnsureSuccessStatusCode();
            await AssertTotals(42, 5.5m, 2, 2);
        }
        finally
        {
            cmd.Transaction = null;
            cmd.CommandText = $"DROP TABLE `{child}`;DROP TABLE `{parent}`";
            await cmd.ExecuteNonQueryAsync();
        }
    }
}

using System.Security.Claims;
using System.Text.Json;
using System.Threading.RateLimiting;
using DbWeb.Api;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using static DbWeb.Api.RecordPresentation;
using static DbWeb.Api.TableAccess;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDbContext<AppDb>(
    (services, o) =>
    {
        var data =
            services.GetRequiredService<IConfiguration>()["DataDirectory"]
            ?? Path.Combine(builder.Environment.ContentRootPath, "data");
        Directory.CreateDirectory(data);
        o.UseSqlite($"Data Source={Path.Combine(data, "app.db")}");
    }
);
builder.Services.AddDataProtection().SetApplicationName("DbWeb");
builder
    .Services.AddOptions<Microsoft.AspNetCore.DataProtection.KeyManagement.KeyManagementOptions>()
    .Configure<IConfiguration>(
        (o, c) =>
        {
            var path = Path.Combine(
                c["DataDirectory"] ?? Path.Combine(builder.Environment.ContentRootPath, "data"),
                "keys"
            );
            Directory.CreateDirectory(path);
            o.XmlRepository =
                new Microsoft.AspNetCore.DataProtection.Repositories.FileSystemXmlRepository(
                    new DirectoryInfo(path),
                    Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance
                );
        }
    );
builder.Services.AddScoped<DatabaseService>();
builder.Services.AddScoped<PasswordHasher<AppUser>>();
builder.Services.AddAntiforgery(o =>
{
    o.HeaderName = "X-CSRF-TOKEN";
    o.Cookie.SameSite = SameSiteMode.Strict;
});
builder
    .Services.AddAuthentication("Cookies")
    .AddCookie(o =>
    {
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Strict;
        o.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
            ? CookieSecurePolicy.SameAsRequest
            : CookieSecurePolicy.Always;
        o.ExpireTimeSpan = TimeSpan.FromHours(8);
        o.Events.OnRedirectToLogin = c =>
        {
            c.Response.StatusCode = 401;
            return Task.CompletedTask;
        };
        o.Events.OnRedirectToAccessDenied = c =>
        {
            c.Response.StatusCode = 403;
            return Task.CompletedTask;
        };
        o.Events.OnValidatePrincipal = async c =>
        {
            var db = c.HttpContext.RequestServices.GetRequiredService<AppDb>();
            var u = await db.Users.FindAsync(
                int.Parse(c.Principal!.FindFirstValue(ClaimTypes.NameIdentifier)!)
            );
            if (u == null || !u.Enabled || u.Stamp != c.Principal!.FindFirstValue("stamp"))
                c.RejectPrincipal();
        };
    });
builder.Services.AddAuthorization(o => o.AddPolicy("Admin", p => p.RequireRole("Admin")));
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.AddPolicy(
        "login",
        c =>
            RateLimitPartition.GetFixedWindowLimiter(
                c.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ =>
                    new()
                    {
                        PermitLimit = builder.Configuration.GetValue("LoginRateLimit:PermitLimit", 10),
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                    }
            )
    );
});
builder.Services.AddProblemDetails();
var app = builder.Build();
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDb>();
    db.Database.EnsureCreated();
    PageSchema.EnsureCreated(db);
    FieldAccess.EnsureCreated(db);
    ObjectModel.Migrate(db);
    if (!db.Users.Any())
    {
        var password = builder.Configuration["Bootstrap:Password"];
        if (string.IsNullOrEmpty(password) || password.Length < 14)
            throw new InvalidOperationException(
                "Set Bootstrap__Password to a unique password of at least 14 characters for first startup."
            );
        var u = new AppUser
        {
            Username = builder.Configuration["Bootstrap:Username"] ?? "admin",
            IsAdmin = true,
        };
        u.PasswordHash = new PasswordHasher<AppUser>().HashPassword(u, password);
        db.Users.Add(u);
        db.SaveChanges();
    }
}
if (args.Contains("--provision-data-types", StringComparer.Ordinal))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDb>();
    var service = scope.ServiceProvider.GetRequiredService<DatabaseService>();
    foreach (var configuredConnection in await db.Connections.OrderBy(item => item.Id).ToListAsync())
    {
        await using var connection = await service.Open(configuredConnection);
        await using var gate = await SumupGate.Enter(connection);
        foreach (var table in await service.Tables(connection))
        {
            var configuration = await db.Layouts.SingleOrDefaultAsync(item =>
                item.ConnectionId == configuredConnection.Id && item.Table == table
            );
            var stored = ObjectModel.Stored(configuration?.FieldsJson);
            await DataTypeColumn.Ensure(
                connection,
                service,
                table,
                stored.Object.DefaultDataTypeKey!,
                stored.Object.DataTypes!.Select(type => type.Key)
            );
            Console.WriteLine($"Provisioned datatype for {configuredConnection.Id}/{table}");
        }
    }
    return;
}
app.UseExceptionHandler();
app.Use(
    async (c, next) =>
    {
        c.Response.Headers["X-Content-Type-Options"] = "nosniff";
        c.Response.Headers["X-Frame-Options"] = "DENY";
        c.Response.Headers["Referrer-Policy"] = "same-origin";
        c.Response.Headers.CacheControl = "no-store";
        try
        {
            await next();
        }
        catch (Exception ex)
            when (ex
                    is ApiError
                        or MySqlException
                        or AntiforgeryValidationException
                        or DbUpdateException
                        or FormatException
                        or OverflowException
            )
        {
            var status = ex switch
            {
                ApiError e => e.Status,
                AntiforgeryValidationException => 400,
                MySqlException m when m.Number == 1062 => 409,
                DbUpdateException => 409,
                _ => 400,
            };
            await Results
                .Problem(
                    statusCode: status,
                    title: ex is ApiError ? ex.Message
                        : ex is AntiforgeryValidationException ? "Invalid security token."
                        : "Operation failed. Check the values, connection, and database constraints."
                )
                .ExecuteAsync(c);
        }
    }
);
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.Use(
    async (c, next) =>
    {
        if (
            c.Request.Path.StartsWithSegments("/api")
            && !HttpMethods.IsGet(c.Request.Method)
            && !HttpMethods.IsHead(c.Request.Method)
        )
            await c.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(c);
        await next();
    }
);
app.MapGet("/health", () => new { status = "ok" });
app.MapGet(
    "/api/auth/csrf",
    (HttpContext c, IAntiforgery af) => new { token = af.GetAndStoreTokens(c).RequestToken }
);
app.MapPost(
        "/api/auth/login",
        async (LoginInput input, AppDb db, PasswordHasher<AppUser> hash, HttpContext c) =>
        {
            var u = await db.Users.SingleOrDefaultAsync(x => x.Username == input.Username);
            var dummy = new AppUser();
            var stored =
                u?.PasswordHash ?? hash.HashPassword(dummy, "constant-time-dummy-password");
            var valid = hash.VerifyHashedPassword(u ?? dummy, stored, input.Password);
            if (u == null || !u.Enabled || valid == PasswordVerificationResult.Failed)
                return Results.Unauthorized();
            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, u.Id.ToString()),
                new Claim(ClaimTypes.Name, u.Username),
                new Claim(ClaimTypes.Role, u.IsAdmin ? "Admin" : "User"),
                new Claim("stamp", u.Stamp),
            };
            await c.SignInAsync(
                "Cookies",
                new ClaimsPrincipal(new ClaimsIdentity(claims, "Cookies"))
            );
            return Results.Ok(
                new
                {
                    u.Id,
                    u.Username,
                    u.IsAdmin,
                }
            );
        }
    )
    .RequireRateLimiting("login");
app.MapPost(
        "/api/auth/logout",
        async (HttpContext c) =>
        {
            await c.SignOutAsync();
            return Results.NoContent();
        }
    )
    .RequireAuthorization();
app.MapGet(
        "/api/auth/me",
        (HttpContext c) =>
            new
            {
                id = int.Parse(c.User.FindFirstValue(ClaimTypes.NameIdentifier)!),
                username = c.User.Identity!.Name,
                isAdmin = c.User.IsInRole("Admin"),
            }
    )
    .RequireAuthorization();
var admin = app.MapGroup("/api/admin").RequireAuthorization("Admin");
SchemaDesigner.Map(admin);
admin.MapGet(
    "/users",
    async (AppDb db) =>
        await db
            .Users.Select(x => new
            {
                x.Id,
                x.Username,
                x.IsAdmin,
                x.Enabled,
            })
            .ToListAsync()
);
admin.MapPost(
    "/users",
    async (UserInput i, AppDb db, PasswordHasher<AppUser> h) =>
    {
        ValidateUser(i, true);
        var u = new AppUser
        {
            Username = i.Username.Trim(),
            IsAdmin = i.IsAdmin,
            Enabled = i.Enabled,
        };
        u.PasswordHash = h.HashPassword(u, i.Password!);
        db.Users.Add(u);
        await db.SaveChangesAsync();
        return Results.Ok(new
        {
            u.Id
        });
    }
);
admin.MapPut(
    "/users/{id:int}",
    async (int id, UserInput i, AppDb db, PasswordHasher<AppUser> h, HttpContext c) =>
    {
        ValidateUser(i, false);
        var u = await db.Users.FindAsync(id) ?? throw new ApiError(404, "User not found.");
        if (
            id == int.Parse(c.User.FindFirstValue(ClaimTypes.NameIdentifier)!)
            && (!i.Enabled || !i.IsAdmin)
        )
            throw new ApiError(400, "You cannot disable or demote your own account.");
        u.Username = i.Username.Trim();
        u.Enabled = i.Enabled;
        u.IsAdmin = i.IsAdmin;
        u.Stamp = Guid.NewGuid().ToString();
        if (!string.IsNullOrEmpty(i.Password))
            u.PasswordHash = h.HashPassword(u, i.Password);
        await db.SaveChangesAsync();
        return Results.NoContent();
    }
);
admin.MapGet(
    "/connections",
    async (AppDb db) =>
        await db
            .Connections.Select(x => new
            {
                x.Id,
                x.Name,
                x.Host,
                x.Port,
                x.Database,
                x.Username,
                x.VerifyTls,
            })
            .ToListAsync()
);
admin.MapPost(
    "/connections",
    async (ConnectionInput i, AppDb db, IDataProtectionProvider p) =>
    {
        var c = new DatabaseConnection();
        SetConnection(c, i, p);
        db.Connections.Add(c);
        await db.SaveChangesAsync();
        return Results.Ok(new
        {
            c.Id
        });
    }
);
admin.MapPut(
    "/connections/{id:int}",
    async (int id, ConnectionInput i, AppDb db, IDataProtectionProvider p) =>
    {
        var c =
            await db.Connections.FindAsync(id) ?? throw new ApiError(404, "Connection not found.");
        SetConnection(c, i, p);
        await db.SaveChangesAsync();
        return Results.NoContent();
    }
);
admin.MapDelete(
    "/connections/{id:int}",
    async (int id, AppDb db) =>
    {
        await db.FieldPolicies.Where(x => x.ConnectionId == id).ExecuteDeleteAsync();
        await db.Grants.Where(x => x.ConnectionId == id).ExecuteDeleteAsync();
        await db.Layouts.Where(x => x.ConnectionId == id).ExecuteDeleteAsync();
        await db.Pages.Where(x => x.ConnectionId == id).ExecuteDeleteAsync();
        await db.Connections.Where(x => x.Id == id).ExecuteDeleteAsync();
        return Results.NoContent();
    }
);
admin.MapPost(
    "/connections/{id:int}/test",
    async (int id, AppDb db, DatabaseService service) =>
    {
        await using var c = await service.Open(
            await db.Connections.FindAsync(id) ?? throw new ApiError(404, "Connection not found.")
        );
        return Results.Ok(new
        {
            tables = (await service.Tables(c)).Count
        });
    }
);
admin.MapGet(
    "/grants",
    async (AppDb db) =>
    {
        var grants = await db.Grants.ToListAsync();
        var policies = await db.FieldPolicies.ToListAsync();
        foreach (var grant in grants)
        {
            var policy = policies.Find(p =>
                p.UserId == grant.UserId
                && p.ConnectionId == grant.ConnectionId
                && p.Table == grant.Table
            );
            grant.Fields =
                policy == null
                    ? null
                    : JsonSerializer.Deserialize<Dictionary<string, string>>(policy.FieldsJson);
        }
        return grants;
    }
);
admin.MapPut(
    "/grants",
    async (TableGrant i, AppDb db, DatabaseService s) =>
    {
        if (!await db.Users.AnyAsync(x => x.Id == i.UserId))
            throw new ApiError(400, "Unknown user.");
        var c =
            await db.Connections.FindAsync(i.ConnectionId)
            ?? throw new ApiError(404, "Connection not found.");
        await using var conn = await s.Open(c);
        var columns = await s.Columns(conn, i.Table);
        var layout = await db.Layouts.SingleOrDefaultAsync(l =>
            l.ConnectionId == i.ConnectionId && l.Table == i.Table
        );
        var names = columns
            .Select(c => c.Name)
            .Concat(DatabaseService.LayoutFields(layout?.FieldsJson).Select(f => f.Name))
            .ToHashSet();
        if (i.Fields != null)
        {
            if (
                i.Fields.Count > 2000
                || i.Fields.Any(f =>
                    !names.Contains(f.Key)
                    || f.Value is not "none" and not "read" and not "write"
                )
            )
                throw new ApiError(
                    400,
                    "Choose existing fields and No access, Read or Write levels."
                );
            var policy = await db.FieldPolicies.SingleOrDefaultAsync(p =>
                p.UserId == i.UserId && p.ConnectionId == i.ConnectionId && p.Table == i.Table
            );
            if (policy == null)
            {
                policy = new()
                {
                    UserId = i.UserId,
                    ConnectionId = i.ConnectionId,
                    Table = i.Table,
                };
                db.FieldPolicies.Add(policy);
            }
            policy.FieldsJson = JsonSerializer.Serialize(i.Fields);
        }
        var g = await db.Grants.SingleOrDefaultAsync(x =>
            x.UserId == i.UserId && x.ConnectionId == i.ConnectionId && x.Table == i.Table
        );
        if (g == null)
        {
            g = new()
            {
                UserId = i.UserId,
                ConnectionId = i.ConnectionId,
                Table = i.Table,
            };
            db.Grants.Add(g);
        }
        g.Read = i.Read;
        g.Create = i.Create;
        g.Update = i.Update;
        g.Delete = i.Delete;
        await db.SaveChangesAsync();
        return Results.NoContent();
    }
);
admin.MapGet(
    "/audit",
    async (AppDb db) => await db.Audit.OrderByDescending(x => x.Id).Take(200).ToListAsync()
);
var api = app.MapGroup("/api/connections").RequireAuthorization();
api.MapGet(
    "",
    async (AppDb db, HttpContext ctx) =>
    {
        var uid = int.Parse(ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        return await db
            .Connections.Where(x =>
                ctx.User.IsInRole("Admin")
                || db.Grants.Any(g => g.ConnectionId == x.Id && g.UserId == uid && g.Read)
            )
            .Select(x => new
            {
                x.Id,
                x.Name,
                x.Database,
            })
            .ToListAsync();
    }
);
api.MapGet(
    "/{id:int}/tables",
    async (int id, AppDb db, DatabaseService s, HttpContext ctx) =>
    {
        var uid = int.Parse(ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        if (
            !ctx.User.IsInRole("Admin")
            && !await db.Grants.AnyAsync(x => x.UserId == uid && x.ConnectionId == id && x.Read)
        )
            throw new ApiError(403, "Access denied.");
        await using var c = await s.Open(
            await db.Connections.FindAsync(id) ?? throw new ApiError(404, "Connection not found.")
        );
        var tables = await s.Tables(c);
        if (ctx.User.IsInRole("Admin"))
            return tables;
        var allowed = await db
            .Grants.Where(x => x.UserId == uid && x.ConnectionId == id && x.Read)
            .Select(x => x.Table)
            .ToListAsync();
        return tables.Intersect(allowed).ToList();
    }
);
api.MapGet(
    "/{id:int}/tables/{table}/records",
    async (
        int id,
        string table,
        int? page,
        int? size,
        string? sort,
        bool? descending,
        string? search,
        AppDb db,
        DatabaseService s,
        HttpContext ctx
    ) =>
    {
        var config = await Access(db, ctx, id, table, "read");
        await using var c = await s.Open(config);
        await Sumups.RepairIfPending(db, s, c, id);
        var layout = await db.Layouts.SingleOrDefaultAsync(x =>
            x.ConnectionId == id && x.Table == table
        );
        var definition = DatabaseService.Layout(layout?.FieldsJson);
        var columns = await s.Columns(c, table);
        var access = await FieldAccess.For(db, ctx, id, table);
        var safeFields = await FieldAccess.VisibleFields(
            db,
            ctx,
            id,
            table,
            definition.Fields,
            columns
        );
        var blocked = definition
            .Fields.Where(f => !safeFields.Any(x => x.Name == f.Name))
            .Select(f => f.Name)
            .ToHashSet();
        var readable = columns
            .Where(col => access.Read(col.Name) && !blocked.Contains(col.Name))
            .Select(col => col.Name)
            .ToHashSet();
        var result = await s.List(
            c,
            table,
            page ?? 1,
            size ?? 25,
            sort,
            descending ?? false,
            search,
            definition.View,
            safeFields,
            readable: readable,
            lookupSearchFields: string.IsNullOrEmpty(search)
                ? []
                : await FieldAccess.SearchLookups(db, ctx, id, safeFields)
        );
        await Decorate(db, ctx, id, table, c, s, definition.Fields, result);
        return result;
    }
);
api.MapGet(
    "/{id:int}/tables/{table}/settings",
    async (int id, string table, AppDb db, DatabaseService service, HttpContext ctx) =>
    {
        var config = await Access(db, ctx, id, table, "read");
        var uid = int.Parse(ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var grant = ctx.User.IsInRole("Admin")
            ? new TableGrant
            {
                Read = true,
                Create = true,
                Update = true,
                Delete = true,
            }
            : await db.Grants.SingleAsync(x =>
                x.UserId == uid && x.ConnectionId == id && x.Table == table
            );
        var l = await db.Layouts.SingleOrDefaultAsync(x =>
            x.ConnectionId == id && x.Table == table
        );
        await using var c = await service.Open(config);
        var stored = ObjectModel.Stored(l?.FieldsJson);
        var fields = await FieldAccess.VisibleFields(
            db,
            ctx,
            id,
            table,
            ObjectModel.Merge(stored).Fields,
            await service.Columns(c, table)
        );
        var access = await FieldAccess.For(db, ctx, id, table);
        return new
        {
            grant,
            fields,
            view = FieldAccess.PublicView(DatabaseService.Layout(l?.FieldsJson).View, access)
                ?? new ListView(),
        };
    }
);
admin.MapGet(
    "/connections/{id:int}/tables/{table}/object",
    async (int id, string table, AppDb db, DatabaseService service) =>
    {
        var config =
            await db.Connections.FindAsync(id) ?? throw new ApiError(404, "Connection not found.");
        await using var connection = await service.Open(config);
        var stored = ObjectModel.Stored(
            (
                await db.Layouts.SingleOrDefaultAsync(x => x.ConnectionId == id && x.Table == table)
            )?.FieldsJson
        );
        var columns = await service.Columns(connection, table);
        return ObjectModel.WithColumns(stored.Object, columns);
    }
);
admin.MapGet(
    "/connections/{id:int}/tables/{table}/layout",
    async (int id, string table, AppDb db, DatabaseService service) =>
    {
        var config =
            await db.Connections.FindAsync(id) ?? throw new ApiError(404, "Connection not found.");
        await using var connection = await service.Open(config);
        await service.Columns(connection, table);
        return ObjectModel
            .Stored(
                (
                    await db.Layouts.SingleOrDefaultAsync(x =>
                        x.ConnectionId == id && x.Table == table
                    )
                )?.FieldsJson
            )
            .Layout;
    }
);
admin.MapDelete(
    "/connections/{id:int}/tables/{table}/fields/{field}",
    async (int id, string table, string field, AppDb db, DatabaseService service, HttpContext ctx) =>
    {
        if (DataTypeColumn.Is(field))
            throw new ApiError(400, "The datatype column is managed by the backend.");
        var config = await db.Connections.FindAsync(id) ?? throw new ApiError(404, "Connection not found.");
        await using var connection = await service.Open(config);
        await using var gate = await SumupGate.Enter(connection);
        var columns = await service.Columns(connection, table);
        var configuration = await db.Layouts.SingleOrDefaultAsync(x => x.ConnectionId == id && x.Table == table);
        var stored = ObjectModel.Stored(configuration?.FieldsJson);
        var completedObject = ObjectModel.WithColumns(stored.Object, columns);
        stored = new(
            completedObject,
            ObjectModel.CompleteLayout(completedObject, stored.Layout)
        );
        var existing = stored.Object.Fields.SingleOrDefault(x => x.Name.Equals(field, StringComparison.OrdinalIgnoreCase));
        if (existing == null)
            throw new ApiError(404, "Field not found.");
        var physical = columns.Any(x => x.Name.Equals(field, StringComparison.OrdinalIgnoreCase));
        var schema = physical ? await SchemaDesigner.Inspect(connection, service, table) : null;
        var schemaColumn = schema?.Columns.Single(x => x.Name.Equals(field, StringComparison.OrdinalIgnoreCase));
        if (schemaColumn?.PrimaryKey == true || schemaColumn?.AutoIncrement == true)
            throw new ApiError(400, "Primary key fields cannot be deleted here.");
        if (schemaColumn?.Referenced == true)
            throw new ApiError(
                409,
                "This field is referenced by another table. Remove that foreign key first."
            );
        if (await db.Pages.AnyAsync(page => page.ConnectionId == id && page.Table == table && page.LinkColumn == field))
            throw new ApiError(409, "This field is a record page link column. Change or remove that page first.");

        var removed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { field };
        var removedByTable = new Dictionary<string, HashSet<string>>(
            StringComparer.OrdinalIgnoreCase
        )
        {
            [table] = removed,
        };
        var currentFields = ObjectModel.Merge(stored).Fields;
        var addedDependency = true;
        while (addedDependency)
        {
            addedDependency = false;
            foreach (var candidate in stored.Object.Fields.Where(x => !removed.Contains(x.Name)))
            {
                var dependent =
                    candidate.Join is { } join
                    && (
                        removed.Contains(join.SourceColumn)
                        || join.Table.Equals(table, StringComparison.OrdinalIgnoreCase)
                        && (removed.Contains(join.KeyColumn) || removed.Contains(join.ValueColumn))
                    )
                    || candidate.Widget == "formula"
                    && Formulas.Dependencies(candidate.Formula, columns, currentFields)
                        .Any(removed.Contains);
                if (dependent && removed.Add(candidate.Name))
                    addedDependency = true;
            }
        }
        var fields = stored.Object.Fields.Where(x => !removed.Contains(x.Name)).Select(candidate =>
        {
            if (
                candidate.Sumup is { } sumup
                && sumup.ChildTable.Equals(table, StringComparison.OrdinalIgnoreCase)
                && (
                    removed.Contains(sumup.LookupField)
                    || removed.Contains(sumup.SourceField ?? "")
                )
            )
                return candidate with
                {
                    Widget = "number",
                    Sumup = null,
                    ReadOnly = false,
                };
            if (candidate.Lookup is not { } lookup)
                return candidate;
            var pointsToTable = lookup.Table.Equals(table, StringComparison.OrdinalIgnoreCase);
            if (
                pointsToTable
                && (removed.Contains(lookup.KeyColumn) || removed.Contains(lookup.DisplayColumn))
            )
                return candidate with
                {
                    Widget = "auto",
                    Lookup = null,
                    Options = null
                };
            var mappings = lookup.CopyMappings?.Where(mapping =>
                    !removed.Contains(mapping.DestinationColumn)
                    && (!pointsToTable || !removed.Contains(mapping.SourceColumn))
                )
                .ToList();
            var criteria = lookup.Criteria;
            if (pointsToTable && criteria != null)
                criteria = criteria with
                {
                    Sort = removed.Contains(criteria.Sort ?? "") ? null : criteria.Sort,
                    Filters = criteria.Filters?.Where(filter => !removed.Contains(filter.Column))
                        .ToList(),
                };
            return candidate with
            {
                Lookup = lookup with
                {
                    SearchColumns = pointsToTable
                        ? lookup.SearchColumns.Where(name => !removed.Contains(name)).ToList()
                        : lookup.SearchColumns,
                    CopyMappings = mappings,
                    Criteria = criteria,
                },
            };
        }).ToList();
        var view = stored.Object.View;
        if (view != null)
            view = view with
            {
                Sort = removed.Contains(view.Sort ?? "") ? null : view.Sort,
                Filters = view.Filters?.Where(filter => !removed.Contains(filter.Column)).ToList()
            };
        var definition = stored.Object with
        {
            Fields = fields,
            View = view,
            FieldSetRules = (stored.Object.FieldSetRules ?? []).Where(rule =>
                    !removed.Contains(rule.Field)
                    && !Formulas.Dependencies(rule.Condition, columns, currentFields).Any(removed.Contains)
                    && !Formulas.Dependencies(rule.Value, columns, currentFields).Any(removed.Contains)
                )
                .ToList()
        };
        var presentation = new LayoutPresentation(stored.Layout.Fields.Where(x => !removed.Contains(x.Name)).ToList());
        var cleaned = ObjectModel.Normalize(new(definition, presentation));
        var merged = await ObjectConfigurationRules.Validate(
            service,
            connection,
            table,
            cleaned.Object,
            cleaned.Layout
        );
        var affected = new List<(RecordLayout Configuration, StoredObjectDefinition Definition)>();
        if (configuration != null)
            affected.Add((configuration, merged));
        foreach (
            var other in await db.Layouts.Where(layout =>
                layout.ConnectionId == id && layout.Table != table
            ).ToListAsync()
        )
        {
            var otherStored = ObjectModel.Stored(other.FieldsJson);
            var changed = false;
            var otherFields = new List<ObjectField>();
            foreach (var candidate in otherStored.Object.Fields)
            {
                if (
                    candidate.Join is { } join
                    && join.Table.Equals(table, StringComparison.OrdinalIgnoreCase)
                    && (removed.Contains(join.KeyColumn) || removed.Contains(join.ValueColumn))
                )
                {
                    if (!removedByTable.TryGetValue(other.Table, out var otherRemoved))
                    {
                        otherRemoved = new(StringComparer.OrdinalIgnoreCase);
                        removedByTable[other.Table] = otherRemoved;
                    }
                    otherRemoved.Add(candidate.Name);
                    changed = true;
                    continue;
                }
                if (
                    candidate.Lookup is { } lookup
                    && lookup.Table.Equals(table, StringComparison.OrdinalIgnoreCase)
                )
                {
                    if (removed.Contains(lookup.KeyColumn) || removed.Contains(lookup.DisplayColumn))
                    {
                        otherFields.Add(
                            candidate with
                            {
                                Widget = "auto",
                                Lookup = null,
                                Options = null
                            }
                        );
                        changed = true;
                        continue;
                    }
                    var search = lookup.SearchColumns.Where(name => !removed.Contains(name)).ToList();
                    var copies = lookup.CopyMappings?.Where(mapping =>
                            !removed.Contains(mapping.SourceColumn)
                        )
                        .ToList();
                    var criteria = lookup.Criteria;
                    if (criteria != null)
                        criteria = criteria with
                        {
                            Sort = removed.Contains(criteria.Sort ?? "") ? null : criteria.Sort,
                            Filters = criteria.Filters?.Where(filter =>
                                    !removed.Contains(filter.Column)
                                )
                                .ToList(),
                        };
                    if (
                        search.Count != lookup.SearchColumns.Count
                        || copies?.Count != lookup.CopyMappings?.Count
                        || criteria != lookup.Criteria
                    )
                    {
                        otherFields.Add(
                            candidate with
                            {
                                Lookup = lookup with
                                {
                                    SearchColumns = search,
                                    CopyMappings = copies,
                                    Criteria = criteria,
                                },
                            }
                        );
                        changed = true;
                        continue;
                    }
                }
                if (
                    candidate.Sumup is { } sumup
                    && sumup.ChildTable.Equals(table, StringComparison.OrdinalIgnoreCase)
                    && (
                        removed.Contains(sumup.LookupField)
                        || removed.Contains(sumup.SourceField ?? "")
                    )
                )
                {
                    otherFields.Add(
                        candidate with
                        {
                            Widget = "number",
                            Sumup = null,
                            ReadOnly = false,
                        }
                    );
                    changed = true;
                    continue;
                }
                otherFields.Add(candidate);
            }
            if (!changed)
                continue;
            var names = otherFields.Select(item => item.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var otherDefinition = otherStored.Object with
            {
                Fields = otherFields
            };
            var otherPresentation = new LayoutPresentation(
                otherStored.Layout.Fields.Where(item => names.Contains(item.Name)).ToList()
            );
            var otherCleaned = ObjectModel.Normalize(new(otherDefinition, otherPresentation));
            affected.Add(
                (
                    other,
                    await ObjectConfigurationRules.Validate(
                        service,
                        connection,
                        other.Table,
                        otherCleaned.Object,
                        otherCleaned.Layout
                    )
                )
            );
        }
        if (physical)
            await SchemaDesigner.DropColumn(connection, table, field);
        foreach (var item in affected)
            await Sumups.SaveLayout(db, service, connection, id, item.Configuration, item.Definition);
        foreach (var page in await db.Pages.Where(page => page.ConnectionId == id).ToListAsync())
        {
            var tabs = JsonSerializer.Deserialize<List<RelatedTab>>(page.TabsJson) ?? [];
            var changed = false;
            tabs = tabs.Where(tab =>
            {
                var parentReference =
                    page.Table.Equals(table, StringComparison.OrdinalIgnoreCase)
                    && removed.Contains(tab.ParentColumn);
                var childReference =
                    tab.Table.Equals(table, StringComparison.OrdinalIgnoreCase)
                    && new[] { tab.RelatedColumn, tab.LinkColumn, tab.LookupField }.Any(name =>
                        name != null && removed.Contains(name)
                    );
                changed |= parentReference || childReference;
                return !parentReference && !childReference;
            }).Select(tab =>
            {
                if (!tab.Table.Equals(table, StringComparison.OrdinalIgnoreCase))
                    return tab;
                var next = tab.Columns.Where(name => !removed.Contains(name)).ToList();
                changed |= next.Count != tab.Columns.Count;
                return tab with
                {
                    Columns = next
                };
            }).ToList();
            if (changed)
                page.TabsJson = JsonSerializer.Serialize(tabs);
        }
        foreach (
            var policy in await db.FieldPolicies.Where(policy =>
                policy.ConnectionId == id
            ).ToListAsync()
        )
        {
            if (!removedByTable.TryGetValue(policy.Table, out var removedNames))
                continue;
            var levels =
                JsonSerializer.Deserialize<Dictionary<string, string>>(policy.FieldsJson) ?? [];
            foreach (var name in levels.Keys.Where(removedNames.Contains).ToList())
                levels.Remove(name);
            policy.FieldsJson = JsonSerializer.Serialize(levels);
        }
        db.Audit.Add(new()
        {
            Actor = ctx.User.Identity!.Name!,
            Action = physical ? "delete-column" : "delete-virtual-field",
            Resource = $"{id}/{table}/{field}"
        });
        await db.SaveChangesAsync();
        return Results.NoContent();
    }
);
admin.MapPut(
    "/connections/{id:int}/tables/{table}/object",
    async (int id, string table, JsonElement input, AppDb db, DatabaseService service) =>
    {
        var config =
            await db.Connections.FindAsync(id) ?? throw new ApiError(404, "Connection not found.");
        await using var connection = await service.Open(config);
        await using var gate = await SumupGate.Enter(connection);
        var configuration = await db.Layouts.SingleOrDefaultAsync(x =>
            x.ConnectionId == id && x.Table == table
        );
        var stored = ObjectModel.Stored(configuration?.FieldsJson);
        var submitted = ObjectModel.ParseObject(input);
        if (configuration != null && !ObjectModel.HasProperty(input, "fieldSetRules"))
            submitted = submitted with
            {
                FieldSetRules = stored.Object.FieldSetRules,
            };
        if (
            configuration != null
            && ObjectModel.HasCanonicalDataTypes(configuration.FieldsJson)
            && !ObjectModel.HasProperty(input, "dataTypes")
        )
        {
            if (ObjectModel.HasLegacyFieldBehavior(input))
                throw new ApiError(
                    400,
                    "Required and mask settings moved to Data Types. Refresh this object before saving."
                );
            submitted = submitted with
            {
                DataTypes = stored.Object.DataTypes,
                DefaultDataTypeKey = stored.Object.DefaultDataTypeKey,
            };
            submitted = ObjectModel.Normalize(new(submitted, stored.Layout)).Object;
        }
        else if (
            configuration != null
            && ObjectModel.HasCanonicalDataTypes(configuration.FieldsJson)
        )
        {
            if (submitted.DataTypes?.Any(type => type == null) == true)
                throw new ApiError(400, "Data type definitions cannot be null.");
            if (!ObjectModel.HasProperty(input, "defaultDataTypeKey"))
                submitted = submitted with
                {
                    DefaultDataTypeKey = stored.Object.DefaultDataTypeKey,
                };
            var submittedKeys = (submitted.DataTypes ?? [])
                .Select(type => type.Key)
                .ToHashSet(StringComparer.Ordinal);
            var storedKeys = stored.Object.DataTypes!
                .Select(type => type.Key)
                .ToHashSet(StringComparer.Ordinal);
            var removed = storedKeys.Where(key => !submittedKeys.Contains(key)).ToList();
            var added = submittedKeys.Where(key => !storedKeys.Contains(key)).ToList();
            if (
                removed.Count > 0
                && added.Count > 0
                || removed.Contains(stored.Object.DefaultDataTypeKey!, StringComparer.Ordinal)
            )
                throw new ApiError(
                    400,
                    "Data type keys are immutable. Delete a non-default type or add a new type in a separate save."
                );
        }
        var normalized = submitted with
        {
            SumupsPending = false
        };
        var merged = await ObjectConfigurationRules.Validate(
            service,
            connection,
            table,
            normalized,
            ObjectModel.CompleteLayout(normalized, stored.Layout)
        );
        var configuredTypeKeys = merged.Object.DataTypes!.Select(type => type.Key).ToList();
        await DataTypeColumn.ValidateExisting(
            connection,
            service,
            table,
            configuredTypeKeys
        );
        await DataTypeColumn.Prepare(connection, service, table);
        if (configuration == null)
        {
            configuration = new()
            {
                ConnectionId = id,
                Table = table
            };
            db.Layouts.Add(configuration);
        }
        await Sumups.SaveLayout(db, service, connection, id, configuration, merged);
        await DataTypeColumn.Finalize(
            connection,
            service,
            table,
            merged.Object.DefaultDataTypeKey!,
            configuredTypeKeys
        );
        return Results.NoContent();
    }
);
admin.MapPut(
    "/connections/{id:int}/tables/{table}/layout",
    async (int id, string table, JsonElement input, AppDb db, DatabaseService service) =>
    {
        var config =
            await db.Connections.FindAsync(id) ?? throw new ApiError(404, "Connection not found.");
        await using var connection = await service.Open(config);
        await using var gate = await SumupGate.Enter(connection);
        var configuration = await db.Layouts.SingleOrDefaultAsync(x =>
            x.ConnectionId == id && x.Table == table
        );
        var stored = ObjectModel.Stored(configuration?.FieldsJson);

        // Compatibility: existing API clients may still submit the former combined
        // field array/object. New clients submit presentation-only fields here.
        // Data Type-owned behavior is invalid in either shape. Check before shape
        // detection because a combined object can omit both widget and view.
        ObjectModel.ValidateLegacyLayoutWrite(input);
        var legacy = input.ValueKind == JsonValueKind.Array;
        if (input.ValueKind == JsonValueKind.Object)
        {
            var fieldsProperty = input
                .EnumerateObject()
                .FirstOrDefault(p => p.Name.Equals("fields", StringComparison.OrdinalIgnoreCase));
            if (
                fieldsProperty.Value.ValueKind == JsonValueKind.Array
                && fieldsProperty.Value.GetArrayLength() > 0
                && fieldsProperty.Value[0].ValueKind == JsonValueKind.Object
            )
                legacy = fieldsProperty
                    .Value[0]
                    .EnumerateObject()
                    .Any(p => p.Name.Equals("widget", StringComparison.OrdinalIgnoreCase));
            legacy |= input
                .EnumerateObject()
                .Any(p => p.Name.Equals("view", StringComparison.OrdinalIgnoreCase));
        }

        StoredObjectDefinition merged;
        if (legacy)
        {
            var legacyDefinition = DatabaseService.ParseLayout(input) with
            {
                SumupsPending = false,
            };
            if (input.ValueKind == JsonValueKind.Array)
                legacyDefinition = legacyDefinition with
                {
                    View = stored.Object.View
                };
            var split = ObjectModel.Split(legacyDefinition);
            if (configuration != null && ObjectModel.HasCanonicalDataTypes(configuration.FieldsJson))
            {
                split = split with
                {
                    Object = split.Object with
                    {
                        DataTypes = stored.Object.DataTypes,
                        DefaultDataTypeKey = stored.Object.DefaultDataTypeKey,
                    },
                };
                split = ObjectModel.Normalize(split);
            }
            merged = await ObjectConfigurationRules.Validate(
                service,
                connection,
                table,
                split.Object,
                split.Layout
            );
        }
        else
        {
            LayoutPresentation presentation;
            try
            {
                presentation =
                    input.Deserialize<LayoutPresentation>(
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
                    ) ?? throw new ApiError(400, "Layout fields are required.");
            }
            catch (JsonException)
            {
                throw new ApiError(400, "Invalid layout configuration.");
            }
            if (presentation.Fields == null)
                throw new ApiError(400, "Layout fields are required.");
            var fieldsElement = input
                .EnumerateObject()
                .FirstOrDefault(p => p.Name.Equals("fields", StringComparison.OrdinalIgnoreCase))
                .Value;
            var fieldsWithSection =
                fieldsElement.ValueKind == JsonValueKind.Array
                    ? fieldsElement
                        .EnumerateArray()
                        .Where(field =>
                            field.ValueKind == JsonValueKind.Object
                            && field
                                .EnumerateObject()
                                .Any(p =>
                                    p.Name.Equals("section", StringComparison.OrdinalIgnoreCase)
                                )
                        )
                        .Select(field =>
                            field
                                .EnumerateObject()
                                .FirstOrDefault(p =>
                                    p.Name.Equals("name", StringComparison.OrdinalIgnoreCase)
                                )
                                .Value
                        )
                        .Where(name => name.ValueKind == JsonValueKind.String)
                        .Select(name => name.GetString() ?? "")
                        .ToHashSet(StringComparer.OrdinalIgnoreCase)
                    : [];
            var existingSections = stored.Layout.Fields.ToDictionary(
                field => field.Name,
                field => field.Section,
                StringComparer.OrdinalIgnoreCase
            );
            var existingLabels = stored.Layout.Fields.ToDictionary(
                field => field.Name,
                field => field.Label,
                StringComparer.OrdinalIgnoreCase
            );
            presentation = presentation with
            {
                Fields = presentation
                    .Fields.Select(field =>
                    {
                        var result = field;
                        if (
                            !fieldsWithSection.Contains(field.Name)
                            && existingSections.TryGetValue(field.Name, out var section)
                        )
                            result = result with
                            {
                                Section = section
                            };
                        if (result.Label == null)
                            result = result with
                            {
                                Label = existingLabels.GetValueOrDefault(field.Name)
                                    ?? stored.Object.Fields.FirstOrDefault(candidate =>
                                            candidate.Name.Equals(
                                                field.Name,
                                                StringComparison.OrdinalIgnoreCase
                                            )
                                        )?.Label
                                    ?? field.Name,
                            };
                        return result;
                    }
                    )
                    .ToList(),
            };
            merged = await ObjectConfigurationRules.Validate(
                service,
                connection,
                table,
                ObjectModel.WithColumns(stored.Object, await service.Columns(connection, table)),
                presentation
            );
        }
        if (configuration == null)
        {
            configuration = new()
            {
                ConnectionId = id,
                Table = table
            };
            db.Layouts.Add(configuration);
        }
        var configuredTypeKeys = merged.Object.DataTypes!.Select(type => type.Key).ToList();
        await DataTypeColumn.ValidateExisting(
            connection,
            service,
            table,
            configuredTypeKeys
        );
        await DataTypeColumn.Prepare(connection, service, table);
        await Sumups.SaveLayout(db, service, connection, id, configuration, merged);
        await DataTypeColumn.Finalize(
            connection,
            service,
            table,
            merged.Object.DefaultDataTypeKey!,
            configuredTypeKeys
        );
        return Results.NoContent();
    }
);
api.MapGet(
    "/{id:int}/tables/{table}/schema",
    async (int id, string table, AppDb db, DatabaseService s, HttpContext ctx) =>
    {
        var config = await Access(db, ctx, id, table, "read");
        await using var c = await s.Open(config);
        var access = await FieldAccess.For(db, ctx, id, table);
        return new
        {
            columns = (await s.Columns(c, table))
                .Where(col => access.Read(col.Name))
                .Select(access.Column),
            lookupKeys = (await s.LookupKeys(c, table)).Where(access.Read),
        };
    }
);
api.MapGet(
    "/{id:int}/tables/{table}/lookups/{field}",
    async (
        int id,
        string table,
        string field,
        int? page,
        int? size,
        string? search,
        string? key,
        AppDb db,
        DatabaseService s,
        HttpContext ctx
    ) =>
    {
        var config = await Access(db, ctx, id, table, "read");
        var layout = await db.Layouts.SingleOrDefaultAsync(x =>
            x.ConnectionId == id && x.Table == table
        );
        var lookup =
            DatabaseService
                .LayoutFields(layout?.FieldsJson)
                .SingleOrDefault(f => f.Name == field && f.Widget == "lookup")
                ?.Lookup
            ?? throw new ApiError(404, "Lookup not configured.");
        (await FieldAccess.For(db, ctx, id, table)).RequireRead([field]);
        await FieldAccess.Related(
            db,
            ctx,
            id,
            lookup.Table,
            [lookup.KeyColumn, lookup.DisplayColumn]
        );
        await using var c = await s.Open(config);
        if (key != null)
        {
            var labels = await s.LookupLabels(c, lookup, new object?[] { key });
            return Results.Ok(
                new
                {
                    found = labels.ContainsKey(key),
                    label = labels.GetValueOrDefault(key)
                }
            );
        }
        lookup = await FieldAccess.Lookup(db, ctx, id, lookup);
        return Results.Ok(await s.LookupSearch(c, lookup, page ?? 1, size ?? 25, search));
    }
);
api.MapPost(
    "/{id:int}/tables/{table}/lookups/{field}/copy",
    async (
        int id,
        string table,
        string field,
        LookupCopyInput input,
        AppDb db,
        DatabaseService s,
        HttpContext ctx
    ) =>
    {
        var config = await Access(db, ctx, id, table, "read");
        var layout = await db.Layouts.SingleOrDefaultAsync(x =>
            x.ConnectionId == id && x.Table == table
        );
        var fields = DatabaseService.LayoutFields(layout?.FieldsJson);
        var lookup =
            fields.SingleOrDefault(f => f.Name == field && f.Widget == "lookup")?.Lookup
            ?? throw new ApiError(404, "Lookup not configured.");
        var access = await FieldAccess.For(db, ctx, id, table);
        access.RequireWrite(
            new[] { field }.Concat(lookup.CopyMappings?.Select(m => m.DestinationColumn) ?? [])
        );
        lookup = await FieldAccess.Lookup(db, ctx, id, lookup, true);
        await using var c = await s.Open(config);
        await s.ValidateCopyMappings(c, fields, await s.Columns(c, table));
        return new
        {
            values = await s.CopyLookupValues(c, lookup, input.Key)
        };
    }
);
api.MapPost(
    "/{id:int}/tables/{table}/joins/resolve",
    async (int id, string table, JoinInput input, AppDb db, DatabaseService s, HttpContext ctx) =>
    {
        var config = await Access(db, ctx, id, table, "read");
        await using var c = await s.Open(config);
        var columns = await s.Columns(c, table);
        if (
            input.Values == null
            || input.ChangedFields?.Any(name => !columns.Any(col => col.Name == name)) == true
            || input.Values.Any(v =>
                !columns.Any(col => col.Name == v.Key)
                || v.Value.ValueKind
                    is not JsonValueKind.String
                        and not JsonValueKind.Number
                        and not JsonValueKind.Null
                        and not JsonValueKind.True
                        and not JsonValueKind.False
            )
        )
            throw new ApiError(400, "Provide scalar values and changed fields for existing source columns.");
        (await FieldAccess.For(db, ctx, id, table)).RequireRead(
            input.Values.Keys.Concat(input.ChangedFields ?? [])
        );
        var layout = await db.Layouts.SingleOrDefaultAsync(x =>
            x.ConnectionId == id && x.Table == table
        );
        var safeFields = await FieldAccess.VisibleFields(
            db,
            ctx,
            id,
            table,
            DatabaseService.LayoutFields(layout?.FieldsJson),
            columns
        );
        var row = new RecordRow(
            input.Values.ToDictionary(
                v => v.Key,
                v =>
                    v.Value.ValueKind switch
                    {
                        JsonValueKind.Null => null,
                        JsonValueKind.True => (object?)1,
                        JsonValueKind.False => 0,
                        _ => v.Value.ToString(),
                    }
            ),
            ""
        );
        var joinedColumns = await PopulateJoins(
            db,
            ctx,
            id,
            c,
            s,
            safeFields,
            columns,
            [row],
            input.ChangedFields?.ToHashSet()
        );
        return new
        {
            values = row.JoinedValues,
            columns = joinedColumns,
            calculationErrors = row.CalculationErrors,
        };
    }
);
api.MapPost(
    "/{id:int}/tables/{table}/create-preview",
    async (int id, string table, AppDb db, DatabaseService service, HttpContext ctx) =>
    {
        var config = await Access(db, ctx, id, table, "create");
        await using var c = await service.Open(config);
        var values = await RecordWrites.Preview(db, ctx, id, table, service, c);
        var access = await FieldAccess.For(db, ctx, id, table);
        return new
        {
            values = values.Where(v => access.Read(v.Key)).ToDictionary()
        };
    }
);
foreach (var operation in new[] { "create", "update", "delete" })
{
    var op = operation;
    api.MapPost(
        "/{id:int}/tables/{table}/" + op,
        async (
            int id,
            string table,
            RowMutation input,
            AppDb db,
            DatabaseService s,
            HttpContext ctx
        ) =>
        {
            var config = await Access(db, ctx, id, table, op);
            await using var c = await s.Open(config);
            await using var gate = await SumupGate.Enter(c);
            var plans = await Sumups.Ready(db, s, c, id);
            input = await FieldAccess.DecodeMutation(db, ctx, id, table, input, op);
            var validated = await RecordWrites.Validate(db, ctx, id, table, input, op, s, c);
            await s.Mutate(
                c,
                table,
                input,
                op,
                validated.Fields,
                sumups: plans,
                fieldSetRules: validated.FieldSetRules,
                dataTypeKeys: validated.DataTypeKeys
            );
            db.Audit.Add(
                new()
                {
                    Actor = ctx.User.Identity!.Name!,
                    Action = op,
                    Resource = $"{id}/{table}",
                }
            );
            await db.SaveChangesAsync();
            return Results.NoContent();
        }
    );
}
admin.MapPost(
    "/connections/{id:int}/tables/{table}/formulas/validate",
    async (int id, string table, FormulaValidationInput input, AppDb db, DatabaseService service) =>
    {
        await using var c = await service.Open(
            await db.Connections.FindAsync(id) ?? throw new ApiError(404, "Connection not found.")
        );
        var fields = input.Fields ?? [];
        if (fields.Count > 200 || fields.Any(f => f == null))
            throw new ApiError(400, "Invalid draft layout fields.");
        Formulas.Compile(input.Formula, await service.Columns(c, table), fields);
        return Results.Ok(
            new
            {
                message = "Formula is valid. Syntax, column references and function arguments checked; results depend on record values.",
            }
        );
    }
);
admin.MapPost(
    "/connections/{id:int}/tables/{table}/field-set-rules/validate",
    async (int id, string table, FieldSetRuleValidationInput input, AppDb db, DatabaseService service) =>
    {
        await using var c = await service.Open(
            await db.Connections.FindAsync(id) ?? throw new ApiError(404, "Connection not found.")
        );
        var fields = input.Fields ?? [];
        if (fields.Count > 200 || fields.Any(field => field == null))
            throw new ApiError(400, "Invalid draft layout fields.");
        var columns = await service.Columns(c, table);
        if (!columns.Any(column => DataTypeColumn.Is(column.Name)))
            columns.Add(new(DataTypeColumn.Name, "varchar", false, false, false, false, null, 64));
        ObjectConfigurationRules.ValidateFieldSetRule(
            new(input.Field ?? "", input.Condition ?? "", input.Value ?? ""),
            columns,
            fields
        );
        return Results.Ok(new
        {
            message = "Field set rule formulas are valid. Syntax, field references and target eligibility checked; results depend on record values.",
        });
    }
);
admin.MapGet(
    "/connections/{id:int}/tables/{table}/sumups/relations",
    async (int id, string table, AppDb db, DatabaseService service) =>
    {
        await using var c = await service.Open(
            await db.Connections.FindAsync(id) ?? throw new ApiError(404, "Connection not found.")
        );
        var result = new List<object>();
        foreach (var layout in await db.Layouts.Where(l => l.ConnectionId == id).ToListAsync())
        {
            var fields = DatabaseService.LayoutFields(layout.FieldsJson);
            var relations = fields
                .Where(f => f.Widget == "lookup" && f.Lookup?.Table == table)
                .ToList();
            if (relations.Count == 0)
                continue;
            var columns = await service.Columns(c, layout.Table);
            var sources = columns
                .Where(col =>
                    Sumups.Numeric(col.Type)
                    && !fields.Any(f => f.Name == col.Name && f.Widget == "sumup")
                )
                .Select(col => new
                {
                    name = col.Name,
                    label = fields.Find(f => f.Name == col.Name)?.Label ?? col.Name,
                    formula = false,
                })
                .Concat(
                    fields
                        .Where(f => f.Widget == "formula")
                        .Select(f => new
                        {
                            name = f.Name,
                            label = f.Label,
                            formula = true,
                        })
                )
                .ToList();
            foreach (var field in relations)
                result.Add(
                    new
                    {
                        childTable = layout.Table,
                        lookupField = field.Name,
                        label = field.Label,
                        keyColumn = field.Lookup!.KeyColumn,
                        sources,
                    }
                );
        }
        return result;
    }
);
admin.MapPost(
    "/connections/{id:int}/tables/{table}/sumups/recalculate",
    async (int id, string table, AppDb db, DatabaseService service, HttpContext ctx) =>
    {
        await using var c = await service.Open(
            await db.Connections.FindAsync(id) ?? throw new ApiError(404, "Connection not found.")
        );
        await using var gate = await SumupGate.Enter(c);
        var plans = await Sumups.Ready(db, service, c, id);
        var selected = plans.Where(p => p.ParentTable == table).ToList();
        if (selected.Count == 0)
            throw new ApiError(400, "Save at least one sum-up on this layout first.");
        await using var tx = await c.BeginTransactionAsync();
        await service.RebuildSumups(c, tx, selected);
        await tx.CommitAsync();
        db.Audit.Add(
            new()
            {
                Actor = ctx.User.Identity!.Name!,
                Action = "recalculate sumups",
                Resource = $"{id}/{table}",
            }
        );
        await db.SaveChangesAsync();
        return Results.Ok(new
        {
            fields = selected.Count
        });
    }
);
PageEndpoints.Map(app);
app.Run();
static void ValidateUser(UserInput i, bool create)
{
    if (string.IsNullOrWhiteSpace(i.Username) || i.Username.Length > 100)
        throw new ApiError(400, "Username required (maximum 100 characters).");
    if ((create || !string.IsNullOrEmpty(i.Password)) && (i.Password?.Length ?? 0) < 14)
        throw new ApiError(400, "Passwords must contain at least 14 characters.");
}
static void SetConnection(DatabaseConnection c, ConnectionInput i, IDataProtectionProvider p)
{
    if (
        new[] { i.Name, i.Host, i.Database, i.Username }.Any(string.IsNullOrWhiteSpace)
        || i.Port is 0 or > 65535
    )
        throw new ApiError(400, "Complete all connection fields with a valid port.");
    if (c.Id == 0 && i.Password == null)
        throw new ApiError(400, "Password required.");
    c.Name = i.Name;
    c.Host = i.Host;
    c.Port = i.Port;
    c.Database = i.Database;
    c.Username = i.Username;
    c.VerifyTls = i.VerifyTls;
    if (i.Password != null)
        c.ProtectedPassword = p.CreateProtector("database-passwords").Protect(i.Password);
}

public partial class Program
{
}

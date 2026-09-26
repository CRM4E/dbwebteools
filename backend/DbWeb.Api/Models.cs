using Microsoft.EntityFrameworkCore;
using System.Text.Json.Serialization;

namespace DbWeb.Api;

public class AppUser
{
    public int Id
    {
        get; set;
    }
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public bool IsAdmin
    {
        get; set;
    }
    public bool Enabled { get; set; } = true;
    public string Stamp { get; set; } = Guid.NewGuid().ToString();
}

public class DatabaseConnection
{
    public int Id
    {
        get; set;
    }
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public uint Port { get; set; } = 3306;
    public string Database { get; set; } = "";
    public string Username { get; set; } = "";
    public string ProtectedPassword { get; set; } = "";
    public bool VerifyTls { get; set; } = true;
}

public class TableGrant
{
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public Dictionary<string, string>? Fields
    {
        get; set;
    }
    public int Id
    {
        get; set;
    }
    public int UserId
    {
        get; set;
    }
    public int ConnectionId
    {
        get; set;
    }
    public string Table { get; set; } = "";
    public bool Read
    {
        get; set;
    }
    public bool Create
    {
        get; set;
    }
    public bool Update
    {
        get; set;
    }
    public bool Delete
    {
        get; set;
    }
}

public class RecordLayout
{
    public int Id
    {
        get; set;
    }
    public int ConnectionId
    {
        get; set;
    }
    public string Table { get; set; } = "";
    public string FieldsJson { get; set; } = "[]";
}

public record LayoutField(
    string Name,
    string Label,
    string Section,
    int Order,
    bool Hidden,
    bool ReadOnly,
    string Widget,
    LookupConfig? Lookup = null,
    List<DropdownOption>? Options = null,
    bool ShowInList = true,
    int? ListOrder = null,
    JoinConfig? Join = null,
    bool Required = false,
    string? Formula = null,
    SumupConfig? Sumup = null,
    CreationDefault? CreationDefault = null,
    InputMask? Mask = null
)
{
    public List<string>? EnabledOptionKeys
    {
        get; init;
    }
}

public class AuditEntry
{
    public long Id
    {
        get; set;
    }
    public DateTime At { get; set; } = DateTime.UtcNow;
    public string Actor { get; set; } = "";
    public string Action { get; set; } = "";
    public string Resource { get; set; } = "";
}

public class AppDb(DbContextOptions<AppDb> options) : DbContext(options)
{
    public DbSet<FieldPolicy> FieldPolicies => Set<FieldPolicy>();
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<DatabaseConnection> Connections => Set<DatabaseConnection>();
    public DbSet<TableGrant> Grants => Set<TableGrant>();
    public DbSet<RecordLayout> Layouts => Set<RecordLayout>();
    public DbSet<AuditEntry> Audit => Set<AuditEntry>();
    public DbSet<PageConfiguration> Pages => Set<PageConfiguration>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<FieldPolicy>()
            .HasIndex(p => new
            {
                p.UserId,
                p.ConnectionId,
                p.Table,
            })
            .IsUnique();
        b.Entity<PageConfiguration>()
            .HasIndex(x => new
            {
                x.ConnectionId,
                x.Table,
                x.LinkColumn,
            })
            .IsUnique();
        b.Entity<AppUser>().HasIndex(x => x.Username).IsUnique();
        b.Entity<TableGrant>()
            .HasIndex(x => new
            {
                x.UserId,
                x.ConnectionId,
                x.Table,
            })
            .IsUnique();
        b.Entity<RecordLayout>().HasIndex(x => new { x.ConnectionId, x.Table }).IsUnique();
    }
}

public record LoginInput(string Username, string Password);

public record UserInput(string Username, string? Password, bool IsAdmin, bool Enabled);

public record ConnectionInput(
    string Name,
    string Host,
    uint Port,
    string Database,
    string Username,
    string? Password,
    bool VerifyTls
);

public record RowMutation(
    Dictionary<string, System.Text.Json.JsonElement> Values,
    Dictionary<string, System.Text.Json.JsonElement>? Key,
    string? Version
);

public class ApiError(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

public record LookupConfig(
    string Table,
    string KeyColumn,
    string DisplayColumn,
    List<string> SearchColumns,
    List<LookupCopyMapping>? CopyMappings = null,
    ListView? Criteria = null
);

public record LookupCopyMapping(
    string SourceColumn,
    string DestinationColumn,
    bool Editable = false
);

public record LookupCopyInput(System.Text.Json.JsonElement Key);

public record RecordRow(Dictionary<string, object?> Values, string Version)
{
    public string? KeyToken
    {
        get; init;
    }
    public Dictionary<string, string?> DisplayValues { get; } = new();
    public Dictionary<string, object?> JoinedValues { get; } = new();
    public Dictionary<string, string> CalculationErrors { get; } = new();
}

public record RecordPage(
    long Total,
    int Page,
    int Size,
    List<ColumnInfo> Columns,
    List<RecordRow> Rows
)
{
    public bool HasPrimaryKey
    {
        get; set;
    }
    public List<ColumnInfo> JoinedColumns { get; } = new();
    public string? Sort
    {
        get; init;
    }
    public bool Descending
    {
        get; init;
    }
}

public record DropdownOption(string Key, string Display);

public record JoinConfig(string SourceColumn, string Table, string KeyColumn, string ValueColumn);

public record JoinInput(
    Dictionary<string, System.Text.Json.JsonElement> Values,
    List<string>? ChangedFields = null
);

public record FormulaValidationInput(string? Formula, List<LayoutField>? Fields);

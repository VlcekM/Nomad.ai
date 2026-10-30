using Microsoft.EntityFrameworkCore;

namespace Nomad.Hub.Data;

public sealed class HubDb(DbContextOptions<HubDb> options) : DbContext(options)
{
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<EnrollToken> EnrollTokens => Set<EnrollToken>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<ProjectFolder> ProjectFolders => Set<ProjectFolder>();
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<SessionVersion> SessionVersions => Set<SessionVersion>();
    public DbSet<SessionFile> SessionFiles => Set<SessionFile>();
    public DbSet<SessionState> SessionStates => Set<SessionState>();
    public DbSet<UsageRecord> UsageRecords => Set<UsageRecord>();
    public DbSet<Counter> Counters => Set<Counter>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Device>().HasIndex(d => d.SecretHash).IsUnique();
        b.Entity<EnrollToken>().HasIndex(t => t.TokenHash).IsUnique();
        b.Entity<Project>().HasIndex(p => p.Key).IsUnique();
        b.Entity<ProjectFolder>().HasKey(f => new { f.ProjectId, f.DeviceId });

        b.Entity<Session>().HasIndex(s => new { s.Backend, s.SessionId }).IsUnique();
        b.Entity<Session>().HasIndex(s => s.Seq);
        b.Entity<SessionVersion>().HasIndex(v => new { v.SessionPk, v.Version }).IsUnique();
        b.Entity<SessionFile>().HasIndex(f => f.Hash);
        b.Entity<SessionVersion>().HasMany(v => v.Files).WithOne().HasForeignKey(f => f.VersionId).OnDelete(DeleteBehavior.Cascade);

        b.Entity<SessionState>().HasKey(s => new { s.SessionPk, s.Key });
        b.Entity<SessionState>().HasIndex(s => s.Seq);

        b.Entity<UsageRecord>().HasKey(u => new { u.Backend, u.SessionId, u.MessageId });
        b.Entity<UsageRecord>().HasIndex(u => u.AtUtc);

        b.Entity<Counter>().HasKey(c => c.Name);
        b.Entity<Counter>().HasData(new Counter { Name = Counter.Seq, Value = 0 });
    }
}

public sealed class Device
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public required string SecretHash { get; set; }
    public DateTime EnrolledUtc { get; set; }
    public DateTime LastSeenUtc { get; set; }
    public bool Revoked { get; set; }
}

public sealed class EnrollToken
{
    public int Id { get; set; }
    public required string TokenHash { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime ExpiresUtc { get; set; }
    public DateTime? UsedUtc { get; set; }
    public Guid? CreatedBy { get; set; }
}

public sealed class Project
{
    public Guid Id { get; set; }
    public required string Key { get; set; }
    public required string Name { get; set; }
    public DateTime CreatedUtc { get; set; }
}

public sealed class ProjectFolder
{
    public Guid ProjectId { get; set; }
    public Guid DeviceId { get; set; }
    public required string Path { get; set; }
}

public sealed class Session
{
    public int Id { get; set; }
    public required string Backend { get; set; }
    public required string SessionId { get; set; }
    public Guid? ProjectId { get; set; }
    public string Title { get; set; } = "";
    public string? WorkingDirectory { get; set; }
    public string? GitBranch { get; set; }
    public DateTime LastActivityUtc { get; set; }
    public long Size { get; set; }
    public int Version { get; set; }
    public Guid UpdatedBy { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public long Seq { get; set; }
    public bool Deleted { get; set; }
    public Guid? LeaseDevice { get; set; }
    public DateTime? LeaseExpiresUtc { get; set; }
}

public sealed class SessionVersion
{
    public int Id { get; set; }
    public int SessionPk { get; set; }
    public int Version { get; set; }
    public Guid Device { get; set; }
    public DateTime CreatedUtc { get; set; }
    public List<SessionFile> Files { get; set; } = [];
}

public sealed class SessionFile
{
    public int Id { get; set; }
    public int VersionId { get; set; }
    public required string Path { get; set; }
    public required string Hash { get; set; }
    public long Length { get; set; }
}

public sealed class SessionState
{
    public int SessionPk { get; set; }
    public required string Key { get; set; }
    public string? Value { get; set; }
    public DateTime SetUtc { get; set; }
    public Guid Device { get; set; }
    public long Seq { get; set; }
}

public sealed class UsageRecord
{
    public required string Backend { get; set; }
    public required string SessionId { get; set; }
    public required string MessageId { get; set; }
    public Guid Device { get; set; }
    public string? Provider { get; set; }
    public string? Model { get; set; }
    public DateTime AtUtc { get; set; }
    public long Input { get; set; }
    public long Output { get; set; }
    public long Reasoning { get; set; }
    public long CacheRead { get; set; }
    public long CacheWrite { get; set; }
    public double? ReportedUsd { get; set; }
}

/// <summary>Named counters; <see cref="Seq"/> numbers every change for the change feed.</summary>
public sealed class Counter
{
    public const string Seq = "seq";
    public required string Name { get; set; }
    public long Value { get; set; }
}

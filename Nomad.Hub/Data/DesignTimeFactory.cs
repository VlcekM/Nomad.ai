using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Nomad.Hub.Data;

/// <summary>For <c>dotnet ef</c> only, so it never runs the app's startup.</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<HubDb>
{
    public HubDb CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<HubDb>().UseSqlite("Data Source=design-time.db").Options);
}

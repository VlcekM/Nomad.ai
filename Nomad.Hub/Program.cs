using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.EntityFrameworkCore;
using Nomad.Hub;
using Nomad.Hub.Contracts;
using Nomad.Hub.Data;
using Nomad.Hub.Services;

var builder = WebApplication.CreateBuilder(args);

var options = builder.Configuration.GetSection("Hub").Get<HubOptions>() ?? new HubOptions();
Directory.CreateDirectory(options.DataPath);
builder.Services.AddSingleton(options);
builder.Services.AddDbContext<HubDb>(o => o.UseSqlite($"Data Source={Path.Combine(options.DataPath, "hub.db")}"));
builder.Services.AddSingleton<BlobStore>();
builder.Services.AddSingleton<Presence>();
builder.Services.AddSingleton<ChangeLog>();
builder.Services.AddSingleton<RelayState>();
builder.Services.AddSingleton<Maintenance>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Maintenance>());

builder.Services.AddAuthentication(DeviceAuthHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, DeviceAuthHandler>(DeviceAuthHandler.SchemeName, null);
builder.Services.AddAuthorization();
builder.Services.AddSignalR(o => o.MaximumReceiveMessageSize = HubNames.MaxMessageBytes).AddJsonProtocol(o => o.PayloadSerializerOptions.TypeInfoResolverChain.Insert(0, HubJson.Default));
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.TypeInfoResolverChain.Insert(0, HubJson.Default));
builder.Services.AddRequestDecompression();
builder.Services.AddResponseCompression(o =>
{
    o.Providers.Add<GzipCompressionProvider>();
    o.MimeTypes = ResponseCompressionDefaults.MimeTypes.Append("application/octet-stream");
});
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 4L * 1024 * 1024 * 1024);

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<HubDb>();
    await db.Database.MigrateAsync();
    await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
    if (!await db.Devices.AnyAsync(d => !d.Revoked))
    {
        // Only hashes are stored, so a hub with no devices yet prints a fresh token at every start.
        var token = await DeviceEndpoints.NewEnrollTokenAsync(db, null);
        app.Logger.LogWarning("No devices enrolled yet. Enrollment token (valid until {Expires:u}): {Token}", token.ExpiresUtc, token.Token);
    }
}

app.UseRequestDecompression();
app.UseResponseCompression();
app.UseAuthentication();
app.UseAuthorization();

DeviceEndpoints.MapAnonymous(app);
var api = app.MapGroup("/api").RequireAuthorization();
DeviceEndpoints.Map(api);
BlobEndpoints.Map(api);
Sessions.Map(api);
UsageEndpoints.Map(api);
app.MapHub<NomadHub>(HubNames.HubPath);

app.Run();

public partial class Program;

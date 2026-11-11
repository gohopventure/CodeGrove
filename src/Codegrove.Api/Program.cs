using System.Text.Json.Serialization;
using Codegrove.Api;
using Codegrove.Api.Data;
using Codegrove.Api.Data.Seeding;
using Codegrove.Api.Infrastructure;
using Codegrove.Api.Services;
using Codegrove.Api.Services.Email;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

// ---- Options ------------------------------------------------------------------------------------
var appOptions = config.GetSection("App").Get<AppOptions>() ?? new AppOptions();
builder.Services.AddSingleton(appOptions);
builder.Services.AddSingleton(TimeProvider.System);

// ---- Data ---------------------------------------------------------------------------------------
// SQL Server in every deployed environment. SQLite is supported for quick local runs without
// Docker and for integration tests; it uses EnsureCreated instead of migrations.
var provider = config["Database:Provider"] ?? "SqlServer";
var connectionString = config.GetConnectionString("Default")
    ?? throw new InvalidOperationException("ConnectionStrings:Default is not set.");

builder.Services.AddDbContext<AppDbContext>(o =>
{
    if (provider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase)) o.UseSqlite(connectionString);
    else o.UseSqlServer(connectionString);
});

// ---- Auth, limits, API --------------------------------------------------------------------------
builder.Services.AddAppAuth(builder.Environment);
builder.Services.AddAppRateLimits(config);
builder.Services.AddMemoryCache();
builder.Services.AddProblemDetails();
builder.Services
    .AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// App Service terminates TLS and forwards; trust its headers so rate limits see the real client IP.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

// Telemetry: first-party only, and only when configured. Application Insights doesn't store
// client IPs by default; we never add usernames or code to telemetry.
if (!string.IsNullOrEmpty(config["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
    builder.Services.AddApplicationInsightsTelemetry();

// ---- Domain services ----------------------------------------------------------------------------
builder.Services.AddSingleton<IContentFilter, ContentFilter>();
builder.Services.AddScoped<ConsentService>();
builder.Services.AddScoped<ProgressService>();
builder.Services.AddScoped<ActivityService>();
builder.Services.AddScoped<ContentSeeder>();
builder.Services.AddScoped<IdentitySeeder>();
if (config.GetValue("Jobs:Enabled", true))
    builder.Services.AddHostedService<ConsentCleanupJob>();

var acs = config.GetSection("Email:Acs").Get<AcsEmailOptions>();
if (!string.IsNullOrEmpty(acs?.ConnectionString))
    builder.Services.AddSingleton<IEmailSender>(sp => new AcsEmailSender(acs, sp.GetRequiredService<ILogger<AcsEmailSender>>()));
else
    builder.Services.AddSingleton<IEmailSender, LoggingEmailSender>();

var app = builder.Build();

// ---- Database setup and seeding -----------------------------------------------------------------
await InitializeDatabaseAsync(app);

// ---- Pipeline -----------------------------------------------------------------------------------
app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler();
    app.UseHsts();
}
app.UseStatusCodePages();

app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h["X-Content-Type-Options"] = "nosniff";
    h["Referrer-Policy"] = "no-referrer";
    h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
    // The SPA may only frame the preview origin, where learner code runs.
    h["Content-Security-Policy"] =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob: " +
        $"{appOptions.MediaOrigin}; font-src 'self' data:; worker-src 'self' blob:; connect-src 'self'; " +
        $"frame-src {appOptions.PreviewOrigin}; object-src 'none'; base-uri 'self'; frame-ancestors 'none'";
    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseMiddleware<RequireJsonMiddleware>();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapGet("/api/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
app.Map("/api/{**rest}", () => Results.NotFound()); // unknown API routes never fall through to the SPA
app.MapFallbackToFile("index.html");

app.Run();

static async Task InitializeDatabaseAsync(WebApplication app)
{
    using var scope = app.Services.CreateScope();
    var sp = scope.ServiceProvider;
    var cfg = app.Configuration;
    var db = sp.GetRequiredService<AppDbContext>();

    if (db.Database.IsSqlite()) await db.Database.EnsureCreatedAsync();
    else if (cfg.GetValue("Database:MigrateOnStartup", false)) await db.Database.MigrateAsync();

    var identity = sp.GetRequiredService<IdentitySeeder>();
    await identity.SeedRolesAsync();

    if (cfg.GetValue("Seed:Content", true))
        await sp.GetRequiredService<ContentSeeder>().SeedAsync(Path.Combine(app.Environment.ContentRootPath, "Content"));

    if (app.Environment.IsDevelopment() && cfg.GetValue("Seed:DemoAccounts", true))
        await identity.SeedDemoAccountsAsync();
}

/// <summary>Exposed so integration tests can use WebApplicationFactory&lt;Program&gt;.</summary>
public partial class Program;

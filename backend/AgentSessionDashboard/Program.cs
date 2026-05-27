using System.Text.Json;
using AgentSessionDashboard.BackgroundServices;
using AgentSessionDashboard.Data;
using AgentSessionDashboard.Services;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// ── SQLite / EF Core ──────────────────────────────────────────────────────────
var dbPath = Environment.GetEnvironmentVariable("DB_PATH")
    ?? Path.Combine(AppContext.BaseDirectory, "sessions.db");

builder.Services.AddDbContext<SessionDbContext>(options =>
    options.UseSqlite($"Data Source={dbPath}"));

// ── Application services ──────────────────────────────────────────────────────
builder.Services.AddScoped<SessionService>();
builder.Services.AddScoped<ShortcutService>();
builder.Services.AddSingleton<SseService>();
builder.Services.AddHostedService<SessionStatusWorker>();
builder.Services.AddHttpClient("scraper");

// ── CORS — allow any origin (Angular dev server) ──────────────────────────────
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod());
});

// ── JSON serialisation ────────────────────────────────────────────────────────
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
});

var app = builder.Build();

// ── Ensure schema is created ──────────────────────────────────────────────────
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<SessionDbContext>();
    db.Database.EnsureCreated();
    // Add columns that may not exist on older DBs
    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Sessions\" ADD COLUMN \"Branch\" TEXT NULL"); } catch { }
    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Sessions\" ADD COLUMN \"LastActivity\" TEXT NULL"); } catch { }
    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Sessions\" ADD COLUMN \"Worktree\" TEXT NULL"); } catch { }
    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Sessions\" ADD COLUMN \"TokensIn\" INTEGER NOT NULL DEFAULT 0"); } catch { }
    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Sessions\" ADD COLUMN \"TokensOut\" INTEGER NOT NULL DEFAULT 0"); } catch { }
    db.Database.ExecuteSqlRaw("""
        CREATE TABLE IF NOT EXISTS "SessionEvents" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_SessionEvents" PRIMARY KEY AUTOINCREMENT,
            "SessionId" TEXT NOT NULL,
            "Timestamp" TEXT NOT NULL,
            "Message" TEXT NOT NULL
        )
    """);
    db.Database.ExecuteSqlRaw("""
        CREATE INDEX IF NOT EXISTS "IX_SessionEvents_SessionId" ON "SessionEvents" ("SessionId")
    """);
    db.Database.ExecuteSqlRaw("""
        CREATE TABLE IF NOT EXISTS "Shortcuts" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_Shortcuts" PRIMARY KEY AUTOINCREMENT,
            "Url" TEXT NOT NULL,
            "Name" TEXT NOT NULL,
            "Order" INTEGER NOT NULL DEFAULT 0
        )
    """);
}

// ── Middleware ────────────────────────────────────────────────────────────────
app.UseCors();

// Angular 20 outputs to wwwroot/browser/ — serve static files from there
var browserRoot = Path.Combine(app.Environment.WebRootPath ?? "wwwroot", "browser");
if (Directory.Exists(browserRoot))
{
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(browserRoot),
        RequestPath = ""
    });
}
else
{
    app.UseStaticFiles();
}

// ── API endpoints ─────────────────────────────────────────────────────────────

// POST /api/sessions/event — receives Claude Code hook payloads.
// Deserializes manually so that malformed JSON never causes a 400 —
// Claude hooks must NEVER be blocked.
var hookJsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
app.MapPost("/api/sessions/event", async (
    HttpRequest request,
    SessionService sessionService,
    ILogger<Program> logger) =>
{
    try
    {
        var payload = await JsonSerializer.DeserializeAsync<HookPayload>(request.Body, hookJsonOptions);
        if (payload is not null)
        {
            await sessionService.UpsertFromHookAsync(payload);
        }
    }
    catch (Exception ex)
    {
        // Always return 200 — hooks must never be blocked
        logger.LogError(ex, "Error processing hook event.");
    }
    return Results.Ok();
});

// GET /api/sessions — returns all sessions not older than 7 days
app.MapGet("/api/sessions", async (SessionService sessionService) =>
{
    var sessions = await sessionService.GetActiveSessionsAsync();
    return Results.Ok(sessions);
});

// DELETE /api/sessions/{id}
app.MapDelete("/api/sessions/{id}", async (string id, SessionService sessionService) =>
{
    var deleted = await sessionService.DeleteSessionAsync(id);
    return deleted ? Results.Ok() : Results.NotFound();
});

// GET /api/sessions/stream — SSE endpoint
app.MapGet("/api/sessions/stream", async (
    HttpContext httpContext,
    SseService sseService,
    ILogger<Program> logger) =>
{
    var response = httpContext.Response;
    var cancellationToken = httpContext.RequestAborted;

    response.Headers.ContentType = "text/event-stream";
    response.Headers.CacheControl = "no-cache";
    response.Headers.Append("X-Accel-Buffering", "no");
    response.Headers.Append("Connection", "keep-alive");

    // Disable response buffering so events are flushed immediately
    httpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

    var (clientId, reader) = sseService.AddClient();
    logger.LogInformation("SSE client {ClientId} connected. Total: {Count}", clientId, sseService.ClientCount);

    try
    {
        await foreach (var message in reader.ReadAllAsync(cancellationToken))
        {
            await response.WriteAsync(message, cancellationToken);
            await response.Body.FlushAsync(cancellationToken);
        }
    }
    catch (OperationCanceledException)
    {
        // Client disconnected — expected
        logger.LogInformation("SSE client {ClientId} disconnected.", clientId);
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "SSE client {ClientId} error.", clientId);
    }
    finally
    {
        sseService.RemoveClient(clientId);
        logger.LogInformation("SSE client {ClientId} removed. Total: {Count}", clientId, sseService.ClientCount);
    }
});

// GET /api/sessions/{id}/events — last 50 activity events for a session
app.MapGet("/api/sessions/{id}/events", async (string id, SessionDbContext db) =>
{
    var events = await db.SessionEvents
        .Where(e => e.SessionId == id)
        .OrderByDescending(e => e.Timestamp)
        .Take(50)
        .OrderBy(e => e.Timestamp)
        .ToListAsync();
    return Results.Ok(events);
});

// ── Shortcuts endpoints ───────────────────────────────────────────────────────

// GET /api/shortcuts
app.MapGet("/api/shortcuts", async (ShortcutService shortcutService) =>
{
    var shortcuts = await shortcutService.GetAllAsync();
    return Results.Ok(shortcuts);
});

// POST /api/shortcuts
app.MapPost("/api/shortcuts", async (CreateShortcutRequest body, ShortcutService shortcutService) =>
{
    var shortcut = await shortcutService.CreateAsync(body.Url, body.Name);
    return Results.Created($"/api/shortcuts/{shortcut.Id}", shortcut);
});

// PATCH /api/shortcuts/{id}/name
app.MapMethods("/api/shortcuts/{id}/name", ["PATCH"], async (int id, UpdateNameRequest body, ShortcutService shortcutService) =>
{
    var shortcut = await shortcutService.UpdateNameAsync(id, body.Name);
    return shortcut is not null ? Results.Ok(shortcut) : Results.NotFound();
});

// PATCH /api/shortcuts/reorder
app.MapMethods("/api/shortcuts/reorder", ["PATCH"], async (List<int> orderedIds, ShortcutService shortcutService) =>
{
    await shortcutService.ReorderAsync(orderedIds);
    return Results.Ok();
});

// DELETE /api/shortcuts/{id}
app.MapDelete("/api/shortcuts/{id}", async (int id, ShortcutService shortcutService) =>
{
    var deleted = await shortcutService.DeleteAsync(id);
    return deleted ? Results.Ok() : Results.NotFound();
});

// ── SPA fallback — serve index.html for non-API GET requests ─────────────────
app.MapFallback(async (HttpContext httpContext) =>
{
    // Only handle GET; all other verbs on unmatched routes → 404
    if (httpContext.Request.Method != HttpMethods.Get
        || httpContext.Request.Path.StartsWithSegments("/api"))
    {
        httpContext.Response.StatusCode = 404;
        return;
    }

    var webRoot = app.Environment.WebRootPath ?? "wwwroot";
    var indexPath = Path.Combine(webRoot, "browser", "index.html");
    if (!File.Exists(indexPath))
        indexPath = Path.Combine(webRoot, "index.html");
    if (File.Exists(indexPath))
    {
        httpContext.Response.ContentType = "text/html";
        await httpContext.Response.SendFileAsync(indexPath);
    }
    else
    {
        httpContext.Response.StatusCode = 404;
    }
});

app.Run();

// ── Request DTOs ──────────────────────────────────────────────────────────────
record CreateShortcutRequest(string Url, string? Name);
record UpdateNameRequest(string Name);

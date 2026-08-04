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
builder.Services.AddScoped<QuestionService>();
builder.Services.AddScoped<JobRunService>();
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
    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Sessions\" ADD COLUMN \"Model\" TEXT NULL"); } catch { }
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
    // Decision inbox — see docs/DECISION-INBOX.md. The unique index on
    // Fingerprint is the load-bearing part: it makes duplicate questions
    // impossible rather than merely unlikely.
    db.Database.ExecuteSqlRaw("""
        CREATE TABLE IF NOT EXISTS "Questions" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_Questions" PRIMARY KEY AUTOINCREMENT,
            "Fingerprint" TEXT NOT NULL,
            "Source" TEXT NOT NULL,
            "SubjectId" TEXT NULL,
            "DetectionType" TEXT NOT NULL,
            "DetectionKey" TEXT NULL,
            "Title" TEXT NOT NULL,
            "Body" TEXT NOT NULL,
            "Options" TEXT NOT NULL DEFAULT '[]',
            "Recommendation" TEXT NULL,
            "PinnedDelete" TEXT NULL,
            "SubjectHash" TEXT NULL,
            "State" TEXT NOT NULL DEFAULT 'open',
            "AnswerKey" TEXT NULL,
            "AnswerText" TEXT NULL,
            "AnsweredAt" TEXT NULL,
            "AppliedAt" TEXT NULL,
            "DismissedAt" TEXT NULL,
            "AppliedOps" TEXT NULL,
            "History" TEXT NULL,
            "Note" TEXT NULL,
            "CreatedAt" TEXT NOT NULL
        )
    """);
    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Questions\" ADD COLUMN \"Recommendation\" TEXT NULL"); } catch { }
    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Questions\" ADD COLUMN \"DetectionKey\" TEXT NULL"); } catch { }
    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Questions\" ADD COLUMN \"DismissedAt\" TEXT NULL"); } catch { }
    db.Database.ExecuteSqlRaw("""
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_Questions_Fingerprint" ON "Questions" ("Fingerprint")
    """);
    db.Database.ExecuteSqlRaw("""
        CREATE TABLE IF NOT EXISTS "JobRuns" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_JobRuns" PRIMARY KEY AUTOINCREMENT,
            "JobName" TEXT NOT NULL,
            "StartedAt" TEXT NOT NULL,
            "FinishedAt" TEXT NULL,
            "Outcome" TEXT NULL,
            "Detail" TEXT NULL,
            "Created" INTEGER NOT NULL DEFAULT 0,
            "Applied" INTEGER NOT NULL DEFAULT 0
        )
    """);
    db.Database.ExecuteSqlRaw("""
        CREATE INDEX IF NOT EXISTS "IX_JobRuns_JobName_StartedAt" ON "JobRuns" ("JobName", "StartedAt")
    """);
    // Manual "run now" requests. The container cannot start a host process, so this
    // table is the only channel between the button and the job's own runner.
    db.Database.ExecuteSqlRaw("""
        CREATE TABLE IF NOT EXISTS "JobRequests" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_JobRequests" PRIMARY KEY AUTOINCREMENT,
            "JobName" TEXT NOT NULL,
            "RequestedAt" TEXT NOT NULL,
            "ConsumedAt" TEXT NULL
        )
    """);
    db.Database.ExecuteSqlRaw("""
        CREATE INDEX IF NOT EXISTS "IX_JobRequests_JobName_ConsumedAt" ON "JobRequests" ("JobName", "ConsumedAt")
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

// GET /api/version — returns the git SHA baked in at build time
var gitSha = Environment.GetEnvironmentVariable("GIT_SHA") ?? "unknown";
app.MapGet("/api/version", () => Results.Ok(new { sha = gitSha }));

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

// DELETE /api/sessions — purge all closed sessions
app.MapDelete("/api/sessions", async (SessionService sessionService) =>
{
    var count = await sessionService.DeleteAllClosedAsync();
    return Results.Ok(new { deleted = count });
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

// ── Decision inbox endpoints ──────────────────────────────────────────────────
// The store is domain-agnostic: it holds questions and answers and knows nothing
// about Linear. The job that produces and applies them runs on the host, because
// this process is containerised and cannot reach `claude` or `linctl`.
// See docs/DECISION-INBOX.md.

// GET /api/questions[?state=open,answered] — card list, and the job's pre-flight
app.MapGet("/api/questions", async (string? state, QuestionService questions) =>
{
    var list = await questions.GetAsync(state);
    return Results.Ok(list.Select(SseService.QuestionPayload));
});

// POST /api/questions — idempotent ask, keyed on fingerprint
app.MapPost("/api/questions", async (UpsertQuestionRequest body, QuestionService questions) =>
{
    if (string.IsNullOrWhiteSpace(body.Source)
        || string.IsNullOrWhiteSpace(body.DetectionType)
        || string.IsNullOrWhiteSpace(body.Title))
    {
        return Results.BadRequest(new { error = "source, detectionType and title are required" });
    }

    var (question, disposition) = await questions.UpsertAsync(body);
    if (disposition == "at-cap")
    {
        return Results.Json(
            new { disposition, cap = QuestionService.OpenQuestionCap },
            statusCode: StatusCodes.Status409Conflict);
    }

    return Results.Ok(new
    {
        disposition,
        question = question is null ? null : SseService.QuestionPayload(question)
    });
});

// POST /api/questions/{id}/answer — from the card UI
app.MapPost("/api/questions/{id:int}/answer", async (int id, AnswerRequest body, QuestionService questions) =>
{
    if (string.IsNullOrWhiteSpace(body.Key))
        return Results.BadRequest(new { error = "key is required" });

    var q = await questions.AnswerAsync(id, body.Key, body.Text);
    return q is null ? Results.NotFound() : Results.Ok(SseService.QuestionPayload(q));
});

// POST /api/questions/{id}/dismiss — closes with no downstream write
app.MapPost("/api/questions/{id:int}/dismiss", async (int id, QuestionService questions) =>
{
    var q = await questions.DismissAsync(id);
    return q is null ? Results.NotFound() : Results.Ok(SseService.QuestionPayload(q));
});

// POST /api/questions/{id}/applied — records what ran, rebaselines subjectHash
app.MapPost("/api/questions/{id:int}/applied", async (int id, AppliedRequest body, QuestionService questions) =>
{
    var q = await questions.MarkAppliedAsync(id, body.Ops, body.SubjectHash);
    return q is null ? Results.NotFound() : Results.Ok(SseService.QuestionPayload(q));
});

// POST /api/questions/{id}/reopen — pre-flight found the subject had moved
app.MapPost("/api/questions/{id:int}/reopen", async (int id, ReopenRequest body, QuestionService questions) =>
{
    var q = await questions.ReopenAsync(id, body.Reason ?? "Subject changed.", body.SubjectHash);
    return q is null ? Results.NotFound() : Results.Ok(SseService.QuestionPayload(q));
});

// POST /api/questions/{id}/failed — apply blew up; surfaces on the card
app.MapPost("/api/questions/{id:int}/failed", async (int id, FailedRequest body, QuestionService questions) =>
{
    var q = await questions.MarkFailedAsync(id, body.Detail ?? "Apply failed.");
    return q is null ? Results.NotFound() : Results.Ok(SseService.QuestionPayload(q));
});

// ── Job heartbeat endpoints ───────────────────────────────────────────────────

// GET /api/jobs/runs?name=decision-inbox&limit=1 — drives the stale indicator
app.MapGet("/api/jobs/runs", async (string? name, int? limit, JobRunService jobRuns) =>
{
    var runs = await jobRuns.GetAsync(name, limit ?? 10);
    return Results.Ok(runs);
});

// POST /api/jobs/runs — the job records its own run in one shot (start and finish
// together), which is still what a no-op cadence window does.
app.MapPost("/api/jobs/runs", async (RecordJobRunRequest body, JobRunService jobRuns) =>
{
    if (string.IsNullOrWhiteSpace(body.JobName))
        return Results.BadRequest(new { error = "jobName is required" });

    var run = await jobRuns.RecordAsync(body);
    return Results.Created($"/api/jobs/runs/{run.Id}", run);
});

// POST /api/jobs/runs/start — opens an in-flight run. A row with no FinishedAt *is*
// the "running" state: the UI disables the button on it, the runner avoids
// overlapping itself with it, and there is no second field to disagree with it.
app.MapPost("/api/jobs/runs/start", async (StartJobRunRequest body, JobRunService jobRuns) =>
{
    if (string.IsNullOrWhiteSpace(body.JobName))
        return Results.BadRequest(new { error = "jobName is required" });

    var run = await jobRuns.StartAsync(body.JobName);
    return Results.Created($"/api/jobs/runs/{run.Id}", run);
});

// POST /api/jobs/runs/{id}/finish — closes it with the outcome
app.MapPost("/api/jobs/runs/{id:int}/finish", async (int id, RecordJobRunRequest body, JobRunService jobRuns) =>
{
    var run = await jobRuns.FinishAsync(id, body);
    return run is null ? Results.NotFound() : Results.Ok(run);
});

// ── Manual trigger ───────────────────────────────────────────────────────────
// The dashboard cannot start a host process, so it cannot run the job. It can only
// ask, and the runner — which ticks every minute — picks the request up.

// GET /api/jobs/requests?name=decision-inbox — pending presses, so a reloaded tab
// still shows "requested" rather than forgetting
app.MapGet("/api/jobs/requests", async (string? name, JobRunService jobRuns) =>
{
    var pending = await jobRuns.GetPendingRequestsAsync(name ?? "decision-inbox");
    return Results.Ok(pending);
});

// POST /api/jobs/requests — the button
app.MapPost("/api/jobs/requests", async (StartJobRunRequest body, JobRunService jobRuns) =>
{
    if (string.IsNullOrWhiteSpace(body.JobName))
        return Results.BadRequest(new { error = "jobName is required" });

    var request = await jobRuns.RequestAsync(body.JobName);
    return Results.Created($"/api/jobs/requests/{request.Id}", request);
});

// POST /api/jobs/requests/claim — the runner, at the START of a run. Claiming at the
// start rather than the end is deliberate: a press that arrives mid-run must survive
// to trigger the next tick, not be cleared by the run it was too late for.
app.MapPost("/api/jobs/requests/claim", async (StartJobRunRequest body, JobRunService jobRuns) =>
{
    if (string.IsNullOrWhiteSpace(body.JobName))
        return Results.BadRequest(new { error = "jobName is required" });

    var claimed = await jobRuns.ClaimRequestsAsync(body.JobName);
    return Results.Ok(new { claimed });
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

// Decision inbox
record AnswerRequest(string Key, string? Text);
record AppliedRequest(System.Text.Json.Nodes.JsonNode? Ops, string? SubjectHash);
record ReopenRequest(string? Reason, string? SubjectHash);
record FailedRequest(string? Detail);

using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using VehicleCounter.Api.Models;
using VehicleCounter.Api.Services;

var builder = WebApplication.CreateBuilder(args);
if (args.Contains("--verify-runtime")) { RuntimeCheck.Verify(builder.Environment, builder.Configuration); return; }
if (int.TryParse(Environment.GetEnvironmentVariable("PORT"), out var port))
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
var origins = builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? [];
builder.Services.AddCors(options => options.AddPolicy("Dashboard", policy =>
{
    if (origins.Length > 0) policy.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod();
}));
builder.Services.AddSingleton<JobAccess>();
const long maxUpload = 500L * 1024 * 1024;
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = maxUpload + 1024 * 1024);
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = maxUpload + 1024 * 1024);
builder.Services.AddSingleton<IVehicleDetector, OnnxVehicleDetector>();
builder.Services.AddSingleton<AnalysisService>();
builder.Services.AddHostedService(p => p.GetRequiredService<AnalysisService>());
var app = builder.Build();
app.UseRouting();
app.UseCors("Dashboard");
app.Use((HttpContext context, RequestDelegate next) => context.RequestServices.GetRequiredService<JobAccess>()
    .Authorize(context, context.RequestServices.GetRequiredService<AnalysisService>(), next));
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapGet("/api/health", (AnalysisService service) => Results.Ok(new
{
    status = "ok", modelReady = service.ModelReady, modelError = service.ModelError,
    model = "YOLO11n / ONNX Runtime CPU", classes = new[] { "car", "motorcycle", "bus", "truck" }
}));
app.MapGet("/api/jobs", (AnalysisService service, JobAccess access, HttpContext context) =>
    Results.Ok(service.List().Where(snapshot => !access.Required || snapshot.OwnerHash == access.Owner(context)).Select(access.PublicSnapshot)));
app.MapGet("/api/jobs/{id:guid}", (Guid id, AnalysisService service, JobAccess access) =>
    service.Find(id) is { } job ? Results.Ok(access.PublicSnapshot(job.Snapshot)) : Results.NotFound(new { error = "Job not found." }));
app.MapGet("/api/jobs/{id:guid}/preview", (Guid id, AnalysisService service, HttpContext context) =>
{
    context.Response.Headers.CacheControl = "no-store";
    return service.Find(id)?.Preview is { } preview ? Results.File(preview, "image/jpeg") : Results.NotFound();
});
app.MapGet("/api/jobs/{id:guid}/video", (Guid id, AnalysisService service) =>
{
    if (service.Find(id) is not { } job) return Results.NotFound();
    if (!job.Snapshot.PlaybackAvailable || !File.Exists(job.InputPath))
        return Results.Problem("The video is unavailable for this older result. Reanalyze it to enable vehicle review.", statusCode: 409);
    var type = Path.GetExtension(job.InputPath).ToLowerInvariant() switch
    {
        ".webm" => "video/webm", ".mov" => "video/quicktime", ".avi" => "video/x-msvideo",
        ".mkv" => "video/x-matroska", _ => "video/mp4"
    };
    return Results.File(job.InputPath, type, enableRangeProcessing: true);
});
app.MapGet("/api/jobs/{id:guid}/vehicles/{trackId:int}/review", (Guid id, int trackId, AnalysisService service, JobAccess access) =>
{
    if (service.Find(id) is not { } job) return Results.NotFound();
    var snapshot = job.Snapshot;
    var vehicle = snapshot.Vehicles?.FirstOrDefault(vehicle => vehicle.TrackId == trackId);
    if (vehicle is null) return Results.NotFound(new { error = "Vehicle not found in this job." });
    var observations = job.GetObservations(trackId);
    if (!snapshot.PlaybackAvailable || !File.Exists(job.InputPath) || snapshot.Video is null || observations.Count == 0)
        return Results.Problem("Reanalyze this older result to enable video navigation and highlighting.", statusCode: 409);
    return Results.Ok(new VehicleReview(id, trackId, snapshot.Video, vehicle, observations, access.Link(id, "video", "video")));
});
app.MapPost("/api/jobs/{id:guid}/cancel", (Guid id, AnalysisService service) =>
{
    if (service.Find(id) is not { } job) return Results.NotFound();
    if (job.Snapshot.Status is "queued" or "processing") job.Cancellation.Cancel();
    return Results.Ok(job.Snapshot);
});
app.MapGet("/api/jobs/{id:guid}/export", (Guid id, AnalysisService service) =>
{
    if (service.Find(id) is not { } job) return Results.NotFound();
    var csv = new StringBuilder("track_id,vehicle_class,direction,video_seconds\r\n");
    foreach (var e in job.Snapshot.Events)
        csv.Append(e.TrackId).Append(',').Append(e.VehicleClass).Append(',').Append(e.Direction).Append(',')
           .Append(e.VideoSeconds.ToString("F3", CultureInfo.InvariantCulture)).Append("\r\n");
    return Results.File(Encoding.UTF8.GetBytes(csv.ToString()), "text/csv", $"vehicle-counts-{id}.csv");
});
app.MapGet("/api/jobs/{id:guid}/vehicles/export", (Guid id, AnalysisService service) =>
{
    if (service.Find(id) is not { } job) return Results.NotFound();
    if (job.Snapshot.Vehicles is not { } vehicles)
        return Results.Problem("Reanalyze this older result to generate the full vehicle list.", statusCode: 409);
    var csv = new StringBuilder("track_id,vehicle_class,crossing_status,direction,first_seen_seconds,last_seen_seconds,crossed_at_seconds,max_confidence,observations\r\n");
    foreach (var vehicle in vehicles)
        csv.Append(vehicle.TrackId).Append(',').Append(vehicle.VehicleClass).Append(',')
           .Append(vehicle.Crossed ? "Crossed" : "Not crossed").Append(',').Append(vehicle.Direction).Append(',')
           .Append(vehicle.FirstSeenSeconds.ToString("F3", CultureInfo.InvariantCulture)).Append(',')
           .Append(vehicle.LastSeenSeconds.ToString("F3", CultureInfo.InvariantCulture)).Append(',')
           .Append(vehicle.CrossedAtSeconds?.ToString("F3", CultureInfo.InvariantCulture)).Append(',')
           .Append(vehicle.MaxConfidence.ToString("F3", CultureInfo.InvariantCulture)).Append(',')
           .Append(vehicle.ObservationCount).Append("\r\n");
    return Results.File(Encoding.UTF8.GetBytes(csv.ToString()), "text/csv", $"vehicle-activity-{id}.csv");
});
app.MapPost("/api/jobs", async (HttpRequest request, HttpContext context, AnalysisService service, JobAccess access, CancellationToken cancellationToken) =>
{
    if (!service.ModelReady) return Results.Problem(service.ModelError ?? "Model is still loading.", statusCode: 503);
    if (!request.HasFormContentType) return Results.BadRequest(new { error = "Send multipart form data with video and settings fields." });
    var form = await request.ReadFormAsync(cancellationToken);
    var file = form.Files.GetFile("video");
    if (file is null || file.Length == 0 || file.Length > maxUpload)
        return Results.BadRequest(new { error = "Choose a non-empty video up to 500 MB." });
    var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
    if (!new[] { ".mp4", ".avi", ".mov", ".mkv", ".webm" }.Contains(extension))
        return Results.BadRequest(new { error = "Supported extensions: MP4, AVI, MOV, MKV, WEBM." });
    AnalysisSettings? settings;
    try { settings = JsonSerializer.Deserialize<AnalysisSettings>(form["settings"].ToString(), new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
    catch (JsonException) { return Results.BadRequest(new { error = "Settings must contain a valid normalized counting line." }); }
    if (settings?.Line is not { } line || !Valid(line.Start) || !Valid(line.End) || line.Length < 0.05 ||
        !float.IsFinite(settings.Confidence) || settings.Confidence is < 0.1f or > 0.95f || settings.FrameStride is < 1 or > 5)
        return Results.BadRequest(new { error = "Line points must be between 0 and 1 and at least 0.05 apart. Confidence: 0.1 to 0.95; frameStride: 1 to 5." });
    Directory.CreateDirectory(service.DataDirectory);
    var id = Guid.NewGuid();
    var path = Path.Combine(service.DataDirectory, $"{id}{extension}");
    try
    {
        await using (var stream = File.Create(path)) await file.CopyToAsync(stream, cancellationToken);
        var job = new JobState(id, Path.GetFileName(file.FileName), path, settings, access.Owner(context));
        if (!service.TryEnqueue(job))
        {
            File.Delete(path);
            return Results.Problem("The processing queue is full. Try again after a job finishes.", statusCode: 429);
        }
        return Results.Accepted($"/api/jobs/{id}", access.PublicSnapshot(job.Snapshot));
    }
    catch
    {
        if (File.Exists(path)) File.Delete(path);
        throw;
    }
}).DisableAntiforgery();
app.Run();
static bool Valid(Point2D point) => double.IsFinite(point.X) && double.IsFinite(point.Y) &&
    point.X is >= 0 and <= 1 && point.Y is >= 0 and <= 1;

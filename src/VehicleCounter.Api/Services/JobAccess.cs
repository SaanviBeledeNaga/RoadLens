using System.Security.Cryptography;
using System.Text;
using VehicleCounter.Api.Models;

namespace VehicleCounter.Api.Services;

// Anonymous browser capabilities keep cloud uploads private to the browser that submitted them.
public sealed class JobAccess(IConfiguration configuration)
{
    private readonly byte[] _signingKey = RandomNumberGenerator.GetBytes(32);
    public bool Required { get; } = configuration.GetValue<bool>("Access:RequireClientKey");
    public string? Owner(HttpContext context) => context.Items["RoadLensOwner"] as string;
    public string Ticket(Guid jobId, string purpose)
    {
        var expiry = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds();
        var payload = $"{jobId:N}:{purpose}:{expiry}";
        var signature = Convert.ToHexString(HMACSHA256.HashData(_signingKey, Encoding.UTF8.GetBytes(payload)));
        return $"{expiry}.{signature}";
    }
    public bool ValidateTicket(Guid jobId, string purpose, string ticket)
    {
        var parts = ticket.Split('.');
        if (parts.Length != 2 || !long.TryParse(parts[0], out var expiry) || expiry < DateTimeOffset.UtcNow.ToUnixTimeSeconds() ||
            expiry > DateTimeOffset.UtcNow.AddMinutes(31).ToUnixTimeSeconds()) return false;
        byte[] supplied;
        try { supplied = Convert.FromHexString(parts[1]); } catch (FormatException) { return false; }
        var expected = HMACSHA256.HashData(_signingKey, Encoding.UTF8.GetBytes($"{jobId:N}:{purpose}:{expiry}"));
        return CryptographicOperations.FixedTimeEquals(supplied, expected);
    }
    public string Link(Guid jobId, string suffix, string purpose) =>
        $"/api/jobs/{jobId}/{suffix}" + (Required ? $"?ticket={Ticket(jobId, purpose)}" : "");
    public JobSnapshot PublicSnapshot(JobSnapshot snapshot) => snapshot with
    {
        PreviewUrl = Link(snapshot.Id, "preview", "preview"),
        VehicleExportUrl = Link(snapshot.Id, "vehicles/export", "vehicles-export")
    };
    public async Task Authorize(HttpContext context, AnalysisService jobs, RequestDelegate next)
    {
        var path = context.Request.Path;
        if (!Required || !path.StartsWithSegments("/api") || path == "/api/health") { await next(context); return; }
        Guid? id = Guid.TryParse(context.Request.RouteValues["id"]?.ToString(), out var parsed) ? parsed : null;
        if (id.HasValue && HttpMethods.IsGet(context.Request.Method))
        {
            var purpose = path.Value!.EndsWith("/preview") ? "preview" : path.Value.EndsWith("/video") ? "video" :
                path.Value.EndsWith("/vehicles/export") ? "vehicles-export" : path.Value.EndsWith("/export") ? "crossings-export" : null;
            if (purpose is not null && ValidateTicket(id.Value, purpose, context.Request.Query["ticket"].ToString()))
            { await next(context); return; }
        }
        if (!Guid.TryParse(context.Request.Headers["X-RoadLens-Client"].ToString(), out var key) || key == Guid.Empty)
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new { error = "Open RoadLens in your browser to access your uploads." });
            return;
        }
        var owner = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key.ToString("N"))));
        context.Items["RoadLensOwner"] = owner;
        if (id.HasValue && jobs.Find(id.Value)?.Snapshot.OwnerHash != owner)
        { context.Response.StatusCode = 404; await context.Response.WriteAsJsonAsync(new { error = "Job not found." }); return; }
        await next(context);
    }
}

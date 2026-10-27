using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using OpenCvSharp;
using VehicleCounter.Api.Models;

namespace VehicleCounter.Api.Services;

public sealed class AnalysisService(IWebHostEnvironment environment, IServiceProvider provider,
    ILogger<AnalysisService> logger, IConfiguration configuration) : BackgroundService
{
    private readonly Channel<JobState> _queue = Channel.CreateBounded<JobState>(4);
    private readonly ConcurrentDictionary<Guid, JobState> _jobs = new();
    private readonly string _dataDirectory = configuration["Storage:DataDirectory"] ?? Path.Combine(environment.ContentRootPath, "App_Data");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public string? ModelError { get; private set; }
    public bool ModelReady { get; private set; }
    public IEnumerable<JobSnapshot> List() => _jobs.Values.Select(x => x.Snapshot).OrderByDescending(x => x.CreatedAt);
    public JobState? Find(Guid id) => _jobs.GetValueOrDefault(id);
    public string DataDirectory => _dataDirectory;
    public bool TryEnqueue(JobState job)
    {
        // Bound queued work and keep the most recent results in memory.
        if (!_jobs.TryAdd(job.Id, job)) return false;
        if (!_queue.Writer.TryWrite(job)) { _jobs.TryRemove(job.Id, out _); return false; }
        foreach (var old in _jobs.Values.OrderByDescending(j => j.Snapshot.CreatedAt).Skip(30))
            if (old.Snapshot.Status is "completed" or "cancelled" or "failed") _jobs.TryRemove(old.Id, out _);
        return true;
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory(_dataDirectory);
        foreach (var path in Directory.EnumerateFiles(_dataDirectory, "*.json").Where(path => !path.EndsWith(".tracks.json", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(File.GetLastWriteTimeUtc).Take(30))
        {
            try
            {
                var saved = JsonSerializer.Deserialize<JobSnapshot>(File.ReadAllText(path), JsonOptions);
                if (saved is null || saved.Status is not ("completed" or "cancelled" or "failed")) continue;
                var inputPath = Directory.EnumerateFiles(_dataDirectory, $"{saved.Id}.*")
                    .FirstOrDefault(file => new[] { ".mp4", ".avi", ".mov", ".mkv", ".webm" }.Contains(Path.GetExtension(file).ToLowerInvariant())) ?? string.Empty;
                saved = saved with { PlaybackAvailable = File.Exists(inputPath) };
                var restored = new JobState(saved.Id, saved.FileName, inputPath, saved.Settings);
                var observationPath = Path.Combine(_dataDirectory, $"{saved.Id}.tracks.json");
                if (File.Exists(observationPath))
                    restored.RestoreObservations(JsonSerializer.Deserialize<Dictionary<int, TrackObservation[]>>(File.ReadAllText(observationPath), JsonOptions) ?? new());
                var previewPath = Path.ChangeExtension(path, ".jpg");
                restored.Update(_ => saved, File.Exists(previewPath) ? File.ReadAllBytes(previewPath) : null);
                _jobs.TryAdd(saved.Id, restored);
            }
            catch (Exception ex) { logger.LogWarning(ex, "Could not restore saved job {Path}", path); }
        }
        IVehicleDetector? detector = null;
        try
        {
            detector = provider.GetRequiredService<IVehicleDetector>();
            ModelReady = true;
            logger.LogInformation("ONNX vehicle detector is ready.");
        }
        catch (Exception ex)
        {
            ModelError = ex.GetBaseException().Message;
            logger.LogError(ex, "Could not initialize the detection model.");
        }
        try
        {
            await foreach (var job in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, job.Cancellation.Token);
                try
                {
                    linked.Token.ThrowIfCancellationRequested();
                    if (detector is null) throw new InvalidOperationException(ModelError ?? "Model is unavailable.");
                    Process(job, detector, linked.Token);
                    job.Update(s => s with { Status = "completed", Progress = 1 });
                }
                catch (OperationCanceledException) { job.Update(s => s with { Status = "cancelled" }); }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Video job {JobId} failed", job.Id);
                    job.Update(s => s with { Status = "failed", Error = ex.GetBaseException().Message });
                }
                finally
                {
                    try
                    {
                        await File.WriteAllTextAsync(Path.Combine(_dataDirectory, $"{job.Id}.json"),
                            JsonSerializer.Serialize(job.Snapshot, JsonOptions), CancellationToken.None);
                        if (job.Preview is { } preview)
                            await File.WriteAllBytesAsync(Path.Combine(_dataDirectory, $"{job.Id}.jpg"), preview, CancellationToken.None);
                        if (job.Snapshot.PlaybackAvailable)
                            await File.WriteAllTextAsync(Path.Combine(_dataDirectory, $"{job.Id}.tracks.json"),
                                JsonSerializer.Serialize(job.GetObservationIndex(), JsonOptions), CancellationToken.None);
                        else if (File.Exists(job.InputPath)) File.Delete(job.InputPath);
                    }
                    catch (Exception ex) { logger.LogWarning(ex, "Could not save or clean job {JobId}", job.Id); }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
    private static void Process(JobState job, IVehicleDetector detector, CancellationToken cancellationToken)
    {
        using var capture = new VideoCapture(job.InputPath);
        if (!capture.IsOpened()) throw new InvalidOperationException("The video could not be decoded. Try an H.264 MP4 file.");
        var width = (int)capture.FrameWidth;
        var height = (int)capture.FrameHeight;
        var fps = capture.Fps;
        var totalFrames = capture.FrameCount;
        if (width <= 0 || height <= 0) throw new InvalidOperationException("Video dimensions are invalid.");
        if (!double.IsFinite(fps) || fps <= 0) fps = 25;
        var info = new VideoInfo(width, height, fps, totalFrames, totalFrames / fps);
        var normalized = job.Settings.Line;
        var line = new CountingLine(new(normalized.Start.X * width, normalized.Start.Y * height),
            new(normalized.End.X * width, normalized.End.Y * height));
        var tracker = new VehicleTracker(line, width, height);
        var timer = Stopwatch.StartNew();
        var processed = 0;
        var sourceFrame = 0;
        var lastPreview = -1d;
        using var frame = new Mat();
        job.Update(s => s with { Status = "processing", Video = info, PlaybackAvailable = true });
        while (capture.Read(frame) && !frame.Empty())
        {
            cancellationToken.ThrowIfCancellationRequested();
            sourceFrame++;
            if ((sourceFrame - 1) % job.Settings.FrameStride != 0) continue;
            var detections = detector.Detect(frame, job.Settings.Confidence);
            var seconds = (sourceFrame - 1) / fps;
            var tracks = tracker.Update(detections, seconds);
            job.RecordTracks(tracks, sourceFrame - 1, seconds);
            processed++;
            byte[]? preview = null;
            if (timer.Elapsed.TotalSeconds - lastPreview >= 0.2)
            {
                Draw(frame, line, tracks, tracker.Counts.Values.Sum());
                if (frame.Width > 1280)
                {
                    using var resized = new Mat();
                    Cv2.Resize(frame, resized, new Size(1280, (int)(frame.Height * 1280d / frame.Width)));
                    preview = resized.ToBytes(".jpg", new ImageEncodingParam(ImwriteFlags.JpegQuality, 80));
                }
                else preview = frame.ToBytes(".jpg", new ImageEncodingParam(ImwriteFlags.JpegQuality, 80));
                lastPreview = timer.Elapsed.TotalSeconds;
            }
            job.Update(s => s with
            {
                Progress = totalFrames > 0 ? Math.Clamp((double)sourceFrame / totalFrames, 0, 1) : 0,
                ProcessedFrames = processed, SourceFrame = sourceFrame, VideoSeconds = seconds,
                ProcessingFps = processed / Math.Max(0.001, timer.Elapsed.TotalSeconds),
                Counts = tracker.Counts, Directions = tracker.Directions, Total = tracker.Counts.Values.Sum(),
                Tracks = tracks, Events = tracker.Events, Vehicles = tracker.Vehicles
            }, preview);
        }
        if (processed == 0) throw new InvalidOperationException("The video contained no decodable frames.");
        // Retain a preview matching the final count even if the last frame fell between preview intervals.
        if (!frame.Empty())
        {
            Draw(frame, line, job.Snapshot.Tracks, job.Snapshot.Total);
            job.Update(s => s, frame.ToBytes(".jpg"));
        }
    }
    private static void Draw(Mat frame, CountingLine line, IReadOnlyList<TrackView> tracks, int total)
    {
        var color = new Scalar(80, 220, 150);
        Cv2.Line(frame, new Point((int)line.Start.X, (int)line.Start.Y),
            new Point((int)line.End.X, (int)line.End.Y), new Scalar(30, 210, 255), 3);
        foreach (var track in tracks)
        {
            var b = track.Box;
            var c = track.Counted ? new Scalar(180, 160, 90) : color;
            Cv2.Rectangle(frame, new Rect((int)b.X, (int)b.Y, Math.Max(1, (int)b.Width), Math.Max(1, (int)b.Height)), c, 2);
            Cv2.PutText(frame, $"#{track.Id} {track.VehicleClass} {track.Confidence:P0}",
                new Point((int)b.X, Math.Max(18, (int)b.Y - 8)), HersheyFonts.HersheySimplex, 0.55, c, 2);
            Cv2.Circle(frame, new Point((int)b.Anchor.X, (int)b.Anchor.Y), 4, c, -1);
        }
        Cv2.PutText(frame, $"Counted: {total}", new Point(16, 30), HersheyFonts.HersheySimplex, 0.8,
            new Scalar(0, 0, 0), 5);
        Cv2.PutText(frame, $"Counted: {total}", new Point(16, 30), HersheyFonts.HersheySimplex, 0.8, color, 2);
    }
}

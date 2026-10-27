namespace VehicleCounter.Api.Models;

public readonly record struct Point2D(double X, double Y);
public sealed record CountingLine(Point2D Start, Point2D End)
{
    public double Length => Math.Sqrt(Math.Pow(End.X - Start.X, 2) + Math.Pow(End.Y - Start.Y, 2));
    public double SignedDistance(Point2D p) =>
        ((End.X - Start.X) * (p.Y - Start.Y) - (End.Y - Start.Y) * (p.X - Start.X)) / Length;
    public bool Intersects(Point2D a, Point2D b)
    {
        static double Cross(Point2D u, Point2D v) => u.X * v.Y - u.Y * v.X;
        var r = new Point2D(b.X - a.X, b.Y - a.Y);
        var s = new Point2D(End.X - Start.X, End.Y - Start.Y);
        var delta = new Point2D(Start.X - a.X, Start.Y - a.Y);
        var denominator = Cross(r, s);
        if (Math.Abs(denominator) < 1e-9) return false;
        var t = Cross(delta, s) / denominator;
        var u = Cross(delta, r) / denominator;
        return t is >= 0 and <= 1 && u is >= 0 and <= 1;
    }
}
public sealed record Box(double X, double Y, double Width, double Height)
{
    public Point2D Center => new(X + Width / 2, Y + Height / 2);
    public Point2D Anchor => new(X + Width / 2, Y + Height);
    public double Area => Math.Max(0, Width) * Math.Max(0, Height);
    public double IoU(Box other)
    {
        var intersection = Math.Max(0, Math.Min(X + Width, other.X + other.Width) - Math.Max(X, other.X)) *
                           Math.Max(0, Math.Min(Y + Height, other.Y + other.Height) - Math.Max(Y, other.Y));
        return intersection / Math.Max(1e-9, Area + other.Area - intersection);
    }
}
public sealed record Detection(string VehicleClass, float Confidence, Box Box);
public sealed record TrackView(int Id, string VehicleClass, float Confidence, Box Box, bool Counted);
public sealed record CountEvent(int TrackId, string VehicleClass, string Direction, double VideoSeconds);
public sealed record VehicleActivity(int TrackId, string VehicleClass, double FirstSeenSeconds,
    double LastSeenSeconds, float MaxConfidence, bool Crossed, string? Direction,
    double? CrossedAtSeconds, int ObservationCount);
public sealed record TrackObservation(int SourceFrame, double VideoSeconds, Box Box);
public sealed record VehicleReview(Guid JobId, int TrackId, VideoInfo Video, VehicleActivity Vehicle,
    IReadOnlyList<TrackObservation> Observations, string VideoUrl);
public sealed record AnalysisSettings(CountingLine Line, float Confidence = 0.35f, int FrameStride = 2);
public sealed record VideoInfo(int Width, int Height, double Fps, int TotalFrames, double DurationSeconds);
public sealed record JobSnapshot(
    Guid Id, string FileName, string Status, double Progress, int ProcessedFrames, int SourceFrame,
    double VideoSeconds, double ProcessingFps, VideoInfo? Video, AnalysisSettings Settings,
    IReadOnlyDictionary<string, int> Counts, IReadOnlyDictionary<string, int> Directions,
    int Total, IReadOnlyList<TrackView> Tracks, IReadOnlyList<CountEvent> Events,
    string? Error, DateTimeOffset CreatedAt, IReadOnlyList<VehicleActivity>? Vehicles = null, bool PlaybackAvailable = false, string? OwnerHash = null, string? PreviewUrl = null, string? VehicleExportUrl = null);
public sealed class JobState(Guid id, string fileName, string inputPath, AnalysisSettings settings, string? ownerHash = null)
{
    private readonly object _gate = new();
    private JobSnapshot _snapshot = new(id, fileName, "queued", 0, 0, 0, 0, 0, null, settings,
        EmptyCounts(), new Dictionary<string, int> { ["A to B"] = 0, ["B to A"] = 0 },
        0, [], [], null, DateTimeOffset.UtcNow, [], false, ownerHash);
    private byte[]? _preview;
    private readonly Dictionary<int, List<TrackObservation>> _observations = new();
    public Guid Id => id;
    public string InputPath => inputPath;
    public AnalysisSettings Settings => settings;
    public CancellationTokenSource Cancellation { get; } = new();
    public JobSnapshot Snapshot { get { lock (_gate) return _snapshot; } }
    public byte[]? Preview { get { lock (_gate) return _preview; } }
    public void Update(Func<JobSnapshot, JobSnapshot> update, byte[]? preview = null)
    {
        lock (_gate)
        {
            _snapshot = update(_snapshot);
            if (preview is not null) _preview = preview;
        }
    }
    public void RecordTracks(IReadOnlyList<TrackView> tracks, int sourceFrame, double videoSeconds)
    {
        lock (_gate)
        {
            foreach (var track in tracks)
            {
                if (!_observations.TryGetValue(track.Id, out var observations))
                    _observations[track.Id] = observations = new List<TrackObservation>();
                observations.Add(new TrackObservation(sourceFrame, videoSeconds, track.Box));
            }
        }
    }
    public IReadOnlyList<TrackObservation> GetObservations(int trackId)
    {
        lock (_gate) return _observations.TryGetValue(trackId, out var observations) ? observations.ToArray() : [];
    }
    public Dictionary<int, TrackObservation[]> GetObservationIndex()
    {
        lock (_gate) return _observations.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());
    }
    public void RestoreObservations(Dictionary<int, TrackObservation[]> observations)
    {
        lock (_gate)
        {
            _observations.Clear();
            foreach (var pair in observations) _observations[pair.Key] = pair.Value.ToList();
        }
    }
    public static Dictionary<string, int> EmptyCounts() => new()
    { ["car"] = 0, ["motorcycle"] = 0, ["bus"] = 0, ["truck"] = 0 };
}



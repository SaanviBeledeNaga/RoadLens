using VehicleCounter.Api.Models;

namespace VehicleCounter.Api.Services;

// Global geometry assignment, short-gap recovery, and a stable-side line counter.
public sealed class VehicleTracker(CountingLine line, int width, int height)
{
    private readonly Dictionary<int, Track> _tracks = new();
    private int _nextId = 1;
    private readonly Dictionary<int, VehicleActivity> _activity = new();
    private readonly List<CountEvent> _events = [];
    private readonly Dictionary<string, int> _counts = JobState.EmptyCounts();
    private readonly Dictionary<string, int> _directions = new() { ["A to B"] = 0, ["B to A"] = 0 };
    public IReadOnlyList<CountEvent> Events => _events.ToArray();
    public IReadOnlyList<VehicleActivity> Vehicles => _activity.Values.OrderBy(v => v.TrackId).ToArray();
    public IReadOnlyDictionary<string, int> Counts => new Dictionary<string, int>(_counts);
    public IReadOnlyDictionary<string, int> Directions => new Dictionary<string, int>(_directions);
    public IReadOnlyList<TrackView> Update(IReadOnlyList<Detection> detections, double seconds)
    {
        detections = SuppressDuplicateDetections(detections);
        foreach (var id in _tracks.Values.Where(track => seconds - track.LastSeenSeconds > 2).Select(track => track.Id).ToArray())
            _tracks.Remove(id);
        foreach (var track in _tracks.Values) track.Missed++;
        var diagonal = Math.Sqrt((double)width * width + (double)height * height);
        var pairs = new List<(double Cost, int TrackId, int DetectionIndex)>();
        foreach (var track in _tracks.Values)
        {
            var prediction = new Box(track.Box.X + track.Velocity.X * (seconds - track.LastSeenSeconds),
                track.Box.Y + track.Velocity.Y * (seconds - track.LastSeenSeconds), track.Box.Width, track.Box.Height);
            for (var i = 0; i < detections.Count; i++)
            {
                var box = detections[i].Box;
                var predictedDistance = Math.Sqrt(Math.Pow(prediction.Center.X - box.Center.X, 2) + Math.Pow(prediction.Center.Y - box.Center.Y, 2));
                var lastDistance = Math.Sqrt(Math.Pow(track.Box.Center.X - box.Center.X, 2) + Math.Pow(track.Box.Center.Y - box.Center.Y, 2));
                var distance = Math.Min(predictedDistance, lastDistance) / diagonal;
                var iou = Math.Max(prediction.IoU(box), track.Box.IoU(box));
                var ratio = box.Area / Math.Max(1, prediction.Area);
                if (ratio is < 0.25 or > 4 || distance > 0.12 || (iou < 0.03 && distance > 0.04)) continue;
                pairs.Add((1 - iou + distance * 3 + .03 * (seconds - track.LastSeenSeconds), track.Id, i));
            }
        }
        var matchedTracks = new HashSet<int>();
        var matchedDetections = new HashSet<int>();
        foreach (var pair in AssignGlobally(_tracks.Keys.Order().ToArray(), detections.Count, pairs))
        {
            if (matchedTracks.Contains(pair.TrackId) || matchedDetections.Contains(pair.DetectionIndex)) continue;
            var track = _tracks[pair.TrackId];
            var detection = detections[pair.DetectionIndex];
            var elapsed = Math.Max(1e-6, seconds - track.LastSeenSeconds);
            track.Velocity = new Point2D((detection.Box.Center.X - track.Box.Center.X) / elapsed,
                (detection.Box.Center.Y - track.Box.Center.Y) / elapsed);
            track.LastSeenSeconds = seconds;
            track.Box = detection.Box;
            track.Confidence = detection.Confidence;
            track.Votes[detection.VehicleClass] = track.Votes.GetValueOrDefault(detection.VehicleClass) + 1;
            track.Missed = 0;
            track.Hits++;
            UpdateCrossing(track, seconds);
            UpdateActivity(track, seconds);
            matchedTracks.Add(pair.TrackId);
            matchedDetections.Add(pair.DetectionIndex);
        }
        for (var i = 0; i < detections.Count; i++)
        {
            if (matchedDetections.Contains(i)) continue;
            var track = new Track(_nextId++, detections[i]);
            track.LastSeenSeconds = seconds;
            _tracks.Add(track.Id, track);
            UpdateCrossing(track, seconds);
            UpdateActivity(track, seconds);
        }
        return _tracks.Values.Where(t => t.Missed == 0).Select(t =>
            new TrackView(t.Id, t.VehicleClass, t.Confidence, t.Box, t.Counted)).ToArray();
    }
    // A car and truck prediction can describe the same box. Keep one physical detection.
    public static IReadOnlyList<Detection> SuppressDuplicateDetections(IReadOnlyList<Detection> detections)
    {
        var selected = new List<Detection>();
        foreach (var detection in detections.OrderByDescending(d => d.Confidence))
            if (!selected.Any(existing => existing.Box.IoU(detection.Box) > .65)) selected.Add(detection);
        return selected;
    }

    // Rectangular Hungarian assignment; each track also has an unmatched dummy column.
    private static IEnumerable<(double Cost, int TrackId, int DetectionIndex)> AssignGlobally(
        int[] trackIds, int detectionCount, List<(double Cost, int TrackId, int DetectionIndex)> pairs)
    {
        if (trackIds.Length == 0 || detectionCount == 0) return [];
        const double unmatched = 1.4;
        var rows = trackIds.Length;
        var columns = detectionCount + rows;
        var cost = new double[rows + 1, columns + 1];
        var lookup = trackIds.Select((id, index) => (id, index: index + 1)).ToDictionary(x => x.id, x => x.index);
        for (var row = 1; row <= rows; row++)
            for (var column = 1; column <= columns; column++) cost[row, column] = column <= detectionCount ? 100000 : unmatched;
        foreach (var pair in pairs) cost[lookup[pair.TrackId], pair.DetectionIndex + 1] = pair.Cost;
        var u = new double[rows + 1];
        var v = new double[columns + 1];
        var p = new int[columns + 1];
        var way = new int[columns + 1];
        for (var row = 1; row <= rows; row++)
        {
            p[0] = row;
            var column = 0;
            var min = Enumerable.Repeat(double.PositiveInfinity, columns + 1).ToArray();
            var used = new bool[columns + 1];
            do
            {
                used[column] = true;
                var currentRow = p[column];
                var delta = double.PositiveInfinity;
                var next = 0;
                for (var j = 1; j <= columns; j++)
                {
                    if (used[j]) continue;
                    var reduced = cost[currentRow, j] - u[currentRow] - v[j];
                    if (reduced < min[j]) { min[j] = reduced; way[j] = column; }
                    if (min[j] < delta) { delta = min[j]; next = j; }
                }
                for (var j = 0; j <= columns; j++)
                    if (used[j]) { u[p[j]] += delta; v[j] -= delta; } else min[j] -= delta;
                column = next;
            } while (p[column] != 0);
            do { var previous = way[column]; p[column] = p[previous]; column = previous; } while (column != 0);
        }
        var result = new List<(double Cost, int TrackId, int DetectionIndex)>();
        for (var column = 1; column <= detectionCount; column++)
            if (p[column] != 0 && cost[p[column], column] < unmatched)
                result.Add((cost[p[column], column], trackIds[p[column] - 1], column - 1));
        return result;
    }
    private void UpdateActivity(Track track, double seconds)
    {
        _activity.TryGetValue(track.Id, out var previous);
        _activity[track.Id] = new VehicleActivity(track.Id, previous?.Crossed == true ? previous.VehicleClass : track.VehicleClass,
            previous?.FirstSeenSeconds ?? seconds, seconds,
            Math.Max(previous?.MaxConfidence ?? 0, track.Confidence), track.Counted,
            track.Direction, track.CrossedAtSeconds, track.Hits);
    }
    private void UpdateCrossing(Track track, double seconds)
    {
        var position = track.Box.Anchor;
        var distance = line.SignedDistance(position);
        var tolerance = Math.Max(2, height * 0.004);
        if (Math.Abs(distance) <= tolerance) return;
        var side = Math.Sign(distance);
        if (!track.Counted && track.Hits >= 2 && track.LastSide != 0 && side != track.LastSide &&
            line.Intersects(track.LastStableAnchor, position))
        {
            var direction = track.LastSide < 0 ? "A to B" : "B to A";
            _counts[track.VehicleClass]++;
            _directions[direction]++;
            _events.Add(new CountEvent(track.Id, track.VehicleClass, direction, seconds));
            track.Counted = true;
            track.Direction = direction;
            track.CrossedAtSeconds = seconds;
        }
        track.LastSide = side;
        track.LastStableAnchor = position;
    }
    private sealed class Track(int id, Detection detection)
    {
        public int Id { get; } = id;
        public Box Box { get; set; } = detection.Box;
        public float Confidence { get; set; } = detection.Confidence;
        public Dictionary<string, int> Votes { get; } = new() { [detection.VehicleClass] = 1 };
        public string VehicleClass => Votes.MaxBy(p => p.Value).Key;
        public Point2D Velocity { get; set; }
        public int Missed { get; set; }
        public double LastSeenSeconds { get; set; }
        public int Hits { get; set; } = 1;
        public int LastSide { get; set; }
        public Point2D LastStableAnchor { get; set; }
        public bool Counted { get; set; }
        public string? Direction { get; set; }
        public double? CrossedAtSeconds { get; set; }
    }
}

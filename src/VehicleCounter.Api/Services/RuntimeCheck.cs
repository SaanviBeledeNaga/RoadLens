using OpenCvSharp;

namespace VehicleCounter.Api.Services;

public static class RuntimeCheck
{
    public static void Verify(IWebHostEnvironment environment, IConfiguration configuration)
    {
        using var detector = new OnnxVehicleDetector(environment, configuration);
        var sample = Path.Combine(environment.WebRootPath ?? Path.Combine(environment.ContentRootPath, "wwwroot"), "samples", "traffic.mp4");
        using var capture = new VideoCapture(sample);
        using var frame = new Mat();
        if (!capture.Read(frame) || frame.Empty()) throw new InvalidOperationException("Native video decoding is unavailable.");
        detector.Detect(frame, .25f);
        Console.WriteLine("RoadLens runtime check passed: video decoding and ONNX inference.");
    }
}

using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using System.Runtime.InteropServices;
using VehicleCounter.Api.Models;

namespace VehicleCounter.Api.Services;

public interface IVehicleDetector
{
    IReadOnlyList<Detection> Detect(Mat frame, float threshold);
}
public sealed class OnnxVehicleDetector : IVehicleDetector, IDisposable
{
    private readonly InferenceSession _session;
    private readonly string _input;
    private readonly int _width;
    private readonly int _height;
    private static readonly Dictionary<int, string> VehicleClasses = new()
    { [2] = "car", [3] = "motorcycle", [5] = "bus", [7] = "truck" };
    public OnnxVehicleDetector(IWebHostEnvironment environment, IConfiguration configuration)
    {
        var configured = configuration["Detector:ModelPath"] ?? "ModelsData/yolo11n.onnx";
        var path = Path.IsPathRooted(configured) ? configured : Path.Combine(environment.ContentRootPath, configured);
        if (!File.Exists(path))
            throw new FileNotFoundException("Detection model is missing. Run setup.ps1 to download the model.", path);
        using var options = new Microsoft.ML.OnnxRuntime.SessionOptions { IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 8) };
        _session = new InferenceSession(path, options);
        var input = _session.InputMetadata.First();
        _input = input.Key;
        var shape = input.Value.Dimensions;
        if (shape.Length != 4 || shape[1] != 3)
            throw new InvalidOperationException("Expected a YOLO RGB input with shape [1,3,height,width].");
        _height = shape[2] > 0 ? shape[2] : 640;
        _width = shape[3] > 0 ? shape[3] : 640;
    }
    public IReadOnlyList<Detection> Detect(Mat frame, float threshold)
    {
        var scale = Math.Min((double)_width / frame.Width, (double)_height / frame.Height);
        var newWidth = Math.Max(1, (int)Math.Round(frame.Width * scale));
        var newHeight = Math.Max(1, (int)Math.Round(frame.Height * scale));
        var left = (_width - newWidth) / 2;
        var top = (_height - newHeight) / 2;
        using var resized = new Mat();
        Cv2.Resize(frame, resized, new Size(newWidth, newHeight));
        using var letterbox = new Mat();
        Cv2.CopyMakeBorder(resized, letterbox, top, _height - newHeight - top,
            left, _width - newWidth - left, BorderTypes.Constant, new Scalar(114, 114, 114));
        using var rgb = new Mat();
        Cv2.CvtColor(letterbox, rgb, ColorConversionCodes.BGR2RGB);
        var pixels = new byte[_width * _height * 3];
        Marshal.Copy(rgb.Data, pixels, 0, pixels.Length);
        var values = new float[pixels.Length];
        var plane = _width * _height;
        for (var i = 0; i < plane; i++)
        {
            values[i] = pixels[i * 3] / 255f;
            values[plane + i] = pixels[i * 3 + 1] / 255f;
            values[plane * 2 + i] = pixels[i * 3 + 2] / 255f;
        }
        var tensor = new DenseTensor<float>(values, new[] { 1, 3, _height, _width });
        using var results = _session.Run(new[] { NamedOnnxValue.CreateFromTensor(_input, tensor) });
        var output = results.First().AsTensor<float>();
        if (output.Dimensions.Length != 3 || output.Dimensions[0] != 1)
            throw new InvalidOperationException("Expected raw YOLO detection output [1,84,N] or [1,N,84].");
        var channelsFirst = output.Dimensions[1] == 84;
        if (!channelsFirst && output.Dimensions[2] != 84)
            throw new InvalidOperationException("Expected an 80-class COCO YOLO export without embedded NMS.");
        var count = output.Dimensions[channelsFirst ? 2 : 1];
        float Read(int index, int channel) => channelsFirst ? output[0, channel, index] : output[0, index, channel];
        var candidates = new List<Detection>();
        for (var i = 0; i < count; i++)
        {
            var best = 0f;
            var classId = -1;
            for (var c = 0; c < 80; c++)
            {
                var score = Read(i, c + 4);
                if (score > best) { best = score; classId = c; }
            }
            if (best < threshold || !VehicleClasses.TryGetValue(classId, out var label)) continue;
            var cx = Read(i, 0); var cy = Read(i, 1);
            var width = Read(i, 2); var height = Read(i, 3);
            var x1 = Math.Clamp((cx - width / 2 - left) / scale, 0, frame.Width);
            var y1 = Math.Clamp((cy - height / 2 - top) / scale, 0, frame.Height);
            var x2 = Math.Clamp((cx + width / 2 - left) / scale, 0, frame.Width);
            var y2 = Math.Clamp((cy + height / 2 - top) / scale, 0, frame.Height);
            if (x2 <= x1 || y2 <= y1) continue;
            candidates.Add(new Detection(label, best, new Box(x1, y1, x2 - x1, y2 - y1)));
        }
        var selected = new List<Detection>();
        foreach (var detection in candidates.OrderByDescending(x => x.Confidence))
        {
            if (selected.Any(x => x.VehicleClass == detection.VehicleClass && x.Box.IoU(detection.Box) > 0.45)) continue;
            selected.Add(detection);
            if (selected.Count >= 200) break;
        }
        return selected;
    }
    public void Dispose() => _session.Dispose();
}


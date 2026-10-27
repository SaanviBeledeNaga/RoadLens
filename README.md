# RoadLens - Vehicle Detection and Counting

A Windows application with a C# / ASP.NET Core backend and a local browser dashboard. The backend reads uploaded video with OpenCvSharp, runs a YOLO11n ONNX model with ONNX Runtime, tracks vehicles, and counts crossings of a user-defined line.

## Run the ready-built app

1. Keep this folder intact and double-click `Start.cmd`.
2. Open http://127.0.0.1:5080 in your browser.
3. Choose a video or click **Try sample traffic footage**.
4. Drag across the video preview to position the counting line.
5. Click **Analyze video**. Detections, progress, class totals, direction totals, and crossing activity update as processing runs.
6. **Vehicle activity** lists every track with its crossing status. Filter All detected vehicles / Crossed / Not crossed, and use Previous / Next to reach the full history. **Export all vehicles** downloads every row, including vehicles that did not cross. Click any track number to seek to its crossing (or first appearance for a vehicle that did not cross) and highlight that vehicle. The First seen / Crossing buttons jump between those moments. Stop processing to keep a partial result.

The `app` folder includes the Windows x64 runtime and model. You do not need a .NET SDK, Python, or an API key to run it. Keep the terminal open while using the app. Only one server can use port 5080 at a time.

## Current capabilities

- Uploaded MP4, MOV, AVI, MKV, and WEBM files, up to 500 MB. Codec support depends on the decoder; H.264 MP4 is recommended for browser preview.
- Real ONNX inference for four COCO classes: car, motorcycle, bus, and truck.
- A configurable finite counting line, two crossing directions, and per-track duplicate prevention.
- Global assignment matches vehicles across frames. Overlapping vehicle predictions are suppressed across classes, and tracks can recover after detection gaps of up to two seconds. Stable-side counting reduces line jitter.
- Adjustable confidence and frame stride. Every frame is the best setting when vehicles move quickly.
- Annotated frame previews, cancellation, per-class crossing totals, and CSV exports. Vehicle activity retains every detected track after it leaves the frame and updates its crossing status in the same row. Activity is paginated rather than truncated to 100 rows.
- Each job has isolated counts. Recent completed jobs, final JSON results, and annotated previews are saved under `App_Data` and restored after a restart. The latest job with full vehicle history is shown when the dashboard is refreshed. Older crossing-only results must be reanalyzed to include uncounted vehicles.
- Videos and per-track position records are saved locally under `App_Data` for seeking and vehicle highlighting. Valid videos are retained after processing and after cancellation if frames were processed. Invalid or never-processed uploads are removed. `App_Data` is excluded from the downloadable application package.

## Source and development

Source is under `src/VehicleCounter.Api`. Requires the .NET 8 SDK and Windows x64. Restore uses NuGet.org. The `setup.ps1` script downloads the documented model if it is missing.

```powershell
./setup.ps1
dotnet run --project src/VehicleCounter.Api --urls http://127.0.0.1:5080
```

Publish a standalone Windows build:

```powershell
dotnet publish src/VehicleCounter.Api -c Release -r win-x64 --self-contained true -o app
```

The model file is included under `ModelsData`. Its SHA-256 is `634279B40C07C6391472C51AD45B81EBC48706A9A1FE72DD3396322ACD0C053B`.

## API

| Method | Route | Purpose |
|---|---|---|
| GET | `/api/health` | API and model readiness |
| POST | `/api/jobs` | Upload a video and settings as multipart form data |
| GET | `/api/jobs` | Recent jobs held in memory |
| GET | `/api/jobs/{id}` | Counts, progress, tracks, events, and status |
| GET | `/api/jobs/{id}/preview` | Latest annotated JPEG |
| GET | `/api/jobs/{id}/video` | Original video with byte-range seeking |
| GET | `/api/jobs/{id}/vehicles/{trackId}/review` | Selected vehicle metadata and timestamped bounding boxes |
| POST | `/api/jobs/{id}/cancel` | Stop a queued or active job |
| GET | `/api/jobs/{id}/export` | CSV of crossings |
| GET | `/api/jobs/{id}/vehicles/export` | CSV of all tracked vehicles, crossing status, and first/last seen times |

`POST /api/jobs` expects a `video` file and a JSON `settings` field:

```json
{
  "line": { "start": { "x": 0.02, "y": 0.6 }, "end": { "x": 0.98, "y": 0.6 } },
  "confidence": 0.25,
  "frameStride": 2
}
```

Coordinates are normalized to the video dimensions (0 to 1). A vehicle's bottom-center anchor must cross the segment between the endpoints. A to B means negative to positive signed distance from the directed line; reversing the line reverses direction labels.

## Practical limitations

This first version supports recorded video files, one worker, and one counting line per job. RTSP and webcam capture are future extensions. The tracker uses geometry rather than appearance-based re-identification; occlusion or missed detections may split an identity and affect counts. CPU processing can be slower than video playback. Detection is sensitive to camera angle, lighting, vehicle size, and model training data. No counting accuracy percentage is claimed.

The app listens on localhost and has no login system. Browser interaction checks could not be completed because local browser automation is disabled in the current app settings. Server and model verification are documented in `VERIFICATION.md`.

## Third-party components and sample

- [ONNX Runtime](https://onnxruntime.ai/docs/get-started/with-csharp.html), Microsoft, MIT license.
- [OpenCvSharp](https://github.com/shimat/opencvsharp), Apache-2.0 license.
- [YOLO11n ONNX model](https://github.com/ultralytics/assets/releases/download/v8.3.0/yolo11n.onnx), Ultralytics. The model is governed by Ultralytics licensing; the AGPL-3.0 text is included alongside it. See https://www.ultralytics.com/license for licensing options.
- [Sample footage](https://github.com/intel-iot-devkit/sample-videos/blob/master/person-bicycle-car-detection.mp4), Intel IoT DevKit, CC BY 4.0. Original video is included unmodified. Attribution and license are under `wwwroot/samples`.

Run the focused counter checks with `dotnet run --project tests/VehicleCounter.Checks`.



The default confidence is 25%. The two-car sample previously returned one at 35% because the first car was filtered before crossing. See `VERIFICATION.md` for the diagnosis and regression checks.

Run activity filtering and pagination checks with `node tests/vehicle-activity-checks.cjs`. The tracker uses temporary identities; a vehicle that loses its identity can appear under another track ID.


Activity is sorted by numeric track ID in ascending order. Clicking a track number pauses the video at its crossing or first appearance and draws only that selected track's box. Playing the video follows its recorded positions; the highlight disappears when it is not observed. Older results without saved video/position records need reanalysis. Browser review requires a playable codec such as H.264 in MP4.

The uploaded test clip exposed duplicate boxes under car/truck labels and fragmented identities. Suppressing overlapping detections and improving matching reduced its activity entries from 101 to 55. This is a reduction in repeated tracker entries, not a ground-truth claim about the number of physical vehicles. Longer occlusions or difficult views can still break an identity.

Run review geometry and selection checks with `node tests/vehicle-review-checks.cjs` and `node tests/vehicle-review-controller-checks.cjs`.

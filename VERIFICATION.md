# Verification performed

The Windows x64 standalone executable was launched locally and loaded the YOLO11n ONNX model successfully. The source was built and published with the locally installed .NET 8 SDK.

## Functional checks

- Dashboard HTML returned HTTP 200 and JavaScript passed Node syntax checking.
- Model sanity check detected the bus in Ultralytics' reference bus image with confidence 0.939.
- A real sample video completed through the multipart upload API: 647 source frames, 324 analyzed frames, frame stride 2, confidence threshold 0.30.
- The configured line ran from normalized (0.02, 0.60) to (0.98, 0.60).
- The run produced one car crossing, B to A, at video time 46.833 seconds. This is a functional result, not a ground-truth accuracy measurement.
- Observed processing rate in this environment was approximately 3.54 analyzed frames per second.
- CSV export contained exactly the same number of events as the returned total.
- A queued job was cancelled and reached the cancelled state.
- Uploaded files for both completed and cancelled jobs were removed.
- Invalid counting-line settings returned HTTP 400.
- An unknown job returned HTTP 404.

The exact sample result and CSV are in `verification/`.

## Counter checks

Eight checks passed: a crossing through the line tolerance band; direction assignment; no duplicate count after reversal; no count beyond a line endpoint; line jitter suppression; two separate identities; reverse direction; and isolation between jobs.

Run these from source with `dotnet run --project tests/VehicleCounter.Checks`.

## Verification limits

Browser automation is disabled in the current Work settings. The dashboard could not be visually inspected or exercised through a browser here. The HTTP endpoints, native video decoder, real model inference, counting logic, and standalone executable were exercised directly. No production accuracy claim is made; crowding, occlusion, low light, and difficult camera angles need further evaluation.

## Two-car sample correction

The original 35% confidence default discarded the first car before its crossing. Its detection at source frame 204 was about 27%; it first exceeded 35% at frame 210, when its bottom-center anchor was already within the counting line's tolerance band. With no earlier accepted position on the other side, a crossing could not be established.

The dashboard default and API example now use 25%. The existing backend binary is retained; the dashboard explicitly sends the selected threshold. A captured-detection regression reproduces the original count of one at 35% and the expected count of two at the 25% setting. The counter check suite now contains ten passing checks. The earlier sample result is retained as the before-fix record.

The complete video was then rerun through the upload API at 25% confidence and stride 2. It finished with two car crossings: 17.667 seconds and 46.833 seconds, both B to A. The full result and CSV are saved as `sample-result-after-fix.json` and `sample-crossings-after-fix.csv` under `verification/`.



## Complete vehicle activity

The activity view previously read only the crossing-event log. A separate vehicle history now records every detected track, its first and last detection time, maximum confidence, and crossing status. A crossing updates that track's existing record. Track expiry does not remove the record.

Six additional C# checks cover listing detections before crossing, updating crossing status, crossing time, first/last seen times, retaining expired tracks, and agreement with crossing totals (16 C# checks in total). Five JavaScript checks cover complete pagination beyond 100 vehicles, crossed/not-crossed filtering, page bounds, older result handling, and timestamp formatting. The updated C# backend built with zero warnings and errors.

The dashboard now uses explicit filters and page controls, and its CSV export includes all tracked vehicles. The separate crossing-only CSV endpoint is retained. Completed jobs and their previews are restored from disk after a restart. Browser visual verification remains unavailable in this session.

An end-to-end rerun of the uploaded 14-second video retained 101 track entries: 11 crossed and 90 did not cross. All currently visible tracks had corresponding history entries, old entries remained in the history, CSV contained all 101 entries, and the activity filters matched crossing totals. The actual result was checked across three pages. After a server restart, all 101 records, the crossing total, full CSV, and preview were restored successfully. These are tracker identities, not a ground-truth count of distinct physical vehicles.


## Ascending IDs, vehicle review, and repeated identities

Activity now sorts track IDs numerically in ascending order. Selecting an ID loads its saved video and timestamped boxes, seeks to its crossing or first appearance, and highlights that track. Separate First seen and Crossing buttons are available. Source video and observations are retained locally for review and excluded from the application archive.

The prior uploaded clip contained 64 pairs of track IDs with at least one simultaneous bounding-box IoU above 0.65. An example car/truck pair overlapped for 69 analyzed frames, reaching IoU 0.991. The tracker now suppresses overlapping detections across classes, uses Hungarian global assignment, and recovers identities across detection gaps up to two seconds. The same clip produced 55 tracked entries instead of 101 and no simultaneously overlapping ID pairs above the same 0.65 threshold. Crossing total was 13 after improved track continuity. These automated checks do not establish the ground-truth vehicle count.

The C# suite now has 23 passing checks, including duplicate-label suppression, short-gap recovery, new arrivals after a long absence, global assignment, and review-position persistence. JavaScript checks cover ascending numeric order, exact-frame boxes, interpolation, gaps, scaling, selecting a track and seeking to its own timestamp, clearing highlights when absent, and ignoring stale responses during rapid selection. Controller checks use DOM/media stubs; actual browser visual verification remains unavailable.

All 55 vehicle-review API responses were checked against the selected track's observation count and target timestamp. The video endpoint returned HTTP 206 for a 1024-byte range request, confirming support for browser seeking.

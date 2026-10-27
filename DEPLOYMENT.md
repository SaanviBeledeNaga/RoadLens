# RoadLens deployment

The dashboard uses Firebase Hosting on the Spark plan in project `roadlens-20261027`. The C# API runs as a free Docker web service on Render.

## Render

Build from the repository root using `Dockerfile`, branch `main`, plan `free`. No paid disk or database is required. Set:

- `AllowedHosts=*`
- `Access__RequireClientKey=true`
- `Cors__Origins__0=https://roadlens-20261027.web.app`
- `Cors__Origins__1=https://roadlens-20261027.firebaseapp.com`

The server listens on Render's `PORT`. The Docker build downloads and verifies the ONNX model and licensed demo clip, publishes for Linux x64, and runs a native video-decoding/inference smoke check. A Linux container cannot be run locally without Docker; the Render Docker build provides the native runtime check.

Free Render services sleep when idle and use temporary storage. Uploaded videos, review links, and results can disappear after a restart. Processing is CPU-based and may be slow on the free instance.

Cloud requests use a random browser capability stored in local storage. API lists and jobs are scoped to that browser. Images, video, and exports use expiring links so they can load without exposing the browser capability in media URLs. Clearing browser storage loses access to that browser's prior cloud jobs.

## Firebase Hosting

After Render is live, prepare static hosting with its actual HTTPS service URL:

```powershell
python tools/prepare_hosting.py --api-url https://YOUR-SERVICE.onrender.com
firebase deploy --only hosting --project roadlens-20261027
```

Only the generated `hosting` directory is deployed. Source code, models, credentials, and uploaded videos are excluded. The local Windows app keeps its API on the same origin.

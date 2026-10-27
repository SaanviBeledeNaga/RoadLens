@echo off
cd /d "%~dp0app"
echo RoadLens vehicle counter
echo Open http://127.0.0.1:5080 after the server starts.
echo Press Ctrl+C to stop.
VehicleCounter.Api.exe --urls http://127.0.0.1:5080
pause

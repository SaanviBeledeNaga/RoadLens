$ErrorActionPreference='Stop'
$modelDirectory=Join-Path $PSScriptRoot 'src\VehicleCounter.Api\ModelsData'
$model=Join-Path $modelDirectory 'yolo11n.onnx'
$expected='634279B40C07C6391472C51AD45B81EBC48706A9A1FE72DD3396322ACD0C053B'
New-Item -ItemType Directory -Force -Path $modelDirectory | Out-Null
if(!(Test-Path -LiteralPath $model)) {
    Invoke-WebRequest 'https://github.com/ultralytics/assets/releases/download/v8.3.0/yolo11n.onnx' -OutFile $model
}
if((Get-FileHash -LiteralPath $model -Algorithm SHA256).Hash -ne $expected) {
    throw 'The model checksum differs from the verified YOLO11n model. Check the file before proceeding.'
}
Write-Host 'Model is ready. Run dotnet run --project src/VehicleCounter.Api --urls http://127.0.0.1:5080'

FROM mcr.microsoft.com/dotnet/sdk:8.0-bookworm-slim AS build
RUN apt-get update && apt-get install -y --no-install-recommends curl ca-certificates && rm -rf /var/lib/apt/lists/*
WORKDIR /src
COPY NuGet.Config ./
COPY src/VehicleCounter.Api/VehicleCounter.Api.csproj src/VehicleCounter.Api/
RUN dotnet restore src/VehicleCounter.Api/VehicleCounter.Api.csproj -r linux-x64
COPY src/VehicleCounter.Api/ src/VehicleCounter.Api/
RUN mkdir -p src/VehicleCounter.Api/ModelsData src/VehicleCounter.Api/wwwroot/samples \
    && curl -fsSL https://github.com/ultralytics/assets/releases/download/v8.3.0/yolo11n.onnx -o src/VehicleCounter.Api/ModelsData/yolo11n.onnx \
    && echo '634279b40c07c6391472c51ad45b81ebc48706a9a1fe72dd3396322acd0c053b  src/VehicleCounter.Api/ModelsData/yolo11n.onnx' | sha256sum -c - \
    && curl -fsSL https://raw.githubusercontent.com/intel-iot-devkit/sample-videos/master/person-bicycle-car-detection.mp4 -o src/VehicleCounter.Api/wwwroot/samples/traffic.mp4 \
    && echo '452b11b7e0efbd019f1d9570d0c790e90416ad4ad29eec6003872d08443140ef  src/VehicleCounter.Api/wwwroot/samples/traffic.mp4' | sha256sum -c -
RUN dotnet publish src/VehicleCounter.Api/VehicleCounter.Api.csproj -c Release -r linux-x64 --self-contained false --no-restore -o /out

FROM mcr.microsoft.com/dotnet/aspnet:8.0-bookworm-slim
USER root
RUN apt-get update && apt-get install -y --no-install-recommends libgtk-3-0 libgomp1 ca-certificates && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /out/ ./
ENV DOTNET_gcServer=0 DOTNET_GCHeapHardLimit=0x0C000000 ASPNETCORE_HTTP_PORTS=10000 AllowedHosts=*
RUN dotnet VehicleCounter.Api.dll --verify-runtime
RUN mkdir -p /tmp/roadlens-data && chown -R app:app /tmp/roadlens-data
USER app
ENV Storage__DataDirectory=/tmp/roadlens-data
EXPOSE 10000
ENTRYPOINT ["dotnet", "VehicleCounter.Api.dll"]


FROM mcr.microsoft.com/dotnet/sdk:8.0.423 AS build
WORKDIR /src

COPY src/Perfcho.Performance/Perfcho.Performance.csproj src/Perfcho.Performance/
COPY src/Perfcho.Performance/packages.lock.json src/Perfcho.Performance/
RUN dotnet restore src/Perfcho.Performance/Perfcho.Performance.csproj --locked-mode

COPY src/Perfcho.Performance/ src/Perfcho.Performance/
RUN dotnet publish src/Perfcho.Performance/Perfcho.Performance.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0.29 AS runtime
WORKDIR /app

RUN apt-get update \
    && apt-get install --yes --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

ENV ASPNETCORE_URLS=http://0.0.0.0:6001 \
    CACHE_DIRECTORY=/data/cache

VOLUME ["/data/cache"]
COPY --from=build /app/publish .

EXPOSE 6001
HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
    CMD curl --fail http://127.0.0.1:6001/healthz || exit 1

ENTRYPOINT ["dotnet", "Perfcho.Performance.dll"]

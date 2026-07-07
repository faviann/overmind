FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/MemSrv.Core/MemSrv.Core.csproj src/MemSrv.Core/
COPY src/MemSrv.Server/MemSrv.Server.csproj src/MemSrv.Server/
COPY src/MemCtl/MemCtl.csproj src/MemCtl/
RUN dotnet restore src/MemSrv.Server/MemSrv.Server.csproj \
 && dotnet restore src/MemCtl/MemCtl.csproj

COPY src/ src/
RUN dotnet publish src/MemSrv.Server/MemSrv.Server.csproj -c Release -o /out/server --no-restore \
 && dotnet publish src/MemCtl/MemCtl.csproj -c Release -o /out/memctl --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app

# Npgsql probes GSSAPI at connect time; without this it logs a load error.
RUN apt-get update \
 && apt-get install -y --no-install-recommends libgssapi-krb5-2 \
 && rm -rf /var/lib/apt/lists/*

COPY --from=build /out/server server/
COPY --from=build /out/memctl memctl/
COPY appsettings.json ./
COPY config/ config/
COPY migrations/ migrations/

# memctl resolves appsettings, config/never_store.yaml and migrations/ from the
# working directory; keep WORKDIR /app when overriding the entrypoint.
RUN printf '#!/bin/sh\nexec dotnet /app/memctl/MemCtl.dll "$@"\n' > /usr/local/bin/memctl \
 && chmod +x /usr/local/bin/memctl

ENTRYPOINT ["dotnet", "/app/server/MemSrv.Server.dll"]

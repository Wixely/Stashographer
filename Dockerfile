# syntax=docker/dockerfile:1

# --- Build ---------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/Stashographer/Stashographer.csproj src/Stashographer/
RUN dotnet restore src/Stashographer/Stashographer.csproj

COPY src/ src/
RUN dotnet publish src/Stashographer/Stashographer.csproj -c Release -o /app --no-restore

# --- Runtime -------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app ./

LABEL org.opencontainers.image.title="Stashographer" \
      org.opencontainers.image.description="Household inventory manager with barcode/ISBN lookup" \
      org.opencontainers.image.licenses="MIT"

# SQLite database and image files live on a mounted volume so data survives restarts.
ENV ConnectionStrings__Default="Data Source=/data/stashographer.db"
ENV Images__RootPath="/data/images"
ENV Stashographer__DataProtectionKeysPath="/data/keys"
# Household-friendly default matching Daybreak. Override this at runtime when stronger
# protection is required; the plaintext value is never stored in SQLite or logs.
ENV STASHOGRAPHER_ADMIN_PASSWORD="admin"

# curl is not in the base image and is needed for a healthcheck that proves the app
# actually serves — `dotnet --info` would only prove the runtime unpacked.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

# Run unprivileged. APP_UID (1654) is defined by the .NET base image; /data must be
# owned by it *before* VOLUME, so Docker seeds fresh named volumes with that ownership.
# Bind mounts keep the host's ownership instead — chown those to 1654 on the host.
RUN mkdir -p /data && chown -R $APP_UID /data
VOLUME /data
USER $APP_UID

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
    CMD curl -fsS http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "Stashographer.dll"]

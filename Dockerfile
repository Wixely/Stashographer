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

# SQLite database and image files live on a mounted volume so data survives restarts.
ENV ConnectionStrings__Default="Data Source=/data/stashographer.db"
ENV Images__RootPath="/data/images"
VOLUME /data

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "Stashographer.dll"]

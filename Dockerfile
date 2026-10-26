# One Dockerfile for all three services:
#   docker build --build-arg PROJECT=Ta9.JobService -t ta9-job-service .
# Multi-stage: the SDK image only builds; the runtime image only runs.

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG PROJECT
WORKDIR /src

# Restore first, from project files only, so this layer is cached until a project file changes.
COPY Directory.Build.props Directory.Packages.props ./
COPY src/Ta9.Contracts/Ta9.Contracts.csproj src/Ta9.Contracts/
COPY src/Ta9.ServiceDefaults/Ta9.ServiceDefaults.csproj src/Ta9.ServiceDefaults/
COPY src/${PROJECT}/${PROJECT}.csproj src/${PROJECT}/
RUN dotnet restore src/${PROJECT}/${PROJECT}.csproj

COPY src/ src/
RUN dotnet publish src/${PROJECT}/${PROJECT}.csproj -c Release -o /app --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
ARG PROJECT

# curl is only used by the container health check (/health/ready).
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app .

ENV APP_DLL=${PROJECT}.dll \
    ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

# Non-root user provided by the .NET 8 images.
USER $APP_UID

ENTRYPOINT ["/bin/sh", "-c", "exec dotnet \"$APP_DLL\""]

# syntax=docker/dockerfile:1.7
# Multi-stage build for the Solvix API and the one-shot migrator.
#   docker build -f src/backend/Dockerfile --target api      -t solvix-api .
#   docker build -f src/backend/Dockerfile --target migrator -t solvix-migrator .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first (layer cache): only project/props files.
COPY Directory.Build.props Directory.Packages.props global.json ./
COPY src/backend/Solvix.SharedKernel/Solvix.SharedKernel.csproj src/backend/Solvix.SharedKernel/
COPY src/backend/Solvix.Infrastructure/Solvix.Infrastructure.csproj src/backend/Solvix.Infrastructure/
COPY src/backend/Solvix.Api/Solvix.Api.csproj src/backend/Solvix.Api/
COPY src/backend/Solvix.Migrator/Solvix.Migrator.csproj src/backend/Solvix.Migrator/
COPY src/backend/Modules/Solvix.Modules.Tenancy/Solvix.Modules.Tenancy.csproj src/backend/Modules/Solvix.Modules.Tenancy/
COPY src/backend/Modules/Solvix.Modules.Identity/Solvix.Modules.Identity.csproj src/backend/Modules/Solvix.Modules.Identity/
COPY src/backend/Modules/Solvix.Modules.Organization/Solvix.Modules.Organization.csproj src/backend/Modules/Solvix.Modules.Organization/
COPY src/backend/Modules/Solvix.Modules.Devices/Solvix.Modules.Devices.csproj src/backend/Modules/Solvix.Modules.Devices/
COPY src/shared/Solvix.Agent.Contracts/Solvix.Agent.Contracts.csproj src/shared/Solvix.Agent.Contracts/
RUN dotnet restore src/backend/Solvix.Api/Solvix.Api.csproj && dotnet restore src/backend/Solvix.Migrator/Solvix.Migrator.csproj

COPY src/backend/ src/backend/
COPY src/shared/ src/shared/
RUN dotnet publish src/backend/Solvix.Api/Solvix.Api.csproj -c Release --no-restore -o /out/api \
 && dotnet publish src/backend/Solvix.Migrator/Solvix.Migrator.csproj -c Release --no-restore -o /out/migrator

# ---------------------------------------------------------------- runtime: api
# Alpine runtime: small, no package installs needed (busybox wget drives the
# health check). Globalization-invariant mode is enabled in Directory.Build.props.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS api
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_EnableDiagnostics=0
# Alpine ships no tz database; .NET resolves IANA ids (e.g. "Europe/Berlin") from it.
# Note: Npgsql probes libgssapi_krb5 once at start-up and logs a load error on Alpine;
# password authentication is unaffected. Add `apk add --no-cache krb5-libs` here if
# GSSAPI/Kerberos authentication to PostgreSQL is ever required.
COPY --from=build /usr/share/zoneinfo /usr/share/zoneinfo
COPY --from=build /out/api .
# PKI volume (device CA, development agent TLS certificate) writable by the app user only.
RUN mkdir -p /var/lib/solvix && chown app /var/lib/solvix && chmod 700 /var/lib/solvix
USER app
EXPOSE 8080 8443
HEALTHCHECK --interval=15s --timeout=5s --start-period=20s --retries=5 \
  CMD wget -qO- http://localhost:8080/health/live >/dev/null || exit 1
ENTRYPOINT ["dotnet", "Solvix.Api.dll"]

# ----------------------------------------------------------- runtime: migrator
# The migrator shares the Infrastructure assemblies, which reference the ASP.NET Core shared framework.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS migrator
WORKDIR /app
COPY --from=build /usr/share/zoneinfo /usr/share/zoneinfo
COPY --from=build /out/migrator .
USER app
ENTRYPOINT ["dotnet", "Solvix.Migrator.dll"]

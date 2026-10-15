FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props LtDatabaseApi.slnx ./
COPY src ./src
RUN dotnet publish src/Ldb.Api/Ldb.Api.csproj -c Release -o /app --no-self-contained

# Chiseled image: non-root user, no shell, no package manager.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS final
WORKDIR /app
COPY --from=build /app .
ENV Kestrel__Endpoints__Http__Url=http://0.0.0.0:8080 \
    DOTNET_EnableDiagnostics=0 \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "Ldb.Api.dll"]

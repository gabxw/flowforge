# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props NuGet.Config ./
# Copy existing lockfiles before restore as well as sources.
COPY src/ src/
RUN dotnet restore src/FlowForge.Worker/FlowForge.Worker.csproj --locked-mode
RUN dotnet publish src/FlowForge.Worker/FlowForge.Worker.csproj -c Release --no-restore -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
USER $APP_UID
ENTRYPOINT ["dotnet", "FlowForge.Worker.dll"]

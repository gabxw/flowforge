FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props NuGet.Config ./
# Mantém restore em cache quando somente o código muda.
COPY src/FlowForge.Domain/FlowForge.Domain.csproj src/FlowForge.Domain/packages.lock.json src/FlowForge.Domain/
COPY src/FlowForge.Application/FlowForge.Application.csproj src/FlowForge.Application/packages.lock.json src/FlowForge.Application/
COPY src/FlowForge.Infrastructure/FlowForge.Infrastructure.csproj src/FlowForge.Infrastructure/packages.lock.json src/FlowForge.Infrastructure/
RUN dotnet restore src/FlowForge.Infrastructure/FlowForge.Infrastructure.csproj --locked-mode
COPY src/FlowForge.Worker/FlowForge.Worker.csproj src/FlowForge.Worker/packages.lock.json src/FlowForge.Worker/
RUN dotnet restore src/FlowForge.Worker/FlowForge.Worker.csproj --locked-mode
COPY src/ src/
RUN dotnet publish src/FlowForge.Worker/FlowForge.Worker.csproj -c Release --no-restore -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
RUN mkdir -p /var/lib/flowforge/keys && chown $APP_UID:$APP_UID /var/lib/flowforge/keys && chmod 700 /var/lib/flowforge/keys
USER $APP_UID
ENTRYPOINT ["dotnet", "FlowForge.Worker.dll"]

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props NuGet.Config ./
# Mantém restore em cache quando somente o código muda.
COPY src/FlowForge.Domain/FlowForge.Domain.csproj src/FlowForge.Domain/packages.lock.json src/FlowForge.Domain/
COPY src/FlowForge.Application/FlowForge.Application.csproj src/FlowForge.Application/packages.lock.json src/FlowForge.Application/
COPY src/FlowForge.Infrastructure/FlowForge.Infrastructure.csproj src/FlowForge.Infrastructure/packages.lock.json src/FlowForge.Infrastructure/
RUN dotnet restore src/FlowForge.Infrastructure/FlowForge.Infrastructure.csproj --locked-mode
COPY src/FlowForge.Api/FlowForge.Api.csproj src/FlowForge.Api/packages.lock.json src/FlowForge.Api/
RUN dotnet restore src/FlowForge.Api/FlowForge.Api.csproj --locked-mode
COPY src/ src/
RUN dotnet publish src/FlowForge.Api/FlowForge.Api.csproj -c Release --no-restore -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
COPY --from=build /app/publish .
RUN mkdir -p /var/lib/flowforge/keys && chown $APP_UID:$APP_UID /var/lib/flowforge/keys && chmod 700 /var/lib/flowforge/keys
USER $APP_UID
ENTRYPOINT ["dotnet", "FlowForge.Api.dll"]

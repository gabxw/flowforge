FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props NuGet.Config ./
# Copy existing lockfiles before restore as well as sources.
COPY src/ src/
RUN dotnet restore src/FlowForge.Api/FlowForge.Api.csproj --locked-mode
RUN dotnet publish src/FlowForge.Api/FlowForge.Api.csproj -c Release --no-restore -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
COPY --from=build /app/publish .
RUN mkdir -p /var/lib/flowforge/keys && chown $APP_UID:$APP_UID /var/lib/flowforge/keys && chmod 700 /var/lib/flowforge/keys
USER $APP_UID
ENTRYPOINT ["dotnet", "FlowForge.Api.dll"]

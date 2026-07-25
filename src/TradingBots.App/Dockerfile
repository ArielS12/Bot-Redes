FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY ["src/TradingBots.App/TradingBots.App.csproj", "src/TradingBots.App/"]
RUN dotnet restore "src/TradingBots.App/TradingBots.App.csproj"

COPY . .
WORKDIR /src/src/TradingBots.App
RUN dotnet publish "TradingBots.App.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

ENV ASPNETCORE_URLS=http://+:5001
ENV ASPNETCORE_ENVIRONMENT=Production
# Render free: evita FileSystemWatcher/inotify (crash "user limit 128 inotify instances").
ENV DOTNET_HOSTBUILDER__RELOADCONFIGONCHANGE=false
ENV DOTNET_USE_POLLING_FILE_WATCHER=true
EXPOSE 5001

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "TradingBots.App.dll"]

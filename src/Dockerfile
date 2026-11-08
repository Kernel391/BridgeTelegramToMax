# Образ моста MAX <-> Telegram. Собирается из КОРНЯ репозитория: docker build -f src/Dockerfile .
# Так его собирают и Visual Studio, и CI (см. .github/workflows/docker.yml). Как запустить: docs/deployment.md.
# См. также https://aka.ms/customizecontainer: как Visual Studio использует этот файл для отладки в контейнере.

# Этот этап используется при запуске из VS в быстром режиме (по умолчанию для конфигурации отладки)
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
# Папка для соответствия чатов MAX и тем Telegram и недавних сообщений (TelegramConfig:StatePath). Чтобы это пережило
# пересоздание контейнера, подключите к /app/state постоянный том. Приложение работает не от root, поэтому владельца
# папки задаём здесь: том при первом подключении наследует его.
RUN mkdir -p /app/state && chown $APP_UID /app/state
USER $APP_UID
EXPOSE 8080
EXPOSE 8081


# Этот этап используется для сборки проекта службы. Конфигурация по умолчанию Release.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["src/BridgeTelegramToMax.csproj", "src/"]
RUN dotnet restore "./src/BridgeTelegramToMax.csproj"
COPY . .
WORKDIR "/src/src"
RUN dotnet build "./BridgeTelegramToMax.csproj" -c $BUILD_CONFIGURATION -o /app/build

# Этот этап используется для публикации проекта службы, который будет скопирован на последний этап
FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./BridgeTelegramToMax.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# Этот этап используется в рабочей среде или при запуске из VS в обычном режиме (по умолчанию, когда конфигурация отладки не используется)
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "BridgeTelegramToMax.dll"]

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src/Auth
COPY Auth/Directory.Build.props Auth/global.json ./
COPY Auth/HabitTracker.Contracts/ HabitTracker.Contracts/
COPY Auth/HabitTracker.Messaging/ HabitTracker.Messaging/
COPY Auth/HabitTracker.Notifications/ HabitTracker.Notifications/
RUN dotnet publish HabitTracker.Notifications/HabitTracker.Notifications.csproj -c Release -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app ./
ENV ASPNETCORE_HTTP_PORTS=80
EXPOSE 80
USER $APP_UID
ENTRYPOINT ["dotnet", "HabitTracker.Notifications.dll"]

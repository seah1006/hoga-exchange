# Runtime only. Build context contains publish/ and web/ generated on the PC.
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY publish/ ./
COPY web/ ./wwwroot/
RUN install -d -m 0700 -o app -g app /data /data/journal /data/keys /data/backups
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "Hoga.Server.dll"]

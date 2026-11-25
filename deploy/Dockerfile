# Runtime only. Build context contains publish/ and web/ generated on the PC.
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY publish/ ./
COPY web/ ./wwwroot/
RUN install -d -o app -g app /data/journal
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "Hoga.Server.dll"]

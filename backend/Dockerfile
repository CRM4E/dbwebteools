FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props ./
COPY backend/DbWeb.Api/ backend/DbWeb.Api/
RUN dotnet restore backend/DbWeb.Api --locked-mode && dotnet publish backend/DbWeb.Api -c Release --no-restore -o /out
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out ./
USER root
RUN mkdir -p /data && chown app:app /data
USER app
ENV ASPNETCORE_HTTP_PORTS=8080 DataDirectory=/data
EXPOSE 8080
ENTRYPOINT ["dotnet","DbWeb.Api.dll"]

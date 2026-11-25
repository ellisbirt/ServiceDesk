FROM node:24-bookworm-slim AS frontend
WORKDIR /src/agentdesk/frontend
COPY agentdesk/frontend/package*.json ./
RUN npm ci
COPY agentdesk/frontend/ ./
ARG VITE_ENTRA_CLIENT_ID
ARG VITE_ENTRA_AUTHORITY
ARG VITE_API_SCOPE
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY helpdesk/backend/src/ helpdesk/backend/src/
COPY agentdesk/backend/ agentdesk/backend/
RUN dotnet publish agentdesk/backend/src/AgentDesk.Api/AgentDesk.Api.csproj -c Release -o /app/publish
COPY --from=frontend /src/agentdesk/frontend/dist/ /app/publish/wwwroot/

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080
COPY --from=build /app/publish/ ./
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "AgentDesk.Api.dll"]

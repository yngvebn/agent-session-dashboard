# Stage 1: Build Angular frontend
FROM node:22-alpine AS frontend-build
WORKDIR /frontend
COPY frontend/package*.json ./
RUN npm ci
COPY frontend/ ./
RUN npm run build

# Stage 2: Build .NET backend
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS backend-build
WORKDIR /src
COPY backend/AgentSessionDashboard/AgentSessionDashboard.csproj ./AgentSessionDashboard/
RUN dotnet restore ./AgentSessionDashboard/AgentSessionDashboard.csproj
COPY backend/AgentSessionDashboard/ ./AgentSessionDashboard/
COPY --from=frontend-build /backend/AgentSessionDashboard/wwwroot/ ./AgentSessionDashboard/wwwroot/
RUN dotnet publish ./AgentSessionDashboard/AgentSessionDashboard.csproj -c Release -o /publish

# Stage 3: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=backend-build /publish ./
VOLUME ["/data"]
ENV DB_PATH=/data/sessions.db
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "AgentSessionDashboard.dll"]

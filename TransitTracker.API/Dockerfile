# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY ["TransitTracker.sln", "."]
COPY ["NuGet.Config", "."]
COPY ["TransitTracker.API/TransitTracker.API.csproj", "TransitTracker.API/"]
COPY ["TransitTracker.Application/TransitTracker.Application.csproj", "TransitTracker.Application/"]
COPY ["TransitTracker.Infrastructure/TransitTracker.Infrastructure.csproj", "TransitTracker.Infrastructure/"]
COPY ["TransitTracker.Domain/TransitTracker.Domain.csproj", "TransitTracker.Domain/"]

RUN dotnet restore "TransitTracker.API/TransitTracker.API.csproj" --configfile NuGet.Config

COPY . .
RUN dotnet publish "TransitTracker.API/TransitTracker.API.csproj" -c Release -o /app/publish --no-restore

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "TransitTracker.API.dll"]

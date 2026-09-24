FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Mapache.sln ./
COPY src/Mapache.Api/Mapache.Api.csproj src/Mapache.Api/
COPY src/Mapache.Domain/Mapache.Domain.csproj src/Mapache.Domain/
COPY src/Mapache.Infrastructure/Mapache.Infrastructure.csproj src/Mapache.Infrastructure/
COPY src/Mapache.Sip/Mapache.Sip.csproj src/Mapache.Sip/
RUN dotnet restore src/Mapache.Api/Mapache.Api.csproj
COPY src/ src/
RUN dotnet publish src/Mapache.Api/Mapache.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "Mapache.Api.dll"]

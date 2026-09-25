# El SDK corre en la arquitectura de quien compila y publica para la de destino: la imagen arm64
# se arma sin emular el compilador con QEMU, que es muy lento.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src
COPY Mapache.sln ./
COPY src/Mapache.Api/Mapache.Api.csproj src/Mapache.Api/
COPY src/Mapache.Domain/Mapache.Domain.csproj src/Mapache.Domain/
COPY src/Mapache.Infrastructure/Mapache.Infrastructure.csproj src/Mapache.Infrastructure/
COPY src/Mapache.Sip/Mapache.Sip.csproj src/Mapache.Sip/
RUN dotnet restore src/Mapache.Api/Mapache.Api.csproj -a $TARGETARCH
COPY src/ src/
RUN dotnet publish src/Mapache.Api/Mapache.Api.csproj -c Release -a $TARGETARCH -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
# El volumen de grabaciones hereda este dueño al crearse: sin esto el usuario app no podría escribir.
RUN mkdir -p /app/data/recordings && chown -R app:app /app/data
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "Mapache.Api.dll"]

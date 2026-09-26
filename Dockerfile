# El SDK corre en la arquitectura de quien compila y publica para la de destino: la imagen arm64
# se arma sin emular el compilador con QEMU, que es muy lento.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src
COPY Wan.sln ./
COPY src/Wan.Api/Wan.Api.csproj src/Wan.Api/
COPY src/Wan.Domain/Wan.Domain.csproj src/Wan.Domain/
COPY src/Wan.Infrastructure/Wan.Infrastructure.csproj src/Wan.Infrastructure/
COPY src/Wan.Sip/Wan.Sip.csproj src/Wan.Sip/
RUN dotnet restore src/Wan.Api/Wan.Api.csproj -a $TARGETARCH
COPY src/ src/
RUN dotnet publish src/Wan.Api/Wan.Api.csproj -c Release -a $TARGETARCH -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
# El volumen de grabaciones hereda este dueño al crearse: sin esto el usuario app no podría escribir.
RUN mkdir -p /app/data/recordings && chown -R app:app /app/data
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "Wan.Api.dll"]

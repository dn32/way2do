# API do Way2Do. Contexto de build: raiz do repositório (docker compose build).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# Restore separado do código: a camada de pacotes só refaz quando o .csproj muda.
COPY src/Way2Do.Api/Way2Do.Api.csproj src/Way2Do.Api/
RUN dotnet restore src/Way2Do.Api/Way2Do.Api.csproj
COPY src/Way2Do.Api/ src/Way2Do.Api/
RUN dotnet publish src/Way2Do.Api/Way2Do.Api.csproj -c Release -o /app --no-restore -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
# curl só para o healthcheck do Compose (a imagem oficial não traz cliente HTTP).
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
# Sem root: usuário "app" da imagem oficial, que já escuta na 8080.
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Way2Do.Api.dll"]

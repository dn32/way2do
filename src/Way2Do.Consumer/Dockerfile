# Consumidor simulado. Contexto de build: raiz do repositório (o CSS da marca, docs/design/way2do.css, é embutido no
# assembly a partir de lá).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/Way2Do.Consumer/Way2Do.Consumer.csproj src/Way2Do.Consumer/
RUN dotnet restore src/Way2Do.Consumer/Way2Do.Consumer.csproj
COPY docs/design/way2do.css docs/design/
COPY src/Way2Do.Consumer/ src/Way2Do.Consumer/
RUN dotnet publish src/Way2Do.Consumer/Way2Do.Consumer.csproj -c Release -o /app --no-restore -p:UseAppHost=false

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
ENTRYPOINT ["dotnet", "Way2Do.Consumer.dll"]

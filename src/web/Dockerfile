# Frontend (build do Vite) servido por nginx sem root. Contexto de build: raiz do repositório (o CSS importa
# docs/design/way2do.css, fonte única dos tokens).
FROM node:24-alpine AS build
WORKDIR /repo/src/web
COPY src/web/package.json src/web/package-lock.json ./
RUN npm ci
COPY docs/design/ /repo/docs/design/
COPY src/web/ ./
RUN npm run build

FROM nginxinc/nginx-unprivileged:1.28-alpine
# Modelo: o entrypoint da imagem gera /etc/nginx/conf.d/default.conf com as variáveis do Compose.
COPY src/web/nginx/templates/ /etc/nginx/templates/
COPY src/web/nginx/security-headers.conf /etc/nginx/snippets/security-headers.conf
COPY --from=build /repo/src/web/dist /usr/share/nginx/html
EXPOSE 8080

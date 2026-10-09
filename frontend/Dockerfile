# syntax=docker/dockerfile:1
FROM node:22-alpine AS build
WORKDIR /app
COPY frontend/package*.json ./
# Use npm ci after generating and committing the lockfile.
RUN if [ -f package-lock.json ]; then npm ci; else npm install; fi
COPY frontend/ ./
RUN npm run build

FROM nginxinc/nginx-unprivileged:1.28-alpine AS runtime
COPY frontend/nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist /usr/share/nginx/html
EXPOSE 8080
HEALTHCHECK --interval=10s --timeout=3s --start-period=10s --retries=3 CMD wget -q -O /dev/null http://127.0.0.1:8080/ || exit 1

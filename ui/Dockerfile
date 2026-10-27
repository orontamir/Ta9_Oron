# Build the Angular app, then serve it with an unprivileged nginx (listens on 8080, runs as non-root).
FROM node:20-alpine AS build
# No analytics prompt in a non-interactive build.
ENV NG_CLI_ANALYTICS=false
WORKDIR /app
COPY package.json package-lock.json ./
# Fail here, with a clear message, if the install was incomplete (e.g. a proxy that breaks TLS to the npm registry).
RUN npm ci --no-audit --no-fund \
    && npm ls @angular/cli @angular-devkit/build-angular --depth=0 \
    || (echo "npm install incomplete: the npm registry is probably not reachable from Docker on this network." \
              "Use scripts/docker-up-prebuilt.ps1 (builds the UI on the host)." && exit 1)
COPY . .
# The local Angular CLI, called directly (no npx, no PATH lookup, no download).
RUN node node_modules/@angular/cli/bin/ng.js build --configuration production

FROM nginxinc/nginx-unprivileged:1.27-alpine
COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist/ta9-ui/browser /usr/share/nginx/html
EXPOSE 8080

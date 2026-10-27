# Build the Angular app, then serve it with an unprivileged nginx (listens on 8080, runs as non-root).
FROM node:20-alpine AS build
# No analytics prompt in a non-interactive build.
ENV NG_CLI_ANALYTICS=false
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY . .
# npm run uses the local Angular CLI from node_modules (npx could try to download a package named "ng").
RUN npm run build -- --configuration production

FROM nginxinc/nginx-unprivileged:1.27-alpine
COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist/ta9-ui/browser /usr/share/nginx/html
EXPOSE 8080

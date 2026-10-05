FROM node:22-alpine AS build
WORKDIR /src
COPY package.json package-lock.json* ./
RUN npm install
COPY . .
RUN npx vue-tsc -b && npx vite build

FROM nginx:alpine AS final
COPY --from=build /src/dist /usr/share/nginx/html
COPY nginx.conf /etc/nginx/conf.d/default.conf
EXPOSE 80

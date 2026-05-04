FROM node:20-alpine

WORKDIR /app

COPY package.json ./
RUN npm install --omit=dev

COPY index.js ./

ENV PORT=8765
EXPOSE 8765

CMD ["node", "index.js"]

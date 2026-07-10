#!/bin/bash
set -e

cd "$(dirname "$0")"

echo "=== Building frontend ==="
cd ~/artgallery/frontend
npm ci
npm run build

echo "=== Copying frontend to backend wwwroot ==="
mkdir -p WebApplication1/WebApplication1/wwwroot
cp -r ~/artgallery/frontend/dist/* WebApplication1/WebApplication1/wwwroot/
mkdir -p WebApplication1/WebApplication1/wwwroot/embed
cp ~/artgallery/frontend/dist/embed.html WebApplication1/WebApplication1/wwwroot/embed/
cp -r ~/artgallery/frontend/dist/assets WebApplication1/WebApplication1/wwwroot/embed/assets

cd ~/APlusInspiration

echo "=== Building backend ==="
docker build -f WebApplication1/Dockerfile -t webapp1 .

echo "=== Stopping old container ==="
docker stop webapp1 2>/dev/null || true
docker rm webapp1 2>/dev/null || true

echo "=== Running ==="
docker run -d -p 8080:8080 -p 8081:8081 \
  --name webapp1 \
  --network appnet \
  -v /data/uploads:/app/uploads \
  -e ConnectionStrings__PhotoAppDb="$DB_CONNECTION_STRING" \
  -e Jwt__Key="$JWT_KEY" \
  -e Stripe__SecretKey="$STRIPE_SECRET_KEY" \
  -e Stripe__PublishableKey="$STRIPE_PUBLISHABLE_KEY" \
  -e Stripe__WebhookSecret="$STRIPE_WEBHOOK_SECRET" \
  -e Stripe__PriceId="$STRIPE_PRICE_ID" \
  webapp1

echo "=== Removing Nginx ==="
sudo systemctl stop nginx 2>/dev/null || true
sudo systemctl disable nginx 2>/dev/null || true

echo "=== Updating Cloudflare Tunnel to point at backend (port 8080) ==="
sudo sed -i 's|--url http://localhost:80|--url http://localhost:8080|' /etc/systemd/system/cloudflared.service
sudo systemctl daemon-reload
sudo systemctl restart cloudflared

docker image prune -f
echo "=== Done ==="

#!/bin/bash
set -e

cd "$(dirname "$0")"

echo "=== Building ==="
docker build -f WebApplication1/Dockerfile -t webapp1 .

echo "=== Stopping old container ==="
docker stop webapp1 2>/dev/null || true
docker rm webapp1 2>/dev/null || true

echo "=== Running ==="
docker run -d -p 8080:8080 -p 8081:8081 \
  --name webapp1 \
  --network appnet \
  -e ConnectionStrings__PhotoAppDb="$DB_CONNECTION_STRING" \
  -e Jwt__Key="$JWT_KEY" \
  -e Stripe__SecretKey="$STRIPE_SECRET_KEY" \
  -e Stripe__PublishableKey="$STRIPE_PUBLISHABLE_KEY" \
  -e Stripe__WebhookSecret="$STRIPE_WEBHOOK_SECRET" \
  -e Stripe__PriceId="$STRIPE_PRICE_ID" \
  webapp1

docker image prune -f
echo "=== Done ==="

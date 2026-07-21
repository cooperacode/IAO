#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$ROOT_DIR"

if [ -f docker-compose.yml ]; then
  docker compose up -d --wait
  docker compose exec -T postgres psql -U todoapp -d todoapp -v ON_ERROR_STOP=1 -f /docker-entrypoint-initdb.d/001-create-tasks.sql
fi

: "${TODO_DB_CONNECTION:=Host=localhost;Port=54329;Database=todoapp;Username=todoapp;Password=todoapp}"
export TODO_DB_CONNECTION

dotnet restore TodoApp.sln
dotnet test TodoApp.sln

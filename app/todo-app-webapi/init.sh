#!/usr/bin/env bash
set -euo pipefail

export TODO_DB_CONNECTION="${TODO_DB_CONNECTION:-Host=localhost;Port=5432;Database=todoapp;Username=todoapp;Password=todoapp}"

docker compose up -d --wait
dotnet restore TodoApp.sln
dotnet test TodoApp.sln

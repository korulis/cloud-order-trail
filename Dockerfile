# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:8.0
WORKDIR /app

COPY . .

WORKDIR /app/Challenge
ENTRYPOINT ["dotnet", "run", "--"]

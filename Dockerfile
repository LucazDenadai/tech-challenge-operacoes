FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/OficinaMecanica.Operacoes.Domain/OficinaMecanica.Operacoes.Domain.csproj src/OficinaMecanica.Operacoes.Domain/
COPY src/OficinaMecanica.Operacoes.Application/OficinaMecanica.Operacoes.Application.csproj src/OficinaMecanica.Operacoes.Application/
COPY src/OficinaMecanica.Operacoes.Infrastructure/OficinaMecanica.Operacoes.Infrastructure.csproj src/OficinaMecanica.Operacoes.Infrastructure/
COPY src/OficinaMecanica.Operacoes.API/OficinaMecanica.Operacoes.API.csproj src/OficinaMecanica.Operacoes.API/

RUN dotnet restore src/OficinaMecanica.Operacoes.API/OficinaMecanica.Operacoes.API.csproj

COPY src/ src/

RUN dotnet publish src/OficinaMecanica.Operacoes.API/OficinaMecanica.Operacoes.API.csproj \
    -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build --chown=app:app --chmod=755 /app/publish .
USER app
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "OficinaMecanica.Operacoes.API.dll"]

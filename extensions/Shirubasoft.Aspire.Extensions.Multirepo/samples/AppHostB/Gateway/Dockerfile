FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ModularSample.Gateway.csproj .
RUN dotnet restore ModularSample.Gateway.csproj
COPY . .
RUN dotnet publish ModularSample.Gateway.csproj --configuration Release --output /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 8080
ENTRYPOINT ["dotnet", "ModularSample.Gateway.dll"]

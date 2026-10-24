FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ModularSample.DotnetApi.csproj .
RUN dotnet restore ModularSample.DotnetApi.csproj
COPY . .
RUN dotnet publish ModularSample.DotnetApi.csproj --configuration Release --output /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 8080
ENTRYPOINT ["dotnet", "ModularSample.DotnetApi.dll"]

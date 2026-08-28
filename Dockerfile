# Stage 1: Build and publish using the .NET SDK image
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy the project file and restore dependencies
COPY ["WebAPI_v1.0.0.csproj", "."]
RUN dotnet restore "./WebAPI_v1.0.0.csproj"

# Copy the rest of the source code and publish
COPY . .
RUN dotnet publish "./WebAPI_v1.0.0.csproj" -c Release -o /app/publish /p:PublishAot=false

# Stage 2: Use the ASP.NET Core runtime image
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
EXPOSE 8080

ENV ASPNETCORE_URLS=http://+:8080

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "WebAPI_v1.0.0.dll"]
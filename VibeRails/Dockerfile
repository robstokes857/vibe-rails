# See https://aka.ms/customizecontainer to learn how to customize your debug container and how Visual Studio uses this Dockerfile to build your images for faster debugging.

# These ARGs allow for swapping out the base used to make the final image when debugging from VS
ARG LAUNCHING_FROM_VS
# This sets the base image for final, but only if LAUNCHING_FROM_VS has been defined
ARG FINAL_BASE_IMAGE=${LAUNCHING_FROM_VS:+aotdebug}

# .NET 11 publishes Ubuntu 26.04 images. Copy its portable binaries onto the
# existing Noble dependency images to keep our Ubuntu 24.04/glibc build floor.
FROM mcr.microsoft.com/dotnet/aspnet:11.0.0-resolute AS dotnet11-runtime
FROM mcr.microsoft.com/dotnet/sdk:11.0.100-resolute AS dotnet11-sdk

# This stage is used when running from VS in fast mode (Default for Debug configuration)
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS base
COPY --from=dotnet11-runtime /usr/share/dotnet /usr/share/dotnet
ENV DOTNET_VERSION=11.0.0 ASPNET_VERSION=11.0.0
USER $APP_UID
WORKDIR /app
EXPOSE 8080


# Shared with deploy/build.ps1's local Linux NativeAOT build.
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble-aot AS build-base
COPY --from=dotnet11-sdk /usr/share/dotnet /usr/share/dotnet
ENV DOTNET_SDK_VERSION=11.0.100 DOTNET_VERSION=11.0.0 ASPNET_VERSION=11.0.0
RUN test "$(dotnet --version)" = "11.0.100"

# This stage is used to build the service project
FROM build-base AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["global.json", "./"]
COPY ["VibeRails/VibeRails.csproj", "VibeRails/"]
RUN dotnet restore "./VibeRails/VibeRails.csproj"
COPY . .
WORKDIR "/src/VibeRails"
RUN dotnet build "./VibeRails.csproj" -c $BUILD_CONFIGURATION -o /app/build

# This stage is used to publish the service project to be copied to the final stage
FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./VibeRails.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=true

# This stage is used as the base for the final stage when launching from VS to support debugging in regular mode (Default when not using the Debug configuration)
FROM base AS aotdebug
USER root
# Install GDB to support native debugging
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
    gdb
USER app

# This stage is used in production or when running from VS in regular mode (Default when not using the Debug configuration)
FROM ${FINAL_BASE_IMAGE:-mcr.microsoft.com/dotnet/runtime-deps:10.0-noble} AS final
WORKDIR /app
EXPOSE 8080
COPY --from=publish /app/publish .
ENTRYPOINT ["./vb"]

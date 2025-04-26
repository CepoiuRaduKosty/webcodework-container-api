# --- Build Stage ---
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /source

# Copy csproj and restore as distinct layers
COPY *.csproj .
# Copy rest of source code
COPY . .
# Publish the application
RUN dotnet publish -c Release -o /app/publish

# --- Runtime Stage ---
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final

# Install C compiler and utilities
RUN apt-get update && apt-get install -y --no-install-recommends \
    gcc \
    libc6-dev \
    coreutils \
    diffutils \
    && rm -rf /var/lib/apt/lists/*

# Create non-root user and sandbox directory
ARG USER_UID=1001
ARG USER_GID=1001
RUN groupadd --gid $USER_GID coder && \
    useradd --uid $USER_UID --gid $USER_GID -m coder && \
    mkdir /sandbox && \
    chown coder:coder /sandbox

WORKDIR /app
COPY --from=build /app/publish .

USER coder
WORKDIR /sandbox # Set working directory for execution consistency

# Expose port (will be overridden by ASPNETCORE_URLS typically)
EXPOSE 5000

# Set default environment variables (can be overridden at runtime)
ENV ASPNETCORE_URLS=http://+:5000 \
    Execution__Language="c" \
    DOTNET_RUNNING_IN_CONTAINER=true 

ENTRYPOINT ["dotnet", "/app/InternalApi.dll"]
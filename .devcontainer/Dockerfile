# Use Ubuntu 24.04 as the base
FROM mcr.microsoft.com/devcontainers/base:ubuntu-24.04

ENV DEBIAN_FRONTEND=noninteractive

# Install dependencies for .NET
RUN apt-get update && apt-get install -y --no-install-recommends \
    curl \
    ca-certificates \
    icu-devtools \
    libicu74 \
    libssl3 \
    xz-utils && \
    rm -rf /var/lib/apt/lists/*

# Set up .NET install environment
ENV DOTNET_ROOT=/usr/share/dotnet
ENV PATH="${PATH}:${DOTNET_ROOT}:${DOTNET_ROOT}/tools"

# Download and run the dotnet-install script for 8, 9, and 10
RUN curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh && \
    chmod +x /tmp/dotnet-install.sh && \
    # Install .NET 8, 9, 10
    /tmp/dotnet-install.sh --channel 8.0 --install-dir $DOTNET_ROOT && \
    /tmp/dotnet-install.sh --channel 9.0 --install-dir $DOTNET_ROOT && \
    /tmp/dotnet-install.sh --channel 10.0 --install-dir $DOTNET_ROOT && \
    rm /tmp/dotnet-install.sh

# Fix permissions for the vscode user (provided by the base image)
RUN chown -R vscode:vscode $DOTNET_ROOT

USER vscode

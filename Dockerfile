# Stage 1 - Build the frontend
FROM node:lts-alpine AS node-build-env
ARG TARGETPLATFORM
ENV TARGETPLATFORM=${TARGETPLATFORM:-linux/amd64}
ARG BUILDPLATFORM
ENV BUILDPLATFORM=${BUILDPLATFORM:-linux/amd64}

RUN mkdir /appclient
WORKDIR /appclient

RUN apk add --no-cache git python3 py3-pip make g++

COPY client ./client
COPY root ./root
RUN \
   cd client && \
   echo "**** Building Code  ****" && \
   npm install && \
   npx ng build --output-path=out

RUN ls -FCla /appclient/root

# Stage 2 - Build the backend
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS dotnet-build-env
ARG TARGETPLATFORM
ENV TARGETPLATFORM=${TARGETPLATFORM:-linux/amd64}
ARG BUILDPLATFORM
ENV BUILDPLATFORM=${BUILDPLATFORM:-linux/amd64}

RUN mkdir /appserver
WORKDIR /appserver

COPY server ./server
RUN \
   echo "**** Building Source Code for $TARGETPLATFORM on $BUILDPLATFORM ****" && \
   cd server && \
   dotnet restore --no-cache RdtClient.sln && \
   dotnet test && \
   dotnet publish --no-restore -c Release -o out ; 

# Stage 3 - Build runtime image
FROM ghcr.io/linuxserver/baseimage-alpine:3.20
ARG TARGETPLATFORM
ENV TARGETPLATFORM=${TARGETPLATFORM:-linux/amd64}
ARG BUILDPLATFORM
ENV BUILDPLATFORM=${BUILDPLATFORM:-linux/amd64}

# set version label
ARG BUILD_DATE
ARG VERSION
LABEL build_version="Linuxserver.io extended version:- ${VERSION} Build-date:- ${BUILD_DATE}"
LABEL maintainer="ravensorb"

# set environment variables
ARG DEBIAN_FRONTEND="noninteractive"
ENV XDG_CONFIG_HOME="/config/xdg"
ENV RDTCLIENT_BRANCH="main"

RUN \
   mkdir -p /data/downloads /data/db || true && \
   echo "**** Updating package information ****" && \
   apk update && \
   echo "**** Install pre-reqs ****" && \
   apk add bash icu-libs krb5-libs libgcc libintl libssl3 libstdc++ zlib && \
   echo "**** Installing dotnet ****" && \
   mkdir -p /usr/share/dotnet

RUN \
   set -eu ; \
   TP="${TARGETPLATFORM:-}" ; \
   case "$(uname -m)" in \
     aarch64) HOST_TP=linux/arm64 ;; \
     armv7l)  HOST_TP=linux/arm/v7 ;; \
     x86_64)  HOST_TP=linux/amd64 ;; \
     *)       HOST_TP="" ;; \
   esac ; \
   if [ -z "$TP" ]; then \
     if [ -n "$HOST_TP" ]; then \
       TP="$HOST_TP" ; \
       echo "INFO: TARGETPLATFORM unset, derived from host arch ($(uname -m)) -> ${TP}" ; \
     else \
       echo "ERROR: TARGETPLATFORM unset and host arch $(uname -m) is not in {x86_64, aarch64, armv7l}. Pass --build-arg TARGETPLATFORM=linux/<amd64|arm64|arm/v7> or use 'docker buildx build --platform ...'." >&2 ; \
       exit 1 ; \
     fi ; \
   fi ; \
   if [ -n "$HOST_TP" ] && [ "$TP" != "$HOST_TP" ]; then \
     echo "WARN: TARGETPLATFORM=${TP} differs from host arch (${HOST_TP}). Cross-compile only works under buildx; classic 'docker build --platform' silently produces host-arch binaries — verify the final image's dotnet binary matches the target." ; \
   fi ; \
   case "$TP" in \
     linux/arm/v7) ARCH=arm ;; \
     linux/arm64)  ARCH=arm64 ;; \
     linux/amd64)  ARCH=x64 ;; \
     *) echo "ERROR: unsupported TARGETPLATFORM='$TP' — expected linux/amd64, linux/arm64, or linux/arm/v7." >&2 ; exit 1 ;; \
   esac ; \
   echo "**** Installing aspnetcore-runtime for ${TP} (musl-${ARCH}) ****" ; \
   wget -q "https://builds.dotnet.microsoft.com/dotnet/aspnetcore/Runtime/10.0.0/aspnetcore-runtime-10.0.0-linux-musl-${ARCH}.tar.gz" ; \
   tar zxf "aspnetcore-runtime-10.0.0-linux-musl-${ARCH}.tar.gz" -C /usr/share/dotnet ; \
   rm -f "aspnetcore-runtime-10.0.0-linux-musl-${ARCH}.tar.gz"

RUN \
   echo "**** Setting permissions ****" && \
   chown -R abc:abc /data && \
   rm -rf \
   /tmp/* \
   /var/cache/apk/* \
   /var/tmp/* || true

ENV PATH "$PATH:/usr/share/dotnet"

# Copy files for app
WORKDIR /app
COPY --from=dotnet-build-env /appserver/server/out .
COPY --from=node-build-env /appclient/client/out/browser ./wwwroot
COPY --from=node-build-env /appclient/root/ /

# ports and volumes
EXPOSE 6500

# Check Status
HEALTHCHECK --interval=30s --timeout=30s --start-period=30s --retries=3 CMD curl --fail http://localhost:6500 || exit 

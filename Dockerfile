# Ripparr: the app plus every tool it drives, each at a pinned version.
# The weekly "Tool updates" workflow bumps these ARG lines in a pull request; nothing updates on its own.
ARG MAKEMKV_VERSION=1.18.4
ARG DGDEMUX_VERSION=1.0.0.85
ARG MKVTOOLNIX_VERSION=103.0
ARG MEDIAINFO_VERSION=24.12
ARG DOVI_TOOL_VERSION=2.3.4
ARG FFMPEG_VERSION=8.1
ARG SEVENZIP_VERSION=25.01

# MakeMKV built from its own source (versions older than the newest move to /download/old/) on the same Ubuntu as the final image, command line only (no Qt).
# makemkv-bin holds the closed part (makemkvcon); building it means accepting its EULA,
# which is why the image is for your own use and the package stays private.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS makemkv
ARG MAKEMKV_VERSION
RUN apt-get update && apt-get install -y --no-install-recommends \
        build-essential pkg-config curl ca-certificates \
        libc6-dev libssl-dev libexpat1-dev zlib1g-dev libavcodec-dev \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /build
RUN set -eux; \
    case "${MAKEMKV_VERSION}" in 1.*) ;; *) echo "MakeMKV ${MAKEMKV_VERSION}: 2.x is held back, see README" >&2; exit 1 ;; esac; \
    for part in oss bin; do \
        name="makemkv-${part}-${MAKEMKV_VERSION}.tar.gz"; \
        curl -fsSL "https://www.makemkv.com/download/${name}" -o "${part}.tar.gz" \
            || curl -fsSL "https://www.makemkv.com/download/old/${name}" -o "${part}.tar.gz"; \
        tar -xzf "${part}.tar.gz"; \
    done; \
    cd "makemkv-oss-${MAKEMKV_VERSION}"; \
    ./configure --prefix=/usr --disable-gui; \
    make -j"$(nproc)"; \
    make install DESTDIR=/out; \
    cd "../makemkv-bin-${MAKEMKV_VERSION}"; \
    mkdir -p tmp; echo accepted > tmp/eula_accepted; \
    make; \
    make install DESTDIR=/out PREFIX=/usr; \
    ls -l /out/usr/bin

# DGDemux for Linux from rationalqm.us (Donald Graft). The site refuses downloads from build
# servers (403 to GitHub's runners), so the image is built without it when that happens and the
# binary goes in /config/tools/dgdemux instead (Ripparr uses that file when it exists).
FROM debian:13-slim AS dgdemux
ARG DGDEMUX_VERSION
RUN apt-get update && apt-get install -y --no-install-recommends curl ca-certificates unzip \
    && rm -rf /var/lib/apt/lists/*
RUN set -eux; \
    mkdir -p /tmp/dgdemux /out; \
    if curl -fsSL "https://www.rationalqm.us/dgdemux/binaries/dgdemux_linux_${DGDEMUX_VERSION}.zip" -o /tmp/dgdemux.zip; then \
        unzip -q /tmp/dgdemux.zip -d /tmp/dgdemux; \
        bin="$(find /tmp/dgdemux -type f -iname dgdemux | head -n1)"; \
        test -n "$bin"; \
        install -m 0755 "$bin" /out/dgdemux; \
    else \
        echo "WARNING: DGDemux ${DGDEMUX_VERSION} could not be downloaded; put it in /config/tools/dgdemux" >&2; \
    fi

# MediaInfo CLI, the static build MediaArea makes for AWS Lambda (no library packages needed).
FROM debian:13-slim AS mediainfo
ARG MEDIAINFO_VERSION
RUN apt-get update && apt-get install -y --no-install-recommends curl ca-certificates unzip \
    && rm -rf /var/lib/apt/lists/*
RUN set -eux; \
    curl -fsSL "https://mediaarea.net/download/binary/mediainfo/${MEDIAINFO_VERSION}/MediaInfo_CLI_${MEDIAINFO_VERSION}_Lambda_x86_64.zip" -o /tmp/mediainfo.zip; \
    mkdir -p /tmp/mediainfo /out; \
    unzip -q /tmp/mediainfo.zip -d /tmp/mediainfo; \
    install -m 0755 "$(find /tmp/mediainfo -type f -name mediainfo | head -n1)" /out/mediainfo; \
    /out/mediainfo --version

# Static ffprobe from BtbN, the same build Muxarr, Salvarr and Specarr use.
FROM debian:13-slim AS ffprobe
ARG FFMPEG_VERSION
RUN apt-get update && apt-get install -y --no-install-recommends curl ca-certificates xz-utils \
    && rm -rf /var/lib/apt/lists/*
RUN set -eux; \
    curl -fsSL "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-n${FFMPEG_VERSION}-latest-linux64-gpl-${FFMPEG_VERSION}.tar.xz" -o /tmp/ffmpeg.tar.xz; \
    mkdir -p /out; \
    tar -xJf /tmp/ffmpeg.tar.xz -C /out --strip-components=2 --wildcards '*/bin/ffprobe'; \
    /out/ffprobe -version

# dovi_tool static (musl) build from quietvoid.
FROM debian:13-slim AS dovi
ARG DOVI_TOOL_VERSION
RUN apt-get update && apt-get install -y --no-install-recommends curl ca-certificates \
    && rm -rf /var/lib/apt/lists/*
RUN set -eux; \
    curl -fsSL "https://github.com/quietvoid/dovi_tool/releases/download/${DOVI_TOOL_VERSION}/dovi_tool-${DOVI_TOOL_VERSION}-x86_64-unknown-linux-musl.tar.gz" -o /tmp/dovi_tool.tar.gz; \
    mkdir -p /out; \
    tar -xzf /tmp/dovi_tool.tar.gz -C /out; \
    /out/dovi_tool --version

# 7-Zip (7zz) from the official ip7z releases: lists and unpacks UDF ISOs without mounting them.
FROM debian:13-slim AS sevenzip
ARG SEVENZIP_VERSION
RUN apt-get update && apt-get install -y --no-install-recommends curl ca-certificates xz-utils \
    && rm -rf /var/lib/apt/lists/*
RUN set -eux; \
    short="$(echo "${SEVENZIP_VERSION}" | tr -d .)"; \
    curl -fsSL "https://github.com/ip7z/7zip/releases/download/${SEVENZIP_VERSION}/7z${short}-linux-x64.tar.xz" -o /tmp/7z.tar.xz; \
    mkdir -p /tmp/7z /out; \
    tar -xJf /tmp/7z.tar.xz -C /tmp/7z; \
    install -m 0755 /tmp/7z/7zz /out/7zz; \
    /out/7zz | head -n 3

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json ./
COPY src/Ripparr/Ripparr.csproj src/Ripparr/
RUN dotnet restore src/Ripparr/Ripparr.csproj
COPY src/ src/
RUN dotnet publish src/Ripparr/Ripparr.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
ARG MAKEMKV_VERSION
ARG DGDEMUX_VERSION
ARG MKVTOOLNIX_VERSION
ARG MEDIAINFO_VERSION
ARG DOVI_TOOL_VERSION
ARG FFMPEG_VERSION
ARG SEVENZIP_VERSION

# MKVToolNix from the project's own repo (Ubuntu's is v82 from 2024), key checked against the
# fingerprint on mkvtoolnix.download/authenticity.html, as in Muxarr. libavcodec, libexpat and
# libssl are what makemkvcon links to.
RUN apt-get update && apt-get install -y --no-install-recommends \
        curl ca-certificates gnupg tzdata libexpat1 libavcodec60 \
    && . /etc/os-release \
    && curl -fsSL https://mkvtoolnix.download/gpg-pub-moritzbunkus.gpg -o /usr/share/keyrings/mkvtoolnix.gpg \
    && gpg --show-keys --with-colons /usr/share/keyrings/mkvtoolnix.gpg | grep -q '^fpr:::::::::D9199745B0545F2E8197062B0F92290A445B9007:' \
    && echo "deb [signed-by=/usr/share/keyrings/mkvtoolnix.gpg] https://mkvtoolnix.download/ubuntu/ ${VERSION_CODENAME} main" > /etc/apt/sources.list.d/mkvtoolnix.list \
    && apt-get update && apt-get install -y --no-install-recommends "mkvtoolnix=${MKVTOOLNIX_VERSION}-*" \
    && apt-get purge -y gnupg && apt-get autoremove -y \
    && rm -rf /var/lib/apt/lists/*

COPY --from=makemkv /out/usr/ /usr/
COPY --from=dgdemux /out/ /usr/local/bin/
COPY --from=mediainfo /out/mediainfo /usr/local/bin/mediainfo
COPY --from=ffprobe /out/ffprobe /usr/local/bin/ffprobe
COPY --from=dovi /out/dovi_tool /usr/local/bin/dovi_tool
COPY --from=sevenzip /out/7zz /usr/local/bin/7zz
RUN ln -s /usr/local/bin/7zz /usr/local/bin/7z && ldconfig

WORKDIR /app
COPY --from=build /app ./

# /config holds ripparr.db, the job logs and MakeMKV's settings.conf (the key). Never put the key in compose.
RUN mkdir -p /config && chown $APP_UID /config
VOLUME /config

# The pins show on the Tools page next to what each tool reports and the newest upstream version.
ENV ASPNETCORE_HTTP_PORTS=8186 \
    TZ=Etc/UTC \
    RIPPARR_PIN_MAKEMKV=${MAKEMKV_VERSION} \
    RIPPARR_PIN_DGDEMUX=${DGDEMUX_VERSION} \
    RIPPARR_PIN_MKVTOOLNIX=${MKVTOOLNIX_VERSION} \
    RIPPARR_PIN_MEDIAINFO=${MEDIAINFO_VERSION} \
    RIPPARR_PIN_DOVI_TOOL=${DOVI_TOOL_VERSION} \
    RIPPARR_PIN_FFPROBE=${FFMPEG_VERSION} \
    RIPPARR_PIN_7ZIP=${SEVENZIP_VERSION}
EXPOSE 8186

# Compose picks the user (user: 1000:1000) and adds the cdrom group for the disc drive.
USER $APP_UID
ENTRYPOINT ["dotnet", "Ripparr.dll"]

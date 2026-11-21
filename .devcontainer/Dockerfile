# The development sandbox (`tools/dev`, AGENTS.md): the .NET 10 SDK the projects target (net10.0, CI: 10.0.x),
# clang 19 for native/ (#embed needs 19+), vpk and mksquashfs for packaging/pack-linux.sh, Node and the AI coding CLIs.
# Everything that needs macOS (apps/mac, Unity editor, the .dylib) is built on the Mac or a UTM virtual machine.
ARG NODE_IMAGE=node:26.13.1-trixie-slim@sha256:e6bedef217677b276c591795e169840edd9096dbf13f29df8ef8d1856b8820bc
ARG DOTNET_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0.404@sha256:bef136a371963aaa46ec97882845ef6f54dec15728bb4b128b1c2a400ffbc9b4

FROM ${NODE_IMAGE} AS node

FROM ${DOTNET_IMAGE}
COPY --from=node /usr/local/ /usr/local/

RUN apt-get update -qq \
 && apt-get install -y -qq --no-install-recommends \
      git make curl procps less ripgrep sudo clang-19 python3 file squashfs-tools >/dev/null \
 && rm -rf /var/lib/apt/lists/* \
 && ln -s /usr/bin/clang-19 /usr/local/bin/clang

ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
    PATH=/opt/dotnet-tools:$PATH
# The same vpk as .github/workflows/release.yml.
RUN dotnet tool install --tool-path /opt/dotnet-tools vpk --version 1.2.161 >/dev/null && rm -rf /root/.nuget

RUN npm install -g -s @anthropic-ai/claude-code @openai/codex && rm -rf /root/.npm

# A non-root user with sudo (instead of the image's own `ubuntu`, uid 1000): agents may install what they need, it stays inside this container.
RUN userdel -r ubuntu && useradd -m -s /bin/bash -u 1000 dev \
 && echo 'dev ALL=(ALL) NOPASSWD:ALL' > /etc/sudoers.d/dev \
 && git config --system safe.directory '*'

USER dev
WORKDIR /work

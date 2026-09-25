FROM ubuntu:24.04
RUN apt-get update && apt-get install -y --no-install-recommends \
    ca-certificates xvfb xauth libgl1-mesa-dri libglx-mesa0 libicu74 libssl3t64 zlib1g \
    libfontconfig1 libxext6 libxrender1 libxi6 libxtst6 libxrandr2 libasound2t64 fonts-dejavu-core \
    && rm -rf /var/lib/apt/lists/* \
    && useradd --uid 10001 --create-home benchmark
USER 10001:10001
ENV HOME=/home/benchmark LIBGL_ALWAYS_SOFTWARE=1
ENTRYPOINT ["xvfb-run", "-a", "-s", "-screen 0 1280x720x24", "/harness/Nexa.Minecraft.Benchmarks"]

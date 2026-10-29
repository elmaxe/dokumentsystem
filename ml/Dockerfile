# Dokumentsystem's machine learning service (docs/design.md §11). Build with --build-arg VARIANT=cuda for NVIDIA GPUs.
FROM python:3.12-slim

ARG VARIANT=cpu
ARG VERSION=0.0.0-dev

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    PIP_NO_CACHE_DIR=1 \
    DOKUMENT_VERSION=${VERSION} \
    ML_CACHE_DIR=/cache \
    HF_HOME=/cache/huggingface

WORKDIR /app
COPY pyproject.toml ./
COPY dokument_ml ./dokument_ml
RUN pip install . \
    && if [ "$VARIANT" = "cuda" ]; then pip install nvidia-cublas-cu12==12.9.2.10 nvidia-cudnn-cu12==9.27.0.42; fi

# CTranslate2 finds cuBLAS and cuDNN from the pip packages above; the paths are harmless on the CPU variant.
ENV LD_LIBRARY_PATH=/usr/local/lib/python3.12/site-packages/nvidia/cublas/lib:/usr/local/lib/python3.12/site-packages/nvidia/cudnn/lib

RUN useradd --system --uid 1000 --home-dir /app ml && mkdir -p /cache && chown ml /cache
USER ml
VOLUME /cache
EXPOSE 3003

# Any HTTP answer counts, so a configured API key doesn't make the container look unhealthy.
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s CMD ["python", "-c", "import urllib.request as u, urllib.error as e\ntry: u.urlopen('http://localhost:3003/health', timeout=4)\nexcept e.HTTPError: pass"]

CMD ["uvicorn", "dokument_ml.main:app", "--host", "0.0.0.0", "--port", "3003"]

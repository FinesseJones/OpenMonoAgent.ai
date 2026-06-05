# OpenMono.ai — Scrapling scrape service (backs the agent's WebFetch tool).
FROM python:3.12-slim

ENV PYTHONUNBUFFERED=1 \
    PIP_NO_CACHE_DIR=1 \
    PIP_DISABLE_PIP_VERSION_CHECK=1

# Base libs the browsers need; Playwright pulls the rest via --with-deps below.
# xvfb provides a virtual display so headed (headless=false) browser launches
# work in this displayless container — the server is wrapped in xvfb-run below.
RUN apt-get update && apt-get install -y --no-install-recommends \
        wget ca-certificates fonts-liberation xvfb \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY requirements.txt .
RUN pip install -r requirements.txt

# Download browsers + OS deps used by StealthyFetcher (Camoufox) and the
# Playwright-based fetchers. Kept in a separate layer so app edits don't
# re-trigger the (slow) browser download.
RUN python -m playwright install --with-deps chromium \
    && scrapling install

COPY app.py .

EXPOSE 5000
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=5 \
    CMD python -c "import urllib.request,sys; sys.exit(0 if urllib.request.urlopen('http://localhost:5000/health').status==200 else 1)"
# Wrap in xvfb-run so a virtual X display is always available; headless launches
# ignore it, headed ones render into it. -a picks a free server number.
CMD ["xvfb-run", "-a", "--server-args=-screen 0 1280x1024x24", \
     "uvicorn", "app:app", "--host", "0.0.0.0", "--port", "5000"]

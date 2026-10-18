# Building from Source { #building-from-source }

You'll need the .NET 10 SDK, Node.js 22+, and Rust 1.94+ (matching what the Dockerfile builds with).

```bash
git clone https://github.com/regix1/lancache-manager.git
cd lancache-manager

# Rust processor
cd rust-processor && cargo build --release

# Web interface
cd ../Web && npm install && npm run dev  # http://localhost:3000

# API
cd ../Api/LancacheManager && dotnet run  # http://localhost:5000
```

Rust tests (from `rust-processor/`):

```bash
cargo test --no-fail-fast
```

19 of the tests need a PostgreSQL server. Point `LANCACHE_TEST_DATABASE_URL` at one (for example
`postgres://lancache:lancache@127.0.0.1:5432/lancache`) and they run; if that variable is unset they
skip.

Multi-arch Docker build:

```bash
docker buildx build \
  --platform linux/amd64,linux/arm64 \
  -t ghcr.io/regix1/lancache-manager:latest \
  --push .
```

-----

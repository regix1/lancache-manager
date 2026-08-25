# 从源码构建 { #building-from-source }

你需要 .NET 10 SDK、Node.js 22+ 和 Rust 1.94+（与 Dockerfile 构建所用版本一致）。

```bash
git clone https://github.com/regix1/lancache-manager.git
cd lancache-manager

# Rust 处理器
cd rust-processor && cargo build --release

# Web 界面
cd ../Web && npm install && npm run dev  # http://localhost:3000

# API
cd ../Api/LancacheManager && dotnet run  # http://localhost:5000
```

Rust 测试（在 `rust-processor/` 目录下运行）：

```bash
cargo test --no-fail-fast
```

其中 15 个测试需要一个 PostgreSQL 服务器。将 `DATABASE_URL` 指向一个可用的服务器（例如
`postgres://lancache:lancache@127.0.0.1:5432/lancache`），这些测试才会运行；否则它们会
因连接错误而失败，而不是被静默跳过。

多架构 Docker 构建：

```bash
docker buildx build \
  --platform linux/amd64,linux/arm64 \
  -t ghcr.io/regix1/lancache-manager:latest \
  --push .
```

-----

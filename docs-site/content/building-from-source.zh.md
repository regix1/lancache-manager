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

多架构 Docker 构建：

```bash
docker buildx build \
  --platform linux/amd64,linux/arm64 \
  -t ghcr.io/regix1/lancache-manager:latest \
  --push .
```

-----

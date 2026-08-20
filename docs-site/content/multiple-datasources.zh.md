# 多数据源 { #multiple-datasources }

大多数人只运行单个 LANCache 实例，永远用不到这一节。只有当服务分散在不同的缓存目录，或者需要把多台 LANCache 服务器合并到一个仪表板中时，才需要它。

"数据源"是一对成组的日志 + 缓存目录。每个数据源被单独处理和跟踪，然后在仪表板和下载视图中汇总。

常见的使用场景：

- **服务外包到独立存储**——Steam 与其他服务位于不同的驱动器上。
- **多个 LANCache 实例**——为不同房间或不同用途分别部署缓存服务器。
- **分区存储**——不同服务位于不同的分区。

### 自动发现（推荐）

把应用指向父目录，让它自己扫描：

```yaml
environment:
  - LanCache__LogPath=/logs
  - LanCache__CachePath=/cache
  - LanCache__AutoDiscoverDatasources=true
```

自动发现会把你的缓存路径和日志路径成对地逐层遍历，最深到根目录下第三层。该深度是固定的，不可配置。只要某一层的缓存文件夹和日志文件夹都有实际内容，这一层就会成为一个数据源；发现一个数据源之后，仍会继续搜索它的内部：

1. **根目录**——如果 `/logs/access.log` 存在，且 `/cache` 中包含 LANCache 的哈希目录（`00/`、`01/` 等），根目录会成为 "Default"。
2. **嵌套文件夹**——第 1 到第 3 层中任何匹配成功的缓存/日志文件夹对，都会创建一个以该缓存文件夹命名的数据源（例如 `/cache/steam` + `/logs/steam` → "Steam"）。
3. **第 4 层及更深处永远不会被扫描**——请把文件夹上移一层，或改用手动配置。

匹配规则：

- **名称匹配**先精确匹配，再忽略大小写，最后做归一化匹配（忽略短横线、下划线以及末尾的 "s"）。
- **命名不同的中间包装文件夹不会阻断发现。** 如果某一层恰好只有一个缓存文件夹和一个日志文件夹，而两者名称不一致，这一对仍会被匹配上。而两个各自已经是有效数据源的文件夹，永远不会被互相配对。
- **会被跳过、但不影响扫描继续进行的：** 隐藏和系统文件夹、LANCache 的两字符哈希桶、符号链接，以及无法读取的分支。
- **与已发现的数据源重名的名称会被跳过并记录日志**，而不是悄悄覆盖前一个。
- **如果任何地方都没有发现有效结构，** 应用会回退到使用你配置的路径构建单个 `default` 数据源。

带分组父目录的示例布局，仍然只创建三个数据源（Default、Steam、Epic）：

```
/mnt/lancache/
├── cache/
│   ├── 00/, 01/, a1/, ff/       ← 默认缓存（哈希目录在根级，第 0 层）
│   └── outsourced/
│       ├── steam/
│       │   └── 00/, 01/, ...    ← Steam，第 2 层
│       └── epic/
│           └── 00/, 01/, ...    ← Epic，第 2 层
└── logs/
    ├── access.log               ← 默认日志
    └── outsourced/
        ├── steam/
        │   └── access.log       ← Steam 日志
        └── epic/
            └── access.log       ← Epic 日志
```

如果一个缓存文件夹在同一层级下没有对应的日志文件夹（反之亦然），会被静默跳过。它不会成为数据源，也不会报错。如果驱动器或目录结构过于不对称，自动发现无法正确配对，请改用下面的手动配置显式声明数据源。

### 手动配置

如果驱动器完全位于不同位置，或者需要更精细的控制，可以显式声明每个数据源。若两者都设置了，手动配置优先于自动发现。

```yaml
environment:
  # 主 LANCache
  - LanCache__DataSources__0__Name=Default
  - LanCache__DataSources__0__CachePath=/cache
  - LanCache__DataSources__0__LogPath=/logs
  - LanCache__DataSources__0__Enabled=true

  # 独立驱动器上的 Steam
  - LanCache__DataSources__1__Name=Steam
  - LanCache__DataSources__1__CachePath=/steam-cache
  - LanCache__DataSources__1__LogPath=/steam-logs
  - LanCache__DataSources__1__Enabled=true
```

配合相应的数据卷挂载：

```yaml
volumes:
  - /mnt/lancache/cache:/cache:ro
  - /mnt/lancache/logs:/logs:ro
  - /mnt/steam-drive/cache:/steam-cache:ro
  - /mnt/steam-drive/logs:/steam-logs:ro
```

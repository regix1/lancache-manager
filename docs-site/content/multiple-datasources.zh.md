# 多数据源 { #multiple-datasources }

大多数人只运行单个 LANCache 实例，永远用不到这一节。只有当服务分散在不同的缓存目录，或者需要把多台 LANCache 服务器合并到一个仪表板中时，才需要它。

"数据源"是一对成组的日志 + 缓存目录。每个数据源被单独处理和跟踪，然后在仪表板和下载视图中汇总。

数据源名称是持久身份。移动或暂时禁用数据源时，请保持名称不变。已禁用或已退役名称下的下载和日志观察记录仍可读取。全局删除可以使用保留的 URL 历史，但只会修改处于活动状态且数据采集成功完成的数据源文件。

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

### 部署方式与日志写入进程

请根据 LANCache Manager 与日志写入进程的实际运行位置选择配置。目录布局只能说明磁盘上的文件形式，不能证明哪个进程正在写入这些文件。

| 部署方式 | Manager 配置 | 挂载与写入证明 | 应用更改 |
| --- | --- | --- | --- |
| Windows 原生安装，旧式或静态日志 | 在应用环境变量或应用配置中设置 `LanCache__CachePath` 和 `LanCache__LogPath`。 | 指向现有缓存与日志目录。对于复制的日志，记录其刷新方式。 | 重启 LANCache Manager。 |
| Linux 原生安装，nginx 运行在主机上 | 在应用环境中设置主机缓存根目录和 nginx 日志根目录。 | 为 Manager 账户授予读取权限，并确认主机 nginx 进程及其生效配置写入这些文件。 | 重启 LANCache Manager。 |
| 容器部署，每个服务一个日志文件 | 在 Manager 服务中添加显式的 `LanCache__DataSources__N__*` 环境变量。 | 挂载每个缓存根目录和包含 `*-access.log` 的目录，并确认缓存容器或主机 nginx 配置写入这些挂载路径。 | 重新创建 Manager 容器。 |
| 单体容器 | 为单体缓存与日志挂载设置一个旧式数据源或一个显式数据源。 | 挂载单体缓存和 `access.log` 所在目录，并确认单体容器拥有日志写入进程。 | 重新创建 Manager 容器。 |
| 静态或导入日志 | 为导入的缓存与日志根目录声明显式数据源。 | 提供可重复的复制、同步或导出任务，并验证出现新的源记录。仅有看似正确的目录结构不能证明写入进程存在。 | 原生安装重启 Manager；容器安装重新创建 Manager 容器。 |
| Windows 原生 Manager 配合实时 Docker 日志写入进程 | 在原生应用环境或配置中设置数据源。 | 使用接收容器卷的主机路径，并确认运行中的容器写入这些准确路径。当前 Windows 原生实现不支持对该 Docker 写入进程占用的日志执行破坏性更改。 | 在挂载相同缓存和日志路径的 Linux 环境中运行 Manager，或提供静态或复制的日志源。 |

原生安装在进程启动时读取应用配置和环境变量。Compose 安装在创建容器时读取服务环境和卷定义，因此修改 Compose 文件后必须重新创建容器。

日志摄取、状态视图和其他只读操作与破坏性日志更改相互独立。不改写日志的纯缓存清除也保持可用。在任何物理更改开始前，破坏性操作会检查所有符合条件的已选日志目标。设置 GET 只提供建议性状态，不会锁定文件或向写入进程发送信号。

在 Windows 原生环境使用 Docker Desktop 的测试中，实时绑定挂载的日志可能在文件替换后仍连接到旧文件。这个部署环境中的观察结果不表示所有 Windows 文件系统都缺少原子重命名。LANCache Manager 会拒绝这种组合，因为当前实现无法保证已验证 Docker 写入进程的日志能安全发布并重新打开。如需执行破坏性日志更改，请使用挂载相同路径的 Linux Manager，或使用静态日志源。

`Enabled=true` 表示该数据源参与当前读取、扫描和文件修改。`sourceCount` 表示一个数据源内部发现的日志流数量，不是数据源数量。如果显式列表中的所有条目都被禁用，则启用的数据源数量为零，也不会生成默认行。

缺失的显式或自动发现根目录会保持不可用状态。LANCache Manager 不会创建这些目录或 `http` 子目录来把不可用挂载伪装成空目录。为了兼容旧配置，旧式单路径模式可以在启动时初始化两个配置根目录；之后的扫描不会重新创建缺失根目录。

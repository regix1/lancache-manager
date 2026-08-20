# 自定义主题 { #custom-themes }

打开**管理 → 主题 → 主题管理**，即可从零开始构建主题并实时预览每个颜色分组，浏览并安装社区主题，或以 TOML 格式导入/导出主题。

主题保存在 `/data/themes/`。最简格式如下：

```toml
[meta]
name = "My Theme"
id = "my-theme"

[colors]
primaryColor = "#3b82f6"
bgPrimary = "#111827"
bgSecondary = "#1f2937"
textPrimary = "#ffffff"
```

这就是全部要求：`meta.name`、`meta.id` 和这四个颜色。缺少其中任何一项的主题会在加载时被拒绝。`isDark`、`version`、`author` 等可选项可以在此基础上添加。

-----

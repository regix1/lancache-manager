# 贡献翻译 { #contributing-translations }

LANCache Manager 支持国际化（i18n），欢迎社区贡献翻译。UI 的字符串都来自 `Web/src/i18n/locales/` 中的语言文件。

### 如何贡献

1. 在 GitHub 上 **Fork 这个仓库**。
2. 打开 `Web/src/i18n/locales/`。
3. 把 `en.json` 复制一份，命名为你的语言（例如 `de.json`、`fr.json`、`es.json`、`pt-BR.json`）。
4. 翻译其中的值，键保持不变。
5. **注册这门语言**——在 `Web/src/i18n/index.ts` 中导入你的文件并把它加入 `resources` 和 `supportedLngs`，再把它的标签加入 `Web/src/components/common/LanguageSelector.tsx` 中的 `LANGUAGE_LABELS`。缺少这一步，应用永远不会读取你的文件。
6. 提交一个 **Pull Request**。

### 文件结构

```
Web/src/i18n/locales/
├── en.json          ← 英语（参考）
├── de.json          ← 德语（你的贡献）
├── fr.json          ← 法语（你的贡献）
└── ...
```

### 指南

- **不要更改 JSON 键**——只翻译字符串值。
- **保留占位符**——`{{variable}}` 保持原样（例如 `{{name}}`）。
- **保留格式**——`<strong>` 这类 HTML 标签保持不变。
- **本地测试**——运行应用，确认你的翻译能正确渲染。

### 翻译示例

```json
// 简化示意——真实的键在 en.json 中
{
  "dashboard": {
    "title": "Dashboard",
    "recentDownloads": "Recent Downloads",
    "totalCache": "Total Cache: {{size}}"
  }
}

// 你的翻译（de.json）
{
  "dashboard": {
    "title": "Übersicht",
    "recentDownloads": "Letzte Downloads",
    "totalCache": "Gesamter Cache: {{size}}"
  }
}
```

-----

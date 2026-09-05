# 更新后的实现计划 V2

## 一、已锁定的产品规则

### 1. 历史记录与收藏都采用紧凑列表

列表项不在原位置展开。点击列表项后，在右侧打开详情。窄窗口中则进入独立详情视图。

每个未展开的列表项必须显示：

1. 原文摘要。
2. 一个成功 Provider 的名称。
3. 一个完整的结果摘要。
4. 语言、查询类型、成功结果数量和时间。

示例：

```text
Artificial intelligence is intelligence...                  10:24
DeepL · EN → ZH · 翻译 · 3 个结果
人工智能是由机器表现出来的智能，与人类和动物所表现出来的自然智能相对。
```

建议列表宽度为 340 至 380 DIP。卡片高度根据结果自动变化，通常控制在 88 至 132 DIP。Card 内边距使用 10 至 12 px，区块间距使用 4 至 6 px。

Fluent 2 的 Card 适合承载围绕单一对象组织的信息和操作，同时建议卡片内容保持简短、优先展示用户最需要的信息。这里的一张 Card 对应一次查询或一个收藏项。([Fluent 2 Design System][1])

---

### 2. 列表至少显示一个完整结果，最大 100 字符

这里的“完整结果”定义为：

* 结果不超过 100 个字符时，完整展示。
* 结果超过 100 个字符时，展示前 100 个字符，并追加 `…`。
* 只展示一个 Provider 的结果。
* 不拼接多个 Provider 的内容。
* 换行、制表符和连续空格统一压缩为一个空格。
* 字符计数按照用户可见字符计算，避免截断 emoji、组合字符或代理项。

建议增加公共方法：

```csharp
internal static class SavedResultPreview
{
    public const int MaxTextElements = 100;

    public static string Create(string text)
    {
        // 1. Normalize whitespace
        // 2. Enumerate grapheme clusters using StringInfo
        // 3. Keep at most 100 text elements
        // 4. Append ellipsis only when truncated
    }
}
```

不同查询类型的预览来源：

| 查询类型 | 列表预览内容     |
| ---- | ---------- |
| 翻译   | 翻译正文       |
| 词典   | 首个主要释义     |
| 语法纠错 | 修改后的文本     |
| OCR  | OCR 后的翻译正文 |
| 长文档  | 不进入历史与收藏   |

列表文本区域允许最多约 4 至 5 行。由于传入 UI 的文本已经限制到 100 字符，因此不要再通过 CSS 提前截断到两行。

---

### 3. Provider 预览选择规则

历史记录默认选择以下 Provider 作为列表预览：

1. 用户设置中的 Provider 排序。
2. 第一个成功且结果非空的 Provider。
3. 搜索状态下，优先展示命中搜索词的 Provider。
4. Provider 筛选状态下，优先展示当前筛选的 Provider。

收藏页面分两种情况：

* 收藏整次查询时，使用首选成功 Provider。
* 收藏单个结果时，始终展示被收藏的那个 Provider。

历史列表可以显示：

```text
DeepL · +2
```

这里的 `+2` 只计算另外两个成功结果。

---

### 4. 失败的 Provider 完全不出现在历史和收藏中

持久化条件统一为：

```csharp
result.Status == Succeeded
&& !string.IsNullOrWhiteSpace(result.Content)
```

行为如下：

| 查询结果             | 历史行为          |
| ---------------- | ------------- |
| 3 个全部成功          | 保存 3 个结果      |
| 2 个成功，1 个失败      | 只保存 2 个成功结果   |
| 1 个成功，2 个超时      | 只保存 1 个成功结果   |
| 全部失败             | 不创建历史记录       |
| 用户取消，但已有 1 个成功结果 | 保存已经完成的成功结果   |
| Provider 返回空结果   | 不保存该 Provider |
| 长文档查询            | 不保存任何历史       |

历史详情中的 Tab、Card、对比列和数量都基于成功结果集合。

因此不会出现：

```text
DeepL
有道
CNKI · 失败
```

只会出现：

```text
DeepL
有道
```

对比视图最多选择两个成功 Provider。失败和空结果没有对比列。

失败信息继续写入现有诊断日志即可，无需进入用户历史数据库。

---

# 二、历史记录页面

## 2.1 页面结构

宽窗口采用三栏：

```text
┌──────────┬────────────────────┬──────────────────────────────┐
│ 导航栏   │ 紧凑历史列表       │ 查询详情                     │
│ 56 px    │ 360 px             │ 自适应                       │
└──────────┴────────────────────┴──────────────────────────────┘
```

页面顶部：

```text
历史记录

[ 搜索原文、结果或 Provider... ] [筛选]

[全部] [翻译] [词典] [语法纠错] [OCR]
```

Searchbox 自带清除能力，也允许附加筛选按钮，适合当前搜索原文、译文和 Provider 的场景。([Fluent 2 Design System][2])

查询类型使用 Fluent `TabList`。TabList 适合在相关内容分类之间切换。在小窗口下，TabList 可以降级成 Dropdown。([Fluent 2 Design System][3])

长文档不会出现在类型筛选中。

---

## 2.2 紧凑历史列表项

推荐结构：

```text
┌────────────────────────────────────────────────────────┐
│ Artificial intelligence is intelligence...       10:24 │
│ [DeepL] EN → ZH · 翻译 · 3 个结果                      │
│ 人工智能是由机器表现出来的智能，与人类和动物所表现出来 │
│ 的自然智能相对。                                       │
└────────────────────────────────────────────────────────┘
```

信息优先级：

1. 原文摘要，一行。
2. Provider 和元数据，一行。
3. 翻译结果，最多 100 字符。
4. 时间放右上角。

列表项不放复制、播放、删除等常驻操作，减少视觉噪音。点击卡片打开详情。

按日期分组：

```text
今天
昨天
最近 7 天
更早
```

搜索时取消日期分组，直接按相关性和时间排序。

---

## 2.3 历史详情

顶部 Source Card：

```text
Artificial intelligence is intelligence demonstrated by machines...

EN → ZH · 翻译
2026 年 9 月 4 日 10:24 · 来自剪贴板

[复制原文] [收藏整次查询] [重新翻译] [更多]
```

下面展示成功 Provider：

```text
[全部结果 3] [DeepL] [有道词典] [CNKI]
```

每张 Provider Card：

```text
┌──────────────────────────────────────────────────────┐
│ [图标] DeepL                  820 ms                 │
│                                                      │
│ 人工智能是由机器表现出来的智能……                    │
│                                                      │
│                         [复制] [播放] [收藏] [更多]   │
└──────────────────────────────────────────────────────┘
```

**复制按钮必须始终可见。**

宽度较小时可以缩成图标按钮，并保留 Tooltip 和 `aria-label`。Fluent Button 指导建议次要操作使用 subtle 或 transparent appearance，页面内只保留一个高视觉权重的主操作。([Fluent 2 Design System][4])

复制成功后显示：

```text
结果已复制
```

使用 Fluent `Toast` 反馈。Toast 适合表达用户操作已经完成的临时状态。([Fluent 2 Design System][5])

剪贴板写入由 C# Host 完成，Web 页面只发出：

```json
{
  "type": "clipboard.copy",
  "payload": {
    "text": "..."
  }
}
```

这样可以复用现有 Windows 剪贴板能力，并避免浏览器剪贴板权限差异。

---

# 三、收藏页面

收藏列表与历史列表共用紧凑 Card 模板。

## 3.1 收藏整次查询

```text
★ Artificial intelligence is intelligence...          10:24
DeepL · EN → ZH · 整次查询 · 3 个结果
人工智能是由机器表现出来的智能，与人类和动物所表现出来的自然智能相对。
[工作] [AI]
```

## 3.2 收藏单个 Provider 结果

```text
★ photosynthesis                                      08:52
DeepL · EN → ZH · 单个结果
光合作用。植物利用光合作用将光能转化为化学能。
[学习] [生物]
```

收藏单个结果时，列表预览始终来自被收藏的 Provider。

详情页首先显示被收藏结果：

```text
已收藏的结果

DeepL
photosynthesis
/ˌfəʊtəʊˈsɪnθəsɪs/

n. 光合作用

[复制] [播放] [取消收藏] [更多]
```

同一次查询的其他成功结果放在下面，默认折叠：

```text
同一次查询的其他结果 2

▸ 有道词典
▸ CNKI
```

失败 Provider 不进入这个区域。

---

# 四、设置页面新增“历史记录”设置

建议放在：

```text
设置
└── 常规
    └── 历史记录与隐私
```

不需要新增一级设置 Tab，目前只有两个核心设置。

## 4.1 设置界面

```text
历史记录与隐私

保存历史记录                                      [开]

保存翻译、词典、语法纠错和 OCR 查询。
长文档不会进入历史记录。

保留天数
[ 30  ▲▼ ] 天

超过保留期限的历史记录将自动清理。
收藏内容不受保留天数影响。

[清除历史记录]
```

推荐默认值：

```csharp
public bool HistoryEnabled { get; set; } = true;
public int HistoryRetentionDays { get; set; } = 30;
```

允许范围：

```text
最小 1 天
最大 3650 天
默认 30 天
步进 1 天
Page Up / Page Down 步进 30 天
```

“保存历史记录”使用 Fluent `Switch`，因为切换后立即生效。Fluent 指导也将 Switch 用于立即应用的开关设置。([Fluent 2 Design System][6])

“保留天数”使用 Fluent `SpinButton`。SpinButton 支持直接输入、箭头调整和键盘步进，适合有明确上下限的天数设置。([Fluent 2 Design System][7])

SpinButton 外层使用 `Field`，包括标签、单位、辅助说明和错误提示。([Fluent 2 Design System][8])

## 4.2 开关语义

关闭“保存历史记录”后：

* 停止写入新的历史记录。
* 收藏功能继续工作。
* 已有历史记录保留。
* “保留天数”字段进入禁用状态。
* 用户仍可浏览和手动清除已有历史。

关闭开关不会立即删除历史，避免一次普通设置操作造成不可逆数据丢失。

## 4.3 保留天数语义

当用户把 30 天改成 7 天：

1. 设置立即保存。
2. 后台启动一次清理。
3. 超过 7 天且没有收藏关系的历史被删除。
4. 收藏整次查询或单个结果关联的查询继续保留。
5. 页面显示 Toast：

```text
历史保留期限已更新
```

自动清理触发点：

* 应用启动后空闲执行一次。
* 保留天数减少后执行一次。
* 应用连续运行时，每 24 小时执行一次。
* 用户取消最后一个收藏后，检查该查询是否已经超过历史期限。

“清除历史记录”只清除普通历史。收藏保持不变。

---

# 五、推荐技术栈

## 5.1 最终选择

```text
WinUI 3 Shell
├── C# / .NET 8
├── Microsoft.Data.Sqlite
├── SettingsService
├── ClipboardService
└── SavedItemsHostPage
    └── WebView2
        └── React + TypeScript
            ├── @fluentui/react-components v9
            ├── @fluentui/react-icons
            └── Vite
```

仓库当前已经采用 WinUI 3 和 .NET 8，并且已经引用 `Microsoft.Data.Sqlite` 和 `Microsoft.Web.WebView2`。设置使用 JSON 持久化，页面导航使用现有 `Frame.Navigate`。因此这套方案可以直接接入当前项目，无需更换窗口框架或翻译后端。

Fluent UI React v9 对应官方 `@fluentui/react-components` 包，能够直接使用 Fluent 2 Web 版本的 Card、Searchbox、Tablist、Button、Switch、SpinButton、Toast、Menu 和 TagPicker。([GitHub][9])

## 5.2 为什么采用混合方案

| 方案                         | Fluent Web 还原度 | 接入成本 | 额外内存 | 结论       |
| -------------------------- | -------------: | ---: | ---: | -------- |
| 纯 WinUI 3                  |              中 |    中 |    低 | 视觉需要自行模仿 |
| WebView2 + React Fluent v9 |              高 |    中 |   中高 | 推荐       |
| Electron 全量重写              |              高 |   极高 |    高 | 当前范围不采用  |
| Blazor Hybrid              |             中高 |    高 |   中高 | 会增加额外技术层 |

当前目标要求使用真正的 Web Fluent UI 元素，因此历史和收藏作为一个 WebView2 Island 最合适。窗口管理、热键、翻译、SQLite、剪贴板和主题仍由 C# 控制。

## 5.3 WebView2 约束

历史和收藏共用**一个 WebView2 实例**：

```text
SavedItemsHostPage
├── route = history
└── route = favorites
```

在历史与收藏之间切换时，只更新 React 路由或状态，不创建新的 WebView2。

WebView2 在 WinUI 3 中需要在 UI 线程创建和交互，异步初始化过程不能通过 `.Wait()` 或 `.Result` 阻塞。([Microsoft Learn][10])

页面与 Host 之间统一使用 `PostWebMessageAsJson` 和 `WebMessageReceived`，并对消息类型和字段进行验证。微软也建议使用结构化 JSON 消息，并将 Web 内容视为不可信输入。([Microsoft Learn][10])

发布版本只加载本地静态资源：

```text
https://easydict.local/
```

通过 `SetVirtualHostNameToFolderMapping` 映射到打包后的 Web 资源目录。该模式支持相对资源、基于 Origin 的 Web API，并由 WebView2 进程直接解析静态资源。([Microsoft Learn][11])

Release 中还应：

* 禁用 DevTools。
* 禁用默认浏览器上下文菜单。
* 阻止导航到非 `easydict.local` 地址。
* 拒绝未知 Bridge 消息。
* 不通过字符串插值执行 JavaScript。

---

# 六、数据库设计

## 6.1 使用独立数据库文件

现有 `translation_cache.db` 继续只负责翻译缓存。仓库里的 `TranslationCacheService` 已经使用 `Microsoft.Data.Sqlite` 和手写 SQL。

历史和收藏使用新文件：

```text
%LOCALAPPDATA%\Easydict\saved_items.db
```

分开存储的原因：

* 清除翻译缓存不会删除历史。
* 清除历史不会破坏缓存。
* 历史保留策略和缓存淘汰策略可以独立演进。
* 数据库迁移风险更小。

继续使用 `Microsoft.Data.Sqlite`，无需加入 EF Core 或 Dapper。

## 6.2 表结构

```sql
saved_queries
--------------
id
mode
source_text
source_language
target_language
source_kind
created_utc
history_visible
preview_provider_id
preview_provider_name
preview_text
success_result_count

saved_results
-------------
id
query_id
provider_id
provider_name
display_order
content_type
plain_text
structured_json
search_text
latency_ms
created_utc

favorites
---------
id
query_id
result_id          -- NULL 表示收藏整次查询
note
pinned
created_utc

favorite_tags
-------------
favorite_id
tag
```

索引：

```sql
CREATE INDEX idx_queries_history_created
ON saved_queries(history_visible, created_utc DESC);

CREATE INDEX idx_results_query_order
ON saved_results(query_id, display_order);

CREATE INDEX idx_favorites_created
ON favorites(pinned DESC, created_utc DESC);

CREATE UNIQUE INDEX uq_favorite_query
ON favorites(query_id)
WHERE result_id IS NULL;

CREATE UNIQUE INDEX uq_favorite_result
ON favorites(result_id)
WHERE result_id IS NOT NULL;
```

一次查询和它的多个成功结果必须在同一个事务中写入。SQLite 事务可以保证这些语句作为一个原子单元提交，发生错误时整体回滚。([Microsoft Learn][12])

---

## 6.3 历史与收藏共存模型

`history_visible` 用于区分历史可见性：

```text
history_visible = 1
普通历史记录

history_visible = 0
仅为收藏保留的查询快照
```

收藏不依赖历史开关。

例如用户关闭历史后，仍然可以在实时翻译页面收藏一个结果：

```text
保存查询快照
history_visible = 0

创建 favorite
result_id = 被收藏结果
```

保留期限到期时：

```sql
UPDATE saved_queries
SET history_visible = 0
WHERE history_visible = 1
  AND created_utc < @cutoff;
```

随后删除没有任何收藏关系的隐藏查询：

```sql
DELETE FROM saved_queries
WHERE history_visible = 0
  AND 没有查询收藏
  AND 没有结果收藏;
```

这样可以同时满足：

* 过期历史从历史页消失。
* 收藏内容永久保留。
* 单个结果收藏仍然可以访问同一次查询的其他成功结果。
* 删除最后一个收藏后，可以清理已经过期的底层查询。

---

# 七、查询记录接入点

建议增加统一服务：

```csharp
IQuerySnapshotRecorder
SavedItemsStore
SavedItemsCleanupService
```

查询流程：

```text
开始查询
   ↓
建立 QuerySnapshotDraft
   ↓
各 Provider 完成
   ↓
只收集成功且非空结果
   ↓
查询结束或取消
   ↓
LongDocument？直接跳过
   ↓
成功结果数量为 0？直接跳过
   ↓
HistoryEnabled？写入历史
```

Main、Mini、Fixed 和 OCR 都调用同一个 recorder：

```csharp
await _historyRecorder.CompleteAsync(new QuerySnapshot
{
    QueryId = queryId,
    Mode = mode,
    SourceText = sourceText,
    SourceLanguage = from,
    TargetLanguage = to,
    SourceKind = sourceKind,
    Results = successfulResults
});
```

长文档 Worker、长文档缓存和文档导出路径都不能调用这个 recorder。

---

# 八、Web 与 C# 的 Bridge API

建议使用请求 ID 的 RPC 风格：

```json
{
  "id": "req-123",
  "type": "history.list",
  "payload": {
    "cursor": null,
    "limit": 50,
    "query": "",
    "mode": "all"
  }
}
```

核心消息：

```text
history.list
history.detail
history.delete
history.clear

favorites.list
favorites.addQuery
favorites.addResult
favorites.remove
favorites.updateNote
favorites.updateTags

clipboard.copy
query.rerun

settings.getHistory
settings.updateHistory

theme.changed
navigation.openMain
navigation.openSettings
```

返回：

```json
{
  "id": "req-123",
  "ok": true,
  "payload": {}
}
```

前端不使用 IndexedDB 保存业务数据。SQLite 和 C# 是唯一数据源。

---

# 九、前端实现建议

目录结构：

```text
web/saved-items/
├── package.json
├── package-lock.json
├── vite.config.ts
├── src/
│   ├── app/
│   │   ├── App.tsx
│   │   ├── FluentThemeHost.tsx
│   │   └── HostBridge.ts
│   ├── history/
│   │   ├── HistoryPage.tsx
│   │   ├── HistoryCompactCard.tsx
│   │   └── HistoryDetail.tsx
│   ├── favorites/
│   │   ├── FavoritesPage.tsx
│   │   ├── FavoriteCompactCard.tsx
│   │   └── FavoriteDetail.tsx
│   ├── results/
│   │   ├── ProviderResultCard.tsx
│   │   └── ProviderCompareView.tsx
│   └── shared/
│       ├── CompactList.tsx
│       ├── CopyButton.tsx
│       └── EmptyState.tsx
└── dist/
```

根节点使用 `FluentProvider`，由 Host 传入 Light、Dark、品牌色和 High Contrast 状态。FluentProvider 本身用于定义整个体验或局部区域使用的 Fluent 样式。([Fluent 2 Design System][13])

第一版不使用 DataGrid。历史项高度会根据 100 字符结果变化，Card List 更符合内容结构。

列表通过 cursor pagination 加载：

```text
每页 50 条
滚动接近底部时加载下一页
搜索输入 debounce 150 ms
DOM 中最多保留有限数量的 Card
```

第一版暂不引入独立虚拟化库。数据量和滚动性能达到需要时，再增加虚拟化。

---

# 十、PR 与里程碑拆分

## PR 1，Web UI 技术验证

内容：

* 新建 React + TypeScript + Fluent UI v9 工程。
* 新增 `SavedItemsHostPage.xaml`。
* 接入单个 WebView2。
* 实现本地资源映射。
* 实现 Light、Dark 主题同步。
* 使用 Mock 数据渲染 200 条紧凑记录。
* 验证打包进 portable 和 MSIX。

验收：

* 历史和收藏可以在同一个 WebView2 内切换。
* Card、Searchbox、Tablist 和 Button 来自 Fluent React v9。
* 离开页面后没有持续增长的 WebView2 实例。
* 200 条记录滚动无明显卡顿。

---

## PR 2，数据库与历史设置

内容：

* 新增 `saved_items.db`。
* 新增 schema migration。
* 新增 `HistoryEnabled`。
* 新增 `HistoryRetentionDays`。
* 设置页加入 Switch、SpinButton 和“清除历史记录”。
* 新增清理服务。

验收：

* 默认保存历史，默认 30 天。
* 关闭后停止新增历史。
* 改小天数会清理过期历史。
* 收藏数据不被清理。
* 清除历史不会清除收藏。
* 非法天数被限制到 1 至 3650。

---

## PR 3，统一查询快照记录

内容：

* 新增 `QuerySnapshotDraft`。
* 新增 `IQuerySnapshotRecorder`。
* 接入 Main Window。
* 接入 Mini Window。
* 接入 Fixed Window。
* 接入 OCR。
* 明确排除 Long Document。
* 实现 100 字符预览生成。

验收：

* 部分成功只保存成功 Provider。
* 全部失败不产生历史。
* 空结果不保存。
* 长文档不产生历史。
* Preview 不超过 100 个用户可见字符。
* 100 字符以内完整显示。

---

## PR 4，历史记录页面

内容：

* 紧凑列表。
* 日期分组。
* 搜索与类型筛选。
* Cursor pagination。
* 详情页。
* 复制原文。
* 复制 Provider 结果。
* 重新翻译。
* 收藏整次查询。
* 两 Provider 对比视图。

验收：

* 每个列表项显示一个成功结果。
* 详情没有失败 Provider。
* Provider 数量只计算成功结果。
* 复制成功显示 Toast。
* 搜索命中非首选 Provider 时，列表显示匹配结果。
* 小窗口进入单页详情。

---

## PR 5，收藏页面

内容：

* 收藏整次查询。
* 收藏单个 Provider 结果。
* 紧凑收藏列表。
* 标签和备注。
* 置顶。
* 同次查询其他成功结果折叠区。
* 历史关闭时仍可收藏实时结果。

验收：

* 单结果收藏始终预览对应 Provider。
* 整次查询收藏展示首选成功 Provider。
* 历史过期后收藏仍可打开。
* 取消最后一个收藏后，过期底层数据可以被清理。
* 失败 Provider 不出现在折叠区。

---

## PR 6，测试、性能与发布

内容：

* SQLite migration tests。
* Retention tests。
* Unicode 预览测试。
* Bridge contract tests。
* React component tests。
* Web UI screenshot baseline。
* Light、Dark、High Contrast。
* Keyboard 和屏幕阅读器检查。
* MSIX、portable、x64、x86、ARM64 smoke test。
* WebView2 内存回归测试。

验收矩阵：

```text
历史开启 / 关闭
保留 1 / 7 / 30 / 3650 天
全成功 / 部分成功 / 全失败
翻译 / 词典 / 语法纠错 / OCR
整次收藏 / 单结果收藏
Light / Dark / High Contrast
100% / 150% / 200% DPI
窄窗口 / 宽窗口
```

---

# 十一、最终验收标准

迁移完成后必须满足：

* 历史和收藏均使用紧凑 Card 列表。
* 每个列表项至少显示一个成功 Provider 的结果。
* 100 字符以内完整显示。
* 超过 100 字符时统一追加省略号。
* 列表结果来自单一 Provider。
* 失败、超时、取消和空结果 Provider 不出现。
* 全部 Provider 失败时不创建历史。
* 详情页复制按钮始终可见。
* 复制后显示确认 Toast。
* 历史关闭后不再产生新记录。
* 历史关闭不会禁用收藏。
* 保留天数只影响历史。
* 收藏不受保留天数影响。
* 清除历史不会删除收藏。
* 长文档没有历史和收藏入口。
* WebView2 只创建一个实例供历史与收藏共用。
* Web UI 全部使用 Fluent UI React v9 组件。
* C# 继续负责业务、数据库、剪贴板、主题和原生导航。

整体工作量预计约 **18 至 28 个工程日**。主要风险集中在多个窗口查询结果的统一收口、WebView2 生命周期和历史过期后收藏关系的正确保留。

[1]: https://fluent2.microsoft.design/components/web/react/core/card/usage "React Card - Fluent 2 Design System"
[2]: https://fluent2.microsoft.design/components/web/react/core/searchbox/usage "Searchbox - Fluent 2 Design System"
[3]: https://fluent2.microsoft.design/components/web/react/core/tablist/usage "React Tablist - Fluent 2 Design System"
[4]: https://fluent2.microsoft.design/components/web/react/core/button/usage "React Button - Fluent 2 Design System"
[5]: https://fluent2.microsoft.design/components/web/react/core/toast/usage "Toast - Fluent 2 Design System"
[6]: https://fluent2.microsoft.design/components/web/react/core/switch/usage "React Switch - Fluent 2 Design System"
[7]: https://fluent2.microsoft.design/components/web/react/core/spin/usage?utm_source=chatgpt.com "React Spin button - Fluent 2 Design System"
[8]: https://fluent2.microsoft.design/components/web/react/core/field/usage?utm_source=chatgpt.com "React Field - Fluent 2 Design System"
[9]: https://github.com/microsoft/fluentui?utm_source=chatgpt.com "GitHub - microsoft/fluentui: Fluent UI web represents a collection of utilities, React components, and web components for building web applications. · GitHub"
[10]: https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/webview2?utm_source=chatgpt.com "WebView2 in WinUI 3 - Windows apps | Microsoft Learn"
[11]: https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/working-with-local-content?utm_source=chatgpt.com "Using local content in WebView2 apps - Microsoft Edge Developer documentation | Microsoft Learn"
[12]: https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/transactions?utm_source=chatgpt.com "Transactions - Microsoft.Data.Sqlite | Microsoft Learn"
[13]: https://fluent2.microsoft.design/components/web/react/core/fluentprovider/usage?utm_source=chatgpt.com "React Fluent provider - Fluent 2 Design System"

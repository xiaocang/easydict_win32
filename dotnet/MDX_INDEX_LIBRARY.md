# MDX 通配符 / 自动补全索引库 — 实施计划

Issue: [#136](https://github.com/xiaocang/easydict_win32/issues/136)
Branch: `claude/mdx-wildcard-autocomplete-7HuSG`

## 1. 决策

经讨论,采用 **方案 C(Trie / DAWG 二进制索引)**,并将索引引擎设计为**独立的通用库**,
后续抽成单独的子仓库通过 git submodule 引入。

> 不再走「方案 A 内存 → 方案 B SQLite」的两阶段渐进,而是直接把高性能索引层做成可复用的库。
> 短期作为 in-tree 库参与开发,稳定后迁出。

### 决策的核心理由

| 项目 | 说明 |
|---|---|
| 性能上限 | Trie/DAWG 的查询复杂度只与匹配长度/结果集相关,不随词典体量退化;通配符场景比 SQLite GLOB 快 10× 以上 |
| 解耦 | 索引引擎和翻译应用没有任何耦合,做成独立库可以独立测试、版本化、发布 |
| 可复用 | 任何「key → blob」的离线词典/字典/前缀检索场景都能用,不限于 MDX |
| Submodule 模式已有先例 | `lib/PdfPig`、`dotnet/lib/MDict.CSharp` 都是 fork 后通过 submodule 接入,流程成熟 |
| MDict.CSharp fork 改动最小 | 只需要加一个 `IEnumerable<string> EnumerateKeys()`,key→value 的解码仍走原库 |

---

## 2. 库的范围与命名

**库名:** `Easydict.WordIndex`(临时,迁出后可改名为 `WordIndex.NET` 之类)

**命名空间:** `Easydict.WordIndex`(子命名空间 `Easydict.WordIndex.Trie`、`Easydict.WordIndex.Storage`、`Easydict.WordIndex.Query`)

**目标框架:** `net8.0`(纯托管,不依赖 WinUI / Win32),Nullable 启用,SourceLink。

**职责边界:**

| 范围内 | 范围外 |
|---|---|
| 接收 `IEnumerable<string>` 词条流,构建 Trie/DAWG | 词典文件解析(MDX/StarDict/…) |
| 序列化 / 反序列化二进制索引文件 | 翻译、定义渲染 |
| 前缀查询(`StartsWith`) | UI 控件 |
| 通配符查询(`*`、`?`) | 拼音 / 部首 / 编辑距离搜索(后续可作为扩展模块) |
| 多索引联合查询(merge / dedup) | 数据库迁移 |
| 大小写、Unicode 规范化策略钩子 | |

---

## 3. 总体架构

```
┌────────────────────────────────────────────────────────┐
│ Easydict.WinUI                                         │
│  ├─ Views/MainPage.xaml (AutoSuggestBox)               │
│  └─ Services/MdxIndexService.cs   ← 适配层(非常薄)   │
└──────────────────┬─────────────────────────────────────┘
                   │ 依赖
                   ▼
┌────────────────────────────────────────────────────────┐
│ Easydict.WordIndex            (独立库,未来 submodule) │
│  ├─ Build/                                             │
│  │   ├─ TrieBuilder.cs        增量插入 + 排序合并      │
│  │   ├─ DawgMinimizer.cs      后缀共享(Daciuk 算法)  │
│  │   └─ NormalizationOptions.cs                        │
│  ├─ Storage/                                           │
│  │   ├─ IndexFile.cs          二进制格式 v1            │
│  │   ├─ IndexHeader.cs        magic / version / flags  │
│  │   ├─ IndexWriter.cs                                 │
│  │   └─ IndexReader.cs        mmap 友好               │
│  ├─ Query/                                             │
│  │   ├─ IWordIndex.cs         查询主接口               │
│  │   ├─ PrefixWalker.cs                                │
│  │   ├─ WildcardWalker.cs     * / ? 回溯               │
│  │   └─ MultiIndex.cs         多索引合并 + dedup       │
│  ├─ Diagnostics/              统计 / Telemetry hook    │
│  └─ Easydict.WordIndex.csproj                          │
└──────────────────┬─────────────────────────────────────┘
                   │
                   ▼
            (no other deps)
```

### 关键属性

- **零外部依赖**:不引 SQLite、不引 WinUI、不引 Win32。便于跨平台/跨场景复用。
- **mmap 友好**:`IndexReader` 可基于 `MemoryMappedFile`,索引文件常驻磁盘,内存里只持有指针,启动开销 ~0。
- **顺序写、随机读**:构建一次写完;查询纯只读。
- **可重入**:多线程并发查询安全(`IndexReader` 不持可变状态)。

---

## 4. 数据结构选型

> **首版选 DAWG**(确定型无环有限自动机,即最小化的后缀共享 Trie)。

### 为什么不是普通 Trie

- 普通 Trie 节点数 ≈ 总字符数,200K 英文 keys ≈ 1.5M 节点 → 序列化后 30–60 MB
- DAWG 通过共享公共后缀(`-tion`、`-ing`、`-ed`)可压缩到 1/5 ~ 1/10
- 对中文 / 长 key,后缀共享率更高

### 为什么不是 FST(如 Lucene FST)

- FST 在每条边上携带输出值,适合 key→int 映射;我们这里 key 本身就是输出,DAWG 已经够用
- FST 实现复杂度更高,首版不引入
- 留作后续优化(如果需要 key→offset 映射,FST 是上选)

### 算法

- 构建:**Daciuk 增量构建算法**(《Incremental Construction of Minimal Acyclic Finite-State Automata》,1998)
  - 输入要求:**词条按字典序升序输入**(MDX 内部本身就是排序的,完美匹配)
  - 时间复杂度:O(N · L)
  - 空间复杂度:与最终最小化结果同阶,无需先建 Trie 再压缩
- 查询:DFS,通配符 `*` 走 ε-跳到任意子节点回溯,`?` 走任意 1 跳

---

## 5. 二进制文件格式 v1

```
+-----------------------------------------+
| Header (32 bytes)                       |
|   magic   : "EWIX" (4)                  |  Easydict Word InDeX
|   version : u32  = 1                    |
|   flags   : u32  (case_fold, nfc, ...)  |
|   nodeCnt : u32                         |
|   keyCnt  : u32                         |
|   meta_off: u32  (元数据 JSON 偏移)     |
|   reserved: u32                         |
+-----------------------------------------+
| Node Table (varint-packed)              |
|   每节点:                              |
|     edgeCount : varint                  |
|     isFinal   : 1 bit (合入 edgeCount)  |
|     edges[]:                            |
|        label   : utf-8 codepoint(varint)|
|        target  : varint(节点索引)       |
+-----------------------------------------+
| Optional payload table                  |
|   (后续扩展:key→外部偏移,本期为空)   |
+-----------------------------------------+
| Metadata JSON (UTF-8)                   |
|   build_time, source_hash, normalizer,  |
|   stats                                 |
+-----------------------------------------+
```

设计要点:
- Little-endian
- varint 节省空间,DAWG 节点目标多分布在前 16 bit
- header 含 `source_hash`(对源文件 SHA-256,放 metadata 中)用于增量重建判定
- 字符使用 codepoint(int)而非字节,避免 UTF-8 多字节边

---

## 6. 公共 API(草案)

```csharp
namespace Easydict.WordIndex;

// ===== 构建 =====
public sealed class IndexBuilder
{
    public IndexBuilder(NormalizationOptions? normalization = null);

    // 增量插入,要求调用方按字典序升序传入
    public void Add(string key);

    // 也接受流式输入(底层会强制校验顺序)
    public void AddRange(IEnumerable<string> sortedKeys);

    // 写入索引文件
    public Task BuildAsync(string outputPath, CancellationToken ct = default);
}

public sealed record NormalizationOptions(
    bool CaseFold = true,
    bool NfcNormalize = true,
    bool TrimWhitespace = true);

// ===== 查询 =====
public interface IWordIndex : IDisposable
{
    int KeyCount { get; }
    NormalizationOptions Normalization { get; }

    // 前缀:返回前 limit 个按字典序匹配
    IEnumerable<string> StartsWith(string prefix, int limit = 50);

    // 通配符:* / ?(可后续加 [abc]、{a,b})
    IEnumerable<string> Match(string pattern, int limit = 200);

    // 精确包含
    bool Contains(string key);
}

public static class WordIndexFactory
{
    // 优先 mmap;不可用时回退到 FileStream
    public static IWordIndex OpenRead(string indexPath);
}

// ===== 多索引联合 =====
public sealed class MultiWordIndex : IWordIndex
{
    public MultiWordIndex(IEnumerable<(string sourceTag, IWordIndex index)> indices);
    // 查询结果带 sourceTag,可用于 UI 分组
    public IEnumerable<MatchHit> StartsWithTagged(string prefix, int limit);
    public IEnumerable<MatchHit> MatchTagged(string pattern, int limit);
}

public readonly record struct MatchHit(string Key, string SourceTag);
```

---

## 7. 仓库内目录结构

```
dotnet/
├── lib/
│   └── Easydict.WordIndex/                ← 新建,后续抽离为 submodule
│       ├── src/
│       │   └── Easydict.WordIndex/
│       │       ├── Easydict.WordIndex.csproj
│       │       ├── Build/
│       │       ├── Storage/
│       │       ├── Query/
│       │       └── Diagnostics/
│       ├── tests/
│       │   └── Easydict.WordIndex.Tests/
│       │       ├── Easydict.WordIndex.Tests.csproj
│       │       ├── BuildTests.cs
│       │       ├── PrefixQueryTests.cs
│       │       ├── WildcardQueryTests.cs
│       │       ├── RoundTripTests.cs
│       │       └── BenchmarkTests.cs (BenchmarkDotNet,可选)
│       ├── README.md                       (后续 standalone 仓库的 README)
│       ├── LICENSE                         (MIT,与 easydict GPL 解耦)
│       └── Directory.Build.props
│
└── src/Easydict.WinUI/Services/
    └── MdxIndexService.cs                  ← 应用层适配,~150 行
```

为什么放 `dotnet/lib/`:与现有 `dotnet/lib/MDict.CSharp/` 路径风格一致,后续替换为 submodule 时挂载点不变。

**Solution 整合:** `Easydict.Win32.sln` 添加 `Easydict.WordIndex` 与 `Easydict.WordIndex.Tests` 两个项目。Easydict.WinUI 通过 `ProjectReference` 引用 `Easydict.WordIndex`(后续切换为 submodule 后保持 `ProjectReference`,只是路径相同)。

---

## 8. MDict.CSharp fork 的改动

需要在 [`xiaocang/MDict.Csharp` 仓库 `plus` 分支](https://github.com/xiaocang/MDict.Csharp) 增加一个公开方法:

```csharp
// 在 MdxDict 类中添加
public IEnumerable<string> EnumerateKeys();
```

**实现要点:**
- MDX 内部的 keyword block 已经按 key 升序排列;只需在解码每个 keyword block 时 yield key
- 不需要解码 record block(value)
- 复用现有解密逻辑(type-2 Salsa20/8)
- 200K keys 实测预期 < 500 ms

**风险:** 需要给上游 fork 提交 PR 并合并。如果短期不方便,可在 `Easydict.WordIndex` 完成后,在 `MdxIndexService` 里通过反射或 InternalsVisibleTo 临时绕过 — 但这只是兜底方案,首选是直接加公共 API。

---

## 9. 应用层集成(`Easydict.WinUI`)

### `MdxIndexService.cs`(新)

```csharp
public sealed class MdxIndexService
{
    private readonly Dictionary<string, IWordIndex> _indices = new(); // serviceId → index
    private readonly string _indexDir;  // %LocalAppData%\Easydict\mdx_index

    public Task EnsureIndexAsync(MdxDictionaryTranslationService dict, CancellationToken ct);
    public IReadOnlyList<MatchHit> Suggest(string input, int limit = 30);
    public IReadOnlyList<MatchHit> WildcardSearch(string pattern, int limit = 200);
    public void Unload(string serviceId);
}
```

- **何时构建索引:** 首次导入 MDX 后台触发;启动时若发现 `<mdx_path>.ewix` 不存在或 `source_hash` 与 MDX SHA-256 不匹配,后台重建
- **进度反馈:** 构建期间在 Settings 页显示进度条(可选,首版用 toast 即可)
- **存储位置:** `%LocalAppData%\Easydict\mdx_index\<serviceId>.ewix`
- **失败降级:** 索引构建/打开失败时,自动降级为「该词典不参与建议」,不阻塞翻译主流程

### UI 改动

- `Views/MainPage.xaml:410` 的 `InputTextBox` 替换为 `AutoSuggestBox`
  - 保留多行能力:外部包一层 `Grid`,把 `AutoSuggestBox` 的 suggestion 用自定义 `ItemsPanel`
  - 或者保持 `TextBox`,在其上用 `Popup + ListView` 自绘下拉(更可控,推荐)
- `Views/MainPage.xaml.cs`:加 `OnInputTextBoxTextChanged` → 防抖 150 ms → 调 `MdxIndexService.Suggest` → 填充 Popup
- 用户敲含 `*`/`?` 的输入并按回车 → 走 `WildcardSearch` → 弹出结果列表
- 选中某项 → 调用现有 `StartQueryAsync` 走 MDX 精确查找
- Settings 加开关:`EnableLocalDictSuggestions`(默认 on)

### 不改动

- `ITranslationService` 接口不动 — 索引层是 Easydict.WinUI 内部能力,不强加给所有翻译服务
- 在线服务无任何影响

---

## 10. 通配符与前缀语义

| 用户输入 | 行为 |
|---|---|
| `apple` | 精确翻译(现有流程) |
| `app`(在输入过程中) | 防抖后触发 `Suggest("app")` 显示候选 |
| `app*` 或 `te?t` 或 `*ing` | 视为 wildcard,按回车触发 `Match(...)` 显示候选列表 |
| 空 / 单字符 | 不触发 |
| `选中候选` | 调 `StartQueryAsync(候选 key)` |

通配符语法:`*` = 任意 ≥0 字符;`?` = 任意 1 字符。后续可扩展 `[abc]`、`{a,b}`。

---

## 11. 性能目标

| 操作 | 目标(单本 200K-key 词典) |
|---|---|
| 索引构建 | < 1 s |
| 索引文件大小 | < 5 MB(DAWG 压缩后) |
| 进程内存(mmap 模式) | < 5 MB working set |
| 前缀查询 p95 | < 5 ms |
| 通配符 `tea*t` p95 | < 30 ms |
| 通配符 `*ing` p95 | < 80 ms(尾部通配最坏情况) |
| App 启动开销 | < 50 ms(仅 mmap header 验证) |

通过 BenchmarkDotNet 测试维持。

---

## 12. 测试矩阵

`tests/Easydict.WordIndex.Tests/`:
- **构建** — 空输入、单 key、乱序输入应抛异常、Unicode、emoji、大小写归一化
- **前缀查询** — 标准前缀、Unicode 前缀、limit=0、prefix 命中所有 keys
- **通配符** — `*`、`?`、混合、首尾通配、纯 `*`、空字符串
- **多索引** — dedup 顺序、tag 透传
- **Round-trip** — 构建后立刻读取并比对全集
- **持久化** — 写入 → 关闭 → 重新打开 → 命中率一致
- **退化输入** — 极长输入、含 `\0`、控制字符
- **基准** — 200K-key 合成数据集(英文 + 中文混合)

`tests/Easydict.WinUI.Tests/Services/MdxIndexServiceTests.cs`:
- 适配层 — `EnsureIndexAsync` 增量、缓存命中、构建失败降级
- 与 `MdxDictionaryTranslationService` 联动

---

## 13. 阶段拆分

| 阶段 | 周期估算 | 交付物 |
|---|---|---|
| **P0 — 库脚手架 + 数据结构** | 3 天 | `Easydict.WordIndex` 项目骨架,DAWG 算法实现,内存级单测通过 |
| **P1 — 二进制格式 + 持久化** | 3 天 | 序列化/反序列化,round-trip 测试通过 |
| **P2 — 通配符引擎** | 2 天 | `*`/`?` 回溯实现 + 性能基准 |
| **P3 — MDict.CSharp fork 改动** | 1–2 天 | `EnumerateKeys()` PR,提交并合并 |
| **P4 — 应用层适配** | 3 天 | `MdxIndexService`,导入流程串通 |
| **P5 — UI 集成** | 3 天 | AutoSuggestBox/Popup,防抖,设置开关 |
| **P6 — 端到端测试 + 文档** | 2 天 | 用真实 MDX 走通,README 更新 |
| **P7(后续) — 抽离为 submodule** | 1 天 | 推到独立仓库,本仓库改 `.gitmodules` |

合计 ~17 个工作日,预计 3–4 周自然时间。

---

## 14. 抽离为 submodule 的迁移路径(P7)

1. 在 `Easydict.WordIndex` 已稳定、API 已冻结后:
2. 在新独立仓库初始化:`git subtree split --prefix=dotnet/lib/Easydict.WordIndex` → push 到新 repo
3. 删除本仓库的 `dotnet/lib/Easydict.WordIndex/` 目录
4. `git submodule add <new-url> dotnet/lib/Easydict.WordIndex`
5. `Easydict.WinUI.csproj` 中的 `ProjectReference` 路径不变,无需改动
6. 新仓库走自己的 NuGet 发布(可选)

---

## 15. 风险登记

| 风险 | 缓解 |
|---|---|
| MDict.CSharp fork PR 合并周期长 | 自管 fork 仓库,直接合到 `plus` 分支即可 |
| Daciuk 算法实现 bug | 用 100% round-trip 测试 + 模糊测试覆盖 |
| mmap 在某些 Windows 版本上权限问题 | 自动 fallback 到 FileStream |
| 中文/CJK 通配符行为不直观(用户用拼音搜) | 文档明确说明,后续模块再支持 |
| 索引文件版本升级 | header 中 `version` 字段 + `IndexReader` 拒绝未知版本 → 触发重建 |
| 用户磁盘空间被索引占用 | Settings 加「清理索引」按钮;失败时降级为内存索引 |

---

## 16. 验收标准

- [ ] 用户导入一本 200K-key 的 MDX 后,索引文件 < 5 MB,首次构建 < 1 s
- [ ] 输入 `a` → 150 ms 内显示 30 条候选
- [ ] 输入 `tea*t` 回车 → 100 ms 内显示匹配列表
- [ ] App 重启后输入 `a` 仍能立刻显示候选(从持久化索引读取)
- [ ] 多本 MDX 时结果按词典分组,无重复
- [ ] 未导入 MDX 的用户不受任何影响
- [ ] 加密未配置 MDX 不阻塞其他词典的索引
- [ ] `Easydict.WordIndex` 100% 单元测试覆盖核心路径
- [ ] BenchmarkDotNet 报告达到第 11 节性能目标

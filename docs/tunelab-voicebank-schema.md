# TuneLab 模型描述文件（`tunelab.yaml`）——初版设计方案

> 状态：草案 v0.2（待 DiffSinger 团队评审）
> 适用：DiffSingerForTuneLab 插件
> schema 版本：`tunelab-voicebank/1`

## 1. 背景与目标

本插件直接读取 **OpenUtau 形态**的 DiffSinger 资源（`dsconfig.yaml` + `character.yaml/.txt` + 预测器子目录 + `.emb` 等）。这套格式是社区既成事实，**不能推翻重做**——否则全社区资源都要重新打包。

> **术语**：我们发布的单位本质上就是"一个 ONNX 骨干 + 多个说话人"这一坨。过去口头叫"声库"，本文档统一按它的本来面目叫 **模型（model）**——这只是正名，不改任何结构。

因此本方案的定位：在模型目录里**新增一个可选的、TuneLab 专属的描述文件**，只解决 OpenUtau 格式**表达不了**的几件事，其余沿用现有推断逻辑。

要解决的核心问题：

1. **模型 / voice ID 稳定性**——当前 voiceId 取文件夹名（见 [VoicebankScanner.cs](../VoicebankScanner.cs#L75-L77)），ID 与显示文本耦合、改名即失效。
2. **同一个人横跨多个模型**——A 模型用 A/B/C 训练，下一次数据升级用 C/D/E/F 训练出 B 模型，C 在两个模型里都有。用户视角下 **C 是同一个人**，不该是两个条目。
3. **retake 能力声明**——本插件实现了 OpenUtau 不具备的 note 级 retake（pitch / variance / acoustic 三族各自独立），需要模型声明导出了哪几族。
4. **版本演进**——同一模型会以独立包不断升级（数据重训 / 修复）。
5. **speaker 暴露白名单**——一个模型可能内含多个 speaker，作者往往只想暴露其中一部分；未列出的应不可见、不可混入。
6. **i18n**——模型名 / voice 名 / 语言显示名要能按宿主语言本地化。

## 2. 设计原则

- **两个文件并存、职责不重叠、都读**：`tunelab.yaml` 不复制 dsconfig 的任何模型事实，只承载 TuneLab 的"作者决策层"。两边没有同名字段 → 无覆盖/合并规则 → 不会 desync。这与今天 `character.yaml` 和 `dsconfig.yaml` 各管一摊地并存是同一形态。
- **作者只手写一个小文件、不重打包**：作者只写 §2.1 左列那几项；DSP/模型事实继续由插件从 dsconfig 读，作者一个数都不用抄。
- **文件缺失 → 纯 OpenUtau 路径**：插件行为与今天完全一致（老资源一个不破），本文件新增的全部机制（retake / voice 拆分 / 多模型合并 / 版本 / i18n）退回默认关闭。
- **不参与发现**：发现逻辑继续认 `dsconfig.yaml` + `character.*`（见 [VoicebankScanner.cs](../VoicebankScanner.cs#L64-L71)）。`tunelab.yaml` 只在已识别为模型后被附加解析。
- **能力是构件的客观事实，不靠手写声明替代**——除两个例外：retake 与 phoneme_mix（同理由，见 §7）。
- **插件无权写用户工程数据**——任何"默认值"只能声明、不能回写 part 属性（这直接决定了 model/version 的"钉死/浮动"形态，见 §8）。

### 2.1 dsconfig × tunelab.yaml 职责分工

经查证（对照 DiffSinger 配置 schema 文档、OpenUtau `DiffSingerConfig.cs`、导出脚本 `acoustic_exporter.py`/`variance_exporter.py` 与真实样例）：**dsconfig 的 43 个字段几乎全部由 DiffSinger 导出脚本生成、是模型自身契约，唯一例外是 `vocoder`**（导出不写、需手填、按名指向声码器目录，是宿主约定）。所以 dsconfig 本质是"对模型的描述"，可放心直接读。

| 归 `tunelab.yaml`（作者手写、TuneLab 决策层） | 留在 `dsconfig` + 预测器子目录（导出生成、单一真相源，插件直接读） |
|---|---|
| `id`（模型 id）/ `version` / `released` / `name` + i18n | `sample_rate` / `hop_size` / `win`·`fft_size` / `num_mel_bins` / `mel_*` / `hidden_size` |
| `voices`（全局 voice id / speaker / name / i18n / portrait / color / 默认语言 + 白名单） | `acoustic` / `phonemes` / `languages` 文件名（ONNX/表引用） |
| `retake.{acoustic,pitch,variance}` 声明 | `use_*_embed`（模型是否吃该输入——导出定死，决定哪些曲线可暴露） |
| `phoneme_mix` 声明 | acoustic/pitch/variance 是否重导出带 `tokens_b`/`blend`/`encoder_out_b`（导出定死） |
| `languages` 的显示名 + i18n + 白名单 | `speakers` / `languages` 原始表（tunelab 的 voices/languages 按 id **引用**，非复制） |
| | 预测器子目录 `predict_*` / `linguistic` / `dur`·`pitch`·`variance` 等 |

> **`vocoder` 字段**：dsconfig 里唯一的宿主约定残留（手填、指向 `Vocoders/{name}/` 安装目录）。第一版**继续从 dsconfig 读**（方案 A，零改动；本插件已复用同款 Vocoders 目录约定）。将来若要与 OpenUtau 解耦，可把它提为 `tunelab.yaml` 的可选字段（方案 B，见 §14）。`voices[].speaker`、`languages.expose[].id` 是对 dsconfig 表的**引用**而非复制，不构成重叠、不会 desync。

## 3. 选型结论

### 3.1 YAML 而非 JSON

| 维度 | YAML | JSON |
|---|---|---|
| 与模型目录内其它文件一致 | ✅ 同目录全是 `*.yaml` | ❌ 风格割裂 |
| 注释（可内联记录暴露约束的原因） | ✅ | ❌ |
| 受众（作者手改手打包） | ✅ 已习惯 | 一般 |
| 插件依赖 | ✅ 已依赖 YamlDotNet，零新增 | 需另走序列化 |

**结论：YAML。** 偏向 JSON 的"TuneLab 自身用 JSON"指的是**插件清单** `description.json`，那是插件级文件、不在模型目录里；本文件住在模型目录、与一堆 `.yaml` 为邻，一致性 + 可注释 + 零新增依赖让 YAML 胜出。

### 3.2 文件名：`tunelab.yaml`

一眼可知归属、发现成本最低、不与现有文件冲突。放在**模型根目录**，与 `dsconfig.yaml` / `character.yaml` 同级。

## 4. 标识与层级模型

### 4.1 用户层级：voice → model → version

用户视角第一层级是**选人（voice）**，第二层级才是**选模型（model）**，第三层级是**选版本（version）**：

```
voice（人，全局）
└─ model（含此人的某个模型）
   └─ version（该模型的某个版本）
```

- 一个 **voice** 可出现在多个 model 里（数据升级重训）；同一 voice id = 同一个人，跨 model 合并为一个顶层条目。
- 一个 **model** 有多个 **version**（同血统的重训/修复）。

### 4.2 三套标识 + 三级显示严格分离

| 标识 | 取值 | 用途 | 稳定性要求 |
|---|---|---|---|
| **VoiceId** | 全局 voice id | TuneLab part 序列化锚点（part 选哪个人） | 跨 model、跨版本、跨改名恒定 |
| **model id** | `tunelab.yaml` 顶层 `id` | 合并键（把同血统多版本归一个 model） | 稳定 |
| **dsconfig speaker** | dsconfig `speakers` 后缀 | 选 `.emb`、对接底层模型 | 可随上游变化 |

| 显示层级 | 取值 |
|---|---|
| 一级（voice 列表） | voice 名（人） |
| 二级（part 属性下拉） | model 名 |
| 三级（part 属性下拉） | version 标签 |

要点：

- **model / version 不进入身份**——它们是 part 属性（两个下拉），合成时解析到具体物理包。换模型/版本不会让 part 的 VoiceId 失效。
- **全局 voice id 由发布方自定**：就是个名字/代号，**我们不做命名要求、不强制 namespace**。同一个人在不同 model 的 `tunelab.yaml` 里填**相同的 voice id** 即自动合并；怕和别家撞名，发布方自己起得独特点即可（撞名 = 误合并，是发布方的责任）。
- **`voices[].speaker` 是对 dsconfig 的引用**：同一个人在两个 model 里的 dsconfig 后缀可以不同，靠 voice id 跨 model join、靠 speaker 对接各自底层。
- 两个**不同的人**显示名恰好相同时，靠二级 model 名消歧。

## 5. Schema

```yaml
# tunelab.yaml —— DiffSinger 模型的 TuneLab 描述（可选）
# 缺失：插件纯走 OpenUtau 加载逻辑（= 今天行为）
# 存在：承载 TuneLab 决策层；与 dsconfig 职责不重叠、各读各的。本文件不参与发现。

format: tunelab-voicebank/1     # 必填：schema 标识 + 主版本

# ---- 模型身份 ----
id: example.choir-2024          # 必填：稳定模型 id。同 id 不同 version → 归一个模型的多版本
name: 合唱团·2024版              # 必填：模型显示名（基准/兜底），作为二级下拉显示
name_i18n:                      # 可选：模型名本地化。key = 宿主 culture（en-US/zh-CN，区域大写）
  en-US: Choir 2024
version: 2                      # 必填：单调递增整数。同一模型内排版本、定"最新版本"（整数比较）
version_label: "2024 重训版"      # 可选：版本的人类可读标签，仅供下拉显示，不参与比较
version_label_i18n: { en-US: "2024 Retrain" }
released: 2024-06              # 可选：跨模型排序用（决定"最新模型"，见 §8）。ISO 日期，可右截断 YYYY / YYYY-MM / YYYY-MM-DD（月/日两位零填充）

# ---- retake 能力（纯声明，三位独立，全默认 false）----
retake:
  acoustic: true                # 已导出 acoustic note 级 retake（软条件 mask）
  pitch: true                   # 已导出 pitch retake mask
  variance: false               # 未导出 → 不暴露 variance seed 轨

# ---- 音素混合能力（纯声明，单个 bool，默认 false）----
phoneme_mix: true               # acoustic + pitch + variance 均已重导出带 tokens_b/blend/encoder_out_b → 暴露帧级音素混合 UI

# ---- 语言（标识符来自 dsconfig，此处只叠加显示名 + i18n + 白名单）----
languages:                      # 可选。整块缺失 = 用 dsconfig 全部语言、显示=裸 id（今天行为）
  default: zh                   # 可选：模型级默认语言（覆盖 dsconfig languages 首项；voices[].default_language 再覆盖）
  expose:                       # 可选：白名单 + 显示名 + i18n。出现 = 只暴露列出的语言并按此顺序
    - id: zh                    # 必填：必须匹配 dsconfig 语言表的键（模型 lang_id 锚此，不可臆造）
      name: 中文                 # 下拉显示名（基准/兜底）；缺省回退裸 id
      name_i18n: { en-US: Chinese }
    - { id: en, name: English, name_i18n: { zh-CN: 英语 } }

# ---- 暴露的 voice 清单（= speaker 白名单）----
voices:                         # 可选：出现 = 白名单；缺失 = 退化为"整模型 1 voice + 全 speaker 下拉"
  - id: singer-c                # 必填：全局 voice id（人）。其他 model 用同一 id = 同一人 → 合并
    speaker: spk_c              # 必填：本模型 dsconfig speakers 后缀（身份≠后缀，显式映射）
    name: 歌手丙                 # 必填：voice 显示名（基准）
    name_i18n: { en-US: Singer C }  # 可选
    default_language: zh         # 可选：覆盖模型级默认语言
    portrait: spk/c.png          # 可选：该 voice 立绘（相对模型根）
    color: "#7AC1FF"             # 可选：UI 配色
  - { id: singer-d, speaker: spk_d, name: 歌手丁, name_i18n: { en-US: Singer D } }
  - { id: singer-e, speaker: spk_e, name: 歌手戊, name_i18n: { en-US: Singer E } }
  - { id: singer-f, speaker: spk_f, name: 歌手己, name_i18n: { en-US: Singer F } }
  # dsconfig 里其余 speaker（不希望暴露的）：未列出 → 既不出现在 voice 列表，也不可作为混音目标
```

### 字段表

| 字段 | 类型 | 必填 | 缺省回退 | 说明 |
|---|---|---|---|---|
| `format` | string | ✅ | — | `tunelab-voicebank/{major}`；major 变更 = 破坏性升级 |
| `id` | string | ✅ | 文件夹名 | 稳定**模型 id**（合并键） |
| `name` | string | ✅ | character.yaml `name` / 文件夹名 | 模型显示名（二级下拉） |
| `name_i18n` | map<locale,string> | ✗ | 回退 `name` | 模型名本地化，key = `en-US`/`zh-CN` |
| `version` | int | ✅ | `0` | 模型内版本号（整数比较） |
| `version_label` | string | ✗ | 显示为 `v{version}` | 版本的人类可读标签，仅显示、不参与比较 |
| `version_label_i18n` | map<locale,string> | ✗ | 回退 `version_label` | 版本标签本地化 |
| `released` | ISO 日期，可右截断 | ✗ | 无（视作最旧；并列→ model id 兜底） | 跨模型排序键。`YYYY`/`YYYY-MM`/`YYYY-MM-DD`，月/日两位零填充，缺省段补 `-01` 后比较 |
| `retake.{acoustic,pitch,variance}` | bool | ✗ | `false` | 是否暴露对应 seed 轨 |
| `phoneme_mix` | bool | ✗ | `false` | 是否暴露帧级音素混合 UI（槽数 + 逐音素目标 + 比例曲线，见 §7） |
| `languages` | map | ✗ | dsconfig 全部语言、显示=裸 id | 语言显示叠加层 |
| `languages.default` | string | ✗ | dsconfig languages 首项 | 模型级默认语言 |
| `languages.expose` | list | ✗ | dsconfig 全部语言 | 出现即白名单 + 排序 |
| `languages.expose[].id` | string | ✅* | — | 必须匹配 dsconfig 语言表的键 |
| `languages.expose[].name` | string | ✗ | 裸 id | 下拉显示名（基准） |
| `languages.expose[].name_i18n` | map<locale,string> | ✗ | 回退 `name` | 语言名本地化 |
| `voices` | list | ✗ | 整模型 1 voice + 全 speaker 下拉 | 出现即白名单 |
| `voices[].id` | string | ✅* | — | **全局 voice id**（人）；跨 model 同 id = 同人 |
| `voices[].speaker` | string | ✅* | — | 本模型 dsconfig speakers 条目 |
| `voices[].name` | string | ✅* | speaker 后缀 | voice 显示名（基准） |
| `voices[].name_i18n` | map<locale,string> | ✗ | 回退 `name` | voice 名本地化 |
| `voices[].default_language` | string | ✗ | `languages.default` | 覆盖模型级默认语言 |
| `voices[].portrait` | string | ✗ | character.yaml `image` | voice 立绘（相对路径） |
| `voices[].color` | string | ✗ | 自动轮转配色 | UI 配色 |

\* 若有对应的 `voices` / `languages.expose` 块则必填。

## 6. voice = 顶层选择单位

TuneLab 的选择单位是 **voice**（`VoiceSourceInfos` 是 voiceId→{Name, Description, Portrait} 的扁平有序表，一个 part 选一个 voiceId）。我们把"人（voice）"做成这个顶层单位：

1. **VoiceSourceInfos 一人一条**：列表里每个全局 voice id 一个条目（人名 + 立绘）。同一人在多个模型里只出现一条。
2. **model / version 是 part 属性**：选了人之后，在 part 面板用两个下拉选 model、选 version（见 §8）。
3. **后端模型按物理 (model, version) 包共享加载**：模型缓存按物理包做 key（见 [DiffSingerVoiceEngine.cs](../DiffSingerVoiceEngine.cs#L116-L126)），voice 只是上层呈现，不会重复加载。
4. **`voices` 块出现 = 白名单**：仅列出的 speaker 暴露为 voice，其余隐藏，并**门控混音目标**（见 §9）。
5. **`voices` 块缺失 = 退化为今天行为**：整模型 1 个 voice、speaker 作为 part 面板下拉 + 混音容器（见 [DiffSingerDeclarations.cs](../DiffSingerDeclarations.cs#L140-L150)）。
6. voice 级展示元数据（人名/立绘）在多模型间若不一致，**以默认 model（见 §8.3）的声明为准**。

## 7. 手写声明的能力（retake 与 phoneme_mix）

有两个能力靠作者**手写声明**而非自动探测——**理由相同**（见 §7.1 末"为什么靠声明"）：`retake` 与 `phoneme_mix`。两者都取决于模型是否用 fork 版重导出、暴露了额外 ONNX 输入。

### 7.1 retake

retake 能力取决于模型 ONNX 是否用 fork 版重导出（把初始噪声外置为 `noise` 输入）。acoustic / pitch / variance 是**三个独立模型族**，可只导出其中一部分。

**为什么靠声明而非探测：**

- 自动探测需创建 `InferenceSession`（加载权重 + 初始化执行设备），是合成阶段才付的重成本，无法在发现阶段对所有模型预先廉价判别。
- 探测出真相后若反向纠正并落盘，等于插件改用户/缓存数据——**越权**。

**因此：retake 三位纯由作者声明，默认 `false`。** 探测顶多作为合成时的**静默兜底**（声明 true 但模型实际无 `noise` 输入 → 该次合成按无 retake 处理，不写任何东西）。作者声明错了是作者责任。

声明位与 seed 自动化轨一一对应（见 [DiffSingerDeclarations.cs](../DiffSingerDeclarations.cs#L91-L97)）：

| 声明位 | 控制的 seed 轨 |
|---|---|
| `retake.pitch` | `seed_pitch`（Pitch seed） |
| `retake.variance` | `seed_variance`（Variance seed） |
| `retake.acoustic` | `seed_acoustic`（Timbre seed） |

> 当前实现里三条 seed 轨**无条件**暴露；接入本字段后应改为按对应 `retake.*` 位 gating。

### 7.2 phoneme_mix

帧级音素混合（让发音在音素间随时间渐变）要求 **acoustic + pitch + variance 三个模型都重导出**、带动态槽输入：acoustic 与共享 linguistic 的 `tokens_b`/`blend`、pitch/variance role 的 `encoder_out_b`/`blend`（详见 [phoneme-mix-export-contract.md](phoneme-mix-export-contract.md)）。三者是**一套**（要么全导出、要么都没有），故用**单个** `phoneme_mix` bool 声明（不像 retake 分三位）。

声明 `phoneme_mix: true` 才暴露音素混合 UI：part 属性「音素混合槽数」`phoneme_mix_slots`（0=关，N=槽 1..N）+ 逐音素 `mix_phoneme:k`/`mix_phoneme_lang:k`（目标音素/语言）+ 自动化轨 `phoneme_mix:k`（逐帧比例包络）。缺省 `false` ⇒ 老库/未重导出的库**完全不显示**任何相关控件。

**为什么和 retake 一样靠声明**：同 §7.1——自动探测需创建 `InferenceSession`（合成期才付的重成本），且探测后反写落盘越权。合成期 `HasInput` 静默兜底：声明 true 但模型实际无这些输入 → 该次合成按无混合处理，不写任何东西。

## 8. 合并、默认与解析

### 8.1 合并算法

```
收集所有模型包（各 tunelab.yaml：model id + version + released + voices[全局id→本模型suffix]）
  → 按 model id 分组（每组 = 一个模型的多个 version；version 整数排序）
  → 跨所有模型按 voice id 取并集（每个 voice id = 一个顶层 voice 条目）
  → 成树：voice(人) → model → version
```

- **物理上各包独立加载各自 ONNX**（权重不同）。合并只在注册/呈现层；模型缓存按物理 (model, version) 包做 key，**不存在权重合并**。
- 每个 (voice → model) 下，只列"**含此 voice 的 version**"（某版本可能移除了某人）。
- 同 model id 同 version 撞包 → 判重，取其一 + 告警，不崩。

### 8.2 model / version 两级下拉（part 属性）

选了人之后，part 面板出现两个下拉：

```
模型：  [ 合唱团·2024版 ]   ← 默认 = 含此 voice 的“最新模型”（见 8.3）
版本：  [ 最新（跟随） ]     ← 默认 sentinel，解析到该模型当前最新版本
          v2 / v1 ...        ← 有 version_label 则显示标签，否则 "v{整数}"
```

- 模型下拉只列"含此 voice 的模型"；版本下拉只列"该模型里含此 voice 的版本"。
- 版本默认 = `最新（跟随）` sentinel（"v" 仅显示前缀，比较按整数）。

### 8.3 默认与"钉死 vs 浮动"（受平台能力约束）

插件**无权写 part 属性**，未被用户碰过的属性永远取声明时动态算出的默认值。因此 **model 与 version 同构**——都是"默认浮动 + 显式钉死"：

- **默认 model = 含此 voice 的最新模型**：取各模型所有包中最大的 `released` 比较。`released` 允许右截断（`YYYY` / `YYYY-MM` / `YYYY-MM-DD`，月/日两位零填充），比较前把缺省段按"最早"补 `-01` 归一化为完整日期（故 `2024` 视作 `2024-01-01`）；无 `released` 的模型视作最旧；并列 → 按 model id 确定性兜底。
- **默认 version = 该模型的最新版本**（`最新（跟随）` sentinel）。
- **钉死 = 显式 opt-in**：用户在下拉里手选具体 model 或 version → 这是用户**主动写**的数据（合法）→ 该 part 钉死、后续升级不变。
- 轻度用户：装了新模型/版本，没动过的 part **自动跟到新的**（"打开旧工程效果自然变好"）；在意复刻的人显式钉死。

> **为什么不能"冻结到创建时的模型"**：那需要插件把"本 part 用哪个 model"写进 part——而插件无权写，没有这个锚点。唯一能"稳定"的是默认锁到一个确定性固定模型（如最早 released），但那会让**新建 part 也默认落到旧模型**，更糟。故只能走"默认最新 + 显式钉死"。
>
> **代价**：换 model 比换 version 剧烈——不同模型可能音素表/支持语言/可编辑曲线都不同，自动切换**可能让钉死音素或已画参数对不上**（同模型内换版本不会）。在意者显式钉死 model 即可。
>
> **"调回去就能恢复"的前提**：旧包仍在。各版本/模型是独立包，升级 = 并排多装；没删旧包就还在下拉里、随时可调回。删旧装新则该项从下拉消失、调不回——这不是 bug，是包没了。

## 9. 混音门控

`speaker_mix` 在**同一个模型同一版本的 embedding 空间**里混。因此：

- **跨 model、跨 version 都不能混**——它们是不同模型/不同 embedding 空间。
- **混入候选 = 当前选中的 (model, version) 里实际存在、且在白名单内的 speaker。** 双重门控：既不能混跨包的人，也不能混白名单外（未暴露）的人。

这是对现有 `SelectedMixTracks` / `BuildSpeakerMixConfig`（见 [DiffSingerDeclarations.cs](../DiffSingerDeclarations.cs#L107-L175)）候选集的收窄。

## 10. i18n（本地化）

### 10.1 边界：只本地化"作者写的内容"

本文件的 i18n **只覆盖作者写的展示串**：模型 `name`、`version_label`、`voices[].name`、`languages.expose[].name`。

**插件自身的 UI 文案**（Speaker / Gender / Energy / 下拉固定 label 等）**不归本文件管**——那是插件 [Localization.cs](../Localization.cs) / `L.Tr` 的职责，作者既不该也不能本地化插件的壳。

### 10.2 形态：内联 per-field，而非集中 locale 块

```yaml
name: 合唱团·2024版
name_i18n: { en-US: Choir 2024 }
```

选内联（每个名字旁挂 `*_i18n`）而非宿主 `description.json` 那种集中 `localizations: { en-US: {...} }` 块的理由：

- 可本地化的串很少（模型名 + 几个 voice 名 + 几个语言名），集中块"一个翻译者编辑一整块"的优势用不上。
- 集中块要按 id 做 join，加一个 voice/语言就得记得去每个语言块补一条，漏了即 orphan；内联把译文贴在被译对象旁，**结构上不可能脱钩**，对手改手打包的作者更稳。

### 10.3 locale key 必须对齐宿主 culture

宿主语言来自 `TuneLabContext.Global.Language`，取值是 **`en-US` / `zh-CN`（BCP-47，区域大写）**（见 SDK [ITuneLabContext.cs](../../TuneLab/TuneLab.SDK/Environment/ITuneLabContext.cs#L7) 注释与本插件 [Localization.cs](../Localization.cs#L12) 的字典键）。因此 `*_i18n` 的 key **规范写法 = `en-US`/`zh-CN`**，与宿主传来的值逐字一致。

### 10.4 解析顺序

`name`（基准串）始终必填、是规范兜底。按宿主当前 locale：

```
精确匹配(zh-CN) → 退到语言码(zh，匹配任一 zh-*) → 退到基准 name
```

为容错作者手抖（写成 `en-us`、`en`），插件解析 `*_i18n` 时应**大小写不敏感** + 支持纯语言码兜底——作者数据比插件自带词典更易写错，值得多一层容错。

## 11. 回退矩阵

| 情形 | 行为 |
|---|---|
| 无 `tunelab.yaml` | 完全等同今天：voiceId = 文件夹名，整模型 1 voice，speaker 走 part 下拉 + 混音容器 |
| 有文件但无 `voices` 块 | 用文件的 `id`/`name`，但仍整模型 1 voice（不拆 speaker） |
| 有 `voices` 块 | speaker 拆成多 voice、白名单生效、混音门控生效 |
| 无 `retake` 块 | 三位皆 false，不暴露任何 seed 轨 |
| 无 `languages` 块 | 用 dsconfig 全部语言，下拉显示裸 id（今天行为） |
| 同 voice id 跨多模型 | 合并为一个顶层 voice + 模型下拉 |
| 同 model id 多版本 | 归一个模型 + 版本下拉 |
| `released` 缺失 | 跨模型默认排序退化为 model id 确定性兜底 |
| 单模型 / 单版本 | 对应下拉只有一项（或直接隐藏） |

## 12. 完整示例（含跨模型合并）

两个模型包，分别描述"数据升级"的前后两代，共享 voice `singer-c`：

**模型包一** `…/choir-v1/tunelab.yaml`：
```yaml
format: tunelab-voicebank/1
id: example.choir-v1
name: 合唱团（初版）
released: 2023-09-01
version: 3
voices:
  - { id: singer-a, speaker: spk_a, name: 歌手甲 }
  - { id: singer-b, speaker: spk_b, name: 歌手乙 }
  - { id: singer-c, speaker: spk_c, name: 歌手丙 }
```

**模型包二** `…/choir-2024/tunelab.yaml`：
```yaml
format: tunelab-voicebank/1
id: example.choir-2024
name: 合唱团（2024）
released: 2024-06-01
version: 2
voices:
  - { id: singer-c, speaker: spk_c2, name: 歌手丙 }   # 同一人，本模型后缀不同
  - { id: singer-d, speaker: spk_d,  name: 歌手丁 }
  - { id: singer-e, speaker: spk_e,  name: 歌手戊 }
  - { id: singer-f, speaker: spk_f,  name: 歌手己 }
```

**合并后用户看到的 voice 列表与层级**：

```
歌手甲   → 合唱团（初版） → v3 / v2 / v1
歌手乙   → 合唱团（初版） → v3 / v2 / v1
歌手丙   → 合唱团（2024）[默认，released 更新] → v2 / v1
          合唱团（初版）                     → v3 / v2 / v1
歌手丁   → 合唱团（2024） → v2 / v1
歌手戊   → 合唱团（2024） → v2 / v1
歌手己   → 合唱团（2024） → v2 / v1
```

> 歌手丙横跨两个模型、合并为一条；默认落在 released 更新的"合唱团（2024）"，想用旧音色则显式选"合唱团（初版）"。dsconfig 的 `sample_rate`/`hop_size`/`use_*_embed`/`speakers` 等一概不出现在本文件——由插件直接读 dsconfig。

## 13. 插件侧接入点改动清单

| 模块 | 现状 | 需改动 |
|---|---|---|
| [VoicebankScanner.cs](../VoicebankScanner.cs) | 每个文件夹 → 1 个 `DiscoveredVoicebank`，VoiceId = 文件夹名 | 解析 `tunelab.yaml`；按 model id 分组多版本；按 `voices` 全局 id 跨模型并集为多 voice；建"voice → {model → {version → 物理目录}}"映射 |
| [DiscoveredVoicebank.cs](../DiscoveredVoicebank.cs) | `(VoiceId, RootPath, Info)` | 改为承载 voice(全局) + model + version 三级 + 物理目录解析表 |
| [CharacterMetadata.cs](../CharacterMetadata.cs) | 读 character.yaml/.txt | 保留为回退源；`tunelab.yaml` 字段优先 |
| [DiffSingerVoiceEngine.cs](../DiffSingerVoiceEngine.cs) `CreateSession` | 按 voiceId 取单一 RootPath | 按 voiceId + model/version 属性解析到具体物理目录；模型缓存 key 纳入 (model, version) |
| [DiffSingerDeclarations.cs](../DiffSingerDeclarations.cs) `BuildPartConfig` | speaker 下拉 + 混音容器 + 语言 | 新增 **model 下拉 + version 下拉** part 属性；混音候选按 §9 收窄到选中 (model,version) |
| `BuildFixedAutomationConfigs` | 三条 seed 轨无条件暴露 | 按 `retake.*` 位 gating |
| `VoicebankConfig` / 新增 `TunelabManifest` | 仅解析 dsconfig | dsconfig 仍出 DSP/模型契约；新增类解析 tunelab.yaml 作者层（id/name+i18n/version/released/retake/voices/languages），两者不重叠合并 |

## 14. part 属性 / 自动化轨字段存储约定（跨引擎复用契约）

> 与上文 manifest（作者写的 `tunelab.yaml`）无关——这里规定的是**插件写进工程 part 的属性 / 自动化轨字段**的存储刻度与命名契约。

### 14.1 为什么要规范

TuneLab 在切换引擎 / 音源时，**不清空 part 里已存的属性与 automation 曲线**。新引擎若声明了同 `key` 的字段，旧数据会被**静默复用**并按新引擎的量程/语义重新解读。要么复用是**正确的 feature**，要么是**脏数据**——取决于字段刻度是否规范。

因此两条核心纪律：

1. **key = 不可变契约。** 一个 key 的完整契约 = `(量程, 基线, 极性, 单位, 语义)`。**一旦发布即冻结**；任何不兼容变更 → **换新 key（或加版本号）**，绝不静默重定义旧 key。
2. **贴合公共认知、能复用就复用。** 用生态标准名（gender/energy/breathiness/voicing/tension，对齐 ACE Studio / ACEP）与归一化刻度，让别的引擎复用同 key 时**值在量程内、不报错**。

### 14.2 归一化刻度：百分比一律改小数

`1.0 = 原样 / 满刻度`，是最通用的归一化单位。统一后的心智模型：

- **乘性 / 百分比参数**：`1.0 = 原样`。
- **加性 / 中性参数**：`0 = 中性`。

| key | 类型 | 量程 | 基线 | 单位含义 | 合成期换算（消费点） |
|---|---|---|---|---|---|
| `gender` | 加性 | `[-1, 1]` | `0` | ±1 = formant 增广满程；正=下移 | clamp 量程 → `GenderConvert`：`±1`→`12/KeyShift*` |
| `speed` | 乘性(对数) | `[0, 2]` | `1` | 1=原速，每 +1 速度 ×2 | clamp 量程 → `SpeedConvert`：`2^(x-1)` |
| `shift_mouth_opening` | 加性 | `[-1, 1]` | `0` | SHMC 口型偏移 alpha：0=不干预，±1=推满开/闭 | clamp 量程 → 透传（模型原生量程即 [-1,1]，无 convert） |
| `energy` | 加性 delta（偏差轨） | `[-1, 1]` | `0` | 1.0 = +12 dB 偏移 | `Delta: x + y*12` → clamp `[-96,0]`dB |
| `breathiness` | 加性 delta（偏差轨） | `[-1, 1]` | `0` | 1.0 = +12 dB 偏移 | `Delta: x + y*12` → clamp `[-96,0]`dB |
| `voicing` | 混合 delta（偏差轨） | `[0, 1.25]` | `1` | 1=不变，0 = 触底 −96 dB，1.25 = +12 dB | 下行 `x − 48(1−y)/(2−y) − (x+72)(1−y)¹²`；上行 `x + 48(y−1)` → clamp `[-96,0]`dB，见下 |
| `tension` | 加性 delta（偏差轨） | `[-1, 1]` | `0` | 1.0 = +5（声学单位） | `Delta: x + y*5` → clamp `[-10,10]` |
| `energy_actual` | 绝对（分段，实参轨） | `[-96, 0]` dB | 无（NaN=未绘制） | 绘制值即该帧的 dB 终值 | 覆盖「预测+`energy` 偏差」→ clamp，见下 |
| `breathiness_actual` | 绝对（分段，实参轨） | `[-96, 0]` dB | 无（NaN=未绘制） | 同上 | 覆盖「预测+`breathiness` 偏差」→ clamp |
| `voicing_actual` | 绝对（分段，实参轨） | `[-96, 0]` dB | 无（NaN=未绘制） | 同上；−96 = 数字静音底（纯气声/耳语） | 覆盖「预测+`voicing` 偏差」→ clamp（mulaw 声库出口再编码回线上域） |
| `tension_actual` | 绝对（分段，实参轨） | `[-10, 10]`（实际天花板 ±9.21，见下） | 无（NaN=未绘制） | 绘制值即该帧的 logit 终值 | 覆盖「预测+`tension` 偏差」→ clamp |
| `expressiveness` | 混合比 | `[0, 1]` | `1` | 1=满表现力（模型自由轮廓）、0=贴谱面音高（OpenUtau PEXP 0~100 小数化） | 逐帧喂 pitch 模型 `expr` 口；仅 dspitch `use_expr` 时暴露 |
| `tone_shift` | 加性 | `[-12, 12]` | `0` | 半音（物理单位不归一；OpenUtau SHFC ±1200 音分）：听感音高不变、音色换音区 | 声学 `f0 × 2^(v/12)`、variance pitch `+v`、声码器吃原始 f0；仅 `pitch_controllable` 声码器时暴露 |
| `mix:<suffix>` | 比例 | `[0, 1]` | `0` | 0=不混入；逐帧标准化(Σ>1 归一) | 直接累积（[DiffSingerSpeakerMix.cs](../DiffSingerSpeakerMix.cs)） |
| `seed_pitch`/`seed_variance`/`seed_acoustic` | 标称 | `[0, 1]` | `0` | 见 §14.3 | `round(v·uint.MaxValue)`→uint32 哈希 |

> 反例：`speed` **不要**归一到 `[0,1]`——百分比倍率（1=原速）本身是更强的公共认知。归一化只对无物理单位或可任意定标的量做。

> **四条 variance 参数各有两条编辑轨：偏差轨（裸 key）+ 实参轨（`_actual` 后缀 key）。**
>
> - **偏差轨 `<key>`**（连续、归一化、显示名即参数名，如"能量"）——**基础调教入口**。相对语义："不管模型给什么，这儿响一点/气一点"。它遵守本节全部纪律（小数刻度、中性基线），且因为是相对量，**换 model / version / seed 重合成后手笔不作废**——模型输出变了，它照样在新输出上加减。
> - **实参轨 `<key>_actual`**（分段、真实声学单位、显示名以冒号追加"实参"，如"能量：实参"——冒号比全角括号省一格宽度）——**细节修改入口**。与回显轨**同 key**，于是宿主认它为**配对轨**：点亮它即带亮回显、右键可把回显曲线整段**烘焙**成本轨锚点（宿主 `SynthesisBake`），即"抓住模型这根线，再改中间一段"。**量程也取与回显一致**——宿主对此不作要求（配对判据只认 Id），但烘焙按真实数值原样写入、不按目标轨量程钳位，量程一致才能让烘焙落在轨内、也才能让两条曲线在参数区叠成同一个坐标系。
>
> 分工同宿主参照实现 `tests/plugins/V1.Voice`（那里是 `energy` 实参 + `energy_offset` 偏差），但**后缀加在哪一条上刻意相反**：
>
> - 裸名 `energy`/`breathiness`/`voicing`/`tension` 是**生态标准名**（对齐 ACE Studio / ACEP）**且是存进工程文件的 key**（`MidiPartInfo.Automations`），按本节纪律 2 该留给**归一化偏差轨**——别的引擎复用同 key 时值在量程内、语义相近，复用是 feature 而非脏数据。
> - 绝对声学量与具体模型的输出单位绑定，本就不该占公共名；挂在插件私有的 `<key>_actual` 上，撞键风险归零。
> - **回显轨的 key 跟着实参轨改（必须同 Id），但显示名仍用裸名**——宿主明文不要求配对两侧同名。于是 tabbar 是「能量」/「能量：实参」、chip 是「能量」，三者不重名而用户认的物理量名不变。回显 key 改动零成本：回显**不进工程文件**（每次合成现算，见 §14.4）。
> - **呈现次序**（tabbar 按引擎声明序排，越常用越靠前）：基础调教区（四条偏差轨 + `gender`/`speed`/口型/表现力/音区偏移）→ **四条实参轨连成一块** → 三条 seed（最不常用，换 take 是偶发动作）。刻意不做"每个参数后面紧跟它的实参轨"的穿插——那会让常用的四条被高级轨隔开、横向找轨得跳着看。
> - 附带好处：**没有任何 key 迁移**。本次改动之前画过 `energy` 等四条曲线的工程，语义原封不动（仍是那条归一化 delta 轨），实参轨是新增的空轨。§14.1 纪律 1 未被触碰。
>
> 残留风险仅在 `<key>_actual` 这组私有 key 上：若将来另一个引擎恰好也声明 `energy_actual` 而量纲不同（比如线性幅度而非 dB），跨引擎复用会脏。这属于"同名同义但不同单位"的通用风险，且这组 key 本就随模型输出单位走、无公共约定可依；相比之下把绝对量塞进裸名 `energy` 会让**每一个**按生态标准声明该 key 的引擎都踩坑，代价大得多。

> **量程钳位是插件的责任，每条轨都要做。** 宿主明文不担保 `Evaluate` 的返回值落在轨声明的 `[MinValue, MaxValue]` 内：`INormalizedScale` 只定义值轴形状与格点（求值投影只落格、不钳值域——标度单调只是约定，非单调时端点不是极值，钳位是无法兑现的保证），而连续轨的值模型是**加性**的——锚点存的是相对默认值的偏移、vibrato 偏移也加性叠到轨上，所以用户拖默认值滑条或让 vibrato 影响该轨，**正常使用即可让求值结果出界**（越界锚点在参数区被裁剪成贴边，肉眼与合法的贴边值无从区分）。分段轨没有默认值也不吃 vibrato，但锚点仍可被拖出量程、更可被手改工程/跨引擎复用/**烘焙**（按真实值原样写入、不钳位）塞进任意值，故一视同仁。上表每个消费点都自钳：`gender`/`speed`/`shift_mouth_opening` 见 [DiffSingerCurveInput.cs](../DiffSingerCurveInput.cs)，八条 variance 轨全在 `CombineVariance` 里钳（实参轨绘制值钳声学值域、偏差轨输入钳 `OffsetMin/Max`、Delta 输出再钳声学值域），`mix:<suffix>`、`phoneme_mix:k`、`expressiveness`、`tone_shift`、三条 seed 各在采样处钳。**OpenUtau 不钳不构成先例**：它在命令层（`ExpCommands.SetCurveCommand`）就把曲线值钳进 descriptor 量程、曲线又存绝对值无加性合成，渲染器读到的必在量程内。

**合成顺序：预测 → 偏差轨叠加 → 实参轨覆盖 → clamp。** 三级各自的语义（[DiffSingerVarianceCurve.cs](../DiffSingerVarianceCurve.cs) `Combine`）：

1. **预测**：方差器出完整基线。不产该通道（`predict_* = false` 而声学仍要这个输入）时取 `0`（`Fallback`）——绝对量没有"中性值"可言，这只是缺基线时的唯一确定选择。
2. **偏差轨叠加**（连续）：`Delta(预测值, 用户归一化值)`。未编辑处求值即中性基线 ⇒ `Delta` 恒等 ⇒ 拿到的就是预测原值。
3. **实参轨覆盖**（分段）：绘制处**整帧替换成绘制值、到此为止**（不再叠任何东西）；未绘制处求值 NaN → 上一级的结果原样透过。于是"哪里接管、哪里不管"逐帧可分，不需要第二条轨来表达作用范围。

**实参轨必须是最后一道**，理由三条，第一条是硬约束：

- **烘焙必须中性。** 宿主的"把回显烘焙进实参轨"语义是"先把模型这根线原样接过来、再改中间一段"。回显本身已含偏差，若烘焙后还要再叠一次偏差，则**一个字没改的纯烘焙就会改变声音**（预测 −30、偏差 +6 → 回显 −24 → 烘焙 → 再叠 +6 → −18）。实参轨在最后 ⇒ 烘焙即恒等（单测 `BakeThenFeedBack_IsIdentity` 逐参数钉住）。
- **名副其实 / 所见即所得。** 实参轨与回显同 key 同量程、叠在同一坐标系里显示，已画段两条必须重合；若偏差还能事后挪动它，这条轨就不是"实参"而只是另一个中间量。
- **分工自洽。** 偏差轨管"模型没定的地方整体挪一挪"，实参轨管"这一段我说了算"——后者当然压过前者。

代价（已知并接受）：**改偏差轨不会带动已被实参轨钉死的帧**，phrase 级调整会在钉死段留个"洞"。要让它跟着走，把新回显重新烘焙一次即可。这与宿主 `Pitch` / `PitchDeviation` 的取舍相反（那边 vibrato 偏差刻意作用到钉死区，因为叠加一个振荡正是它的用途），差别的根源是：vibrato 是宿主注入的载波，而这里的偏差轨是用户自己的另一笔编辑，两笔编辑冲突时以更具体的那笔为准。**两条都没动 = 纯预测**，与历史行为逐比特一致。

回显轨报的就是这份三级合成结果（`BuildReadbackSegment`），它始终诚实报"实际喂进去的值"：已画段与实参轨的线严丝合缝，未画段则是预测叠偏差后的曲线。

**voicing 偏差轨的公式是对 OpenUtau 基准的有意偏离**（其余三条忠实移植 OpenUtau `VarianceDeltaFunctions`，系数按小数刻度 ×100）。OpenUtau 的 voicing 满偏只有 −12 dB 且只降不升：够不着静音底（无法做纯气声/耳语段）、也无法把谐波抬到预测之上。改为双支：下行（y≤1）「饱和有理项 + 十二次幂跳水项」`实参 = x − 48(1−y)/(2−y) − (x+72)(1−y)¹²`，按三个锚定需求逐点定形；上行（1<y≤1.25）线性 `实参 = x + 48(y−1)`，满偏 +12 dB（与 energy 正向满偏对齐、避开 0dBFS 过载区——实测满偏 +48 dB 的版本一画就爆音），中性线两侧斜率相等（48）、过线仅二阶导跳变、无顿挫。（`x` = 预测值；被实参轨钉死的帧不走本公式，见上文合成顺序。）下行三锚定：

- **y→1**：跳水项以十二次方消失，起步斜率精确 48 dB/满程——浅笔即有可闻的去声带化（±12 的基准手感实测"变化太小"）；
- **消声点 ≈ y 0.2**：实测谐波压到预测以下 ~26 dB 即被气声成分完全掩蔽（OpenCpop，典型预测 ≈ −20 dB），有理项饱和值把这一深度铺满 1→0.2 的行程——八成轨道全是有效渐变；
- **y=0**：实参恒精确落在 −96（数字静音底），全域可达。
- **导数刻意非单调**（先陡后缓再跳水），不是 bug：起步斜率 48 高于可听区平均斜率 ~32（26 dB ÷ 0.8 行程），中段必须放缓找平（积分守恒）；末端 ~70 dB 挤在最后两成行程又必然跳水。任何同时满足上述三锚定的曲线导数皆非单调。
- 边角：预测 < −72 dB 的帧（本就近静音）跳水项系数变负、中段轻微越过 −96；用户值越界（宿主数据层无量程硬契约：锚点+默认值合成、跨引擎同 key 复用、手改工程）会使偶次幂/分母变号产生荒谬值——两者皆由合成期双 clamp 兜住（输入钳 `OffsetMin/Max`、输出钳声学值域，见 `CombineVariance`）。
- 设计过程曾比较：加法 ±12/±48（前者够不着底、后者顶部灵敏度糙 4 倍且消声点 0.46 偏高）、归一化 lerp `(x+96)y−96`（顶部糙 ~7 倍、消声点 0.66）、线性+低次幂混合（p3/p4 起步斜率 12~24，"1 附近变化太小"）、分段 S 曲线（规格直接编码但非光滑）——现式为分段 S 的光滑等效替代。
- 实参轨的出现**不削弱**这条曲线的价值：它管的是"整体压一档"的相对手感（换模型不作废），实参轨管的是"这一小段就要这个值"。两者互补，故公式一分未动。

**tension 的域：logit，边界比看起来窄。** tension 不是 dB，而是**比值的 logit**：`tension = ln(r/(1−r))`，
`r` = 非基频谐波振幅 / 全谐波振幅 ∈ [0,1]（上游 `utils/binarizer_utils.py` 的 `get_tension_base_harmonic`）。
logit 本身无界，但提取时 `r` 先被**硬编码**（不是配置项）clip 到 `[1e-4, 1−1e-4]`，于是训练目标的绝对上限是
`ln((1−1e-4)/1e-4) ≈ 9.2103`。因此：

- 上游 hparams `tension_logit_min/max`（默认 ∓10）只管两件事——归一化尺度、以及输出 clamp。作者把它调窄
  （如 ±8）则模型自己截得更紧，调宽（如 ±15）也没有数据支撑它走到那儿。**所以 `[-10, 10]` 对任何配置都是
  安全超集**，插件不必、也无法探知每个库的具体设置：这两个值**不写进 dsconfig**（导出器只写 `predict_*` /
  `voicing_domain` / `voicing_mu` 等），而是烘焙成 variance 图里 `Clip` 节点的常量——那是**图内部结构、不是
  契约**（简化器会折叠重排、多通道靠 trace 展开），去挖它猜错的代价比不挖更大，故不挖。
  （唯一的理论缝：`.ds` 属性可直接提供外部 tension 曲线、绕过上述 clip；但 `tension > 9.21` 意味着基频谐波
  只占全谐波振幅万分之一以下，物理上已不成立，故不为它设计。）
- 另外三条同样是超集，理由不同：它们的 clamp 上界在 `param_adaptor` 里**写死为 `0`**，下界默认 −96 即 16-bit
  动态范围底。注意它们的**归一化** range 上界是 `*_db_max`（默认 energy −12、breathiness/voicing −20），
  与 clamp 上界不同源——上表四条取的一律是 **clamp 域**。
- **手感（画实参轨时有用）**：`0` ↔ `r = 0.5`；`±4` ↔ `r ≈ 0.982 / 0.018`，再往外进入 sigmoid 饱和区、
  听感几乎不再变化；`±10` 的最后约 0.8 是取不到的空头。故 tension 实参轨的有效行程集中在中间小半段，
  与 energy 那种全程可用的 dB 轨手感不同。

**mulaw 声库（dsconfig `voicing_domain: mulaw`，fork feat/mulaw-voicing 实验）**：voicing 线上值改为谐波 RMS 的 mu-law 压缩振幅（`voicing_mu` 缺省 255）线性映射到同一 `[-96,0]` 数值域（上游 API 冻结下的伪装 dB）。**上表公式与两条编辑轨 / 回显轨 / 工程文件恒为 dB 语义**——十二次幂三锚定是感知规格、与模型编码无关；插件在模型边界做三明治转换（[VoicingDomainCodec.cs](../VoicingDomainCodec.cs)：预测的线上值→振幅→dB 进合成，喂声学前反向编码；两种表示均为振幅的单调双射，转换精确），故两种声库上用户画的同一条曲线含义完全一致。红利：实参触底 −96 时 mulaw 域编码为**精确零振幅**（真·静音，而非 db 域 clamp 出的 −96dB 余响）。域标记从声学与 dsvariance 各自的 dsconfig 读取，**缺省 `db` = 历史声库零迁移**；两侧同时在用却异域/异 μ = 坏导出，log 后按 acoustic 侧处理（[VoicebankConfig.cs](../VoicebankConfig.cs) `ReadVoicingDomain`）。数值契约由 SmokeTest `--mulaw-codec` 自检钉住（锚点+全值域往返+真零特判）。

**SHMC 声库（dsconfig `use_shift_mouth_opening_embed: true`，fork PR#2 实验）**：声学接受帧级口型偏移 `shift_mouth_opening = alpha ∈ [-1,1]`（相对模型隐式基线的自蒸馏控制量，非绝对口型曲线；键取偏移语义，`mouth_opening` 留给将来可能的绝对曲线轨）。模型原生量程即编辑量程、中性 0 = 不喂即无偏移 → 老工程/无轨零影响；无域转换、无方差器基线（也就没有回显轨）。冻结导出（`--freeze_shift_mouth_opening`）时导出器写 flag 为 false，轨随能力 gating 消失。alpha→偏移曲线的映射公式烘焙在 student 训练期，插件侧只透传——手感问题（分段 (1−α) 插值的增益漂移/压扁/闭合保真）属训练侧议题。

### 14.3 seed：标称值（nominal），不是幅度（magnitude）

seed 是"哪一个 take"的**身份令牌**，没有大小语义（seed 0.4 不比 0.3"更大"），故：

- **量程 `[0,1]`**：最通用刻度，撞键也不越界；别的 retake 引擎天然兼容、不报错（但不保证产同一 take——哈希实现不同）。
- 合成期 `round(Clamp(v,0,1)·uint.MaxValue)` 放大到 uint32 喂**位置寻址哈希**（[DiffSingerNoise.cs](../DiffSingerNoise.cs)）。哈希白化 ⇒ **刻度不影响噪声质量/分散度**，4 亿+ take、随机几乎不碰撞。
- **只有精确 `v=0` 映成 seed 0 = 保留 take-0**；任何画上去的 `v>0` 都映成非零 → 触发该帧重摇（acoustic retake mask 命门，[DiffSingerSynthesisSession.cs](../DiffSingerSynthesisSession.cs#L385)）。

### 14.4 回显轨：与实参轨配对的只读那面

variance 回显轨（`SynthesizedParameters`）是**实参（预测 → 偏差轨叠加 → 实参轨覆盖 → clamp 后）、绝对值、真实声学单位**（`[-96,0]` dB / tension `[-10,10]`）。它的 key 是 **`<参数名>_actual`**（与实参轨同 Id，见下），**显示名却用裸名**（chip 上就叫"能量"——用户认的是物理量）。除此之外它与实参轨同量程、同为分段形，差别只在读写权（回显恒只读）。宿主两套声明面各自键控（`GetAutomationConfigs` / `GetSynthesizedParameterConfigs`），同 key 不冲突；参数区里回显画成半透明积分面积、编辑轨画成细线，于是"模型给的"与"我画的"在同一坐标系里叠着看。

**同 key 即宿主的配对判据**（`SynthesisBake.HasPairedReadback`，显示名与量程宿主都不要求一致）：点亮实参轨会带亮它的回显，且区间右键菜单出现"把回显烘焙进本轨"——一次性、可撤销、写真实数值（不按目标轨量程钳位，故本插件自愿把两侧量程声明成同一个）。烘焙后曲线就是普通用户数据（不再跟随重合成），这正是"抓住模型这根线再改中间一段"的落地手段。配色上实参轨用同色系提亮版（`VarianceSpec.ActualColor`），压在自己那片半透明回显上仍看得清；偏差轨与回显轨维持本轨历来的 `Color`。

mulaw 声库的 voicing 回显亦恒为 dB（取三明治的 dB 语义中间值，见 §14.2）——回显单位不随模型编码漂移。

回显**不存进 part**（每次合成现算）：既零跨引擎复用风险，也让"为了配对而改回显 key"成为一次零成本改动（工程文件里没有它的痕迹，只有宿主 UI 与 agent 的音源参数报告会看到这个 id）。存进 part 的是两条编辑轨——裸 key 那条守着 §14.2 的归一化契约、`_actual` 那条是插件私有名，故 §14.1 纪律 1 无需破例。声学值域三处复用：实参轨量程、回显轨量程、合成期输出 clamp（单一真相源 `VarianceSpec.AcousticMin/Max`）。

### 14.5 离散字符串字段

同样按 key 静默复用（值为字符串）：

- `language`：**建议**声库制作方用 ISO 639 / BCP-47 码（`zh`/`ja`/`en`）。插件作为桥**无法强制**，只能在 schema 建议 +（可选）加载时对非 ISO 形态打 warning。**不做**别名映射表（那是自造标准）。
- `model` / `version` / `speaker`：受 ComboBox options 校验，查无回落默认 sentinel（`""`）——这类**受校验离散值天然安全**，不会因复用产生脏值。
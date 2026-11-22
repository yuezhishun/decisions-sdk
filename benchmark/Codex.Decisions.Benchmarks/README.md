# Codex.Decisions WorkflowEvals 离线回放

用 TypeSafe 公开 Jev 记录驱动 `Codex.Decisions.CreateJevSystemOneAsync`。SDK 启动仓库构建的本地 Codex App Server fixture。fixture 逐批校验 SDK 收到的 State、题型、指令和选项顺序，并返回录制概率。后续分支与动作用 SDK 的实际响应计算。全程无需模型 API、Codex 登录或外部可执行文件。

## 协议与评分

- 官方工作流使用 Jev System One 的 **Noul、Choice、Score**，按题目 ID 绑定答案，工作流直接使用选项概率、置信度和 Score 期望值。沿用官方动作集合与主要动作的比较规则，动作参数也参与比较。
- OpenAI Decisions 是另一条受支持的入口。若要用它运行这批工作流，适配层需把 Jev 的键名、结构化证据和判定条件映射到 OpenAI 的问题结构，并还原其按序返回的答案。当前公开 Jev 回放走原生 Jev 入口，双协议的接口行为由仓库已有的 fixture 回归验证。
- 冻结的 Jev 记录有 24 个 Choice 和 11 个 Score 的概率合计为 `0.99`。原值用于复算公开基线。喂给 Codex fixture 前先将这 35 个分布按各自总和归一化，再检查 SDK 输出及下游动作。
- 使用 consensus 为主参考，另列 OpenAI 和 Anthropic 标签。模拟用量只用于校验批次聚合。离线一致率衡量回放兼容性，真实模型的判断质量、延迟和费用需要单独测量。该批数据不涉及 Box、图像、拒答和长上下文开关。

## 复现

使用 .NET 11 SDK，在仓库根目录运行。复用 [现有准备命令](../DeepSeek.Decisions.Benchmarks/README.md#先准备验证与估价)生成带文件校验值的 `artifacts/workflowevals/sources.json` 与四份数据快照。已有缓存时可直接离线执行。

```powershell
dotnet run --project benchmark/Codex.Decisions.Benchmarks -c Release -- --root artifacts/workflowevals
```

`codex-replay.json` 保存数据 revision、逐例路径覆盖、预期和实际策略动作、三类参考的分子分母及与公开 Jev 的差值。只评估可完成回放的案例，报告列明未完成和不一致的案例。覆盖不完整时命令以非零状态退出。

冻结数据的全量回放完成了 705 个案例、1,249 次 SDK 请求、12,080 道题及 1,524 个策略决策。全部路径及动作与公开 Jev 记录一致，三类参考的 15 个指标分数及分母均相同。四个工作流主指标的等权平均为 67.76%。

数据及 scoring 规则来自 [TypeSafe 官方 WorkflowEvals 固定版本](https://github.com/typesafe-ai/WorkflowEvals/tree/0ac3b8ad845429f0d8e064ecfb2430a47c5a25cb)。派生内容的声明和许可见 [NOTICE](../DeepSeek.Decisions.Benchmarks/NOTICE.md) 和 [UPSTREAM-LICENSE](../DeepSeek.Decisions.Benchmarks/UPSTREAM-LICENSE)。

## 本机 CLI 试跑

`pilot` 使用指定的本机 Codex CLI 和现有 ChatGPT 登录。派发前会确认账户可用的模型包含 gpt-6.2-sol 且该模型支持 low，再使用此配置运行试跑。各工作流先按公开 Jev 路径题量排序，再取三个等人数分层的中位案例。题量相同按案例 ID 排序。评分配对使用同一批完成的案例，另存最初抽样集合的 Jev 分数。

```powershell
$codex = (Get-Command codex.exe -All | Select-Object -First 1).Source
dotnet run --project benchmark/Codex.Decisions.Benchmarks -c Release -- pilot --codex "$codex"
dotnet run --project benchmark/Codex.Decisions.Benchmarks -c Release -- pilot-report "<试跑目录>/pilot.json"
```

`artifacts/workflowevals/codex-pilot/<运行时间>/pilot.json` 保存逐例动作、SDK 请求、App Server 批次用量、耗时及配额快照。失败案例保留错误和已知用量。遇到模型重路由、无法确认的用量或套餐额度受限时结束试跑。`forecast.json` 从本地报告估算全量，不调用模型。套餐窗口与 credits 的换算依赖账户政策，API 等价金额单列。模型归属未确认的轮次仍计入用量汇总，费用留空。

## 全量运行与续跑

获准全量后，指定已完成且带有 `strict_model: true` 的 12 个 `gpt-6.2-sol/low` 试跑案例作为检查点来源。旧版报告保留供查阅。其余案例使用本机 CLI 的 `gpt-6-luna/low`。报告同时提供混合模型结果、Luna 子集，以及相同案例范围的公开 Jev 对照。

```powershell
$codex = (Get-Command codex.exe -All | Select-Object -First 1).Source
dotnet run --project benchmark/Codex.Decisions.Benchmarks -c Release -- full --codex "$codex" --pilot "<试跑目录>/pilot.json" --parallel 4
```

按案例并行，最多启动 4 个本机 App Server 会话。任一会话发现额度限制后停止派发新轮次，已发送的轮次返回后记录用量。

带 Pilot 的检查点位于 `artifacts/workflowevals/codex-full/gpt-6-luna-low-pilot-<Pilot 文件的 SHA-256>/`。同一 Pilot 文件会续跑同一个目录。历史 `gpt-6-luna-low/` 仍可查阅，也是下文定向复测的固定基线。`progress.json` 每例更新，`report.json` 定期更新并在完成或暂停时写入最终状态。`cases/` 保存终态案例，`attempts/` 保存每次尝试及其轮次用量，适用额度、未完成和失败记录均可追溯。模型输出格式失败且用量已知时，该例按官方指标计错。额度限制和用量未知的案例保留尝试记录，等待续跑。

确认可续跑后使用同一条命令，保留原始 pilot 和数据根目录。检查点会核对来源、运行配置和唯一案例 ID，并跳过符合终态规则的案例。旧检查点中用量未知的输出失败和超时会重新排队，新终态提交时将原记录备份到 `superseded-cases/`。

### Sol 中推理强度全量运行

指定型号和推理强度后，以 10 个本机 App Server 会话评测冻结的 705 个案例。此配置使用独立的 `artifacts/workflowevals/codex-full/gpt-6.2-sol-medium-fresh/` 检查点，无需 pilot。

```powershell
$codex = (Get-Command codex.exe -All | Select-Object -First 1).Source
dotnet run --project benchmark/Codex.Decisions.Benchmarks -c Release -- full --codex "$codex" --model gpt-6.2-sol --effort medium --parallel 10
```

`report.json` 对同一批已获得终态的案例计算 Codex 和公开 Jev 相对 consensus、OpenAI、Anthropic 参考的得分，并列出 Codex 与 Jev 直接动作一致率。覆盖完整时，Jev 的配对得分与冻结的全量基线相同。

## 定向复测

`retest` 从原全量检查点选择 39 个已报告无效响应的 Luna 案例，以及 8 个 Luna 进入 containment、公开 Jev 未进入该节点的安全事件。安全事件包括 EDR、IDP、NET 各两个共识未要求处置的案例，以及两个共识要求处置的案例。评分使用同一批已获得终态的案例，对照原全量运行和公开 Jev。

```powershell
$codex = (Get-Command codex.exe -All | Select-Object -First 1).Source
dotnet run --project benchmark/Codex.Decisions.Benchmarks -c Release -- retest --codex "$codex" --effort max
```

输出位于 `artifacts/workflowevals/codex-retest/gpt-6-luna-max/`。失败批次保存可取得的原始最终文本、校验项及题目位置。安全事件保存逐题原始概率、SDK 接受的概率和公开 Jev 概率。定向复测单批等待上限为 5 分钟。诊断在本地写入，SDK 校验和官方分支阈值沿用原实现。新结果在发布和崩溃恢复时使用同一套终态规则。失败批次用量未知时保留尝试日志，暂停派发，续跑时重试该案例。遇到额度限制或传输故障也会停止派发并保留进度。

## 安全事件初筛提示实验

实验使用 10 个本机 App Server 会话运行 `gpt-6.2-sol/medium`。从已完成的 Sol 全量日志选出 36 个不同活动底稿，额外处置、无需处置对照、需要处置对照各 12 例。新提示只注入 `triage` 请求，模型答案仍经原 SDK 校验并驱动官方工作流。

```powershell
$codex = (Get-Command codex.exe -All | Select-Object -First 1).Source
dotnet run --project benchmark/Codex.Decisions.Benchmarks -c Release -- triage-ab --codex "$codex"
```

`artifacts/workflowevals/codex-triage-ab/security-triage-v1/` 保存提示、选样清单、基线指纹及本轮检查点。报告使用同例比较原 Sol、实验 Sol 和公开 Jev 对 consensus 的额外处置、漏处置和完整动作一致率，并列出参考一致及分歧的额外处置子组。结果范围为定向样本。

V2 沿用 V1 的提示并追加双向证据判据。V2 + 标尺沿用完整的 V2 提示并仅追加 Noul 概率标尺。两个版本各有独立目录，启动前核对前一轮的提示、案例分组与基线指纹。按下列顺序运行。

```powershell
dotnet run --project benchmark/Codex.Decisions.Benchmarks -c Release -- triage-ab --codex "$codex" --variant v2
dotnet run --project benchmark/Codex.Decisions.Benchmarks -c Release -- triage-ab --codex "$codex" --variant v2-scale
```

V3 从原 Sol 的 SDK 提示出发，仅给判断活动是否未授权的 Noul 题追加双向证据判据。使用前 36 例以外的 24 个活动底稿，三组各 8 例。报告核对应处置是否达到 8/8、额外处置是否少于原 Sol，以及完整动作一致数是否至少与原 Sol 相同。

```powershell
dotnet run --project benchmark/Codex.Decisions.Benchmarks -c Release -- triage-ab --codex "$codex" --variant v3
```

V3 的计划、独立检查点和报告保存在 `artifacts/workflowevals/codex-triage-ab/security-triage-v3/`。

V4 复测 V3 的同一批 24 例，只替换判断活动是否未授权的证据判据。启动前按实际案例重算并核对 V3 摘要。报告逐例核对追回两例漏处置、消除一例新增误入、保住六例已纠正的误入，以及完整动作一致至少 15/24。计划和报告保存在 `artifacts/workflowevals/codex-triage-ab/security-triage-v4/`。

```powershell
dotnet run --project benchmark/Codex.Decisions.Benchmarks -c Release -- triage-ab --codex "$codex" --variant v4
```

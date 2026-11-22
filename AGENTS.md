# Decisions SDK 仓库指引

## 项目结构

- `src/DeepSeek.Decisions/` 基于 `DeepSeek.Core` 的 Chat Completions 实现 OpenAI Decisions 和 Jev System One，发布 `DeepSeek.Decisions`，目标框架为 `netstandard2.0`。
- `src/Codex.Decisions/` 通过本机 Codex App Server 实现两套协议。项目、程序集和命名空间均为 `Codex.Decisions`，NuGet 包名为 `Decisions.Codex`，目标框架为 `net11.0`。
- `test/` 包含 DeepSeek 的 xUnit 单元测试和在线集成测试。`samples/DecisionsDemo/` 提供双后端 HTTP 示例，`scripts/` 包含模拟 Codex App Server、OpenAI SDK 探针和 Demo 回归脚本。
- `benchmark/` 存放 WorkflowEvals 的运行与回放代码。数据缓存与运行产物放在 Git 忽略的 `artifacts/` 下。数据准备、回放和真实模型评测命令参见各 benchmark 的 README。

## 构建与验证

从仓库根目录执行。使用 .NET 11 SDK，并安装 .NET 8 和 .NET 10 运行时，以运行默认目标框架分别为 `net8.0` 和 `net10.0` 的两个测试项目。Demo 回归需要 Python 3。

```powershell
dotnet build Decisions.slnx -c Release
dotnet test Decisions.slnx -c Release --no-build --filter 'Category!=Live'
python scripts/test-decisions-demo.py samples/DecisionsDemo/bin/Release/net11.0/DecisionsDemo.dll scripts/codex-fixture/bin/Release/net11.0/CodexFixture.exe scripts/decisions-demo-sdk/bin/Release/net11.0/DecisionsDemoSdk.dll
./.github/Test-NuGetPackage.ps1 -Package Decisions.Codex
./.github/Test-NuGetPackage.ps1 -Package DeepSeek.Decisions
```

Linux 和 macOS 的 fixture 路径省略 `.exe`。Demo 回归使用本地模拟后端并覆盖 OpenAI Decisions 和 Jev System One，无需模型凭据。NuGet 校验脚本复用 Release 构建，产物写入 `artifacts/packages/`。

在线集成测试用 `LiveFact` 和 `Category=Live` 标记。仅在有意调用 DeepSeek API 时运行 `dotnet test test/DeepSeek.Decisions.IntegrationTests -c Release --filter 'Category=Live'`。测试依次读取 `DEEPSEEK_API_KEY` 和 `deepseek-api-key`。发布工作流设置 `DEEPSEEK_REQUIRE_LIVE_TESTS=true`，凭据缺失时失败。

## 代码与测试约定

- 沿用 C# 的 4 空格缩进、文件作用域命名空间、Nullable 和 ImplicitUsings。公开成员使用 PascalCase，局部变量和参数使用 camelCase。
- 保持两套协议的公开请求和响应兼容。改动协议行为时，按影响范围验证对应的 SDK 入口和 Demo HTTP 行为。DeepSeek 单元测试使用内存 `HttpMessageHandler`，Codex 协议回归复用本地 fixture。
- 依赖版本在 `Directory.Packages.props` 中集中管理。修改依赖时同步核对受影响项目已有的 `packages.lock.json`。

## 提交与发布

- `main` 的推送和 PR 运行离线 CI。提交说明简短具体，PR 写明 API 或行为变化、验证结果以及依赖或包版本变化。
- 两个 NuGet 包独立升级和发布。升级时对齐目标项目的包版本、程序集及文件版本，以及根目录中英文 README 和包 README 的安装命令。`DeepSeek.Decisions` 的包依赖须与集中配置中的 `DeepSeek.Core` 版本一致。
- 先在目标提交上手动运行 `Publish NuGet` 并选择目标包。DeepSeek 包的发布验证使用仓库 Secret `DEEPSEEK_API_KEY`，在 `net10.0` 和 `net11.0` 上分别运行在线回归。
- 验证通过后，推送与包版本匹配的注释标签 `Decisions.Codex/v<版本>` 或 `DeepSeek.Decisions/v<版本>`，由工作流上传对应包。确认 NuGet 可下载后再创建 GitHub Release，标题使用版本号，正文提供中英文说明。

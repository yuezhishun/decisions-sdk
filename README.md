# Decisions SDK

.NET clients for OpenAI Decisions and Jev System One request shapes.

| Package | Target | Backend |
| --- | --- | --- |
| [Decisions.Codex](src/Codex.Decisions/README.md) | .NET 11 | Local Codex App Server |
| [DeepSeek.Decisions](src/DeepSeek.Decisions/README.md) | .NET Standard 2.0 | DeepSeek Chat Completions via [DeepSeek.Core](https://github.com/yuezhishun/deepseek-sdk) |

See the [Chinese guide](README_CN.md), the [dual-protocol demo](samples/DecisionsDemo/README.md), the [DeepSeek WorkflowEvals benchmark](benchmark/DeepSeek.Decisions.Benchmarks/README.md), and the [Codex WorkflowEvals replay](benchmark/Codex.Decisions.Benchmarks/README.md).

## Installation

```shell
dotnet add package Decisions.Codex --version 1.0.0
dotnet add package DeepSeek.Decisions --version 1.2.1
```

Check [Decisions.Codex](https://www.nuget.org/packages/Decisions.Codex) and [DeepSeek.Decisions](https://www.nuget.org/packages/DeepSeek.Decisions) for published versions. Build from this repository to try a version before it appears on NuGet.

## Source

`Codex.Decisions` moved from SagWiki.Net at `00654a45cb6a768a4ec8d533ade6e86d3bedae4c`. `DeepSeek.Decisions` moved from [deepseek-sdk](https://github.com/yuezhishun/deepseek-sdk) at `b97621ef8e6b68ba06f6716573918e0bc60a20bc` with its tests and WorkflowEvals benchmark. Both packages use the MIT license. Benchmark fixtures derived from WorkflowEvals retain their [upstream license](benchmark/DeepSeek.Decisions.Benchmarks/UPSTREAM-LICENSE).

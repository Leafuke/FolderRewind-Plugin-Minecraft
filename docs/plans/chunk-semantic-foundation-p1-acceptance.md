# P0/P1 实施与验收记录

日期：2026-10-07。分支：`codex/chunk-semantic-foundation`。
Host 基线保持 `a320b2b`；MineRewind 起点 `8ffb5cb`，最终实现提交 `eb9e612`。
本记录及后续仅文档提交不改变被测行为。

## 交付

- `MineRewind.World.Semantic`：独立来源/维度/component 身份、完整只读 inventory、
  原始 Anvil envelope、payload hash、external 引用、分段 scratch 和验证重建。
- 正常 region 的压缩 payload 与容器残余字节分开保存；异常 region 明确整文件保留。
  Header、timestamp、sector padding、空闲区和尾部字节均逐字节还原。
- 独立 `WorldProbe index / roundtrip / compare`；同一索引用于重建与三方内容分析。
- 受控输出、完整性校验、取消/失败不发布有效完成状态、输入/输出不相交、链接拒绝。
- 源文件/路径 metadata 限额；missing mcc 引用只计数、不按恶意扇出无限缓存。
- 三方比较始终不可应用；实体双改要求更大范围审阅，未知 entities 阻断整个相关维度。
  `.mca` 与其引用 `.mcc` 的容器字节差异不伪装成 payload 冲突。

未修改公共 Plugin API、原有 AnvilReader/SurfaceReader/Preview 生命周期、manifest/
发行版本、Host Artifact transform 阻断、正式备份流程、用户世界或应用配置。
没有更新安装态插件、内置 frplugin、远端分支或发行包。

## 自动化与构建

| 验证 | 结果 | 证据 |
|---|---|---|
| 新领域/工具测试 | **54 passed，0 failed，0 skipped** | `world-final.trx`；最终实现 `eb9e612` |
| 既有 MineRewind 全量 | **93 passed，0 failed，0 skipped** | `plugin-final.trx`；`dd2024b`，后续仅新语义路径修正，未更改原插件/预览路径 |
| World + WorldProbe Release | 0 warnings / 0 errors | 独立构建及上述测试构建 |
| Git whitespace check | 通过 | `git diff --check 8ffb5cb HEAD` |

新测试包含独立 literal NBT/Anvil fixtures、六种 compression/external 组合、1024-slot
region、负坐标与多世界、多维度、entities/poi-only、零字节文件、非零padding/空闲区/
尾部字节、输入移除后的重建、损坏/超限/缺失分类、scratch篡改、非法coverage、链接/
路径越界、取消、拒绝访问、模拟写入失败、inventory预算、1024个缺失mcc引用、
三方增删冲突/独立chunk修改、跨chunk实体迁移、未知实体维度及实际CLI进程测试。

数据安全断言没有使用新writer作为唯一oracle：测试直接构造字节并保留原始byte[]，
删除原输入目录后才重建和逐文件比较。Scratch manifest和pack篡改均被拒绝。

执行命令：

```powershell
dotnet test MineRewind.World.Tests/MineRewind.World.Tests.csproj -c Release
dotnet test MineRewind.Tests/MineRewind.Tests.csproj -c Release --no-restore
dotnet build tools/MineRewind.WorldProbe/MineRewind.WorldProbe.csproj -c Release
```

既有插件测试先通过本阶段独立 NuGet.Config/feed/cache 还原。API包由当前Host
源码构建为3.9.0，仅放在本地隔离feed；没有声称其在NuGet.org发布，未覆盖全局缓存。
本地产物SHA-256：`632BCB734A370538D747B5924467EED4D594D31AEACAFA624C73E35C0119BEE2`。
没有运行会替换Host内置插件的完整Build-WorldPreview脚本。

## 真实世界字节保真

使用已授权世界的独立副本。复制前后及副本均核对路径、长度、SHA-256；不是原子游戏
快照保证。最终统计来自`real-world-run-03`，对应`dd2024b`的capture/reconstruct实现；
后续比较/缺失引用计数修正由新回归覆盖，这些样本均无mcc。

| 样本 | 文件 | region | 数据MiB | 可比较component chunks | opaque region | 重建总秒数 | 峰值工作集MiB |
|---|---:|---:|---:|---:|---:|---:|---:|
| 预览演示 | 7 | 6 | 4.73 | 1,200 | 0 | 0.226 | 52.64 |
| 26.1.2 Fabric | 128 | 99 | 154.40 | 22,974 | 25 | 3.466 | 106.39 |
| 1.21.8 NeoForge | 30 | 12 | 8.42 | 2,145 | 8 | 0.385 | 61.70 |
| 1.21.1 NeoForge | 28 | 12 | 16.37 | 2,768 | 2 | 0.711 | 75.61 |

**193个文件全部路径、长度、SHA-256一致，总计192,857,737字节。**
29,087个已解析component chunks可作opaque内容比较；它们不等于同数量的坐标bundle。
实际原文件逐字节保真检查全部成功，没有通过删除文件来获得通过结果。

35个opaque region全部为零字节文件，诊断为`empty_region_without_header`。
没有location table就不推断chunk不存在，因此三份真实世界工具退出码为1
（验证已完成、语义覆盖需关注），不是执行错误；对应`Verification.Success=true`，
整体`VerificationPassed=false`。不将“字节保存完整”写成“整个世界已可语义合并”。
演示世界退出码0。真实样本没有external mcc，其格式由独立fixtures覆盖。

实际流I/O同时记录。例如26.1.2样本：source读739,641,561字节，scratch读
248,317,382字节、写200,182,145字节，重建写及重建复核读均为161,899,959字节。
这些是当前验证管线的读取放大，不是增量备份节省证明。

## 有界资源与三侧比较

独立生成的32/320-region语料每文件1MiB、slot0有literal NBT，其余为必须保真的
容器残余。`scale-02`结果：

| 规模 | 源体积 | 总时间 | 峰值工作集 | 当前managed heap |
|---|---:|---:|---:|---:|
| 32 regions | 32MiB | 0.490s | 64.47MiB | 37.32MiB |
| 320 regions | 320MiB | 3.467s | 112.64MiB | 22.50MiB |

两档均无opaque fallback、逐文件重建通过。输入体积增大10倍，峰值工作集约1.75倍，
没有把整个payload驻留内存。该实验不能证明任意规模的进程硬内存上限；inventory
另有65,536 entries/4Mi路径字符限额，超过时明确拒绝。固定decode/copy buffer
推导上界17,047,552字节，不冒充实测heap峰值。全部缓存/工作缓冲设计目标256MiB。

`compare-real-02`使用相同26.1.2离线样本作三侧，对应最终实现`eb9e612`：
8.048s，峰值工作集126,431,232字节（120.57MiB），22,009个坐标/未知region锚点
报告，0内容冲突、10,339个阻断、0双实体修改审阅、0文件变化。阻断来自未知region
及未知entities维度的保守传播。三侧相同不会被误报为“双方都修改实体”；未知状态
仍不能被证明为安全自动合并。这个实验验证有界处理与闭锁，不是合并能力发布验收。

所有时间为本机单次热缓存附近观测，不是冷盘p95或游戏加载认证。工具报告分别提供
累计allocated bytes、当前managed heap、峰值working set及实际stream I/O。

## Gate 结论与下一阶段边界

| Gate | 结果 |
|---|---|
| 字节保真 | 通过：合成及真实文件、删除输入后重建、异常文件无损fallback |
| 模型共用 | 通过：重建与比较共用同一identity/index/hash；没有第二套chunk parser |
| 有界资源 | 通过本阶段预算/样本：有限inventory、单chunk解码、按region比较、规模曲线 |
| 真实样本 | 保真通过，语义缺口明确：35个零字节region不能推断为空 |
| 原功能回归 | 93项既有测试通过，public API及预览实现未改 |
| 操作边界 | 工具不可Apply，不写现有History/配置；CLI安全测试通过 |

**P0/P1工程交付完成；不宣告全格式/全世界语义覆盖，也不自动进入P2或解除Host阻断。**
零字节文件继续原样保留并阻断相关语义判断。P2仍需独立完成Artifact—Native History、
加密、retention、Cloud及崩溃事务闭环；P4之前仍需真实文本provider验证公共Merge API。
本阶段不验证实体语义/NBT字段语义、不支持LZ4解码、不生成合并世界、不执行游戏加载。

私有样本、完整路径、SourceId、玩家数据及原始日志均留在Host仓库忽略的
`artifacts/semantic-p1/`，不提交。可复用工具和经过归纳的本记录纳入插件仓库。

## 提交

- `875910b`：约定与阶段计划。
- `e89e0ce`：无损拆解与验证重建。
- `214cb75`：离线工具、比较与有界读取。
- `dd2024b`：领域与CLI测试。
- `71ffff8`：external容器差异与payload冲突分离。
- `d0f5f30` / `16ad5fe`：缺失引用有界记账及限额回归。
- `eb9e612`：未知实体输入与双方修改分开。

Host没有新增提交，保持`a320b2b`。所有提交仅本地，未推送或发布。

# P2 Full 开发验收

2026-10-07，基线 e7d780e，实现提交 f38f285，分支 `codex/native-artifact-full`。

实际候选包：`MineRewind-full-final.frplugin`，开发版本仍为 1.9.8，API 3.9。
SHA-256：`a189e6dd06be568b300f00a1f8869df615835635247f5c4b1044492b271ebd31`。
未推送、发布或替换 Host 内置/安装态包。

World 59 项、MineRewind 93 项测试通过；World/插件独立构建，未引入 Host 源码或项目引用。
格式说明见 `docs/architecture/minecraft-world-full-v1.md`。

Host 通过真实包安装、激活和 capability 路由完成：CoreFull → Full Transformer → Host 封存/加密 →
同一 Version 的双表示 → 进程重启后指定 Plugin 表示事务还原/Checkout。
恢复期间 Core 对照不可访问；Host 逐文件集合、长度、SHA-256 验证后成功。
真实 SafetySnapshot、通用 Merge Apply（输出 CoreFull）、配对 Retention 与真实 rclone 本地 remote 均已通过。

| 样本 | Effective Boundary 文件数 | 字节 | opaque 零字节 region |
|---|---:|---:|---:|
| 地形演示 | 7 | 4,964,453 | 0 |
| 26.1.2 Fabric | 127 | 161,899,956 | 25 |
| 1.21.8 NeoForge | 29 | 8,829,470 | 8 |
| 1.21.1 NeoForge | 27 | 17,163,849 | 2 |

三个真实世界的 session.lock 由既有 Host 边界排除；P2 没有越过边界读取。
每份都通过明文及加密 Cloud 往返。独立字面 `.mcc` 语料另有 16 文件、131,369 字节，
包含内置/外置 gzip/zlib/raw、未知压缩和零字节 region；3 个 opaque 保真成功并保留语义告警。

Host 主验收 30 个成功结果、外置语料 6 个结果；提交阶段 12 次预期进程中断恢复正确。
完整命令、资源测量、脱敏 JSON 和 Gate 见 Host 仓库
`docs/plans/Native_Artifact_Full_P2_Acceptance.md` 与 `Native_Artifact_Full_P2_Results.json`。

边界：仅 Full v1；不做 chunk delta、语义合并、新公共 API 或 UI。Full 格式与实验 probe scratch 分开。
Exact 是文件保真，不能将 opaque 宣称为可语义比较。继续保留 Core 对照和开发入口限制。
World/玩家数据/本机配置不纳入 Git。P3 不自动开启。

# 第三方组件与分发边界

本仓库和前端 ZIP **不包含** Windhawk、任何第三方模组 DLL、安装器、编译器、msdia/symsrv、libc++/libunwind 或 StartAllBack 二进制。运行所需 .NET 8 Desktop Runtime 由用户从 Microsoft 单独获取，未捆绑分发。

## 后端协议所参考的上游

| 组件 | 固定版本 | 上游许可证据 |
| --- | --- | --- |
| Windhawk | 1.7.3 | [官方 LICENSE，GPLv3](https://github.com/ramensoftware/windhawk/blob/v1.7.3/LICENSE) |
| taskbar-start-button-position | 1.3.2 | [源码头部声明 GPLv3](https://github.com/ramensoftware/windhawk-mods/blob/f3ec3675168600d1decfcda97d2a12e375903ae9/mods/taskbar-start-button-position.wh.cpp) |
| taskbar-icon-size | 1.3.10 | [源码头部声明 GPLv3](https://github.com/ramensoftware/windhawk-mods/blob/f3ec3675168600d1decfcda97d2a12e375903ae9/mods/taskbar-icon-size.wh.cpp) |
| explorer-frame-classic | 1.0.8 | [源码头部声明 GPLv3](https://github.com/ramensoftware/windhawk-mods/blob/f3ec3675168600d1decfcda97d2a12e375903ae9/mods/explorer-frame-classic.wh.cpp) |
| explorer-context-menu-classic | 1.0.2 | [模组未另行指定许可时适用上游 MIT 默认规则](https://github.com/ramensoftware/windhawk-mods/blob/f3ec3675168600d1decfcda97d2a12e375903ae9/README.md) |

ClassicDesk 的界面、主题、图标和 C# 宿主实现随本仓库公开；与外部引擎的交互使用独立进程、配置文件和固定消息协议。上游名称、作者和许可不会因使用自有设置前端而被替换。

0.11.9 开发源码另包含可选任务栏样式/背景资产的[固定清单](runtime/windows-x64-assets.json)，以及 [native/](native/README.md) 中基于 Taskbar Background Helper 和 Dynamic Taskbar Transparency 的 GPL-3.0-or-later 原生变体源码、策略头和构建脚本。该部分保留自己的许可声明与 [GPL 正文](native/COPYING)，不受仓库其他代码的版权保留声明覆盖。此次同步没有上传增强 DLL、引擎或本机运行配置，也没有变更公开前端包。

## 为什么首版不发布增强运行包

GPLv3 对二进制分发及对应源代码交付有明确要求；完整对应源码包括生成、安装和运行所需的相关源代码与控制脚本，不能仅以几份模组源码和一个主页链接替代。具体条件参见 [GPLv3 第 1、6 节](https://github.com/ramensoftware/windhawk/blob/v1.7.3/LICENSE)。

计划中的运行闭包还包含 C++ 运行库、符号处理组件及相关 shim。当前公开材料未完成这些精确二进制版本的对应源、完整许可/NOTICE、再分发资格与构建说明整理。因此本次只交付独立前端及其源码，缺失组件时保持停用。

未来增强包发布前必须逐项完成：

1. 固定每个二进制的来源、版本、SHA-256、架构及必要依赖，保留原作者声明。
2. 为 GPL 组件准备与二进制对应的完整源代码、补丁和可复现构建/安装脚本，核对依赖/子模块。
3. 对 libc++、libunwind、shim、msdia/symsrv 分别核查精确版本的许可、NOTICE 与允许的分发方式；不能由主引擎 GPL 声明推断全部依赖权利。
4. 明确标注增强引擎会驻留、可能加载进 Explorer，以及独立的启用/恢复与兼容范围。
5. 在隔离 Windows 验收环境进行真实效果、退出、重启与恢复测试，再更新公开能力状态。

此文档没有授予第三方软件任何额外权利，也不表示未捆绑组件已完成公开分发审核。

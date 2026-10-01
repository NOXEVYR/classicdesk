# 0.11.20-preview 验证与发布边界

## 已有验证记录

2026-10-01 的本地候选完成以下十五组隔离检查，共 **832通过、0失败**：

| 检查 | 通过 |
| --- | ---: |
| Core | 93 |
| Host | 106 |
| Frontend | 92 |
| VisualPreview | 102 |
| FocusMenu | 56 |
| ReleaseBoundary | 30 |
| ServiceUI | 12 |
| AutoHide | 19 |
| ColdUpgrade | 20 |
| UpdatesFeed | 83 |
| UpdatesInstall | 36 |
| UpdatesUI | 19 |
| StartCore | 86 |
| StartUI | 28 |
| LayoutDesign | 50 |

Core/Host与其他十三组按原记录分步执行。最终范围复测为 FocusMenu56、Frontend92、UpdatesUI19、StartUI28、LayoutDesign50，共 **245/0**；这些重复检查不与832相加。

FocusMenu最终112张离屏PNG覆盖六款皮肤、740×550和1080×820两种大小及100/125/150/200%栅格输出。焦点模板的Tag模拟只验证模板外观，不能证明原生键盘焦点或真实显示器DPI切换。

## 多轮排查的结论

- 早期夹具的XamlWriter往返产生InputGestureText.UnsetValue，随后检查画布比Tooltip允许宽度更窄。这两类失败属于夹具问题，修正模型和画布后再检查。
- 第三轮55/0仍遗漏了自然混合菜单；第四轮出现MenuItem样式不能用于Separator的真实缺陷。删除固定异构ItemContainerStyle、使用类型隐式样式，加入没有手工Style的字符串/对象/MenuItem/Separator和二三级子菜单后，最终FocusMenu56/0。独立只读审查复现旧异常并验证修复。
- 长Tooltip明确字符串Wrap并保留自定义UIElement；Esc先交给编辑控件，未处理时才冒泡导航；偏好写入失败回读实际值；重建页面/重命名的焦点恢复有可见、激活与一次请求约束。

原详细运行日志和合成夹具留在开发机，不公开私人路径。公开截图使用合成方案，没有读取真实桌面、文件、应用列表、时钟或壁纸。

## 2026-10-02 发布收尾

复用已经验收的四个程序文件，核对SHA-256、版本与既有ZIP证据。公开源副本仅修改文档，C#/XAML/csproj和测试输入保持原候选字节；本轮没有重建、重跑整套检查、启动GUI、安装或修改Windows。

程序包中的frontend-installation.json及五个独立更新资产重新绑定真实公开源码提交。Release标签、target_commitish、清单Build与四文件摘要须一致。原本地树指纹不是Git提交，原本地候选和本地安装保持不动。

原候选阶段已完成ZIP逐项摘要/路径检查、提取源码构建及四程序文件对照；发布阶段只对新的公开文档、清单和归档重新核对，不将原候选重建结果冒称新Git环境下的可复现构建。

## 未验收

- 原生Alt/Tab/Shift+Tab、Popup定位与真实方向键/Enter/Esc、IME、屏幕阅读器、窗口活跃切换。
- 真实多屏/DPI、触边唤出、睡眠唤醒、Explorer/Windows跨会话恢复及长期稳定性。
- ServiceBundle与真实管理员组件安装、真实外壳增强提交及完整StartAllBack替代。
- 正在使用的真实前端在线升级及WPF重启握手。模拟HTTP和隔离子进程不是用户实机验收。

公开版本始终为prerelease，保存/预览不会自动改变Windows，设计工作区未接真实任务栏后端。

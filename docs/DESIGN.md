# 设计说明

本文记录 GameTrans 的实现取舍与关键细节，便于二次开发与问题定位。

## 1. 总体结构

```
src/GameTrans
├─ Program.cs            进程入口、单实例互斥、异常兜底、调试模式
├─ TrayApp.cs            托盘、快捷键编排、两条翻译流程
├─ ApiProbe.cs           命令行探针（设置 Key、文本/视觉翻译验证）
├─ SelfTest.cs           无界面自检
├─ Core
│  ├─ AppInfo.cs         版本与产品标识
│  ├─ Win32.cs           P/Invoke：GDI、DPI、窗口样式、热键、DPAPI
│  ├─ ScreenCapture.cs   抓帧、PNG 编码、坐标与 DPI 换算
│  ├─ AppSettings.cs     配置模型、默认值、夹紧与归一化
│  ├─ SettingsStore.cs   配置读写、密钥加解密、日志
│  ├─ HotkeyManager.cs   RegisterHotKey 封装
│  ├─ DeepSeekClient.cs  chat/completions 调用与错误映射
│  └─ Prompts.cs         提示词模板（画面翻译 / 输入翻译 / 术语表注入）
└─ UI
   ├─ Theme.cs           深色主题与控件工厂
   ├─ DropDown.cs        下拉控件（规避 ComboBox 主题问题）
   ├─ OverlayContent.cs  叠加层内容与样式
   ├─ OverlayWindow.cs   叠加层窗口与尺寸求解
   ├─ RegionSelectorWindow.cs  全屏冻结截图选区
   ├─ InputWindow.cs     F9 输入翻译
   ├─ SettingsWindow.cs  设置页
   ├─ StartupWindow.cs   启动页
   ├─ GlossaryWindow.cs  术语 / 黑话对照表
   └─ ToastWindow.cs     轻提示
```

技术栈：.NET 9 + WPF（`net9.0-windows`），`win-x64`，自包含单文件，`asInvoker` 与 PerMonitorV2 清单。

## 2. 坐标与 DPI

原则：**进程内部一律使用物理像素；仅在 WPF 窗口定位时换算为 DIP。**

- 截图使用 `GetSystemMetrics(SM_XVIRTUALSCREEN…)` 取得虚拟桌面矩形，`BitBlt` 直接按物理像素读取。
- 区域选择窗口先以 DIP 设置初始位置，随后用 `SetWindowPos` 以物理像素强制贴合整个虚拟桌面，再读取实测 `ActualWidth` 反推 `px/DIP`。这样即使 WPF 的逐显示器 DPI 判定与预期不一致，鼠标坐标到像素的映射仍然正确。
- 叠加层与输入框的目标显示器 DPI 由 `GetDpiForMonitor(MDT_EFFECTIVE_DPI)` 获得，窗口尺寸与位置按 `px / scale` 换算。
- 混合 DPI（如 100% + 150%）下，冻结截图整体拉伸到窗口；由于换算系数由实测尺寸反推，跨屏框选仍能得到正确像素区域。

## 3. 区域选择

沿用「冻结整屏截图」方案：先 `BitBlt` 全桌面生成位图作为窗口背景，再叠加 `CombinedGeometry(EvenOdd)` 遮罩，使选区外变暗。该方案不需要分层窗口即可获得「变暗 + 框选」效果，且不会出现实时桌面更新带来的闪烁。

交互：拖动创建选区，八个控制点调整，工具栏提供重选/全屏/取消/确定，Enter 确认、Esc 或右键取消。

## 4. 叠加层

窗口属性：

| 属性 | 作用 |
|---|---|
| `WindowStyle=None` + `AllowsTransparency` | 无边框、逐像素透明 |
| `WS_EX_TRANSPARENT` | 鼠标事件穿透，不拦截游戏输入 |
| `WS_EX_NOACTIVATE` | 点击不激活、不抢焦点 |
| `WS_EX_TOOLWINDOW` | 不出现在 Alt+Tab |
| `Topmost` | 置顶 |
| `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` | 从屏幕截图中排除，避免重复识别自身译文 |

尺寸求解（`OverlayWindow.Relayout`）：

1. 宽度 = 识别区域宽度（DIP）。
2. 按当前字号使用 `FormattedText` 实测文本高度（含换行）。
3. 高度 = max(区域高度, 文本高度 + 内边距 + 边框 + 状态行)。
4. 溢出策略：
   - `expand`：允许向下扩展，最多到显示器工作区底部；必要时向上借用空间。
   - `shrink`：固定为区域高度，逐像素下调字号直至 `minFontSize`。
5. 结果钳制在显示器工作区内。

字号自适应：`clamp(min(区域宽/420, 区域高/260), 0.75, 2.5) × 基准字号`，兼顾小区域与大区域。

> 历史问题：1.0 使用 `Viewbox(StretchDirection=DownOnly)`，文本略高于区域即整体等比缩小，导致小区域中译文不可读；1.1 改为按实测高度扩展窗口。

## 5. 快捷键

使用 `RegisterHotKey` 注册全局热键（默认 `F8` / `F9`），不使用低级键盘钩子：

- 不注入进程、不拦截全局按键流，降低与反作弊和输入法的冲突面；
- 注册失败可被检测（如已被其他程序占用），并反馈给用户；
- 通过隐藏的 `HwndSource` 接收 `WM_HOTKEY`。

## 6. 模型调用

端点 `{baseUrl}/chat/completions`，请求体为 OpenAI 兼容结构：

```json
{
  "model": "deepseek-flash",
  "messages": [
    { "role": "system", "content": "<提示词>" },
    { "role": "user", "content": [
      { "type": "text", "text": "<指令>" },
      { "type": "image_url", "image_url": {
          "url": "data:image/png;base64,...", "detail": "high" } }
    ]}
  ],
  "max_tokens": 2048,
  "temperature": 0.2
}
```

- 图像仅出现在 user 消息；编码为 data URL，全程内存操作。
- `429` 与 `5xx` 自动重试一次（指数退避）。
- 错误按 `status + 服务端 message` 透出，便于定位 Key、余额、限流与内容问题。

## 7. 提示词设计

三条硬约束决定了叠加层是否可读：

1. **只输出译文正文** —— 不加「译文：」等前缀、不加解释与 Markdown。
2. **严格保持分行** —— 与原文行数与顺序一致，便于与原画面逐行对照。
3. **术语与语气** —— 作品名、术语表、社区通行译法优先；口语与缩写不书面化。

术语表以「`- 词条 = 含义`」列表追加在规则之后，并注明「表中未收录的词按上下文判断」，避免模型对表外词生硬套用。

输入翻译（F9）使用独立系统提示词：目标语言默认英文，要求一行对一行、短句不扩写、语气与原意一致。

## 8. 配置与存储

- 目录：`%APPDATA%\GameTrans`；`portable.txt` 或环境变量 `GAMETRANS_DATA` 可覆盖。
- API Key 通过 `CryptProtectData` 加密后写入 `settings.json`，读取时解密；失败则视为未配置。
- 所有数值在 `Normalize()` 中夹紧，避免手工编辑配置文件导致越界。
- 日志单文件轮转（2 MB）。

## 9. 已知限制

- 无流式输出：单次请求返回完整结果，适合「看结果」而非「看打字」。
- 无帧差/增量刷新：每次触发均为完整的一轮截图与翻译。
- 术语表仅支持单一词条映射，未实现正则或上下文条件。
- 仅在 100% 与 150% 缩放、单双屏环境完成验证；极端混合 DPI 建议实测。
- 独占全屏与受 DRM 保护的画面无法截取，属系统限制。

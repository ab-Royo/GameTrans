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
│  ├─ Localization.cs    界面语言、语言代码、目标 API 预设与外部链接
│  ├─ Win32.cs           P/Invoke：GDI、DPI、窗口样式、热键、DPAPI
│  ├─ ScreenCapture.cs   抓帧、PNG 编码、坐标与 DPI 换算
│  ├─ AppSettings.cs     配置模型、默认值、夹紧与归一化
│  ├─ SettingsStore.cs   配置读写、密钥加解密、日志
│  ├─ ClipboardService.cs 剪贴板写入（带时间预算的 Win32 实现）
│  ├─ HotkeyManager.cs   RegisterHotKey（键盘）+ GetAsyncKeyState 轮询（鼠标键）
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
- 叠加层与输入框的目标显示器 DPI 由 `GetDpiForMonitor(MDT_EFFECTIVE_DPI)` 获得；叠加层按 `px / scale` 换算后用 WPF 的 DIP 属性定位，输入框则直接用物理像素 `SetWindowPos` 定位（见第 13 节），避免逐显示器 DPI 下的点击偏移。
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

### 键盘键

使用 `RegisterHotKey` 注册全局热键（默认 `F8` / `F9`），不使用低级键盘钩子：

- 不注入进程、不拦截全局按键流，降低与反作弊和输入法的冲突面；
- 注册失败可被检测（如已被其他程序占用），并反馈给用户；
- 通过隐藏的 `HwndSource` 接收 `WM_HOTKEY`。

**轮询兜底（1.4.0 起）**：`RegisterHotKey` 在两类场景下不可靠——热键已被其他程序独占（注册直接失败），以及部分游戏使用 DirectInput / 独占全屏、按键不经过消息泵（注册成功但收不到 `WM_HOTKEY`）。因此键盘键同时加入 15 ms 的 `GetAsyncKeyState` 轮询：

- 同一次物理按键会被两条通道看到，用毫秒时间窗去重：轮询侧发现按下时，若原生通道在 80 ms 内刚触发过则跳过；原生通道收到 `WM_HOTKEY` 时，若轮询在 400 ms 内刚触发过则跳过。窗口上下不对称，是为了在「只有轮询有效」时仍能响应快速连按。
- 注册失败的键照常加入轮询，并在托盘提示「已自动启用轮询兼容模式」，功能不中断。
- 轮询同样不安装钩子；代价与鼠标键相同：游戏内同键绑定会同时响应，极短（<15 ms）的点按可能漏掉。
- 程序模拟键入译文时调用 `SuspendFor()` 临时挂起热键，避免模拟按键（尤其当热键被绑定到字母键时）再次触发翻译。

**执行位置（1.4.1 起）**：轮询不再使用 WPF `DispatcherTimer`，改由专用后台线程（`GameTrans.HotkeyPoll`，`ManualResetEventSlim.Wait(15)` 定时唤醒）执行：

- 每次唤醒只做 6–7 次只读 `GetAsyncKeyState` 查询，命中按下边沿后才通过 `Dispatcher.BeginInvoke` 把回调派发到 UI 线程，空闲时不占用 WPF 消息循环；
- 绑定表以不可变快照发布（UI 线程整体替换、轮询线程只读），按键状态与两通道时间戳使用 `Volatile`/`Interlocked` 访问，避免锁竞争；
- 退出或重新注册时通过信号量唤醒线程并 `Join`，避免残留线程。

本机实测（Ryzen 9 8945HX，32 逻辑核）：原 `DispatcherTimer` 方案空闲占用约 11 ms/s CPU 时间，专用线程方案约 3 ms/s，热键触发行为不变。

### 鼠标键（中键 / 侧键1 / 侧键2）

`RegisterHotKey` 只支持键盘虚拟键：对鼠标键（`VK_MBUTTON`=0x04、`VK_XBUTTON1`=0x05、`VK_XBUTTON2`=0x06）调用它会**返回成功但永远不会收到 `WM_HOTKEY`**（本机实测验证）。因此鼠标键改用轮询：

- 鼠标键与键盘兜底共用同一个 15 ms 轮询线程；键盘兜底常开，因此该线程在程序运行期间持续存在，空闲开销见上一节；
- 修饰键必须与绑定时完全一致（`Ctrl` / `Alt` / `Shift` / `Win`），且只在“未按 → 按下”的瞬间触发一次；
- 不安装任何全局鼠标钩子，代价是**按键不会被本程序吞掉**：游戏里若也绑定了同一鼠标键，会同时响应；轮询粒度也意味着极短（<15 ms）的点击可能漏掉。

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

输入翻译（F9）使用独立系统提示词：目标语言由设置决定，要求一行对一行、短句不扩写、语气与原意一致。

提示词内的语言名由 `Languages.PromptName` 统一生成（中文提示词中固定使用「自动识别 / 中文（简体）/ English / 韩语 / 日语」），与界面语言解耦，避免切换 UI 语言影响模型行为。

## 8. 配置与存储

- 目录：`%APPDATA%\GameTrans`；`portable.txt` 或环境变量 `GAMETRANS_DATA` 可覆盖。
- API Key 通过 `CryptProtectData` 加密后写入 `settings.json`，读取时解密；失败则视为未配置。
- 所有数值在 `Normalize()` 中夹紧，避免手工编辑配置文件导致越界。
- 日志单文件轮转（2 MB）。

## 9. 剪贴板写入

F9 译文写入剪贴板不使用 WPF `Clipboard.SetText`：剪贴板被其他进程占用时（加速器、录屏 / overlay 工具会周期性 `OpenClipboard`），WPF 内部会重试约 10 次 × 100 ms 并**阻塞 UI 线程**，导致「按 Enter 关闭」被拖慢数秒甚至看起来无响应（曾定位到占用方是随游戏启动的 GameViewer）。

改为 `ClipboardService.TrySetText`：先 `GlobalAlloc` 准备 `CF_UNICODETEXT` 内存，再 `OpenClipboard` / `EmptyClipboard` / `SetClipboardData`，在 250 ms 预算内重试，失败即返回。调用方据此在状态栏提示「剪贴板被占用」，并立即执行关闭，不再让 UI 线程等待。

## 10. 已知限制

- 无流式输出：单次请求返回完整结果，适合「看结果」而非「看打字」。
- 无帧差/增量刷新：每次触发均为完整的一轮截图与翻译。
- 术语表仅支持单一词条映射，未实现正则或上下文条件。
- 鼠标键热键为轮询实现，无法拦截原始按键；游戏侧同名绑定会同时触发。键盘键在原生注册之外也有轮询兜底（见第 5 节），代价相似：极短（<15 ms）的点按可能漏掉，游戏内同键绑定也会同时响应。
- 输入框的模拟输入依赖游戏窗口仍在接收字符输入（聊天输入框处于打开状态）；若游戏在失焦后关闭聊天框，键入会落到游戏操作上，属使用方式限制。
- 仅在 100% 与 150% 缩放、单双屏环境完成验证；极端混合 DPI 建议实测。
- 独占全屏与受 DRM 保护的画面无法截取，属系统限制。

## 11. 界面本地化

- 语言集合固定为 `zh`（简体中文）与 `en`（English），保存在 `ui.language`。所有用户可见文案通过 `Loc.T("中文", "English")` 二选一，不引入资源文件，避免打包与查找成本；日志与异常堆栈保持中文。
- 语言代码（`auto` / `zh` / `en` / `ko` / `ja`）存储在配置里，界面显示名由 `Languages.Display` 按当前语言生成；提示词内的语言名由 `Languages.PromptName` 固定用中文表述，与 UI 语言解耦。
- 设置页的控件构建全部集中到 `BuildBody()`：切换语言时就地重建整页（保留 `PasswordBox` 内容与未保存的其它编辑），保存后通知 `TrayApp` 重建托盘菜单；若启动页可见则关闭后用新语言重开。取消/关闭未保存时把 `Loc` 还原为打开时的语言，避免「界面变英文但配置仍是中文」。
- 旧配置中的语言名（`中文（简体）`、`English`、`自动识别` 等）在 `AppSettings.Normalize()` 中归一化为语言代码；未知值按 `auto`（源语言）或 `zh`（目标语言）处理。
- 英文布局适配：设置页标签列加宽（88 → 122 DIP）、按钮使用 `MinWidth` 并按内容撑开、复选框拆成两行、启动页底部提示改为两行并把窗口高度从 486 调到 512。
- 离屏演示支持 `--lang zh|en`，`LoadDemoSettings()` 会把演示用配置的语言与目标 API 预设同步到当前渲染语言，用于生成中英文截图。

## 12. 目标 API（中国站 / 国际站）

DeepSeek 官方对国内与海外账号使用同一个 API 端点 `https://api.deepseek.com`，因此「目标 API」不是协议切换，而是**部署/账号区域预设**：

| 预设 | `baseUrl` | 开放平台 | 文档 | 官网 |
|---|---|---|---|---|
| `cn` 中国站 | `https://api.deepseek.com` | `https://platform.deepseek.com/` | `https://api-docs.deepseek.com/zh-cn/` | `https://www.deepseek.com/` |
| `intl` 国际站 | `https://api.deepseek.com` | `https://platform.deepseek.com/` | `https://api-docs.deepseek.com/` | `https://www.deepseek.com/en/` |

- 默认规则：`api.site` 为空或非法时，按界面语言取默认（中文 → `cn`，其它 → `intl`），由 `AppSettings.Normalize()` 落盘。
- 设置页切换预设会把 Base URL 重置为该预设值（当前两站相同），并在「目标 API」下方提供「开放平台 / API 文档 / 官网」三个跳转链接；Base URL 仍可手工覆盖，切换语言不会覆盖用户手动改过的站点选择（`SettingsWindow._siteTouched` 启发式）。
- 若未来官方拆分区域端点，只需修改 `ApiSites` 中的 `BaseUrl` 常量，界面与归一化逻辑无需改动。

## 13. F9 输入窗稳定性（1.4.0）

用户反馈英雄联盟里 F9 输入框「翻译结束后不自动消失、Enter / Esc 无效、关闭按钮点不中要连点几次」。
拆分下来是四个独立问题，均已修复：

| 现象 | 根因 | 修复 |
|---|---|---|
| Enter / Esc 无效，窗口关不掉 | 游戏（无边框 / 独占全屏）持前台锁，`Show()+Activate()` 被拒绝：窗口可见但键盘焦点仍在游戏 | `Win32.ForceForeground()`：`AttachThreadInput` 到当前前台窗口线程后 `SetForegroundWindow`，失败时 150 ms × 8 次重试；`Deactivated` 时立即停止重试，不跟用户抢焦点 |
| 翻译结束后 3 秒不自动关闭 | 旧逻辑只在「剪贴板复制成功」时才 `ArmAutoClose()`；游戏客户端 / 加速器常占用剪贴板，复制失败分支没有倒计时 | 翻译成功后无论复制成败都启动倒计时（成功 3 s、失败 5 s 并提示 Enter 重试）；任何按键取消倒计时 |
| Esc 只在输入框聚焦时有效 | 处理挂在 `TextBox.KeyDown` 上，点过按钮后焦点离开输入框即失效 | 提升到窗口级 `PreviewKeyDown`，Esc / Enter 在任意焦点位置都生效 |
| 关闭按钮「点击区域与显示区域不一致」 | ① 状态文字与按钮同处一个 Grid 单元格且后添加，长文案字形会盖住按钮吞掉点击；② 窗口用 DIP 定位，混合 DPI 下与实际显示器缩放不一致，出现「看得到、点不到」 | 状态栏 `IsHitTestVisible=false` 且与按钮分列；窗口改用物理像素 `SetWindowPos` 定位/定尺寸，DPI 变化时重新摆放 |

其余：关闭时把前台还给游戏（同样走 `ForceForeground`），避免用户关窗后还要手动点回游戏。

## 14. 模拟输入（F9，1.4.0 新增）

设置项 `behavior.simulateInput` 默认关闭。开启后 F9 流程由「复制到剪贴板」改为「模拟键入」：

1. 翻译成功 → 输入框展示译文约 300 ms；
2. `Close()` 触发 `Closing → RestoreForeground`，用 `ForceForeground` 把前台还给游戏；
3. 轮询等待 `GetForegroundWindow()==游戏窗口`（最多 12 × 50 ms）——**确认目标窗口真的拿到焦点才开始键入**，
   避免把译文打进随机窗口；
4. 调用 `HotkeyManager.SuspendFor(len*20+500 ms)` 临时挂起热键（译文里的字符可能命中所绑定的热键）；
5. `InputSimulator.SendText()` → `SendInput` + `KEYEVENTF_UNICODE`，逐 UTF-16 单元发送 keydown/keyup；
6. 任一步失败（切不回游戏、目标窗口属于本程序、SendInput 被 UIPI 拦截）都退回复制到剪贴板并提示。

文本规整（`InputSimulator.Sanitize`）：`\r` / `\n` / `\t` 折叠为单个空格，丢弃其余控制字符并 Trim；
程序不自动按 Enter，避免误发聊天消息。

互操作注意：`INPUT` 结构体在 x64 上必须是 40 字节，`INPUTUNION` 因此保留最大的 `MOUSEINPUT`
（`KEYBDINPUT` 只有 24 字节，若只放它会触发 `ERROR_INVALID_PARAMETER`）。自检里有结构体尺寸断言。

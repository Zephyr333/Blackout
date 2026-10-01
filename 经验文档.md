# Blackout 核心开发经验与技术复盘文档

> **项目名称**：Blackout  
> **适用版本**：v1.2.5 及后续版本  
> **技术栈**：C# 8.0 / .NET 8.0-windows / WinForms / Win32 P/Invoke / DDC/CI (dxva2.dll) / Task Scheduler 1.2 XML

---

## 一、 项目背景与演进路径

Blackout 是一个为多显示器办公环境设计的极速黑屏工具。其核心诉求是：在多屏工作（如写代码、查文档、看视频）时，能通过极低的操作成本对单块或全部屏幕进行**纯黑遮罩覆盖**与**硬件级调光至 0 亮度**，并且在副屏黑屏办公时不夺取主屏打字焦点、不误杀常用快捷键（如 Esc）、原地单击 0 延迟一键开/关。

### 版本演进核心里程碑
1. **v1.0 ~ v1.1**：基础 WinForms 全屏窗口，通过 WMI 调整主屏亮度，依赖普通注册表自启动。存在副屏 DDC/CI 调光缺失、多 DPI 坐标偏移、易被任务管理器等置顶窗口穿透等问题。
2. **v1.2.0 ~ v1.2.1**：引入原生 Win32 弹出菜单（`CreatePopupMenu`），解决 WinForms 菜单左偏与样式粗糙问题；引入任务计划程序 XML 级管理员免 UAC 自启，解决电池供电断流与 72 小时超时问题。
3. **v1.2.2 ~ v1.2.4**：实现物理 `HMONITOR` 多屏隔离、DDC/CI 异步调光退出、副屏 `SW_SHOWNOACTIVATE` 呈现。尝试引入托盘拖拽，但在 Windows 11 下遭遇 OLE 拖拽吞噬与透明穿透。
4. **v1.2.5（当前版本）**：深度逆向并完整移植番茄钟（`pomodoro-timer`）`tray_drag.h` 底层状态机，重构独立常驻 STA 输入线程与低级钩子派发管道，攻克 Windows 11 XAML 任务栏对 `Shell_NotifyIconGetRect` 返回 `0x80004005` 的拖拽死穴，彻底实现**托盘拖拽任意屏幕瞬时单屏黑屏**与**原地 0 延迟单击**的完美并存。

---

## 二、 托盘拖拽引擎（TrayDragEngine）架构设计

### 1. 为什么不能在 UI 主线程安装钩子？
在传统 WinForms 应用中，如果在主窗体线程直接调用 `SetWindowsHookEx(WH_MOUSE_LL)`，会导致严重的问题：
- **卡顿与钩子静默脱钩**：当 UI 主线程进行密集绘制、GC 或执行 DDC/CI I2C 硬件通信时，若未在 Windows 规定的毫秒级超时内（`LowLevelHooksTimeout`）返回，操作系统会自动把该钩子从调用链中永久剔除（Silent Unhooking）。
- **与 WinForms 消息循环死锁**：鼠标拖拽过程中如果触发 WinForms 控件重绘或同步调用，极易发生消息重入死锁。

**解决方案**：采用与番茄钟完全对齐的**独立常驻 STA 输入线程**。

```
[独立 STA 输入线程 (Blackout_TrayDragInputThread)]
  ├─ SetWindowsHookEx(WH_MOUSE_LL)
  ├─ SetWindowsHookEx(WH_KEYBOARD_LL)
  ├─ PeekMessage / GetMessage 专用轻量消息循环
  └─ 捕获物理输入后，仅通过 PostMessage 投递 WM_TRAY_DRAG_INPUT
         │
         ▼
[UI 主线程 (TrayMessageWindow)]
  ├─ WndProc 监听 WM_TRAY_DRAG_INPUT
  ├─ 维护手势状态机 (OnInput -> Confirm -> Finish)
  └─ 触发业务逻辑 (OnTrayDroppedOnScreen)
```

### 2. 手势状态机与事件流转
引擎维护一个自增原子序列号 `_serial` 与 `TrayGesture` 状态：
- **物理按下（`WM_LBUTTONDOWN`）**：
  - 递增 `_serial`，记录起始原点 `Origin`、物理时间戳 `Started` 与当前鼠标所指窗口句柄 `Source = WindowFromPoint(pt)`。
  - 向主消息窗口投递 `WM_TRAY_DRAG_INPUT(serial, WM_LBUTTONDOWN)`。
  - 启动 100ms 看门狗定时器，防止鼠标在非活跃窗口释放时丢失 UP 消息。
- **物理移动（`WM_MOUSEMOVE`）**：
  - 计算位移差量：`dx = |X - Origin.X|`，`dy = |Y - Origin.Y|`。
  - 当位移超出系统拖拽阈值（`SM_CXDRAG` / `SM_CYDRAG`，通常为 4px）时，置位 `_gesture.Moved = true`。
- **物理抬起（`WM_LBUTTONUP`）**：
  - 置位 `_gesture.Pressed = false`，记录释放时间戳，投递 `WM_TRAY_DRAG_INPUT(serial, WM_LBUTTONUP)`，销毁看门狗定时器。
- **手势撤销（`VK_ESCAPE`）**：
  - 键盘钩子检测到 Esc 键，置位 `_gesture.Cancelled = true`，安全放弃本次拖放。

---

## 三、 Windows 11 独有特性与关键避坑记录

### 坑 1：Windows 11 XAML 任务栏对 `Shell_NotifyIconGetRect` 报 `0x80004005` (E_FAIL)
#### 现象与排查
在 Windows 10 及更早系统中，只要图标加入托盘，调用 `Shell_NotifyIconGetRect` 均能稳定获取屏幕矩形。但在 Windows 11 的新版 XAML 任务栏中：
- 当托盘图标未被用户显式拖动固定在主任务栏，而是处于折叠溢出浮窗（`^` 隐藏图标列表）内且浮窗关闭时，Shell API 会直接返回 `0x80004005 (E_FAIL)`，矩形返回 `[0, 0, 0, 0]`。
- 如果代码只依赖 `Shell_NotifyIconGetRect` 来判定“用户是否点击在当前图标上”，会导致拖拽状态机永远无法被确认（Confirmed），拖拽到屏幕完全无反应。

#### 解决方案：三重手势授权机制
1. **第一重：API 精确命中**  
   若 `Shell_NotifyIconGetRect` 返回成功，且起始原点落在图标矩形范围内（附带 2px 容差），立即 `Confirm`。
2. **第二重：WinForms `MouseDown` 事件候选兜底**  
   WinForms 的 `NotifyIconNativeWindow` 在接收到系统投递给该图标的底层托盘消息时，会触发 `notifyIcon.MouseDown` 事件。在此事件中立即标记 `_iconCandidate = true` 并执行 `ConfirmCurrentGesture()`。即使 Shell API 报错，也能确信用户按下的就是本程序图标。
3. **第三重：`ConfirmAndFinishIfPending` 延迟回调授权**  
   在鼠标抬起 `NotifyIcon_MouseUp` 时，如果状态机中已有物理移动位移但尚未 Confirmed，执行最后补确认与结算，消除 Windows 消息到达先后顺序的时序竞态。

---

### 坑 2：WinForms `NotifyIcon` 在 .NET 8 下的私有字段变更
#### 现象与排查
为了构造 `NOTIFYICONIDENTIFIER` 传递给 `Shell_NotifyIconGetRect`，必须获取 `NotifyIcon` 内部注册时使用的 `uID` 与所属窗口句柄 `hWnd`。
在早期 .NET Framework 源码中，字段名为：
```csharp
private int id;
private NotifyIconNativeWindow window;
```
但在 .NET 8（CoreCLR）中，WinForms 仓库重构为：
```csharp
private uint _id;
private NotifyIconNativeWindow _window;
```
直接硬编码反射 `"_id"` 或 `"id"`，会导致在不同运行库版本下一方返回 `null`，导致句柄获取失败、API 返回 0。

#### 解决方案：双重回退反射与类型无关解包
```csharp
var flags = BindingFlags.NonPublic | BindingFlags.Instance;
var type = typeof(NotifyIcon);
var idField = type.GetField("_id", flags) ?? type.GetField("id", flags);
var winField = type.GetField("_window", flags) ?? type.GetField("window", flags);

uint id = Convert.ToUInt32(idField.GetValue(notifyIcon));
object winVal = winField.GetValue(notifyIcon);
IntPtr hwnd = (winVal is NativeWindow nw) ? nw.Handle : (IntPtr)winVal.GetType().GetProperty("Handle")?.GetValue(winVal);
```

---

### 坑 3：C# Lambda 闭包中传 `ref` 参数导致 `CS1628` 编译错误
#### 现象
在扫描排除区域 `GetTrayRegions` 时，起初将 `TrayRegions` 声明为结构体（`struct`），并在 `EnumWindows` 的匿名回调 Lambda 中传递：
```csharp
EnumWindows((hWnd, lParam) => {
    AddRegion(ref regions, rect); // 报错 CS1628
    return true;
}, IntPtr.Zero);
```
C# 编译器规定：**不能在匿名方法、Lambda 表达式、查询表达式或本地函数中使用 `ref`、`out` 或 `in` 参数**。因为委托捕获变量需要生成闭包类，而结构体的栈引用不能被安全提升到堆上。

#### 解决方案
将 `TrayRegions` 从结构体提升为密封类（`sealed class`），方法形参直接传对象引用而非 `ref` 关键字：
```csharp
private sealed class TrayRegions
{
    public Rect[] Rects;
    public int Count;
    public bool Overflow;
}
private static void AddRegion(TrayRegions regions, Rect r) { ... }
```

---

### 坑 4：任务栏与折叠浮窗动态排除地图（TrayRegions）
#### 核心需求
用户将托盘图标从任务栏拖到屏幕松手，触发该屏幕黑屏；但是：
- 如果用户只是在任务栏内部调整图标顺序并松手，绝不能触发黑屏。
- 如果用户把图标拖到折叠浮窗（`TopLevelWindowForOverflowXamlIsland`）内部松手，绝不能触发黑屏。
- 如果用户有多块屏幕，副屏运行着 DisplayFusion 任务栏，副屏任务栏内松手也不能触发黑屏。

#### 解决方案
在手势确认时（`Confirm`）与手势结束时（`Finish`），分别快照当前系统托盘及任务栏的排布区域：
1. 查找所有顶级窗口，匹配类名：
   - Windows 原生主任务栏：`Shell_TrayWnd`
   - Windows 原生副任务栏：`Shell_SecondaryTrayWnd`
   - 任务栏折叠溢出浮窗：`NotifyIconOverflowWindow`、`TopLevelWindowForOverflowXamlIsland`
   - DisplayFusion 多屏任务栏：`DFTaskbar`（校验宿主进程为 `DisplayFusion.exe`）
2. 递归遍历子窗口获取 `TrayNotifyWnd` 或 `DFTaskbarItem:TrayIcon:` 等通知区子矩形，裁剪限制在所属任务栏之内。
3. 判定释放点坐标：
   ```csharp
   if (InTrayRegions(current, pt) || InTrayRegions(_initialRegions, pt)) {
       return; // 落在任务栏或托盘区域内，静默放弃，不触发黑屏
   }
   ```

---

### 坑 5：混合 DPI 多屏环境下的坐标漂移（`Screen` vs `MonitorFromPoint`）
#### 现象
在“主屏 4K 150% DPI + 副屏 1080P 100% DPI”的环境中，低级鼠标钩子捕获的 `pt` 是系统全局物理坐标。若使用 WinForms 原生 `Screen.FromPoint(pt)`，内部换算可能因为进程上下文与逻辑虚拟坐标的缩放差异导致定位到错误的屏幕或返回 `null`。

#### 解决方案
直接调用 Win32 原生物理 API 获取显示器句柄，绕过 WinForms 抽象层：
```csharp
PointStruct ptStruct = new PointStruct { X = dropPt.X, Y = dropPt.Y };
IntPtr hMon = MonitorFromPoint(ptStruct, MONITOR_DEFAULTTONEAREST);
// 结合 EnumDisplayMonitors 获取的物理 MonitorInfoEx.rcMonitor 判定匹配
```

---

## 四、 遮罩渲染与多屏交互细节

### 1. 纯黑实体遮罩（彻底杜绝透明穿透）
- **禁止使用透明样式**：WinForms 中设置 `Opacity` 或 `ControlStyles.Opaque` 时，系统可能会触发分层窗口合成（`WS_EX_LAYERED`），在多屏或高负载重绘时可能导致首帧背景擦除穿透，露出底层桌面。
- **显式覆写绘制**：
  ```csharp
  protected override void OnPaint(PaintEventArgs e)
  {
      e.Graphics.Clear(Color.Black);
  }
  ```
  配合 `BackColor = Color.Black` 与 `FormBorderStyle = FormBorderStyle.None`，保证从第一帧起 100% 呈现物理全黑。

### 2. 遮罩表面按键与 Esc 智能作用域
- **左键单击退出全部黑屏**：遮罩层捕获 `MouseDown`，若 `Button == MouseButtons.Left`，直接执行 `CloseAllWindows()`。
- **右键完全静默**：忽略右键点击，不执行任何操作，不破坏黑屏状态。
- **Esc 键防误杀逻辑**：
  低级键盘钩子拦截到 `VK_ESCAPE` 时，执行作用域判定：
  ```csharp
  if (_isAllScreensBlackout || IsFocusOrCursorOnBlackoutMonitor())
  {
      CloseAllWindows();
      return (IntPtr)1; // 仅在黑屏屏幕上吞噬 Esc
  }
  return CallNextHookEx(...); // 在未黑屏的工作屏幕打字时正常放行 Esc
  ```

### 3. 单屏不抢焦点与前台窗口恢复
- 单屏黑屏调用 `SetWindowPos(..., SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_NOSENDCHANGING)`，黑屏副屏展示时不抢走主屏正活动的前台窗口（如 Word、代码编辑器）的打字焦点。
- 在进入黑屏时通过 `GetForegroundWindow()` 暂存用户原有的工作窗口，退出黑屏时通过 `SetForegroundWindow()` 自动归还焦点，体验完全无感。

---

## 五、 硬件调光（DDC/CI）与多进程生命周期

### 1. 物理显示器句柄安全释放
调用 `dxva2.dll` 的 `GetPhysicalMonitorsFromHMONITOR` 会为显示器创建内核句柄。若未调用 `DestroyPhysicalMonitor`，每次黑屏/退出都会泄漏系统 GDI 与设备句柄，累积几百次后会导致显卡驱动无响应。
**铁律**：必须在 `try...finally` 中显式释放：
```csharp
foreach (PhysicalMonitor physicalMonitor in physicalMonitors)
{
    try {
        SetMonitorBrightness(physicalMonitor.hPhysicalMonitor, brightness);
    } finally {
        DestroyPhysicalMonitor(physicalMonitor.hPhysicalMonitor);
    }
}
```

### 2. 调光恢复异步化
DDC/CI 通信走的是显示器连接线中的 I2C 总线，单次通信延迟在 20ms~100ms 不等。如果在关闭遮罩窗口的主线程同步执行调光恢复，用户点击鼠标后会感觉到几十毫秒的明显停顿。
**实现方式**：主线程立即调用 `Close()` 销毁遮罩并恢复光标（0ms 响应），将 `SetBrightness` 与 DDC/CI 恢复操作投递到后台异步任务（`Task.Run`）中并发执行。

### 3. XML 级自启配置优势
相比普通的注册表 `Run` 项，Blackout 采用 Task Scheduler 1.2 XML 配置计划任务具有决定性优势：
- 具备 `RunLevel: HighestAvailable`，自动以管理员权限启动，能够压制任务管理器等置顶特权窗口。
- 显式关闭 `DisallowStartIfOnBatteries` 和 `StopIfGoingOnBatteries`，笔记本在拔掉电源使用电池时依然能自启和常驻。
- 显式关闭 `ExecutionTimeLimit`，解决 Windows 默认计划任务运行超过 72 小时被系统强制中断杀掉的问题。

---

## 六、 调试与排错方法论总结

1. **先查进程落地，再查界面表现**：
   排查“改动没效果”时，首先检查目标目录的可执行文件修改时间戳与任务计划程序运行 PID，确认代码是否真正编译成功并热替换生效。
2. **Win32 API 返回值严密断言**：
   对于 `Shell_NotifyIconGetRect`、`SetWindowPos`、`SetWindowsHookEx` 等 Windows API，绝不能假设其必然成功；必须针对错误码（如 `0x80004005`）设计完整的回退路径。
3. **输入事件与 UI 消息解耦**：
   涉及底层系统钩子时，切忌在钩子回调中执行复杂计算或 UI 同步阻塞操作；始终遵循“钩子轻量记录状态 -> PostMessage 投递队列 -> 主窗口状态机异步结算”的分离设计。

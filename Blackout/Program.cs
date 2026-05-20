using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Blackout
{
    static class Program
    {
        private class BlackoutOverlayForm : Form
        {
            protected override CreateParams CreateParams
            {
                get
                {
                    CreateParams cp = base.CreateParams;
                    cp.ExStyle |= WS_EX_TOOLWINDOW;
                    return cp;
                }
            }
        }

        // 通知图标，用于在系统托盘中显示应用程序图标
        static NotifyIcon notifyIcon;

        private static Mutex singleInstanceMutex;

        // 存储当前所有黑屏窗口
        static List<Form> windows = new List<Form>();

        // 存储主显示器的原始亮度值
        static List<int> originalBrightness = new List<int>();

        // 存储所有监视器的句柄
        static List<IntPtr> monitorHandles = new List<IntPtr>();

        // 存储副显示器的原始亮度值
        static List<int> otherMonitorBrightness = new List<int>();

        // 键盘钩子的回调函数
        private static LowLevelKeyboardProc _proc = HookCallback;

        // 钩子的句柄
        private static IntPtr _hookID = IntPtr.Zero;

        // 常量定义，用于设置键盘钩子
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;

        // 常量定义，用于保持窗口置顶
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint TOPMOST_FLAGS = SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE;

        // 黑屏期间用于持续保持最前层级的守护器
        private static System.Windows.Forms.Timer topmostGuardTimer;
        private static IntPtr _foregroundEventHook = IntPtr.Zero;
        private static IntPtr _showEventHook = IntPtr.Zero;
        private static WinEventDelegate _foregroundEventProc;
        private static WinEventDelegate _showEventProc;
        private static uint _currentProcessId;
        private static readonly HashSet<IntPtr> hiddenWindows = new HashSet<IntPtr>();
        private static readonly List<Rectangle> blackoutBounds = new List<Rectangle>();
        private static int _maintainScheduled;
        private static long _lastSuppressTickMs;
        private const int SuppressIntervalMs = 300;

        // 导入用户32.dll中的相关函数，用于窗口操作
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        // 导入用户32.dll中的函数，用于枚举显示器
        [DllImport("user32.dll", SetLastError = true)]
        static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumDelegate lpfnEnum, IntPtr dwData);

        // 导入用户32.dll中的函数，用于获取显示器信息
        [DllImport("user32.dll", SetLastError = true)]
        static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfoEx lpmi);

        // 导入dxva2.dll中的函数，用于获取和设置显示器亮度
        [DllImport("dxva2.dll", SetLastError = true)]
        static extern bool GetMonitorBrightness(IntPtr hMonitor, out int pdwMinimumBrightness, out int pdwCurrentBrightness, out int pdwMaximumBrightness);

        [DllImport("dxva2.dll", SetLastError = true)]
        static extern bool SetMonitorBrightness(IntPtr hMonitor, int dwNewBrightness);

        [DllImport("dxva2.dll", SetLastError = true)]
        static extern bool DestroyPhysicalMonitor(IntPtr hMonitor);

        [DllImport("dxva2.dll", SetLastError = true)]
        static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint pdwNumberOfPhysicalMonitors);

        [DllImport("dxva2.dll", SetLastError = true)]
        static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint dwPhysicalMonitorArraySize, [Out] PhysicalMonitor[] pPhysicalMonitorArray);

        // 导入用户32.dll中的函数，用于设置和解除键盘钩子
        [DllImport("user32.dll")]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll")]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        // 导入kernel32.dll中的函数，用于获取模块句柄
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        // 导入用户32.dll中的函数，用于修改窗口样式
        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out Rect lpRect);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

        [DllImport("user32.dll")]
        private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

        private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
        private const uint EVENT_OBJECT_SHOW = 0x8002;
        private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
        private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
        private const int SW_HIDE = 0;
        private const int SW_SHOW = 5;


        // 常量定义，用于设置窗口扩展样式
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOPMOST = 0x00000008;
        private const int WS_EX_TOOLWINDOW = 0x00000080;

        // 键盘钩子的回调委托
        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        // 显示器枚举的回调委托
        private delegate bool MonitorEnumDelegate(IntPtr hMonitor, IntPtr hdcMonitor, ref Rect lprcMonitor, IntPtr dwData);

        // 定义Rect结构，用于存储显示区域
        [StructLayout(LayoutKind.Sequential)]
        private struct Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        // 定义MonitorInfoEx结构，用于获取显示器的详细信息
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MonitorInfoEx
        {
            public int Size;
            public Rect Monitor;
            public Rect WorkArea;
            public uint Flags;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string DeviceName;
        }

        // 定义PhysicalMonitor结构，用于物理监视器信息
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct PhysicalMonitor
        {
            public IntPtr hPhysicalMonitor;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szPhysicalMonitorDescription;
        }

        // 同步上下文，用于在线程之间同步操作
        private static SynchronizationContext syncContext;

        // 菜单项，用于控制主屏和副屏的黑屏
        static ToolStripMenuItem blackoutMainScreenMenuItem;
        static ToolStripMenuItem blackoutOtherScreensMenuItem;
        static ToolStripMenuItem autoStartMenuItem;

        private const string AutoStartTaskName = "Blackout_AutoStart";
        private const string AutoStartValueName = "Blackout";
        private const string RunRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string StartupApprovedRunRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

        [STAThread]
        static void Main()
        {
            singleInstanceMutex = new Mutex(true, @"Local\Blackout_SingleInstance", out bool createdNew);
            if (!createdNew)
            {
                return;
            }

            // 启用视觉样式和文本渲染兼容性
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // 获取当前的同步上下文，如果为空，则使用Windows Forms的同步上下文
            syncContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

            // 初始化通知图标
            notifyIcon = new NotifyIcon();
            notifyIcon.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); // 使用应用程序的图标
            notifyIcon.Visible = true;
            notifyIcon.Text = "Blackout"; // 鼠标悬停时显示的文本

            // 创建上下文菜单
            ContextMenuStrip contextMenu = new ContextMenuStrip();

            // 创建各个菜单项，并绑定点击事件
            ToolStripMenuItem blackoutMenuItem = new ToolStripMenuItem("一键黑屏", null, BlackoutMenuItem_Click);
            blackoutMainScreenMenuItem = new ToolStripMenuItem("主屏黑屏", null, BlackoutMainScreenMenuItem_Click);
            blackoutOtherScreensMenuItem = new ToolStripMenuItem("副屏黑屏", null, BlackoutOtherScreensMenuItem_Click);
            autoStartMenuItem = new ToolStripMenuItem("开机自启", null, AutoStartMenuItem_Click);
            ToolStripMenuItem restartMenuItem = new ToolStripMenuItem("重启软件", null, RestartMenuItem_Click);
            ToolStripMenuItem exitMenuItem = new ToolStripMenuItem("退出软件", null, ExitMenuItem_Click);

            // 将菜单项添加到上下文菜单中
            contextMenu.Items.AddRange(new ToolStripItem[]
            {
                blackoutMenuItem,
                blackoutMainScreenMenuItem,
                blackoutOtherScreensMenuItem,
                autoStartMenuItem,
                restartMenuItem,
                exitMenuItem
            });

            RemoveLegacyRegistryAutoStart();
            autoStartMenuItem.Checked = IsAutoStartEnabled();

            Application.ApplicationExit += (sender, e) =>
            {
                notifyIcon?.Dispose();
                singleInstanceMutex?.ReleaseMutex();
                singleInstanceMutex?.Dispose();
            };

            // 绑定上下文菜单的Opening事件，用于在菜单打开前进行检查
            contextMenu.Opening += ContextMenu_Opening;

            // 将上下文菜单关联到通知图标
            notifyIcon.ContextMenuStrip = contextMenu;

            // 绑定通知图标的鼠标点击事件
            notifyIcon.MouseClick += NotifyIcon_MouseClick;

            // 启动应用程序的消息循环
            Application.Run();
        }

        /// <summary>
        /// 上下文菜单打开前的事件处理程序
        /// 用于检查是否有黑屏窗口正在显示，如果有，则关闭所有窗口并取消菜单的打开
        /// </summary>
        private static void ContextMenu_Opening(object sender, System.ComponentModel.CancelEventArgs e)
        {
            // 检查是否有黑屏窗口正在显示
            if (windows.Count > 0)
            {
                CloseAllWindows(); // 关闭所有黑屏窗口
                e.Cancel = true; // 取消菜单的打开
                return;
            }

            autoStartMenuItem.Checked = IsAutoStartEnabled();
        }

        private static void AutoStartMenuItem_Click(object sender, EventArgs e)
        {
            bool enable = !IsAutoStartEnabled();

            if (SetAutoStartEnabled(enable, out string errorMessage))
            {
                autoStartMenuItem.Checked = enable;
                return;
            }

            MessageBox.Show(errorMessage, "Blackout", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            autoStartMenuItem.Checked = IsAutoStartEnabled();
        }

        private static bool IsAutoStartEnabled()
        {
            return RunSchtasksCommand($"/Query /TN \"{AutoStartTaskName}\"", out _, out _);
        }

        private static bool SetAutoStartEnabled(bool enable, out string errorMessage)
        {
            string exePath = Application.ExecutablePath;
            if (!File.Exists(exePath))
            {
                errorMessage = "未找到程序可执行文件，无法设置开机自启。";
                return false;
            }

            if (enable)
            {
                RemoveLegacyRegistryAutoStart();
                if (CreateOrUpdateAutoStartTask(exePath, out errorMessage))
                {
                    return true;
                }

                errorMessage = "开启开机自启失败。" + Environment.NewLine + errorMessage;
                return false;
            }

            RemoveLegacyRegistryAutoStart();
            if (RunSchtasksCommand($"/Delete /F /TN \"{AutoStartTaskName}\"", out _, out errorMessage))
            {
                return true;
            }

            if (!IsAutoStartEnabled())
            {
                errorMessage = string.Empty;
                return true;
            }

            errorMessage = "关闭开机自启失败。" + Environment.NewLine + errorMessage;
            return false;
        }

        private static void RemoveLegacyRegistryAutoStart()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunRegistryPath, true))
                {
                    key?.DeleteValue(AutoStartValueName, false);
                }

                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(StartupApprovedRunRegistryPath, true))
                {
                    key?.DeleteValue(AutoStartValueName, false);
                }
            }
            catch
            {
            }
        }

        private static bool CreateOrUpdateAutoStartTask(string exePath, out string errorMessage)
        {
            string args = $"/Create /F /TN \"{AutoStartTaskName}\" /SC ONLOGON /RL HIGHEST /TR \"\\\"{exePath}\\\"\"";
            if (RunSchtasksCommand(args, out _, out errorMessage))
            {
                return true;
            }

            errorMessage = string.IsNullOrWhiteSpace(errorMessage)
                ? "创建最高权限计划任务失败。请确认当前 Blackout 是以管理员身份运行。"
                : errorMessage;
            return false;
        }

        private static bool RunSchtasksCommand(string arguments, out string outputText, out string errorText)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using (Process proc = Process.Start(psi))
                {
                    outputText = proc.StandardOutput.ReadToEnd();
                    errorText = proc.StandardError.ReadToEnd();
                    proc.WaitForExit();
                    return proc.ExitCode == 0;
                }
            }
            catch (Exception ex)
            {
                outputText = string.Empty;
                errorText = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// 通知图标的鼠标点击事件处理程序
        /// 用于处理左键点击以切换黑屏模式
        /// </summary>
        private static void NotifyIcon_MouseClick(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) // 仅处理左键点击
            {
                if (windows.Count > 0)
                {
                    CloseAllWindows(); // 如果已处于黑屏状态，关闭所有窗口
                }
                else
                {
                    EnterBlackoutMode(); // 进入黑屏模式
                }
            }
        }

        /// <summary>
        /// 一键黑屏菜单项的点击事件处理程序
        /// 进入全面黑屏模式
        /// </summary>
        private static void BlackoutMenuItem_Click(object sender, EventArgs e)
        {
            EnterBlackoutMode();
        }

        /// <summary>
        /// 主屏黑屏菜单项的点击事件处理程序
        /// 仅黑屏主显示器
        /// </summary>
        private static void BlackoutMainScreenMenuItem_Click(object sender, EventArgs e)
        {
            if (windows.Count > 0)
            {
                CloseAllWindows(); // 如果已处于黑屏状态，先关闭所有窗口
            }
            EnterBlackoutMode(mainScreenOnly: true); // 仅黑屏主显示器
        }

        /// <summary>
        /// 副屏黑屏菜单项的点击事件处理程序
        /// 仅黑屏副显示器
        /// </summary>
        private static void BlackoutOtherScreensMenuItem_Click(object sender, EventArgs e)
        {
            if (windows.Count > 0)
            {
                CloseAllWindows(); // 如果已处于黑屏状态，先关闭所有窗口
            }
            EnterBlackoutMode(otherScreensOnly: true); // 仅黑屏副显示器
        }

        /// <summary>
        /// 重启软件菜单项的点击事件处理程序
        /// 重新启动应用程序
        /// </summary>
        private static void RestartMenuItem_Click(object sender, EventArgs e)
        {
            Application.Restart(); // 重启应用程序
        }

        /// <summary>
        /// 退出软件菜单项的点击事件处理程序
        /// 退出应用程序并隐藏通知图标
        /// </summary>
        private static void ExitMenuItem_Click(object sender, EventArgs e)
        {
            notifyIcon.Visible = false; // 隐藏通知图标
            Application.Exit(); // 退出应用程序
        }

        /// <summary>
        /// 进入黑屏模式的方法
        /// 根据参数决定是全面黑屏、仅主屏黑屏还是仅副屏黑屏
        /// </summary>
        /// <param name="mainScreenOnly">是否仅黑屏主显示器</param>
        /// <param name="otherScreensOnly">是否仅黑屏副显示器</param>
        private static void EnterBlackoutMode(bool mainScreenOnly = false, bool otherScreensOnly = false)
        {
            // 清空现有数据
            windows.Clear();
            originalBrightness.Clear();
            otherMonitorBrightness.Clear();
            monitorHandles.Clear();

            // 获取当前主显示器的亮度并保存
            originalBrightness.Add(GetBrightness());

            // 枚举所有显示器并保存句柄
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, MonitorEnum, IntPtr.Zero);

            // 获取所有副显示器的亮度并保存
            foreach (var monitor in monitorHandles)
            {
                otherMonitorBrightness.Add(GetBrightness(monitor));
            }

            // 根据传入的参数设置亮度
            if (mainScreenOnly)
            {
                RestoreMainMonitorBrightness(0); // 仅设置主显示器亮度为0（黑屏）
            }
            else if (otherScreensOnly)
            {
                SetBrightnessForMonitors(0, excludePrimary: true); // 仅设置副显示器亮度为0
            }
            else
            {
                SetBrightness(0); // 全面设置所有显示器亮度为0
            }

            Cursor.Hide(); // 隐藏鼠标光标

            _hookID = SetHook(_proc); // 设置键盘钩子，监控键盘事件

            // 根据黑屏模式创建对应的黑屏窗口
            foreach (Screen screen in Screen.AllScreens)
            {
                // 判断是否需要创建当前屏幕的黑屏窗口
                if ((!mainScreenOnly && !otherScreensOnly) ||
                    (mainScreenOnly && screen.Primary) ||
                    (otherScreensOnly && !screen.Primary))
                {
                    Form form = CreateBlackoutForm(screen); // 创建黑屏窗口
                    windows.Add(form); // 添加到窗口列表
                }
            }

            // 显示所有黑屏窗口
            foreach (var window in windows)
            {
                window.Show();
            }

            blackoutBounds.Clear();
            foreach (Screen screen in Screen.AllScreens)
            {
                if ((!mainScreenOnly && !otherScreensOnly) ||
                    (mainScreenOnly && screen.Primary) ||
                    (otherScreensOnly && !screen.Primary))
                {
                    blackoutBounds.Add(screen.Bounds);
                }
            }

            _currentProcessId = (uint)Process.GetCurrentProcess().Id;

            StartTopmostGuard();
            MaintainBlackoutLayer();
        }

        private static void StartTopmostGuard()
        {
            StopTopmostGuard();

            if (topmostGuardTimer == null)
            {
                topmostGuardTimer = new System.Windows.Forms.Timer();
                topmostGuardTimer.Interval = 250;
                topmostGuardTimer.Tick += (sender, e) => MaintainBlackoutLayer();
            }

            _maintainScheduled = 0;
            _lastSuppressTickMs = 0;

            _foregroundEventProc = WinEventCallback;
            _showEventProc = WinEventCallback;

            _foregroundEventHook = SetWinEventHook(
                EVENT_SYSTEM_FOREGROUND,
                EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero,
                _foregroundEventProc,
                0,
                0,
                WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

            _showEventHook = SetWinEventHook(
                EVENT_OBJECT_SHOW,
                EVENT_OBJECT_SHOW,
                IntPtr.Zero,
                _showEventProc,
                0,
                0,
                WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

            topmostGuardTimer.Start();
        }

        private static void StopTopmostGuard()
        {
            if (topmostGuardTimer != null)
            {
                topmostGuardTimer.Stop();
            }

            if (_foregroundEventHook != IntPtr.Zero)
            {
                UnhookWinEvent(_foregroundEventHook);
                _foregroundEventHook = IntPtr.Zero;
            }

            if (_showEventHook != IntPtr.Zero)
            {
                UnhookWinEvent(_showEventHook);
                _showEventHook = IntPtr.Zero;
            }
        }

        private static void WinEventCallback(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            if (syncContext == null)
            {
                return;
            }

            if (Interlocked.Exchange(ref _maintainScheduled, 1) == 1)
            {
                return;
            }

            syncContext.Post(_ =>
            {
                try
                {
                    MaintainBlackoutLayer(forceSuppress: true);
                }
                finally
                {
                    Interlocked.Exchange(ref _maintainScheduled, 0);
                }
            }, null);
        }

        private static void MaintainBlackoutLayer(bool forceSuppress = false)
        {
            EnforceBlackoutWindowsTopmost();

            long nowMs = Environment.TickCount64;
            if (forceSuppress || nowMs - _lastSuppressTickMs >= SuppressIntervalMs)
            {
                SuppressCompetingTopmostWindows();
                _lastSuppressTickMs = nowMs;
            }
        }

        private static void EnforceBlackoutWindowsTopmost()
        {
            if (windows.Count == 0)
            {
                return;
            }

            foreach (Form window in windows)
            {
                if (window == null || window.IsDisposed || !window.IsHandleCreated)
                {
                    continue;
                }

                SetWindowPos(window.Handle, HWND_TOPMOST, 0, 0, 0, 0, TOPMOST_FLAGS);
            }
        }

        private static void SuppressCompetingTopmostWindows()
        {
            if (windows.Count == 0 || blackoutBounds.Count == 0)
            {
                return;
            }

            EnumWindows((hWnd, lParam) =>
            {
                if (hWnd == IntPtr.Zero || !IsWindowVisible(hWnd))
                {
                    return true;
                }

                int exStyle = GetWindowLong(hWnd, GWL_EXSTYLE);
                if ((exStyle & WS_EX_TOPMOST) == 0)
                {
                    return true;
                }

                GetWindowThreadProcessId(hWnd, out uint processId);
                if (processId == _currentProcessId)
                {
                    return true;
                }

                if (!GetWindowRect(hWnd, out Rect rect))
                {
                    return true;
                }

                Rectangle windowRect = new Rectangle(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
                if (windowRect.Width < 40 || windowRect.Height < 20)
                {
                    return true;
                }

                foreach (Rectangle bounds in blackoutBounds)
                {
                    if (windowRect.IntersectsWith(bounds))
                    {
                        ShowWindow(hWnd, SW_HIDE);
                        hiddenWindows.Add(hWnd);
                        break;
                    }
                }

                return true;
            }, IntPtr.Zero);
        }

        private static void RestoreHiddenWindows()
        {
            foreach (IntPtr hWnd in hiddenWindows)
            {
                ShowWindow(hWnd, SW_SHOW);
            }

            hiddenWindows.Clear();
        }

        /// <summary>
        /// 创建一个覆盖指定屏幕的黑屏窗口
        /// </summary>
        /// <param name="screen">目标屏幕</param>
        /// <returns>创建的黑屏窗口</returns>
        private static Form CreateBlackoutForm(Screen screen)
        {
            Form form = new BlackoutOverlayForm();
            form.FormBorderStyle = FormBorderStyle.None; // 无边框
            form.WindowState = FormWindowState.Maximized; // 最大化窗口
            form.BackColor = Color.Black; // 设置背景色为黑色
            form.TopMost = true; // 始终置顶
            form.ShowInTaskbar = false; // 不在任务栏中显示
            form.ShowIcon = false; // 不显示窗口图标
            form.Bounds = screen.Bounds; // 设置窗口大小为屏幕大小
            form.StartPosition = FormStartPosition.Manual; // 手动设置位置
            form.Location = screen.Bounds.Location; // 设置窗口位置
            form.Size = screen.Bounds.Size; // 设置窗口大小

            // 绑定键盘按下事件，按下Esc键时关闭黑屏
            form.KeyDown += (sender, e) =>
            {
                if (e.KeyCode == Keys.Escape)
                {
                    CloseAllWindows(); // 关闭所有黑屏窗口
                }
            };

            // 绑定鼠标点击事件，点击时关闭黑屏
            form.MouseDown += (sender, e) =>
            {
                CloseAllWindows(); // 关闭所有黑屏窗口
            };

            // 窗口加载时设置窗口样式
            form.Load += (sender, e) =>
            {
                // 将窗口置顶
                SetWindowPos(form.Handle, HWND_TOPMOST, 0, 0, 0, 0, TOPMOST_FLAGS);
            };

            return form;
        }

        /// <summary>
        /// 键盘钩子的回调函数
        /// 用于检测特定按键（如Esc键）以关闭黑屏
        /// </summary>
        private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            // 检查钩子代码和按键消息
            if (nCode >= 0 && wParam == (IntPtr)WM_KEYDOWN)
            {
                int vkCode = Marshal.ReadInt32(lParam); // 获取按键代码

                if (vkCode == (int)Keys.Escape) // 如果按下的是Esc键
                {
                    syncContext.Post(_ => CloseAllWindows(), null); // 关闭所有黑屏窗口
                    return (IntPtr)1; // 阻止该按键消息被进一步处理
                }
                else if (vkCode == (int)Keys.LWin || vkCode == (int)Keys.RWin) // 如果按下的是左或右Win键
                {
                    return (IntPtr)1; // 阻止Win键功能（如开始菜单）
                }
            }

            // 传递钩子消息给下一个钩子
            return CallNextHookEx(_hookID, nCode, wParam, lParam);
        }

        /// <summary>
        /// 设置键盘钩子的方法
        /// </summary>
        private static IntPtr SetHook(LowLevelKeyboardProc proc)
        {
            using (var curProcess = Process.GetCurrentProcess()) // 获取当前进程
            using (var curModule = curProcess.MainModule) // 获取当前模块
            {
                // 设置全局键盘钩子
                return SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(curModule.ModuleName), 0);
            }
        }

        /// <summary>
        /// 关闭所有黑屏窗口并恢复亮度
        /// </summary>
        private static void CloseAllWindows()
        {
            StopTopmostGuard();
            RestoreHiddenWindows();
            blackoutBounds.Clear();

            // 关闭所有黑屏窗口
            foreach (Form window in windows)
            {
                window.Close();
            }

            // 清空窗口列表
            windows.Clear();

            // 恢复所有显示器的亮度
            RestoreBrightness();

            // 显示鼠标光标
            Cursor.Show();

            // 解除键盘钩子
            UnhookWindowsHookEx(_hookID);
        }

        /// <summary>
        /// 设置所有相关显示器的亮度
        /// </summary>
        /// <param name="brightness">亮度值（0-100）</param>
        private static void SetBrightness(int brightness)
        {
            try
            {
                // 设置主显示器的亮度
                RestoreMainMonitorBrightness(brightness);

                // 设置所有副显示器的亮度
                foreach (var monitor in monitorHandles)
                {
                    SetMonitorBrightnessForPhysicalMonitors(monitor, brightness);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("设置亮度时发生错误: " + ex.Message);
            }
        }

        /// <summary>
        /// 为特定显示器设置亮度
        /// </summary>
        /// <param name="brightness">亮度值（0-100）</param>
        /// <param name="excludePrimary">是否排除主显示器</param>
        private static void SetBrightnessForMonitors(int brightness, bool excludePrimary = false)
        {
            foreach (var monitor in monitorHandles)
            {
                if (excludePrimary)
                {
                    MonitorInfoEx mi = GetMonitorInfoEx(monitor); // 获取显示器信息
                    if ((mi.Flags & 1) != 0) // 如果是主显示器
                        continue; // 跳过
                }
                SetMonitorBrightnessForPhysicalMonitors(monitor, brightness); // 设置亮度
            }
        }

        /// <summary>
        /// 恢复所有显示器的原始亮度
        /// </summary>
        private static void RestoreBrightness()
        {
            try
            {
                // 恢复主显示器的亮度
                if (originalBrightness.Count > 0)
                {
                    int mainBrightness = originalBrightness[0];
                    RestoreMainMonitorBrightness(mainBrightness);
                }

                // 恢复所有副显示器的亮度
                for (int i = 0; i < monitorHandles.Count; i++)
                {
                    int b = otherMonitorBrightness[i];
                    SetMonitorBrightnessForPhysicalMonitors(monitorHandles[i], b);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("恢复亮度时发生错误: " + ex.Message);
            }
        }

        /// <summary>
        /// 恢复主显示器的亮度
        /// </summary>
        /// <param name="brightness">亮度值（0-100）</param>
        private static void RestoreMainMonitorBrightness(int brightness)
        {
            try
            {
                // 连接到WMI
                ManagementScope scope = new ManagementScope("root\\WMI");
                SelectQuery query = new SelectQuery("WmiMonitorBrightnessMethods");

                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(scope, query))
                {
                    using (ManagementObjectCollection objectCollection = searcher.Get())
                    {
                        // 遍历所有管理对象并设置亮度
                        foreach (ManagementObject mObj in objectCollection)
                        {
                            mObj.InvokeMethod("WmiSetBrightness", new object[] { uint.MaxValue, brightness });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("恢复主显示器亮度时发生错误: " + ex.Message);
            }
        }

        /// <summary>
        /// 为物理显示器设置亮度
        /// </summary>
        /// <param name="monitor">物理显示器的句柄</param>
        /// <param name="brightness">亮度值（0-100）</param>
        private static void SetMonitorBrightnessForPhysicalMonitors(IntPtr monitor, int brightness)
        {
            uint numberOfPhysicalMonitors;
            // 获取物理监视器的数量
            GetNumberOfPhysicalMonitorsFromHMONITOR(monitor, out numberOfPhysicalMonitors);

            // 创建物理监视器数组
            PhysicalMonitor[] physicalMonitors = new PhysicalMonitor[numberOfPhysicalMonitors];
            GetPhysicalMonitorsFromHMONITOR(monitor, numberOfPhysicalMonitors, physicalMonitors);

            // 设置每个物理监视器的亮度并销毁监视器句柄
            foreach (var physicalMonitor in physicalMonitors)
            {
                SetMonitorBrightness(physicalMonitor.hPhysicalMonitor, brightness); // 设置亮度
                DestroyPhysicalMonitor(physicalMonitor.hPhysicalMonitor); // 销毁监视器句柄
            }
        }

        /// <summary>
        /// 获取主显示器的当前亮度
        /// </summary>
        /// <returns>亮度值（0-100）</returns>
        private static int GetBrightness()
        {
            int brightness = 0;
            try
            {
                // 连接到WMI
                ManagementScope scope = new ManagementScope("root\\WMI");
                SelectQuery query = new SelectQuery("WmiMonitorBrightness");

                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(scope, query))
                {
                    using (ManagementObjectCollection objectCollection = searcher.Get())
                    {
                        foreach (ManagementObject mObj in objectCollection)
                        {
                            brightness = (byte)mObj.GetPropertyValue("CurrentBrightness"); // 获取当前亮度
                            break; // 只获取第一个显示器的亮度
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("获取亮度时发生错误: " + ex.Message);
            }
            return brightness;
        }

        /// <summary>
        /// 获取指定显示器的当前亮度
        /// </summary>
        /// <param name="monitor">显示器的句柄</param>
        /// <returns>亮度值（0-100）</returns>
        private static int GetBrightness(IntPtr monitor)
        {
            int currentBrightness = 0;
            try
            {
                uint numberOfPhysicalMonitors;
                // 获取物理监视器的数量
                GetNumberOfPhysicalMonitorsFromHMONITOR(monitor, out numberOfPhysicalMonitors);

                // 创建物理监视器数组
                PhysicalMonitor[] physicalMonitors = new PhysicalMonitor[numberOfPhysicalMonitors];
                GetPhysicalMonitorsFromHMONITOR(monitor, numberOfPhysicalMonitors, physicalMonitors);

                // 获取每个物理监视器的亮度，并销毁监视器句柄
                foreach (var physicalMonitor in physicalMonitors)
                {
                    GetMonitorBrightness(physicalMonitor.hPhysicalMonitor, out int min, out currentBrightness, out int max);
                    DestroyPhysicalMonitor(physicalMonitor.hPhysicalMonitor);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("获取亮度时发生错误: " + ex.Message);
            }
            return currentBrightness;
        }

        /// <summary>
        /// 枚举所有显示器时的回调函数
        /// 仅将非主显示器添加到监视器句柄列表中
        /// </summary>
        private static bool MonitorEnum(IntPtr hMonitor, IntPtr hdcMonitor, ref Rect lprcMonitor, IntPtr dwData)
        {
            MonitorInfoEx mi = GetMonitorInfoEx(hMonitor); // 获取显示器信息

            // 如果是主显示器，则不添加到列表中（使用WMI控制主显示器亮度）
            if ((mi.Flags & 1) != 0)
            {
                return true; // 继续枚举
            }

            monitorHandles.Add(hMonitor); // 添加到监视器句柄列表
            return true; // 继续枚举
        }

        /// <summary>
        /// 获取指定显示器的详细信息
        /// </summary>
        /// <param name="hMonitor">显示器的句柄</param>
        /// <returns>显示器信息结构体</returns>
        private static MonitorInfoEx GetMonitorInfoEx(IntPtr hMonitor)
        {
            MonitorInfoEx mi = new MonitorInfoEx();
            mi.Size = Marshal.SizeOf(typeof(MonitorInfoEx)); // 设置结构体大小

            // 获取显示器信息，如果成功则返回信息结构体
            if (GetMonitorInfo(hMonitor, ref mi))
            {
                return mi;
            }

            return mi; // 如果失败，返回默认的结构体
        }
    }
}

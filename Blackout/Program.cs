using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Blackout
{
    static class Program
    {
        private struct MonitorTarget
        {
            public IntPtr Handle;
            public Rectangle Bounds;
            public bool IsPrimary;
        }

        private sealed class BlackoutOverlayForm : Form
        {
            public IntPtr MonitorHandle { get; }
            public Rectangle PhysicalBounds { get; }

            public BlackoutOverlayForm(IntPtr monitorHandle, Rectangle physicalBounds, Cursor blankCursor)
            {
                MonitorHandle = monitorHandle;
                PhysicalBounds = physicalBounds;

                FormBorderStyle = FormBorderStyle.None;
                WindowState = FormWindowState.Normal;
                BackColor = Color.Black;
                TopMost = true;
                ShowInTaskbar = false;
                ShowIcon = false;
                StartPosition = FormStartPosition.Manual;
                Bounds = physicalBounds;
                Location = physicalBounds.Location;
                Size = physicalBounds.Size;
                if (blankCursor != null)
                {
                    Cursor = blankCursor;
                }
            }

            protected override CreateParams CreateParams
            {
                get
                {
                    CreateParams cp = base.CreateParams;
                    cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_TOPMOST;
                    return cp;
                }
            }

            protected override bool ShowWithoutActivation => false;
        }

        private sealed class TrayMessageWindow : NativeWindow
        {
            public TrayMessageWindow()
            {
                CreateParams cp = new CreateParams
                {
                    Caption = "Blackout_TrayMessageWindow",
                    X = 0,
                    Y = 0,
                    Width = 0,
                    Height = 0,
                    Style = 0
                };
                CreateHandle(cp);
            }

            protected override void WndProc(ref Message m)
            {
                if (_wmTaskbarCreated != 0 && m.Msg == _wmTaskbarCreated)
                {
                    RebuildNotifyIcon();
                }
                else if (_wmBlackoutShowTray != 0 && m.Msg == _wmBlackoutShowTray)
                {
                    RebuildNotifyIcon();
                }
                base.WndProc(ref m);
            }
        }

        // 通知图标与托盘守护窗口
        private static NotifyIcon notifyIcon;
        private static ContextMenuStrip contextMenu;
        private static TrayMessageWindow trayMessageWindow;
        private static System.Windows.Forms.Timer trayHealthTimer;
        private static int trayStartupCheckCount;
        private static Cursor blankCursor;

        private static Mutex singleInstanceMutex;
        private static uint _wmTaskbarCreated;
        private static uint _wmBlackoutShowTray;

        // 存储当前所有黑屏窗口与已黑屏显示器句柄
        private static readonly List<BlackoutOverlayForm> windows = new List<BlackoutOverlayForm>();
        private static readonly HashSet<IntPtr> activeBlackoutMonitors = new HashSet<IntPtr>();
        private static readonly List<MonitorTarget> enumeratedMonitors = new List<MonitorTarget>();

        // 原始亮度缓存（仅在从正常亮屏首次进入黑屏时采集锁定，防止切换模式时将 0 亮度误存为原始亮度）
        private static bool _brightnessCaptured;
        private static int _savedMainBrightness = 80;
        private static readonly Dictionary<IntPtr, int> _savedMonitorBrightness = new Dictionary<IntPtr, int>();

        // 静态常驻 Win32 回调委托（严禁动态分配，防止被 .NET GC 回收引发 0xc0000005 原生崩溃）
        private static readonly LowLevelKeyboardProc _keyboardHookProc = HookCallback;
        private static readonly WinEventDelegate _winEventProc = WinEventCallback;
        private static readonly EnumWindowsProc _enumWindowsProc = SuppressEnumWindowsProc;
        private static readonly MonitorEnumDelegate _monitorEnumProc = MonitorEnumCallback;

        // 钩子句柄与状态锁
        private static IntPtr _hookID = IntPtr.Zero;
        private static int _isClosing;
        private static bool _isAllScreensBlackout;

        // 常量定义
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;

        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private static readonly IntPtr HWND_BROADCAST = new IntPtr(0xFFFF);
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;
        private const uint SWP_NOOWNERZORDER = 0x0200;
        private const uint SWP_NOSENDCHANGING = 0x0400;
        private const uint TOPMOST_FLAGS = SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_NOSENDCHANGING;
        private const uint INITIAL_PLACE_FLAGS = SWP_SHOWWINDOW | SWP_NOOWNERZORDER | SWP_NOSENDCHANGING;

        private const uint MONITOR_DEFAULTTONULL = 0x00000000;
        private const int OBJID_WINDOW = 0;
        private const int CHILDID_SELF = 0;

        // 黑屏期间用于持续保持最前层级的守护器
        private static System.Windows.Forms.Timer topmostGuardTimer;
        private static IntPtr _foregroundEventHook = IntPtr.Zero;
        private static IntPtr _showEventHook = IntPtr.Zero;
        private static uint _currentProcessId;
        private static readonly HashSet<IntPtr> hiddenWindows = new HashSet<IntPtr>();
        private static int _maintainScheduled;
        private static long _lastSuppressTickMs;
        private const int SuppressIntervalMs = 300;

        private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
        private const uint EVENT_OBJECT_SHOW = 0x8002;
        private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
        private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
        private const int SW_HIDE = 0;
        private const int SW_SHOW = 5;

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOPMOST = 0x00000008;
        private const int WS_EX_TOOLWINDOW = 0x00000080;

        // Win32 P/Invoke
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumDelegate lpfnEnum, IntPtr dwData);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfoEx lpmi);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(PointStruct pt, uint dwFlags);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out PointStruct lpPoint);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowW(string lpClassName, string lpWindowName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern uint RegisterWindowMessageW(string lpString);

        [DllImport("user32.dll")]
        private static extern bool PostMessageW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr CreateCursor(IntPtr hInst, int xHotSpot, int yHotSpot, int nWidth, int nHeight, byte[] pvANDPlane, byte[] pvXORPlane);

        [DllImport("user32.dll")]
        private static extern bool DestroyCursor(IntPtr hCursor);

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool GetMonitorBrightness(IntPtr hMonitor, out int pdwMinimumBrightness, out int pdwCurrentBrightness, out int pdwMaximumBrightness);

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool SetMonitorBrightness(IntPtr hMonitor, int dwNewBrightness);

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool DestroyPhysicalMonitor(IntPtr hMonitor);

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint pdwNumberOfPhysicalMonitors);

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint dwPhysicalMonitorArraySize, [Out] PhysicalMonitor[] pPhysicalMonitorArray);

        [DllImport("user32.dll")]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll")]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

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
        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        private delegate bool MonitorEnumDelegate(IntPtr hMonitor, IntPtr hdcMonitor, ref Rect lprcMonitor, IntPtr dwData);

        [StructLayout(LayoutKind.Sequential)]
        private struct PointStruct
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

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

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct PhysicalMonitor
        {
            public IntPtr hPhysicalMonitor;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szPhysicalMonitorDescription;
        }

        private static SynchronizationContext syncContext;

        private static ToolStripMenuItem blackoutMainScreenMenuItem;
        private static ToolStripMenuItem blackoutOtherScreensMenuItem;
        private static ToolStripMenuItem autoStartMenuItem;

        private const string AutoStartTaskName = "Blackout_AutoStart";
        private const string AutoStartValueName = "Blackout";
        private const string RunRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string StartupApprovedRunRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

        [STAThread]
        static void Main()
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            Application.ThreadException += (s, e) => { /* 捕获 UI 线程非致命异常，防止托盘闪退 */ };
            AppDomain.CurrentDomain.UnhandledException += (s, e) => { /* 防止驱动/WMI 瞬断异常杀进程 */ };

            _wmTaskbarCreated = RegisterWindowMessageW("TaskbarCreated");
            _wmBlackoutShowTray = RegisterWindowMessageW("Blackout_ShowTray_Msg");

            singleInstanceMutex = new Mutex(true, @"Local\Blackout_SingleInstance", out bool createdNew);
            if (!createdNew)
            {
                // 唤醒后台实例重挂托盘图标；若后台实例无响应卡死，则终止旧实例并接管
                if (_wmBlackoutShowTray != 0)
                {
                    PostMessageW(HWND_BROADCAST, _wmBlackoutShowTray, IntPtr.Zero, IntPtr.Zero);
                }

                bool tookOver = TryTakeOverUnresponsiveInstance();
                if (!tookOver)
                {
                    return;
                }
            }

            _currentProcessId = (uint)Process.GetCurrentProcess().Id;

            // 开机登录阶段等待 explorer.exe 系统托盘窗口就绪（最多等待 12 秒）
            WaitForShellTrayReady(maxWaitMs: 12000);

            syncContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(syncContext);

            blankCursor = CreateBlankCursor();
            trayMessageWindow = new TrayMessageWindow();

            // 创建上下文菜单
            contextMenu = new ContextMenuStrip();
            ToolStripMenuItem blackoutMenuItem = new ToolStripMenuItem("一键黑屏", null, BlackoutMenuItem_Click);
            blackoutMainScreenMenuItem = new ToolStripMenuItem("主屏黑屏", null, BlackoutMainScreenMenuItem_Click);
            blackoutOtherScreensMenuItem = new ToolStripMenuItem("副屏黑屏", null, BlackoutOtherScreensMenuItem_Click);
            autoStartMenuItem = new ToolStripMenuItem("开机自启", null, AutoStartMenuItem_Click);
            ToolStripMenuItem restartMenuItem = new ToolStripMenuItem("重启软件", null, RestartMenuItem_Click);
            ToolStripMenuItem exitMenuItem = new ToolStripMenuItem("退出软件", null, ExitMenuItem_Click);

            contextMenu.Items.AddRange(new ToolStripItem[]
            {
                blackoutMenuItem,
                blackoutMainScreenMenuItem,
                blackoutOtherScreensMenuItem,
                autoStartMenuItem,
                restartMenuItem,
                exitMenuItem
            });
            contextMenu.Opening += ContextMenu_Opening;

            InitializeNotifyIcon();

            RemoveLegacyRegistryAutoStart();
            if (!IsAutoStartDisabledByUser() && File.Exists(Application.ExecutablePath))
            {
                CreateOrUpdateAutoStartTask(Application.ExecutablePath, out _);
            }
            bool autoStartEnabled = IsAutoStartEnabled();
            autoStartMenuItem.Checked = autoStartEnabled;

            // 启动前 30 秒内周期性确认托盘可见性，防止登录初期资源管理器延迟吞图标
            trayStartupCheckCount = 0;
            trayHealthTimer = new System.Windows.Forms.Timer { Interval = 3000 };
            trayHealthTimer.Tick += (s, e) =>
            {
                trayStartupCheckCount++;
                if (notifyIcon != null && windows.Count == 0)
                {
                    notifyIcon.Visible = true;
                }
                if (trayStartupCheckCount >= 10)
                {
                    trayHealthTimer.Stop();
                }
            };
            trayHealthTimer.Start();

            Application.ApplicationExit += (sender, e) =>
            {
                CleanupResourcesOnExit();
            };

            Application.Run();
        }

        private static bool TryTakeOverUnresponsiveInstance()
        {
            try
            {
                int currentId = Process.GetCurrentProcess().Id;
                Process[] procs = Process.GetProcessesByName("Blackout");
                bool killedAny = false;

                foreach (Process p in procs)
                {
                    using (p)
                    {
                        if (p.Id == currentId) continue;
                        if (!p.Responding)
                        {
                            try
                            {
                                p.Kill();
                                p.WaitForExit(2000);
                                killedAny = true;
                            }
                            catch
                            {
                            }
                        }
                    }
                }

                if (killedAny)
                {
                    try
                    {
                        if (singleInstanceMutex.WaitOne(2000))
                        {
                            return true;
                        }
                    }
                    catch (AbandonedMutexException)
                    {
                        return true;
                    }
                }
            }
            catch
            {
            }
            return false;
        }

        private static void WaitForShellTrayReady(int maxWaitMs)
        {
            Stopwatch sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < maxWaitMs)
            {
                if (FindWindowW("Shell_TrayWnd", null) != IntPtr.Zero)
                {
                    return;
                }
                Thread.Sleep(250);
            }
        }

        private static void InitializeNotifyIcon()
        {
            try
            {
                notifyIcon?.Dispose();
            }
            catch
            {
            }

            notifyIcon = new NotifyIcon
            {
                Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application,
                Text = "Blackout",
                ContextMenuStrip = contextMenu,
                Visible = true
            };
            notifyIcon.MouseClick += NotifyIcon_MouseClick;
        }

        private static void RebuildNotifyIcon()
        {
            try
            {
                WaitForShellTrayReady(3000);
                if (notifyIcon == null)
                {
                    InitializeNotifyIcon();
                    return;
                }

                notifyIcon.Visible = false;
                notifyIcon.Visible = true;
            }
            catch
            {
                InitializeNotifyIcon();
            }
        }

        private static Cursor CreateBlankCursor()
        {
            try
            {
                byte[] andPlane = new byte[128];
                for (int i = 0; i < andPlane.Length; i++)
                {
                    andPlane[i] = 0xFF;
                }
                byte[] xorPlane = new byte[128];
                IntPtr hCursor = CreateCursor(IntPtr.Zero, 0, 0, 32, 32, andPlane, xorPlane);
                if (hCursor != IntPtr.Zero)
                {
                    return new Cursor(hCursor);
                }
            }
            catch
            {
            }
            return null;
        }

        private static void CleanupResourcesOnExit()
        {
            try
            {
                if (windows.Count > 0)
                {
                    CloseAllWindows();
                }
            }
            catch
            {
            }

            try
            {
                if (notifyIcon != null)
                {
                    notifyIcon.Visible = false;
                    notifyIcon.Dispose();
                    notifyIcon = null;
                }
            }
            catch
            {
            }

            try
            {
                trayMessageWindow?.DestroyHandle();
            }
            catch
            {
            }

            ReleaseSingleInstanceMutex();
        }

        private static void ReleaseSingleInstanceMutex()
        {
            if (singleInstanceMutex == null) return;
            try
            {
                singleInstanceMutex.ReleaseMutex();
            }
            catch
            {
            }
            try
            {
                singleInstanceMutex.Dispose();
            }
            catch
            {
            }
            singleInstanceMutex = null;
        }

        private static void ContextMenu_Opening(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (windows.Count > 0)
            {
                CloseAllWindows();
                e.Cancel = true;
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
                SetAutoStartDisabledByUser(false);
                RemoveLegacyRegistryAutoStart();
                if (CreateOrUpdateAutoStartTask(exePath, out errorMessage))
                {
                    return true;
                }

                errorMessage = "开启开机自启失败。" + Environment.NewLine + errorMessage;
                return false;
            }

            SetAutoStartDisabledByUser(true);
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

        private static bool IsAutoStartDisabledByUser()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Blackout", false))
                {
                    object val = key?.GetValue("AutoStartDisabledByUser");
                    if (val != null && Convert.ToInt32(val) == 1)
                    {
                        return true;
                    }
                }
            }
            catch
            {
            }
            return false;
        }

        private static void SetAutoStartDisabledByUser(bool disabled)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Blackout"))
                {
                    key?.SetValue("AutoStartDisabledByUser", disabled ? 1 : 0, RegistryValueKind.DWord);
                }
            }
            catch
            {
            }
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

        private static string EscapeXml(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return value
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;")
                .Replace("'", "&apos;");
        }

        private static bool CreateOrUpdateAutoStartTask(string exePath, out string errorMessage)
        {
            string tempXmlPath = Path.Combine(Path.GetTempPath(), $"Blackout_AutoStart_{Guid.NewGuid():N}.xml");
            try
            {
                string userId = WindowsIdentity.GetCurrent()?.Name ?? Environment.UserName;
                string workDir = Path.GetDirectoryName(exePath) ?? AppDomain.CurrentDomain.BaseDirectory;
                string escapedExe = EscapeXml($"\"{exePath}\"");
                string escapedWorkDir = EscapeXml(workDir);
                string escapedUserId = EscapeXml(userId);

                string xmlContent = $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo>
    <Description>Blackout 托盘工具开机自启任务（支持电池供电启动与崩溃恢复）</Description>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>{escapedUserId}</UserId>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id=""Author"">
      <UserId>{escapedUserId}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
    <RestartOnFailure>
      <Interval>PT1M</Interval>
      <Count>3</Count>
    </RestartOnFailure>
  </Settings>
  <Actions Context=""Author"">
    <Exec>
      <Command>{escapedExe}</Command>
      <WorkingDirectory>{escapedWorkDir}</WorkingDirectory>
    </Exec>
  </Actions>
</Task>";

                File.WriteAllText(tempXmlPath, xmlContent, Encoding.Unicode);
                string args = $"/Create /F /TN \"{AutoStartTaskName}\" /XML \"{tempXmlPath}\"";
                if (RunSchtasksCommand(args, out _, out errorMessage))
                {
                    return true;
                }

                // 降级兜底
                string fallbackArgs = $"/Create /F /TN \"{AutoStartTaskName}\" /SC ONLOGON /RL HIGHEST /TR \"\\\"{exePath}\\\"\"";
                if (RunSchtasksCommand(fallbackArgs, out _, out errorMessage))
                {
                    return true;
                }

                errorMessage = string.IsNullOrWhiteSpace(errorMessage)
                    ? "创建最高权限计划任务失败。请确认当前 Blackout 是以管理员身份运行。"
                    : errorMessage;
                return false;
            }
            finally
            {
                try
                {
                    if (File.Exists(tempXmlPath))
                    {
                        File.Delete(tempXmlPath);
                    }
                }
                catch
                {
                }
            }
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

        private static void NotifyIcon_MouseClick(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                if (windows.Count > 0)
                {
                    CloseAllWindows();
                }
                else
                {
                    EnterBlackoutMode();
                }
            }
        }

        private static void BlackoutMenuItem_Click(object sender, EventArgs e)
        {
            if (windows.Count > 0)
            {
                CloseAllWindows();
            }
            EnterBlackoutMode();
        }

        private static void BlackoutMainScreenMenuItem_Click(object sender, EventArgs e)
        {
            if (windows.Count > 0)
            {
                CloseAllWindows();
            }
            EnterBlackoutMode(mainScreenOnly: true);
        }

        private static void BlackoutOtherScreensMenuItem_Click(object sender, EventArgs e)
        {
            if (windows.Count > 0)
            {
                CloseAllWindows();
            }
            EnterBlackoutMode(otherScreensOnly: true);
        }

        private static void RestartMenuItem_Click(object sender, EventArgs e)
        {
            try
            {
                if (windows.Count > 0)
                {
                    CloseAllWindows();
                }
                if (notifyIcon != null)
                {
                    notifyIcon.Visible = false;
                    notifyIcon.Dispose();
                    notifyIcon = null;
                }
                // 必须在拉起新进程前释放单实例 Mutex，防止新实例检测到锁未释放而自杀退出
                ReleaseSingleInstanceMutex();

                Process.Start(new ProcessStartInfo
                {
                    FileName = Application.ExecutablePath,
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(Application.ExecutablePath) ?? AppDomain.CurrentDomain.BaseDirectory
                });
            }
            catch
            {
            }
            Application.Exit();
        }

        private static void ExitMenuItem_Click(object sender, EventArgs e)
        {
            if (windows.Count > 0)
            {
                CloseAllWindows();
            }
            if (notifyIcon != null)
            {
                notifyIcon.Visible = false;
            }
            Application.Exit();
        }

        private static void CaptureInitialBrightnessIfNeeded()
        {
            if (_brightnessCaptured)
            {
                return;
            }

            int mainB = GetBrightness();
            if (mainB > 0)
            {
                _savedMainBrightness = mainB;
            }

            foreach (MonitorTarget m in enumeratedMonitors)
            {
                if (!m.IsPrimary)
                {
                    int subB = GetBrightness(m.Handle);
                    if (subB > 0)
                    {
                        _savedMonitorBrightness[m.Handle] = subB;
                    }
                    else if (!_savedMonitorBrightness.ContainsKey(m.Handle))
                    {
                        _savedMonitorBrightness[m.Handle] = 80;
                    }
                }
            }

            _brightnessCaptured = true;
        }

        private static void EnterBlackoutMode(bool mainScreenOnly = false, bool otherScreensOnly = false)
        {
            Interlocked.Exchange(ref _isClosing, 0);
            _isAllScreensBlackout = !mainScreenOnly && !otherScreensOnly;

            enumeratedMonitors.Clear();
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, _monitorEnumProc, IntPtr.Zero);

            CaptureInitialBrightnessIfNeeded();

            // 根据目标模式设置显示器亮度
            if (mainScreenOnly)
            {
                RestoreMainMonitorBrightness(0);
            }
            else if (otherScreensOnly)
            {
                SetBrightnessForMonitors(0, excludePrimary: true);
            }
            else
            {
                SetBrightness(0);
            }

            if (_hookID == IntPtr.Zero)
            {
                _hookID = SetHook(_keyboardHookProc);
            }

            windows.Clear();
            activeBlackoutMonitors.Clear();

            foreach (MonitorTarget target in enumeratedMonitors)
            {
                bool shouldBlackout =
                    (!mainScreenOnly && !otherScreensOnly) ||
                    (mainScreenOnly && target.IsPrimary) ||
                    (otherScreensOnly && !target.IsPrimary);

                if (shouldBlackout)
                {
                    BlackoutOverlayForm form = CreateBlackoutForm(target);
                    windows.Add(form);
                    activeBlackoutMonitors.Add(target.Handle);
                }
            }

            foreach (BlackoutOverlayForm window in windows)
            {
                window.Show();
                SetWindowPos(
                    window.Handle,
                    HWND_TOPMOST,
                    window.PhysicalBounds.Left,
                    window.PhysicalBounds.Top,
                    window.PhysicalBounds.Width,
                    window.PhysicalBounds.Height,
                    INITIAL_PLACE_FLAGS);
            }

            StartTopmostGuard();
            MaintainBlackoutLayer(forceSuppress: true);
        }

        private static BlackoutOverlayForm CreateBlackoutForm(MonitorTarget target)
        {
            BlackoutOverlayForm form = new BlackoutOverlayForm(target.Handle, target.Bounds, blankCursor);

            form.KeyDown += (sender, e) =>
            {
                if (e.KeyCode == Keys.Escape)
                {
                    CloseAllWindows();
                }
            };

            form.MouseDown += (sender, e) =>
            {
                CloseAllWindows();
            };

            form.Load += (sender, e) =>
            {
                SetWindowPos(
                    form.Handle,
                    HWND_TOPMOST,
                    target.Bounds.Left,
                    target.Bounds.Top,
                    target.Bounds.Width,
                    target.Bounds.Height,
                    INITIAL_PLACE_FLAGS);
            };

            return form;
        }

        private static void StartTopmostGuard()
        {
            StopTopmostGuard();

            if (topmostGuardTimer == null)
            {
                topmostGuardTimer = new System.Windows.Forms.Timer { Interval = 250 };
                topmostGuardTimer.Tick += (sender, e) => MaintainBlackoutLayer();
            }

            _maintainScheduled = 0;
            _lastSuppressTickMs = 0;

            _foregroundEventHook = SetWinEventHook(
                EVENT_SYSTEM_FOREGROUND,
                EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero,
                _winEventProc,
                0,
                0,
                WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

            _showEventHook = SetWinEventHook(
                EVENT_OBJECT_SHOW,
                EVENT_OBJECT_SHOW,
                IntPtr.Zero,
                _winEventProc,
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

        private static bool IsSuppressibleWindow(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return false;
            StringBuilder sb = new StringBuilder(64);
            if (GetClassNameW(hWnd, sb, sb.Capacity) == 0) return false;
            string cls = sb.ToString();

            // 仅针对系统任务栏、DisplayFusion 任务栏与系统通知横幅进行压制，100% 保护 TXGuiFoundation (QQ/微信截图)
            if (string.Equals(cls, "Shell_TrayWnd", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(cls, "Shell_SecondaryTrayWnd", StringComparison.OrdinalIgnoreCase)) return true;
            if (cls.StartsWith("DFTaskbar", StringComparison.OrdinalIgnoreCase)) return true;
            if (cls.StartsWith("DisplayFusionTaskbar", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(cls, "Windows.UI.Core.CoreWindow", StringComparison.OrdinalIgnoreCase)) return true;

            return false;
        }

        private static bool IsOnBlackoutMonitorHandle(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero || activeBlackoutMonitors.Count == 0) return false;
            IntPtr winMon = MonitorFromWindow(hWnd, MONITOR_DEFAULTTONULL);
            return winMon != IntPtr.Zero && activeBlackoutMonitors.Contains(winMon);
        }

        private static void WinEventCallback(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            if (idObject != OBJID_WINDOW || idChild != CHILDID_SELF)
            {
                return;
            }

            if (windows.Count == 0 || activeBlackoutMonitors.Count == 0 || syncContext == null)
            {
                return;
            }

            // 番茄钟 v2.5.29 同款精准门禁：仅放行当前黑屏物理显示器上的可压制任务栏/通知弹窗
            if (eventType == EVENT_OBJECT_SHOW)
            {
                if (!IsSuppressibleWindow(hwnd) || !IsOnBlackoutMonitorHandle(hwnd))
                {
                    return;
                }
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
            if (windows.Count == 0)
            {
                return;
            }

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
            foreach (BlackoutOverlayForm window in windows)
            {
                if (window == null || window.IsDisposed || !window.IsHandleCreated)
                {
                    continue;
                }

                SetWindowPos(window.Handle, HWND_TOPMOST, 0, 0, 0, 0, TOPMOST_FLAGS);
            }
        }

        private static bool SuppressEnumWindowsProc(IntPtr hWnd, IntPtr lParam)
        {
            if (hWnd == IntPtr.Zero || !IsWindowVisible(hWnd))
            {
                return true;
            }

            if (!IsSuppressibleWindow(hWnd))
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

            // 内核级 HMONITOR 物理屏幕严格匹配：绝不触碰未黑屏副屏上的 DisplayFusion 任务栏
            IntPtr winMon = MonitorFromWindow(hWnd, MONITOR_DEFAULTTONULL);
            if (winMon == IntPtr.Zero || !activeBlackoutMonitors.Contains(winMon))
            {
                return true;
            }

            if (!GetWindowRect(hWnd, out Rect rect))
            {
                return true;
            }

            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;
            if (width < 30 || height < 20)
            {
                return true;
            }

            ShowWindow(hWnd, SW_HIDE);
            hiddenWindows.Add(hWnd);
            return true;
        }

        private static void SuppressCompetingTopmostWindows()
        {
            if (windows.Count == 0 || activeBlackoutMonitors.Count == 0)
            {
                return;
            }

            EnumWindows(_enumWindowsProc, IntPtr.Zero);
        }

        private static void RestoreHiddenWindows()
        {
            foreach (IntPtr hWnd in hiddenWindows)
            {
                if (IsWindow(hWnd))
                {
                    ShowWindow(hWnd, SW_SHOW);
                }
            }

            hiddenWindows.Clear();
        }

        private static bool IsFocusOrCursorOnBlackoutMonitor()
        {
            if (_isAllScreensBlackout) return true;
            if (activeBlackoutMonitors.Count == 0) return false;

            if (GetCursorPos(out PointStruct pt))
            {
                IntPtr cursorMon = MonitorFromPoint(pt, MONITOR_DEFAULTTONULL);
                if (cursorMon != IntPtr.Zero && activeBlackoutMonitors.Contains(cursorMon))
                {
                    return true;
                }
            }

            IntPtr fg = GetForegroundWindow();
            if (fg != IntPtr.Zero)
            {
                IntPtr fgMon = MonitorFromWindow(fg, MONITOR_DEFAULTTONULL);
                if (fgMon != IntPtr.Zero && activeBlackoutMonitors.Contains(fgMon))
                {
                    return true;
                }
            }

            return false;
        }

        private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN))
            {
                int vkCode = Marshal.ReadInt32(lParam);

                if (vkCode == (int)Keys.Escape)
                {
                    syncContext?.Post(_ => CloseAllWindows(), null);
                    return (IntPtr)1;
                }
                else if ((vkCode == (int)Keys.LWin || vkCode == (int)Keys.RWin) && IsFocusOrCursorOnBlackoutMonitor())
                {
                    return (IntPtr)1;
                }
            }

            return CallNextHookEx(_hookID, nCode, wParam, lParam);
        }

        private static IntPtr SetHook(LowLevelKeyboardProc proc)
        {
            using (Process curProcess = Process.GetCurrentProcess())
            using (ProcessModule curModule = curProcess.MainModule)
            {
                return SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(curModule.ModuleName), 0);
            }
        }

        private static void CloseAllWindows()
        {
            if (Interlocked.Exchange(ref _isClosing, 1) == 1)
            {
                return;
            }

            try
            {
                StopTopmostGuard();
                RestoreHiddenWindows();
                activeBlackoutMonitors.Clear();

                List<BlackoutOverlayForm> toClose = new List<BlackoutOverlayForm>(windows);
                windows.Clear();

                foreach (BlackoutOverlayForm window in toClose)
                {
                    try
                    {
                        if (window != null && !window.IsDisposed)
                        {
                            window.Close();
                            window.Dispose();
                        }
                    }
                    catch
                    {
                    }
                }

                if (_hookID != IntPtr.Zero)
                {
                    UnhookWindowsHookEx(_hookID);
                    _hookID = IntPtr.Zero;
                }

                RestoreBrightness();
            }
            finally
            {
                Interlocked.Exchange(ref _isClosing, 0);
            }
        }

        private static void SetBrightness(int brightness)
        {
            try
            {
                RestoreMainMonitorBrightness(brightness);

                foreach (MonitorTarget m in enumeratedMonitors)
                {
                    if (!m.IsPrimary)
                    {
                        SetMonitorBrightnessForPhysicalMonitors(m.Handle, brightness);
                    }
                }
            }
            catch
            {
            }
        }

        private static void SetBrightnessForMonitors(int brightness, bool excludePrimary = false)
        {
            foreach (MonitorTarget m in enumeratedMonitors)
            {
                if (excludePrimary && m.IsPrimary)
                {
                    continue;
                }
                SetMonitorBrightnessForPhysicalMonitors(m.Handle, brightness);
            }
        }

        private static void RestoreBrightness()
        {
            if (!_brightnessCaptured)
            {
                return;
            }

            try
            {
                RestoreMainMonitorBrightness(_savedMainBrightness);

                foreach (KeyValuePair<IntPtr, int> kv in _savedMonitorBrightness)
                {
                    SetMonitorBrightnessForPhysicalMonitors(kv.Key, kv.Value);
                }
            }
            catch
            {
            }
            finally
            {
                _brightnessCaptured = false;
            }
        }

        private static void RestoreMainMonitorBrightness(int brightness)
        {
            try
            {
                ManagementScope scope = new ManagementScope("root\\WMI");
                SelectQuery query = new SelectQuery("WmiMonitorBrightnessMethods");

                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(scope, query))
                using (ManagementObjectCollection objectCollection = searcher.Get())
                {
                    foreach (ManagementObject mObj in objectCollection)
                    {
                        using (mObj)
                        {
                            mObj.InvokeMethod("WmiSetBrightness", new object[] { uint.MaxValue, brightness });
                        }
                    }
                }
            }
            catch
            {
            }
        }

        private static void SetMonitorBrightnessForPhysicalMonitors(IntPtr monitor, int brightness)
        {
            try
            {
                if (!GetNumberOfPhysicalMonitorsFromHMONITOR(monitor, out uint numberOfPhysicalMonitors) || numberOfPhysicalMonitors == 0)
                {
                    return;
                }

                PhysicalMonitor[] physicalMonitors = new PhysicalMonitor[numberOfPhysicalMonitors];
                if (!GetPhysicalMonitorsFromHMONITOR(monitor, numberOfPhysicalMonitors, physicalMonitors))
                {
                    return;
                }

                foreach (PhysicalMonitor physicalMonitor in physicalMonitors)
                {
                    try
                    {
                        SetMonitorBrightness(physicalMonitor.hPhysicalMonitor, brightness);
                    }
                    finally
                    {
                        DestroyPhysicalMonitor(physicalMonitor.hPhysicalMonitor);
                    }
                }
            }
            catch
            {
            }
        }

        private static int GetBrightness()
        {
            int brightness = 0;
            try
            {
                ManagementScope scope = new ManagementScope("root\\WMI");
                SelectQuery query = new SelectQuery("WmiMonitorBrightness");

                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(scope, query))
                using (ManagementObjectCollection objectCollection = searcher.Get())
                {
                    foreach (ManagementObject mObj in objectCollection)
                    {
                        using (mObj)
                        {
                            brightness = Convert.ToInt32(mObj.GetPropertyValue("CurrentBrightness"));
                            break;
                        }
                    }
                }
            }
            catch
            {
            }
            return brightness;
        }

        private static int GetBrightness(IntPtr monitor)
        {
            int currentBrightness = 0;
            try
            {
                if (!GetNumberOfPhysicalMonitorsFromHMONITOR(monitor, out uint numberOfPhysicalMonitors) || numberOfPhysicalMonitors == 0)
                {
                    return 0;
                }

                PhysicalMonitor[] physicalMonitors = new PhysicalMonitor[numberOfPhysicalMonitors];
                if (!GetPhysicalMonitorsFromHMONITOR(monitor, numberOfPhysicalMonitors, physicalMonitors))
                {
                    return 0;
                }

                foreach (PhysicalMonitor physicalMonitor in physicalMonitors)
                {
                    try
                    {
                        GetMonitorBrightness(physicalMonitor.hPhysicalMonitor, out _, out currentBrightness, out _);
                    }
                    finally
                    {
                        DestroyPhysicalMonitor(physicalMonitor.hPhysicalMonitor);
                    }
                }
            }
            catch
            {
            }
            return currentBrightness;
        }

        private static bool MonitorEnumCallback(IntPtr hMonitor, IntPtr hdcMonitor, ref Rect lprcMonitor, IntPtr dwData)
        {
            MonitorInfoEx mi = GetMonitorInfoEx(hMonitor);
            int width = mi.Monitor.Right - mi.Monitor.Left;
            int height = mi.Monitor.Bottom - mi.Monitor.Top;
            if (width <= 0 || height <= 0)
            {
                return true;
            }

            enumeratedMonitors.Add(new MonitorTarget
            {
                Handle = hMonitor,
                Bounds = new Rectangle(mi.Monitor.Left, mi.Monitor.Top, width, height),
                IsPrimary = (mi.Flags & 1) != 0
            });
            return true;
        }

        private static MonitorInfoEx GetMonitorInfoEx(IntPtr hMonitor)
        {
            MonitorInfoEx mi = new MonitorInfoEx();
            mi.Size = Marshal.SizeOf(typeof(MonitorInfoEx));
            GetMonitorInfo(hMonitor, ref mi);
            return mi;
        }
    }
}

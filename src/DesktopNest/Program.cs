using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace DesktopNest
{
    internal static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern int SetCurrentProcessExplicitAppUserModelID(
            [MarshalAs(UnmanagedType.LPWStr)] string appId);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr windowHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool AttachConsole(int processId);

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr windowHandle, out Rect rect);

        public const int WM_GETTITLEBARINFOEX = 0x043F;
        private const int STATE_SYSTEM_INVISIBLE = 0x8000;

        [StructLayout(LayoutKind.Sequential)]
        public struct TitleBarInfoEx
        {
            public int cbSize;
            public Rect titleBarRect;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)]
            public int[] states;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)]
            public Rect[] buttonRects;
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern IntPtr SendMessage(
            IntPtr windowHandle,
            int message,
            IntPtr wParam,
            ref TitleBarInfoEx lParam);

        // 读取标题栏真实按钮的屏幕坐标（0=标题栏 1=保留 2=最小化 3=最大化 4=关闭）。
        // Windows 11 实际渲染的按钮比 SystemInformation.CaptionButtonSize 宽，
        // 直接用度量值推算会错位，所以以系统回报的真实矩形为准。
        public static bool TryGetCaptionButtonRect(
            IntPtr windowHandle,
            int buttonIndex,
            out Rect rect)
        {
            rect = new Rect();
            if (windowHandle == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                TitleBarInfoEx info = new TitleBarInfoEx();
                info.cbSize = Marshal.SizeOf(typeof(TitleBarInfoEx));
                SendMessage(
                    windowHandle,
                    WM_GETTITLEBARINFOEX,
                    IntPtr.Zero,
                    ref info);
                if (info.states == null
                    || info.buttonRects == null
                    || info.states.Length <= buttonIndex
                    || info.buttonRects.Length <= buttonIndex)
                {
                    return false;
                }

                if ((info.states[buttonIndex] & STATE_SYSTEM_INVISIBLE) != 0)
                {
                    return false;
                }

                Rect candidate = info.buttonRects[buttonIndex];
                if (candidate.Right <= candidate.Left
                    || candidate.Bottom <= candidate.Top)
                {
                    return false;
                }

                rect = candidate;
                return true;
            }
            catch
            {
                return false;
            }
        }

        [DllImport(
            "user32.dll",
            CharSet = CharSet.Unicode,
            EntryPoint = "SystemParametersInfoW",
            SetLastError = true)]
        private static extern bool SystemParametersInfo(
            int action,
            int parameter,
            StringBuilder value,
            int update);

        [DllImport("user32.dll")]
        public static extern IntPtr GetDC(IntPtr windowHandle);

        [DllImport("user32.dll")]
        public static extern int ReleaseDC(IntPtr windowHandle, IntPtr hdc);

        [DllImport("gdi32.dll")]
        public static extern int GetPixel(IntPtr hdc, int x, int y);

        public static string GetDesktopWallpaper()
        {
            const int getDesktopWallpaper = 0x0073;
            StringBuilder path = new StringBuilder(520);
            if (SystemParametersInfo(
                getDesktopWallpaper,
                path.Capacity,
                path,
                0))
            {
                string value = path.ToString();
                if (!String.IsNullOrWhiteSpace(value) && File.Exists(value))
                {
                    return value;
                }
            }

            string fallback = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Microsoft",
                "Windows",
                "Themes",
                "TranscodedWallpaper");
            return File.Exists(fallback) ? fallback : null;
        }
    }

    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if (HasArgument(args, "--self-test"))
            {
                return SelfTest.Run();
            }

            try
            {
                NativeMethods.SetCurrentProcessExplicitAppUserModelID(AppConstants.AppUserModelId);
            }
            catch
            {
            }

            bool createdNew;
            using (Mutex mutex = new Mutex(true, AppConstants.MutexName, out createdNew))
            {
                if (!createdNew)
                {
                    SignalExistingInstance();
                    return 0;
                }

                using (EventWaitHandle activationEvent = new EventWaitHandle(
                    false,
                    EventResetMode.AutoReset,
                    AppConstants.ActivationEventName))
                {
                    return RunApplication(activationEvent);
                }
            }
        }

        private static int RunApplication(EventWaitHandle activationEvent)
        {
            try
            {
                string dataRoot = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DesktopNest");

                AppSettingsStore settingsStore = new AppSettingsStore(dataRoot);
                string warning;
                AppSettings settings = settingsStore.Load(out warning);

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                MainForm mainForm = new MainForm(settingsStore, settings, warning);
                Thread activationThread = new Thread(delegate()
                {
                    while (true)
                    {
                        activationEvent.WaitOne();
                        if (mainForm == null || mainForm.IsDisposed)
                        {
                            continue;
                        }

                        try
                        {
                            mainForm.BeginInvoke(new MethodInvoker(mainForm.ActivateFromSecondInstance));
                        }
                        catch
                        {
                        }
                    }
                });
                activationThread.IsBackground = true;
                activationThread.Name = "DesktopNest.Activation";
                activationThread.Start();

                Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e)
                {
                    ShowUnexpectedError(e.Exception, dataRoot);
                };
                AppDomain.CurrentDomain.UnhandledException += delegate(
                    object sender,
                    UnhandledExceptionEventArgs e)
                {
                    Exception exception = e.ExceptionObject as Exception;
                    ShowUnexpectedError(exception, dataRoot);
                };

                Application.Run(mainForm);
                return 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "DesktopNest 启动失败：" + Environment.NewLine + ex.Message,
                    "DesktopNest",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return 1;
            }
        }

        private static void SignalExistingInstance()
        {
            for (int i = 0; i < 20; i++)
            {
                try
                {
                    using (EventWaitHandle activationEvent = EventWaitHandle.OpenExisting(
                        AppConstants.ActivationEventName))
                    {
                        activationEvent.Set();
                        return;
                    }
                }
                catch (WaitHandleCannotBeOpenedException)
                {
                    Thread.Sleep(50);
                }
                catch
                {
                    return;
                }
            }
        }

        private static bool HasArgument(string[] args, string expected)
        {
            if (args == null)
            {
                return false;
            }

            for (int i = 0; i < args.Length; i++)
            {
                if (String.Equals(args[i], expected, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static void ShowUnexpectedError(Exception exception, string dataRoot)
        {
            string message = exception == null ? "未知错误。" : exception.Message;
            try
            {
                File.AppendAllText(
                    Path.Combine(dataRoot, "error.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                        + Environment.NewLine
                        + exception
                        + Environment.NewLine
                        + Environment.NewLine);
            }
            catch
            {
            }

            MessageBox.Show(
                "DesktopNest 遇到错误：" + Environment.NewLine + message,
                "DesktopNest",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }
}

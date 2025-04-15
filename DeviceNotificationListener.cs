using System.Runtime.InteropServices;

namespace DeviceListenerChanged
{
    public class TargetVidPid
    {
        internal readonly string TARGET_VID;
        internal readonly string TARGET_PID;

        public TargetVidPid(int vid, int pid)
        {
            TARGET_VID = vid.ToString("X4");
            TARGET_PID = pid.ToString("X4");
        }
    }

    public class DevineInterface
    {
        internal readonly Guid GUID_DEVINTERFACE_USB_DEVICE = new("A5DCBF10-6530-11D2-901F-00C04FB951ED");
        internal readonly Guid GUID_DEVINTERFACE_COMPORT = new("86E0D1E0-8089-11D0-9CE4-08003E301F73");
        internal readonly Guid GUID_DEVINTERFACE_HID = new("4D1E55B2-F16F-11CF-88CB-001111000030");

        internal bool _useUsb;
        internal bool _useComport;
        internal bool _useHid;

        public DevineInterface(bool usb, bool comport, bool hid)
        {
            _useUsb = usb;
            _useComport = comport;
            _useHid = hid;
        }
    }

    public class DeviceNotificationListener : IDisposable
    {
        private TargetVidPid _targetVidPid;
        private DevineInterface _devineInterface;
        private IntPtr _hwnd = IntPtr.Zero;
        private Thread _messageThread;
        private ManualResetEvent _windowReady = new ManualResetEvent(false);
        private WndProcDelegate _wndProcDelegate;

        const int _offset = 28;

        public event Action DeviceMatchedConnected;
        public event Action DeviceMatchedDisconnected;

        private const int WM_DEVICECHANGE = 0x0219;
        private const int DBT_DEVICEARRIVAL = 0x8000;
        private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;
        private const int DBT_DEVTYP_DEVICEINTERFACE = 0x00000005;
        private const int DEVICE_NOTIFY_WINDOW_HANDLE = 0x00000000;
        private static readonly IntPtr HWND_MESSAGE = new IntPtr(-3);

        public DeviceNotificationListener(TargetVidPid target, DevineInterface iInterface)
        {
            _targetVidPid = target;
            _devineInterface = iInterface;
            _messageThread = new Thread(MessageLoopThread)
            {
                IsBackground = true,
                Name = "DeviceNotificationThread"
            };
            _messageThread.Start();
            _windowReady.WaitOne();
        }

        private void MessageLoopThread()
        {
            _wndProcDelegate = WndProc;

            var wc = new WNDCLASS
            {
                lpfnWndProc = _wndProcDelegate,
                lpszClassName = "HiddenDeviceListenerWindow"
            };

            var regResult = RegisterClass(ref wc);
            if (regResult == 0)
            {
                Console.WriteLine($"RegisterClass failed: {Marshal.GetLastWin32Error()}");
                return;
            }

            _hwnd = CreateWindowEx(
                0, wc.lpszClassName, string.Empty, 0,
                0, 0, 0, 0,
                HWND_MESSAGE, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

            if (_hwnd == IntPtr.Zero)
            {
                Console.WriteLine($"CreateWindowEx failed: {Marshal.GetLastWin32Error()}");
                return;
            }

            RegisterForDeviceNotifications(_hwnd);
            _windowReady.Set();

            MSG msg;
            while (GetMessage(out msg, IntPtr.Zero, 0, 0))
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }

        private void RegisterForDeviceNotifications(IntPtr hwnd)
        {
            if (_devineInterface._useUsb)
                RegisterSingle(hwnd, _devineInterface.GUID_DEVINTERFACE_USB_DEVICE);

            if (_devineInterface._useComport)
                RegisterSingle(hwnd, _devineInterface.GUID_DEVINTERFACE_COMPORT);

            if (_devineInterface._useHid)
                RegisterSingle(hwnd, _devineInterface.GUID_DEVINTERFACE_HID);
        }

        private void RegisterSingle(IntPtr hwnd, Guid guid)
        {
            var dbi = new DEV_BROADCAST_DEVICEINTERFACE_RAW
            {
                dbcc_size = Marshal.SizeOf(typeof(DEV_BROADCAST_DEVICEINTERFACE_RAW)),
                dbcc_devicetype = DBT_DEVTYP_DEVICEINTERFACE,
                dbcc_reserved = 0,
                dbcc_classguid = guid
            };

            var buffer = Marshal.AllocHGlobal(dbi.dbcc_size);
            Marshal.StructureToPtr(dbi, buffer, false);

            var notifHandle = RegisterDeviceNotification(hwnd, buffer, DEVICE_NOTIFY_WINDOW_HANDLE);
            if (notifHandle == IntPtr.Zero)
            {
                Console.WriteLine($"RegisterDeviceNotification failed: {Marshal.GetLastWin32Error()}");
            }
        }

        private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (msg == WM_DEVICECHANGE)
                {
                    var eventType = (int)wParam;

                    if (eventType == DBT_DEVICEARRIVAL || eventType == DBT_DEVICEREMOVECOMPLETE)
                    {
                        var hdr = Marshal.PtrToStructure<DEV_BROADCAST_HDR>(lParam);
                        if (hdr.dbch_devicetype == DBT_DEVTYP_DEVICEINTERFACE)
                        {
                            var devicePath = Marshal.PtrToStringAnsi(lParam + _offset);

                            if (!string.IsNullOrEmpty(devicePath) && MatchesTargetVidPid(devicePath))
                            {
                                if (eventType == DBT_DEVICEARRIVAL)
                                    DeviceMatchedConnected?.Invoke();
                                else if (eventType == DBT_DEVICEREMOVECOMPLETE)
                                    DeviceMatchedDisconnected?.Invoke();
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"WndProc exception: {ex}");
            }

            return DefWindowProc(hWnd, msg, wParam, lParam);
        }

        private bool MatchesTargetVidPid(string devicePath)
        {
            var upper = devicePath.ToUpperInvariant();
            var targetVid = _targetVidPid.TARGET_VID.Trim().ToUpperInvariant().Replace("0X", "");
            var targetPid = _targetVidPid.TARGET_PID.Trim().ToUpperInvariant().Replace("0X", "");

            return upper.Contains($"VID_{targetVid}") && upper.Contains($"PID_{targetPid}");
        }

        public void Dispose()
        {
            if (_hwnd != IntPtr.Zero)
            {
                PostMessage(_hwnd, 0x0012, IntPtr.Zero, IntPtr.Zero);
                _messageThread.Join();
                _hwnd = IntPtr.Zero;
            }
        }

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct WNDCLASS
        {
            public uint style;
            public WndProcDelegate lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string lpszMenuName;
            public string lpszClassName;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public POINT pt;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int x;
            public int y;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DEV_BROADCAST_HDR
        {
            public int dbch_size;
            public int dbch_devicetype;
            public int dbch_reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DEV_BROADCAST_DEVICEINTERFACE_RAW
        {
            public int dbcc_size;
            public int dbcc_devicetype;
            public int dbcc_reserved;
            public Guid dbcc_classguid;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern ushort RegisterClass(ref WNDCLASS lpWndClass);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr CreateWindowEx(
            int dwExStyle, string lpClassName, string lpWindowName, int dwStyle,
            int x, int y, int nWidth, int nHeight,
            IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

        [DllImport("user32.dll")]
        private static extern bool GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern IntPtr DefWindowProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr RegisterDeviceNotification(IntPtr hRecipient, IntPtr NotificationFilter,
            uint Flags);

    }
}
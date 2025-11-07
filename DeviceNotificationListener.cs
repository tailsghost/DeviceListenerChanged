using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace DeviceListenerChanged

{
    public enum LogType: int
    {
        Ok,
        Warn,
        Err
    }
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

        internal bool _useUsb = false;
        internal bool _useComport = false;
        internal bool _useHid = false;

        public DevineInterface(bool usb, bool comport, bool hid)
        {
            _useComport = comport;
            _useUsb = usb;
            _useHid = hid;
        }
    }


    public class DeviceNotificationListener : IDisposable
    {
        private readonly TargetVidPid _targetVidPid;
        private readonly DevineInterface _devineInterface;

        private IntPtr _hwnd = IntPtr.Zero;
        private Thread? _messageThread;
        private ManualResetEvent _windowReady = new ManualResetEvent(false);
        private WndProcDelegate? _wndProcDelegate;
        private GCHandle? _wndProcHandle;
        private uint _messageThreadId = 0;
        private readonly List<IntPtr> _notifHandles = new List<IntPtr>();
        private string? _className;

        private Thread? _pollThread;
        private volatile bool _pollStop = false;
        private bool _devicePreviouslyPresent = false;

        public event Action? DeviceMatchedConnected;
        public event Action? DeviceMatchedDisconnected;

        private const int WM_DEVICECHANGE = 0x0219;

        private const int DBT_DEVICEARRIVAL = 0x8000;
        private const int DBT_DEVICEREMOVEPENDING = 0x8003;
        private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;
        private const int DBT_DEVNODES_CHANGED = 0x0007;
        private const int DBT_DEVTYP_DEVICEINTERFACE = 0x00000005;
        private const int DEVICE_NOTIFY_WINDOW_HANDLE = 0x00000000;

        private const int DBCC_NAME_OFFSET = 28;

        const uint WM_QUIT = 0x0012;

        private string _targetVid => _targetVid_cached ??= _targetVidPid.TARGET_VID.Trim().ToUpperInvariant().Replace("0X", "");
        private string _targetPid => _targetPid_cached ??= _targetVidPid.TARGET_PID.Trim().ToUpperInvariant().Replace("0X", "");
        private string? _targetVid_cached;
        private string? _targetPid_cached;

        private const long INVALID_HANDLE_VALUE = -1;


        public event Action<string, LogType> Callback;


        internal void LogOk(string value) => Log(value, LogType.Ok);
        internal void LogWarn(string value) => Log(value, LogType.Warn);
        internal void LogErr(string value) => Log(value, LogType.Err);

        private void Log(string value, LogType type)
        {
            try
            {
                Callback?.Invoke(value, type);
            }
            catch
            {
                Console.WriteLine(value);
                Debug.WriteLine(value);
            }
        }


        public DeviceNotificationListener(TargetVidPid target, DevineInterface iInterface)
        {
            _targetVidPid = target ?? throw new ArgumentNullException(nameof(target));
            _devineInterface = iInterface ?? throw new ArgumentNullException(nameof(iInterface));


            try
            {
                _devicePreviouslyPresent = IsDevicePresentByVidPid(_targetVidPid);
                LogOk($"Device initially present: {_devicePreviouslyPresent}");
            }
            catch (Exception ex)
            {
                LogErr($"[INIT] Presence check failed: {ex.Message}");
                _devicePreviouslyPresent = false;
            }


            _messageThread = new Thread(MessageLoopThread)
            {
                IsBackground = true,
                Name = "DeviceNotificationThread"
            };
            _messageThread.Start();

            _pollThread = new Thread(PollLoop)
            {
                IsBackground = true,
                Name = "DevicePollThread"
            };
            _pollThread.Start();

            _windowReady.WaitOne();
        }

        private void MessageLoopThread()
        {
            try
            {
                _wndProcDelegate = WndProc;
                _wndProcHandle = GCHandle.Alloc(_wndProcDelegate!);

                var hInstance = GetModuleHandle(null);

                _className = "HiddenDeviceListenerWindow_" + Guid.NewGuid().ToString("N");

                var wc = new WNDCLASSEX
                {
                    cbSize = (uint)Marshal.SizeOf(typeof(WNDCLASSEX)),
                    style = 0,
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate!),
                    cbClsExtra = 0,
                    cbWndExtra = 0,
                    hInstance = hInstance,
                    hIcon = IntPtr.Zero,
                    hCursor = IntPtr.Zero,
                    hbrBackground = IntPtr.Zero,
                    lpszMenuName = null,
                    lpszClassName = _className,
                    hIconSm = IntPtr.Zero
                };

                var atom = RegisterClassEx(ref wc);
                if (atom == 0)
                {
                    LogErr($"RegisterClassEx failed: {Marshal.GetLastWin32Error()}");
                    _windowReady.Set();
                    return;
                }

                _hwnd = CreateWindowEx(
                    0,
                    wc.lpszClassName,
                    string.Empty,
                    0,
                    0, 0, 0, 0,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    hInstance,
                    IntPtr.Zero);

                if (_hwnd == IntPtr.Zero)
                {
                    var err = Marshal.GetHRForLastWin32Error();
                    LogErr($"CreateWindowEx failed: {err} -> {GetErrorMessage(err)}");
                    _windowReady.Set();
                    UnregisterClass(wc.lpszClassName, hInstance);
                    return;
                }

                _messageThreadId = GetCurrentThreadId();
                RegisterForDeviceNotifications(_hwnd);

                LogOk("Listener started. hwnd=" + _hwnd);
                _windowReady.Set();

                int res;

                while ((res = GetMessage(out var msg, IntPtr.Zero, 0, 0)) != 0)
                {
                    if (res == -1)
                    {
                        LogErr("GetMessage returned -1.");
                        return;
                    }

                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }

                LogOk("Message loop exited.");
            }
            catch (Exception ex)
            {
                LogErr("MessageLoopThread: " + ex.Message);
            }
            finally
            {
                try
                {
                    foreach (var h in _notifHandles)
                    {
                        if (h != IntPtr.Zero)
                            UnregisterDeviceNotification(h);
                    }

                    _notifHandles.Clear();
                    if (_hwnd != IntPtr.Zero)
                    {
                        DestroyWindow(_hwnd);
                        _hwnd = IntPtr.Zero;
                    }

                    var hInst = GetModuleHandle(null);
                    if (!string.IsNullOrEmpty(_className))
                    {
                        UnregisterClass(_className, hInst);
                    }
                }
                catch (Exception ex)
                {
                    LogErr("cleanup: " + ex.Message);
                }

                if (_wndProcHandle is { IsAllocated: true })
                    _wndProcHandle.Value.Free();
            }
        }

        private void PollLoop()
        {
            try
            {
                while (!_pollStop)
                {
                    Thread.Sleep(2500);
                    bool present;

                    try
                    {
                        present = IsDevicePresentByVidPid(_targetVidPid);
                    }
                    catch (Exception ex)
                    {
                        LogErr("Presence check failed: " + ex.Message);
                        present = _devicePreviouslyPresent;
                    }

                    if (present && !_devicePreviouslyPresent)
                    {
                        LogOk("Detected CONNECT via polling.");
                        _devicePreviouslyPresent = true;
                        DeviceMatchedConnected?.Invoke();
                    }
                    else if (!present && _devicePreviouslyPresent)
                    {
                        LogOk("Detected DISCONNECT via polling.");
                        _devicePreviouslyPresent = false;
                        DeviceMatchedDisconnected?.Invoke();
                    }
                }
            }
            catch (ThreadAbortException)
            {
            }
            catch (Exception ex)
            {
                LogErr("Exception: " + ex.Message);
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
            var dbi = new DEV_BROADCAST_DEVICEINTERFACE
            {
                dbcc_size = Marshal.SizeOf(typeof(DEV_BROADCAST_DEVICEINTERFACE)),
                dbcc_devicetype = DBT_DEVTYP_DEVICEINTERFACE,
                dbcc_reserved = 0,
                dbcc_classguid = guid
            };

            var buffer = IntPtr.Zero;
            try
            {
                buffer = Marshal.AllocHGlobal(dbi.dbcc_size);
                Marshal.StructureToPtr(dbi, buffer, false);

                var notif = RegisterDeviceNotification(hwnd, buffer, DEVICE_NOTIFY_WINDOW_HANDLE);
                if (notif == IntPtr.Zero)
                {
                    var err = Marshal.GetLastWin32Error();
                    LogErr($"RegisterDeviceNotification for {guid} failed: {err} -> {GetErrorMessage(err)}");
                }
                else
                {
                    _notifHandles.Add(notif);
                    LogOk($"Registered device notification for {guid} -> handle {notif}");
                }
            }
            finally
            {
                if (buffer != IntPtr.Zero)
                    Marshal.FreeHGlobal(buffer);
            }
        }

        private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (msg == WM_DEVICECHANGE)
                {
                    var eventType = (int)wParam;
                    LogOk($"wParam=0x{eventType:X} lParam={(lParam == IntPtr.Zero ? "NULL" : lParam.ToString())}");

                    switch (eventType)
                    {
                        case DBT_DEVICEARRIVAL or DBT_DEVICEREMOVECOMPLETE or DBT_DEVICEREMOVEPENDING when lParam == IntPtr.Zero:
                            LogWarn("Arrival/Remove event with null lParam.");
                            RescanPresenceAndFireIfNeeded();
                            break;
                        case DBT_DEVICEARRIVAL or DBT_DEVICEREMOVECOMPLETE or DBT_DEVICEREMOVEPENDING:
                        {
                            var hdr = Marshal.PtrToStructure<DEV_BROADCAST_HDR>(lParam);
                            LogOk($"hdr.size={hdr.dbch_size} hdr.type={hdr.dbch_devicetype} reserved={hdr.dbch_reserved}");

                            if (hdr.dbch_devicetype == DBT_DEVTYP_DEVICEINTERFACE)
                            {
                                var devicePath = TryGetDevicePath(lParam, hdr.dbch_size);
                                LogOk($"Decoded devicePath: '{devicePath ?? "<null>"}'");

                                if (!string.IsNullOrEmpty(devicePath) && MatchesTargetVidPid(devicePath))
                                {
                                    switch (eventType)
                                    {
                                        case DBT_DEVICEARRIVAL:
                                            LogOk("Matched CONNECT");
                                            _devicePreviouslyPresent = true;
                                            DeviceMatchedConnected?.Invoke();
                                            break;
                                        case DBT_DEVICEREMOVECOMPLETE or DBT_DEVICEREMOVEPENDING:
                                            LogOk("Matched DISCONNECT");
                                            _devicePreviouslyPresent = false;
                                            DeviceMatchedDisconnected?.Invoke();
                                            break;
                                    }
                                }
                                else
                                {
                                    LogOk("Device path empty or did not match target VID/PID.");
                                    RescanPresenceAndFireIfNeeded();
                                }
                            }
                            else
                            {
                                LogOk($"Unhandled dbch_devicetype={hdr.dbch_devicetype}");
                                if (eventType == DBT_DEVNODES_CHANGED)
                                    RescanPresenceAndFireIfNeeded();
                            }

                            break;
                        }
                        case DBT_DEVNODES_CHANGED:
                            LogOk("DBT_DEVNODES_CHANGED received -> rescan");
                            RescanPresenceAndFireIfNeeded();
                            break;
                        default:
                            LogOk($"Other WM_DEVICECHANGE code: 0x{eventType:X}");
                            break;
                    }
                }

            }
            catch (Exception ex)
            {
                LogErr("=WndProc: " + ex.Message);
            }

            return DefWindowProc(hWnd, msg, wParam, lParam);
        }

        private void RescanPresenceAndFireIfNeeded()
        {
            try
            {
                var nowPresent = IsDevicePresentByVidPid(_targetVidPid);
                LogOk($"PreviouslyPresent={_devicePreviouslyPresent} nowPresent={nowPresent}");

                if (nowPresent && !_devicePreviouslyPresent)
                {
                    LogOk("Detected device CONNECT via rescan.");
                    _devicePreviouslyPresent = true;
                    DeviceMatchedConnected?.Invoke();
                }
                else if (!nowPresent && _devicePreviouslyPresent)
                {
                    LogOk("Detected device DISCONNECT via rescan.");
                    _devicePreviouslyPresent = false;
                    DeviceMatchedDisconnected?.Invoke();
                }
            }
            catch (Exception ex)
            {
                LogErr("Exception: " + ex.Message);
            }
        }

        private string TryGetDevicePath(IntPtr lParam, int totalSize)
        {
            var namePtr = IntPtr.Add(lParam, DBCC_NAME_OFFSET);
            try
            {
                var uni = Marshal.PtrToStringUni(namePtr);
                if (!string.IsNullOrEmpty(uni))
                    return uni;
            }
            catch
            {
                //ignored
            }

            try
            {
                var ansi = Marshal.PtrToStringAnsi(namePtr);
                if (!string.IsNullOrEmpty(ansi))
                    return ansi;
            }
            catch
            {
                // ignored
            }

            try
            {
                var hdrSize = totalSize;
                if (hdrSize <= 0) hdrSize = 512;
                var rawLen = Math.Max(0, hdrSize - DBCC_NAME_OFFSET);
                if (rawLen > 4096) rawLen = 4096;

                var raw = new byte[rawLen];
                Marshal.Copy(namePtr, raw, 0, rawLen);

                try
                {
                    var maybeutf16 = Encoding.Unicode.GetString(raw);
                    var idx = maybeutf16.IndexOf('\0');
                    if (idx >= 0) maybeutf16 = maybeutf16.Substring(0, idx);
                    if (!string.IsNullOrEmpty(maybeutf16)) return maybeutf16;
                }
                catch
                {
                    //ignored
                }

                try
                {
                    var ansiEnd = 0;
                    while (ansiEnd < rawLen && raw[ansiEnd] != 0) ansiEnd++;

                    var maybeAnsi = Encoding.Default.GetString(raw, 0, ansiEnd);
                    if (!string.IsNullOrEmpty(maybeAnsi)) return maybeAnsi;
                }
                catch
                {
                    // ignored
                }
            }
            catch
            {
                //ignored
            }

            return null;
        }

        private bool MatchesTargetVidPid(string devicePath)
        {
            var upper = devicePath.ToUpperInvariant();
            return upper.Contains($"VID_{_targetVid}") && upper.Contains($"PID_{_targetPid}");
        }

        private bool IsDevicePresentByVidPid(TargetVidPid t)
        {
            var hDevInfo = SetupDiGetClassDevs(IntPtr.Zero, null, IntPtr.Zero, DIGCF_PRESENT | DIGCF_ALLCLASSES);
            if (hDevInfo == IntPtr.Zero || hDevInfo.ToInt64() == INVALID_HANDLE_VALUE)
            {
                var err = Marshal.GetLastWin32Error();
                LogErr($"SetupDiGetClassDevs failed: {err} -> {GetErrorMessage(err)}");
                return false;
            }

            try
            {
                var devInfoData = new SP_DEVINFO_DATA()
                {
                    cbSize = (uint)Marshal.SizeOf(typeof(SP_DEVINFO_DATA))
                };

                uint index = 0;
                while (SetupDiEnumDeviceInfo(hDevInfo, index, ref devInfoData))
                {
                    index++;
                    try
                    {
                        var sb = new StringBuilder(512);
                        var ok = SetupDiGetDeviceInstanceId(hDevInfo, ref devInfoData, sb, sb.Capacity,
                            out var required);
                        var instanceId = ok ? sb.ToString() : string.Empty;

                        if (!string.IsNullOrEmpty(instanceId))
                        {
                            if (MatchesTargetVidPid(instanceId))
                            {
                                return true;
                            }
                        }
                    }
                    catch
                    {
                        //ignored
                    }


                    try
                    {
                        var property = SPDRP_HARDWAREID;
                        var ok2 = SetupDiGetDeviceRegistryProperty(hDevInfo, ref devInfoData, property,
                            out var dataType, null, 0, out var requiredSize);

                        if (!ok2 && requiredSize > 0)
                        {
                            var buffer = new byte[requiredSize];
                            var ok3 = SetupDiGetDeviceRegistryProperty(hDevInfo, ref devInfoData, property,
                                out dataType, buffer, requiredSize, out requiredSize);
                            if (ok3)
                            {
                                var hwids = Encoding.Unicode.GetString(buffer);
                                if (!string.IsNullOrEmpty(hwids))
                                    hwids = Encoding.Default.GetString(buffer);

                                if (MatchesTargetVidPid(hwids))
                                {
                                    return true;
                                }

                            }
                        }
                    }
                    catch
                    {
                        //ignored
                    }
                }

                return false;
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(hDevInfo);
            }
        }

        public void Dispose()
        {
            try
            {
                _pollStop = true;
                if (_pollThread != null && !_pollThread.Join(1000))
                {
                    try
                    {
                        _pollThread.Abort();
                    }
                    catch
                    {
                    }
                }

                if (_messageThreadId != 0)
                {
                    PostThreadMessage(_messageThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
                    if (_messageThread != null && !_messageThread.Join(3000))
                    {
                        LogWarn("Message thread did not exit in time.");
                    }
                }
            }
            catch (Exception ex)
            {
                LogErr("Dispose: " + ex.Message);
            }
        }

        private static string GetErrorMessage(int errorCode)
        {
            var sb = new StringBuilder(512);
            var size = FormatMessage(FORMAT_MESSAGE_FROM_SYSTEM | FORMAT_MESSAGE_IGNORE_INSERTS, IntPtr.Zero, (uint)errorCode, 0, sb, sb.Capacity, IntPtr.Zero);
            if (size == 0) return $"Unknown error {errorCode}";
            return sb.ToString().Trim();
        }

        private const uint FORMAT_MESSAGE_FROM_SYSTEM = 0x00001000;
        private const uint FORMAT_MESSAGE_IGNORE_INSERTS = 0x00000200;

        [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
        private static extern int FormatMessage(uint dwFlags, IntPtr lpSource, uint dwMessageId, uint dwLanguageId, [Out] StringBuilder lpBuffer, int nSize, IntPtr Arguments);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern ushort RegisterClassEx([In] ref WNDCLASSEX lpwcx);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool UnregisterClass(string lpClassName, IntPtr hInstance);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CreateWindowEx(
            int dwExStyle,
            string lpClassName,
            string lpWindowName,
            int dwStyle,
            int x, int y, int nWidth, int nHeight,
            IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage([In] ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage([In] ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern IntPtr DefWindowProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr RegisterDeviceNotification(IntPtr hRecipient, IntPtr NotificationFilter,
            uint Flags);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnregisterDeviceNotification(IntPtr Handle);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool PostThreadMessage(uint idThread, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint GetCurrentThreadId();

        [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct WNDCLASSEX
        {
            public uint cbSize;
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            [MarshalAs(UnmanagedType.LPTStr)] public string lpszMenuName;
            [MarshalAs(UnmanagedType.LPTStr)] public string lpszClassName;
            public IntPtr hIconSm;
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
        private struct POINT { public int x; public int y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct DEV_BROADCAST_HDR
        {
            public int dbch_size;
            public int dbch_devicetype;
            public int dbch_reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DEV_BROADCAST_DEVICEINTERFACE
        {
            public int dbcc_size;
            public int dbcc_devicetype;
            public int dbcc_reserved;
            public Guid dbcc_classguid;
        }

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        private const uint DIGCF_PRESENT = 0x00000002;
        private const uint DIGCF_ALLCLASSES = 0x00000004;
        private const uint SPDRP_HARDWAREID = 0x00000001;

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVINFO_DATA
        {
            public uint cbSize;
            public Guid ClassGuid;
            public uint DevInst;
            public IntPtr Reserved;
        }

        [DllImport("setupapi.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevs(IntPtr ClassGuid, string? Enumerator, IntPtr hwndParent, uint Flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInfo(IntPtr DeviceInfoSet, uint MemberIndex, ref SP_DEVINFO_DATA DeviceInfoData);

        [DllImport("setupapi.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool SetupDiGetDeviceInstanceId(IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData, StringBuilder DeviceInstanceId, int DeviceInstanceIdSize, out int RequiredSize);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiGetDeviceRegistryProperty(IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData, uint Property, out uint PropertyRegDataType, byte[]? PropertyBuffer, uint PropertyBufferSize, out uint RequiredSize);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);
    }


}
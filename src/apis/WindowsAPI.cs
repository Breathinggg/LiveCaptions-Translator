using System.Runtime.InteropServices;

namespace LiveCaptionsTranslator.apis
{
    public static class WindowsAPI
    {
        public const int GWL_EXSTYLE = -20;

        public const int WS_EX_TRANSPARENT = 0x00000020;
        public const int WS_EX_TOOLWINDOW = 0x00000080;

        public const int SW_MINIMIZE = 6;
        public const int SW_RESTORE = 9;

        public const int WM_GETICON = 0x007F;
        public const int ICON_SMALL2 = 2;
        public const int GCLP_HICONSM = -34;
        public const int GCLP_HICON = -14;

        public const int DWMWA_CLOAKED = 14;

        public delegate bool EnumWindowsProc(nint hWnd, nint lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool ShowWindow(nint hWnd, int nCmdShow);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool IsWindowVisible(nint hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern nint GetWindow(nint hWnd, uint uCmd);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern int GetWindowTextLength(nint hWnd);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern int GetWindowText(nint hWnd, System.Text.StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern int GetClassName(nint hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern nint SendMessageTimeout(
            nint hWnd, int msg, nint wParam, nint lParam, int flags, int timeout, out nint result);

        [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW", SetLastError = true)]
        public static extern nint GetClassLongPtr(nint hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool DestroyIcon(nint hIcon);

        [DllImport("dwmapi.dll")]
        public static extern int DwmGetWindowAttribute(nint hWnd, int attribute, out int value, int size);

        public const uint SHGFI_ICON = 0x000000100;
        public const uint SHGFI_SMALLICON = 0x000000001;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern nint SHGetFileInfo(
            string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

        public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern nint OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool QueryFullProcessImageName(
            nint hProcess, int flags, System.Text.StringBuilder exeName, ref int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(nint hObject);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool EnumChildWindows(nint hWndParent, EnumWindowsProc lpEnumFunc, nint lParam);

        public const uint TH32CS_SNAPPROCESS = 0x00000002;

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern nint CreateToolhelp32Snapshot(uint flags, uint processId);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool Process32FirstW(nint snapshot, ref PROCESSENTRY32W entry);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool Process32NextW(nint snapshot, ref PROCESSENTRY32W entry);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int GetWindowLong(nint hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int SetWindowLong(nint hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int GetWindowThreadProcessId(nint hWnd, out int lpdwProcessId);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetForegroundWindow(nint hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool MoveWindow(
            nint hWnd,
            int X,
            int Y,
            int nWidth,
            int nHeight,
            bool bRepaint
        );

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool GetWindowRect(nint hWnd, out RECT lpRect);
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SHFILEINFO
    {
        public nint hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public nint th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }
}

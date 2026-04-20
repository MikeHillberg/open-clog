using Microsoft.UI.Xaml;
using System;
using System.Runtime.InteropServices;
using WinRT.Interop;

namespace open_clog;

public sealed partial class JsonDetailWindow : Window
{
    public JsonDetailWindow(string json)
    {
        this.InitializeComponent();
        JsonText.Text = json;
    }

    public void ActivateAndForeground()
    {
        Activate();
        var hwnd = WindowNative.GetWindowHandle(this);
        SetForegroundWindow(hwnd);
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}

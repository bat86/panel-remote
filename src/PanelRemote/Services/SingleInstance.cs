using System;
using System.Threading;
using PanelRemote.Interop;

namespace PanelRemote.Services;

public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\PanelRemote.SingleInstance.v1";
    private const string ShowMessageName = "PanelRemote.ShowWindow.v1";

    private readonly Mutex _mutex;
    private readonly bool _ownsMutex;

    public SingleInstance()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out _ownsMutex);
        IsFirst = _ownsMutex;
    }

    public bool IsFirst { get; }

    public static uint ShowMessage { get; } = NativeMethods.RegisterWindowMessage(ShowMessageName);

    public static void NotifyExisting()
    {
        var message = ShowMessage;
        if (message == 0)
        {
            return;
        }

        // Первый экземпляр мог ещё создавать окно — повторяем широковещательное сообщение.
        for (var attempt = 0; attempt < 6; attempt++)
        {
            NativeMethods.PostMessage(
                NativeMethods.HWND_BROADCAST,
                message,
                IntPtr.Zero,
                IntPtr.Zero);
            Thread.Sleep(150);
        }
    }

    public void Dispose()
    {
        try
        {
            if (_ownsMutex)
            {
                _mutex.ReleaseMutex();
            }
        }
        catch
        {
            // игнорируем
        }

        _mutex.Dispose();
    }
}

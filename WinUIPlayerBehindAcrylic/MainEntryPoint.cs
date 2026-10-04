using System;
using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Utility.Log;

namespace WinUIPlayerBehindAcrylic;

internal static class MainEntryPoint
{
    [STAThread]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Main(params string[] args)
    {
        Logger.UseConsoleLog(true);
        Logger.LogWriteLine("Starting up WinUI...");

        Application.Start(_ =>
        {
            DispatcherQueue dispatcherQueue = DispatcherQueue.GetForCurrentThread();

            DispatcherQueueSynchronizationContext context = new(dispatcherQueue);
            SynchronizationContext.SetSynchronizationContext(context);

            // ReSharper disable once ObjectCreationAsStatement
            new App
            {
                HighContrastAdjustment = ApplicationHighContrastAdjustment.None
            };
        });
    }
}

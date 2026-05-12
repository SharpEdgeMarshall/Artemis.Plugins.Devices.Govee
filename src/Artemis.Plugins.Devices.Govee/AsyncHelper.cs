using System;
using System.Threading;
using System.Threading.Tasks;

namespace Artemis.Plugins.Devices.Govee;

/// <summary>
/// Helper to run async methods synchronously, used in RGB.NET's synchronous LoadDevices call.
/// </summary>
internal static class AsyncHelper
{
    private static readonly TaskFactory TaskFactory =
        new(CancellationToken.None, TaskCreationOptions.None, TaskContinuationOptions.None, TaskScheduler.Default);

    public static TResult RunSync<TResult>(Func<Task<TResult>> func) =>
        TaskFactory.StartNew(func).Unwrap().GetAwaiter().GetResult();

    public static void RunSync(Func<Task> func) =>
        TaskFactory.StartNew(func).Unwrap().GetAwaiter().GetResult();
}

using System;
using System.Threading.Tasks;
using Serilog;

namespace ErneyTranslateTool.Core;

/// <summary>
/// Helpers for fire-and-forget async invocation that swallow exceptions to a log.
/// </summary>
public static class TaskExtensions
{
    public static async void FireAndForgetSafeAsync(this Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Background task failed");
        }
    }
}

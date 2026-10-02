using System;
using System.Threading.Tasks;

internal sealed class ConfigurationLaunchResult
{
    internal bool Started;
    internal string Message;
}

internal static class ConfigurationLaunchWorkflow
{
    internal static async Task<ConfigurationLaunchResult> RunAsync(Func<Task<string>> configure, Action start)
    {
        // Configuration failure must propagate before the launch can run.
        await configure();
        try
        {
            await Task.Run(start);
            return new ConfigurationLaunchResult { Started = true, Message = "配置完成，已重新打开 Codex。请新建任务使用更新后的配置。" };
        }
        catch (Exception error)
        {
            return new ConfigurationLaunchResult { Started = false, Message = "配置已保存，但 Codex 重新启动失败：" + error.Message + "。可点击“打开 Codex”重试，无需重复配置。" };
        }
    }
}

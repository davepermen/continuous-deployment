using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace ContinuousDeployment
{
    static class AsyncProcessHelper
    {
        public static Task RunAsync(this Process process)
        {
            var tcs = new TaskCompletionSource<object>();
            process.EnableRaisingEvents = true;
            process.Exited += (s, e) => tcs.TrySetResult(null);
            // not sure on best way to handle false being returned
            if (!process.Start()) tcs.SetException(new Exception("Failed to start process."));
            return tcs.Task;
        }

        public static async Task<string> RunAsync(string command, string arguments = null, string workingDirectory = null)
        {
            var p = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = command,
                    WorkingDirectory = workingDirectory,
                    Arguments = arguments,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true
                }
            };
            await p.RunAsync();
            return await p.StandardOutput.ReadToEndAsync();
        }
    }
}

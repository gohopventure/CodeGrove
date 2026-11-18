using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Text.Json;
using Codegrove.CSharpRunner;

// Nothing to do at start-up: the worker calls the exports below.
return;

/// <summary>Called from web/public/csharp-runner/worker.js. JSON in, JSON out.</summary>
[SupportedOSPlatform("browser")]
public static partial class RunnerExports
{
    [JSExport]
    public static async Task<string> Run(string requestJson)
    {
        var request = JsonSerializer.Deserialize(requestJson, RunnerJson.Default.RunRequest)
            ?? throw new ArgumentException("Empty run request.");
        var result = await CSharpCodeRunner.RunAsync(request);
        return JsonSerializer.Serialize(result, RunnerJson.Default.RunResult);
    }

    [JSExport]
    public static Task WarmUp() => CSharpCodeRunner.WarmUpAsync();
}

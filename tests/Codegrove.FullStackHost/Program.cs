using System.Text.Json;
using Codegrove.CSharpRunner;

// One JSON message per line on stdin, one answer per line on stdout:
//   { "op": "start", "run": RunRequest }      → RunResult  (KeepServer is set for you)
//   { "op": "send", "live": LiveRequest }     → LiveResponse
// Learner programs print through Console, which the runner captures and swaps (it also empties Console.In
// after every run), so the host keeps its own streams.
var stdin = new StreamReader(Console.OpenStandardInput());
var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };

while (await stdin.ReadLineAsync() is { } line)
{
    if (string.IsNullOrWhiteSpace(line)) continue;
    using var message = JsonDocument.Parse(line);
    var op = message.RootElement.GetProperty("op").GetString();
    string answer;
    if (op == "start")
    {
        var run = message.RootElement.GetProperty("run").Deserialize(RunnerJson.Default.RunRequest)!;
        var result = await CSharpCodeRunner.RunAsync(run with { KeepServer = true });
        answer = JsonSerializer.Serialize(result, RunnerJson.Default.RunResult);
    }
    else if (op == "send")
    {
        var live = message.RootElement.GetProperty("live").Deserialize(RunnerJson.Default.LiveRequest)!;
        answer = JsonSerializer.Serialize(await CSharpCodeRunner.SendToLiveServerAsync(live), RunnerJson.Default.LiveResponse);
    }
    else
    {
        answer = JsonSerializer.Serialize(new { error = $"unknown op '{op}'" });
    }
    stdout.WriteLine(answer);
}

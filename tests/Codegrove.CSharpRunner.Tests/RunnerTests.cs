using System.Text.Json;
using Codegrove.CSharpRunner;

// The runner swaps Console.Out while a program runs, which is process-wide: run tests one at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Codegrove.CSharpRunner.Tests;

public class RunnerTests
{
    private static Task<RunResult> Run(string program, params TestSpec[] tests) =>
        CSharpCodeRunner.RunAsync(new RunRequest(new() { ["Program.cs"] = program }, [.. tests]));

    private static TestSpec Test(int id, string kind, object spec) =>
        new(id, $"test {id}", kind, JsonSerializer.SerializeToElement(spec), $"fail {id}");

    [Fact]
    public async Task Runs_a_program_and_captures_its_output()
    {
        var r = await Run("""
            var name = "Pixel";
            Console.WriteLine($"Hello, {name}!");
            Console.Write("no newline");
            """);

        Assert.True(r.Compiled);
        Assert.Equal(StopReason.None, r.Stopped);
        Assert.Equal(["Hello, Pixel!", "no newline"], r.Console.Select(l => l.Text));
        Assert.All(r.Console, l => Assert.Equal("log", l.Level));
    }

    [Fact]
    public async Task Compile_errors_name_the_file_and_line_in_plain_words()
    {
        var r = await Run("""
            Console.WriteLine("one");
            Console.WriteLine("two")
            """);

        Assert.False(r.Compiled);
        var e = Assert.Single(r.Diagnostics, d => d.Severity == "error");
        Assert.Equal(("Program.cs", 2, "CS1002"), (e.File, e.Line, e.Code));
        Assert.StartsWith("Error in Program.cs, line 2: C# expected a semicolon", r.Console[0].Text);
        Assert.Equal("error", r.Console[0].Level);
    }

    [Fact]
    public async Task Assignment_in_a_condition_explains_double_equals()
    {
        var r = await Run("int score = 5;\nif (score = 10) { Console.WriteLine(\"ten\"); }");
        var e = Assert.Single(r.Diagnostics, d => d.Severity == "error");
        Assert.Equal(("CS0029", 2), (e.Code, e.Line));
        Assert.Contains("use == (two equals signs)", e.Friendly);
    }

    [Fact]
    public async Task Network_apis_are_not_available()
    {
        // (File APIs live in System.Runtime itself; in the browser they only see an in-memory file system.)
        var r = await Run("""
            var client = new System.Net.Http.HttpClient();
            """);

        Assert.False(r.Compiled);
        Assert.Equal("CS0234", Assert.Single(r.Diagnostics).Code);
    }

    [Fact]
    public async Task Runtime_errors_point_at_the_learners_line_even_after_the_guard_rewrite()
    {
        var r = await Run("""
            int[] scores = { 1, 2, 3 };
            for (int i = 0; i < 3; i++)
                Console.WriteLine(scores[i]);
            while (false) { }
            Console.WriteLine(scores[3]);
            """);

        Assert.True(r.Compiled);
        Assert.Equal(StopReason.Exception, r.Stopped);
        Assert.Equal(["1", "2", "3"], r.Console.Where(l => l.Level == "log").Select(l => l.Text));
        Assert.StartsWith("IndexOutOfRangeException in Program.cs, line 5: The program asked for an item past the end", r.Console[^1].Text);
    }

    [Fact]
    public void The_guard_rewrite_keeps_every_line_where_it_was()
    {
        const string source = """
            // A comment first
            int total = 0;

            for (int i = 0; i < 3; i++)
                total += i;
            if (total > 1) Console.WriteLine("big");
            else
                Console.WriteLine("small");
            switch (total)
            {
                case 3:
                    Console.WriteLine("three");
                    break;
            }
            int Twice(int n) =>
                n * 2;
            class Box { public int Size { get { return 1; } } }
            """;
        var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(source, path: "Program.cs");
        var rewritten = new GuardRewriter("Program.cs").Visit(tree.GetRoot()).ToFullString();

        Assert.Equal(source.Split('\n').Length, rewritten.Split('\n').Length);
        // Each original line's code is still on its line: strip what the guard adds (calls, braces, try/finally,
        // `=>` turned into `return`) and spacing, and the learner's code is left.
        static string Core(string line) => System.Text.RegularExpressions.Regex.Replace(
            System.Text.RegularExpressions.Regex.Replace(line, @"global::__CgGuard\.\w+\([^)]*\);", ""),
            @"\s|[{}]|\btry\b|\bfinally\b|=>|\breturn\b", "");
        var before = source.Split('\n');
        var after = rewritten.Split('\n');
        for (var i = 0; i < before.Length; i++)
            Assert.Equal(Core(before[i]), Core(after[i]));
    }

    [Fact]
    public async Task Runtime_errors_in_unbraced_bodies_and_after_blank_lines_report_their_own_line()
    {
        var r = await Run("""
            int zero = 0;

            // about to divide
            if (zero == 0)
                Console.WriteLine(10 / zero);
            """);
        Assert.StartsWith("DivideByZeroException in Program.cs, line 5:", r.Console[^1].Text);
    }

    [Fact]
    public async Task An_endless_loop_is_stopped_even_inside_a_catch_all()
    {
        var r = await Run("""
            Console.WriteLine("start");
            while (true)
            {
                try { while (true) { } } catch (Exception) { }
            }
            """, Test(1, "noErrors", new { }), Test(2, "consoleIncludes", new { text = "START" }));

        Assert.Equal(StopReason.TooManySteps, r.Stopped);
        Assert.Contains("loop steps", r.Console[^1].Text);
        Assert.False(r.Results[0].Passed);
        Assert.True(r.Results[1].Passed);
    }

    [Fact]
    public async Task Endless_recursion_is_stopped_for_block_and_expression_bodies()
    {
        var block = await Run("""
            int Count(int n) { return Count(n + 1); }
            Console.WriteLine(Count(0));
            """);
        var arrow = await Run("""
            Console.WriteLine(Pets.Forever(0));
            static class Pets { public static int Forever(int n) => Forever(n + 1); }
            """);

        Assert.Equal(StopReason.TooDeep, block.Stopped);
        Assert.Equal(StopReason.TooDeep, arrow.Stopped);
    }

    [Fact]
    public async Task Guarded_methods_still_return_values_and_handle_void_and_async()
    {
        var r = await Run("""
            Console.WriteLine(Square(4));
            Say("hi");
            await Later();
            Console.WriteLine(Fact(5));
            int Square(int n) => n * n;
            void Say(string s) => Console.WriteLine(s);
            async Task Later() => await Task.Delay(1);
            static int Fact(int n)
            {
                if (n <= 1) return 1;
                return n * Fact(n - 1);
            }
            """);

        Assert.Equal(StopReason.None, r.Stopped);
        Assert.Equal(["16", "hi", "120"], r.Console.Select(l => l.Text));
    }

    [Fact]
    public async Task Expression_tests_can_use_the_learners_classes()
    {
        var r = await Run("""
            var rex = new Pet("Rex");
            Console.WriteLine(rex.Greet());

            class Pet
            {
                public string Name { get; }
                public Pet(string name) { Name = name; }
                public string Greet() => $"Woof, I'm {Name}";
            }
            """,
            Test(1, "csharpExpression", new { expression = "new Pet(\"Bo\").Greet()", equals = "Woof, I'm Bo" }),
            Test(2, "csharpExpression", new { expression = "new Cat().Name", equals = "Tom" }), // no Cat class yet
            Test(3, "csharpExpression", new { expression = "new Pet(\"A\").Name.Length", equals = 1 }),
            Test(4, "csharpExpression", new { expression = "new Pet(\"A\").Name", equals = "B" }),
            Test(5, "csharpExpression", new { expression = "new[] { 1, 2 }.Select(x => x * 2)", equals = new[] { 2, 4 } }));

        Assert.Equal([true, false, true, false, true], r.Results.Select(x => x.Passed));
        Assert.Equal("fail 2", r.Results[1].Message);
        Assert.Equal("fail 4 (It was \"A\".)", r.Results[3].Message);
    }

    [Fact]
    public async Task Async_programs_are_awaited_not_blocked_on()
    {
        // In the browser, blocking on a task throws; the runner must call the async method itself.
        var topLevel = await Run("await Task.Delay(5);\nConsole.WriteLine(\"done\");");
        var classic = await CSharpCodeRunner.RunAsync(new RunRequest(new()
        {
            ["Program.cs"] = "class Program { static async Task Main() { await Task.Yield(); Console.WriteLine(\"main\"); } }",
        }));
        Assert.Equal(["done"], topLevel.Console.Select(l => l.Text));
        Assert.Equal(["main"], classic.Console.Select(l => l.Text));
    }

    [Fact]
    public async Task Static_state_starts_fresh_on_every_run()
    {
        const string program = """
            Counter.Count++;
            Console.WriteLine(Counter.Count);
            static class Counter { public static int Count; }
            """;
        Assert.Equal("1", (await Run(program)).Console[0].Text);
        Assert.Equal("1", (await Run(program)).Console[0].Text);
    }

    [Fact]
    public async Task Output_tests_and_source_tests()
    {
        var r = await Run("""
            // foreach would be nice here
            for (int i = 1; i <= 2; i++) Console.WriteLine($"Line {i}  ");
            """,
            Test(1, "consoleEquals", new { lines = new[] { "Line 1", "Line 2" } }),
            Test(2, "consoleEquals", new { lines = new[] { "Line 1" } }),
            Test(3, "sourceMatches", new { pattern = @"\bfor\s*\(" }),
            Test(4, "sourceMatches", new { pattern = @"\bforeach\b" }),
            Test(5, "consoleIncludes", new { text = "line 2" }),
            Test(6, "consoleIncludes", new { text = "line 2", caseSensitive = true }));

        Assert.Equal([true, false, true, false, true, false], r.Results.Select(x => x.Passed));
    }

    [Fact]
    public async Task Compile_error_tests_match_the_code_and_line()
    {
        var r = await Run("int x = \"ten\";",
            Test(1, "compileError", new { code = "CS0029", line = 1 }),
            Test(2, "compileError", new { code = "CS1002" }),
            Test(3, "csharpExpression", new { expression = "1", equals = 1 }));

        Assert.Equal([true, false, false], r.Results.Select(x => x.Passed));
    }

    [Fact]
    public async Task ReadLine_reads_the_given_input()
    {
        var r = await CSharpCodeRunner.RunAsync(new RunRequest(
            new() { ["Program.cs"] = "var name = Console.ReadLine(); Console.WriteLine($\"Hi {name}\"); Console.WriteLine(Console.ReadLine() is null);" },
            Input: "Juni\n"));
        Assert.Equal(["Hi Juni", "True"], r.Console.Select(l => l.Text));
    }

    [Fact]
    public async Task Classes_can_live_in_their_own_files()
    {
        var r = await CSharpCodeRunner.RunAsync(new RunRequest(new()
        {
            ["Program.cs"] = "Console.WriteLine(new Robot().Beep());",
            ["Robot.cs"] = "class Robot\n{\n    public string Beep() => throw new InvalidOperationException(\"low battery\");\n}",
        }));
        Assert.StartsWith("InvalidOperationException in Robot.cs, line 3:", r.Console[^1].Text);
        Assert.EndsWith("(low battery)", r.Console[^1].Text);
    }

    [Fact]
    public async Task Huge_output_is_cut_off_without_failing()
    {
        var r = await Run("for (int i = 0; i < 100000; i++) Console.WriteLine(\"spam spam spam\");", Test(1, "noErrors", new { }));
        Assert.Equal(StopReason.TooMuchOutput, r.Stopped);
        Assert.True(r.Results[0].Passed);
        Assert.True(r.Console.Count <= 2001);
    }
}

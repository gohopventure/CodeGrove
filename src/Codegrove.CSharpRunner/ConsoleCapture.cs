using System.Text;

namespace Codegrove.CSharpRunner;

/// <summary>
/// Collects Console.Out and Console.Error as lines for the Console panel (in the order they were
/// written), plus the full standard output for tests. Stops recording after a limit, so a program
/// printing in a long loop can't use up the browser's memory.
/// </summary>
internal sealed class ConsoleCapture
{
    private const int MaxChars = 100_000;
    private const int MaxLines = 2_000;

    private readonly List<ConsoleLine> _lines;
    private readonly StringBuilder _output = new();
    private readonly LineWriter _out;
    private readonly LineWriter _error;
    private int _chars;

    public ConsoleCapture(List<ConsoleLine> lines)
    {
        _lines = lines;
        _out = new LineWriter(this, "log");
        _error = new LineWriter(this, "error");
    }

    public bool Truncated { get; private set; }
    public string Output => _output.ToString();
    public TextWriter Out => _out;
    public TextWriter Error => _error;

    /// <summary>Adds any unfinished line (output written with Console.Write and no newline).</summary>
    public void Flush()
    {
        _out.FlushLine();
        _error.FlushLine();
    }

    private bool Room()
    {
        if (Truncated) return false;
        if (++_chars > MaxChars || _lines.Count >= MaxLines)
        {
            Truncated = true;
            return false;
        }
        return true;
    }

    private sealed class LineWriter(ConsoleCapture owner, string level) : TextWriter
    {
        private readonly StringBuilder _line = new();

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            if (!owner.Room()) return;
            if (level == "log") owner._output.Append(value);
            if (value == '\n') FlushLine(force: true);
            else if (value != '\r') _line.Append(value);
        }

        public override void Write(string? value)
        {
            if (value is null) return;
            foreach (var c in value) Write(c);
        }

        public override void Write(char[] buffer, int index, int count)
        {
            for (var i = index; i < index + count; i++) Write(buffer[i]);
        }

        public void FlushLine(bool force = false)
        {
            if (_line.Length == 0 && !force) return;
            owner._lines.Add(new ConsoleLine(level, _line.ToString()));
            _line.Clear();
        }
    }
}

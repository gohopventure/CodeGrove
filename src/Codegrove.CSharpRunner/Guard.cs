using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Codegrove.CSharpRunner;

/// <summary>
/// Instrumentation compiled into every learner program:
/// <list type="bullet">
/// <item>Every loop body calls <c>Tick()</c> and every method body is wrapped in <c>Enter()</c>/<c>Exit()</c>, so an
/// endless loop or endless recursion ends with a friendly message instead of freezing the tab or overflowing the stack.</item>
/// <item>Every statement first calls <c>At(file, line)</c>, so a runtime error can say where it happened. (Stack traces
/// would need a PDB, and Roslyn can't write one in single-threaded WebAssembly.)</item>
/// </list>
/// The browser also stops the worker after a time limit; that's the backstop for anything the guard can't see
/// (one huge LINQ query, say). Restarting the worker costs a few seconds; the guard costs almost nothing.
/// </summary>
internal static class Guard
{
    public const long MaxSteps = 5_000_000;
    public const int MaxDepth = 1_000;

    /// <summary>
    /// Internal, so learner code can't see it by accident. Once tripped it stays tripped: a learner's
    /// <c>catch (Exception)</c> can't swallow the stop, because the very next tick throws again.
    /// </summary>
    public static readonly string Source = $$"""
        internal static class __CgGuard
        {
            public static long Steps;
            public static int Depth;
            public static string? Stopped;
            public static string? File;
            public static int Line;
            public static void Reset() { Steps = 0; Depth = 0; Stopped = null; File = null; Line = 0; }
            public static void At(string file, int line) { File = file; Line = line; }
            public static void Tick()
            {
                if (Stopped is not null || ++Steps > {{MaxSteps}}) Stop("steps");
            }
            public static void Enter()
            {
                if (Stopped is not null || ++Depth > {{MaxDepth}}) Stop("depth");
                if (++Steps > {{MaxSteps}}) Stop("steps");
            }
            public static void Exit() => Depth--;
            private static void Stop(string why)
            {
                Stopped ??= why;
                throw new __CgStopException();
            }
        }
        internal sealed class __CgStopException : System.Exception
        {
            public __CgStopException() : base("Codegrove stopped this program.") { }
        }
        """;
}

/// <summary>
/// Adds the guard calls to one file. Every inserted token goes on an existing line (no new line breaks, and a
/// statement's leading trivia moves onto the call put in front of it), so line numbers stay exactly as the
/// learner sees them in the editor.
/// </summary>
internal sealed class GuardRewriter(string file) : CSharpSyntaxRewriter
{
    private static StatementSyntax Call(string name, params ExpressionSyntax[] args) =>
        ExpressionStatement(InvocationExpression(
            MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                AliasQualifiedName(IdentifierName(Token(SyntaxKind.GlobalKeyword)), IdentifierName("__CgGuard")),
                IdentifierName(name)),
            ArgumentList(SeparatedList(args.Select(Argument)))));

    /// <summary>Line of a node in the ORIGINAL tree (rewritten nodes have no position).</summary>
    private static int LineOf(SyntaxNode original) => original.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    private StatementSyntax At(int line) =>
        Call("At", LiteralExpression(SyntaxKind.StringLiteralExpression, Literal(file)), LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(line)));

    /// <summary>Puts At(line) before each statement, taking over the statement's leading trivia.</summary>
    private SyntaxList<StatementSyntax> Located(SyntaxList<StatementSyntax> original, SyntaxList<StatementSyntax> rewritten)
    {
        var result = new List<StatementSyntax>(rewritten.Count * 2);
        for (var i = 0; i < rewritten.Count; i++)
        {
            var statement = rewritten[i];
            result.Add(At(LineOf(original[i])).WithLeadingTrivia(statement.GetLeadingTrivia()));
            result.Add(statement.WithLeadingTrivia(SyntaxTriviaList.Empty));
        }
        return List(result);
    }

    /// <summary>A body that isn't a block (<c>if (x) y++;</c>) becomes <c>{ At(line); y++; }</c>, on the same line.</summary>
    private StatementSyntax Tracked(StatementSyntax original, StatementSyntax rewritten) =>
        rewritten is BlockSyntax ? rewritten : Block(At(LineOf(original)), rewritten);

    /// <summary>Puts a Tick() first in a loop body (always a block after <see cref="Tracked"/>).</summary>
    private static StatementSyntax Ticked(StatementSyntax body) => body is BlockSyntax block
        ? block.WithStatements(block.Statements.Insert(0, Call("Tick")))
        : Block(Call("Tick"), body);

    public override SyntaxNode? VisitCompilationUnit(CompilationUnitSyntax node)
    {
        var n = (CompilationUnitSyntax)base.VisitCompilationUnit(node)!;
        // Top-level statements: one At(line) global statement before each.
        var members = new List<MemberDeclarationSyntax>(n.Members.Count * 2);
        for (var i = 0; i < n.Members.Count; i++)
        {
            if (n.Members[i] is GlobalStatementSyntax g && node.Members[i] is GlobalStatementSyntax original)
            {
                members.Add(GlobalStatement(At(LineOf(original.Statement))).WithLeadingTrivia(g.GetLeadingTrivia()));
                members.Add(g.WithLeadingTrivia(SyntaxTriviaList.Empty));
            }
            else
            {
                members.Add(n.Members[i]);
            }
        }
        return n.WithMembers(List(members));
    }

    public override SyntaxNode? VisitBlock(BlockSyntax node)
    {
        var n = (BlockSyntax)base.VisitBlock(node)!;
        return n.WithStatements(Located(node.Statements, n.Statements));
    }

    public override SyntaxNode? VisitSwitchSection(SwitchSectionSyntax node)
    {
        var n = (SwitchSectionSyntax)base.VisitSwitchSection(node)!;
        return n.WithStatements(Located(node.Statements, n.Statements));
    }

    public override SyntaxNode? VisitIfStatement(IfStatementSyntax node)
    {
        var n = (IfStatementSyntax)base.VisitIfStatement(node)!;
        return n.WithStatement(Tracked(node.Statement, n.Statement));
    }

    public override SyntaxNode? VisitElseClause(ElseClauseSyntax node)
    {
        var n = (ElseClauseSyntax)base.VisitElseClause(node)!;
        return n.Statement is IfStatementSyntax ? n : n.WithStatement(Tracked(node.Statement, n.Statement));
    }

    public override SyntaxNode? VisitWhileStatement(WhileStatementSyntax node)
    {
        var n = (WhileStatementSyntax)base.VisitWhileStatement(node)!;
        return n.WithStatement(Ticked(Tracked(node.Statement, n.Statement)));
    }

    public override SyntaxNode? VisitDoStatement(DoStatementSyntax node)
    {
        var n = (DoStatementSyntax)base.VisitDoStatement(node)!;
        return n.WithStatement(Ticked(Tracked(node.Statement, n.Statement)));
    }

    public override SyntaxNode? VisitForStatement(ForStatementSyntax node)
    {
        var n = (ForStatementSyntax)base.VisitForStatement(node)!;
        return n.WithStatement(Ticked(Tracked(node.Statement, n.Statement)));
    }

    public override SyntaxNode? VisitForEachStatement(ForEachStatementSyntax node)
    {
        var n = (ForEachStatementSyntax)base.VisitForEachStatement(node)!;
        return n.WithStatement(Ticked(Tracked(node.Statement, n.Statement)));
    }

    public override SyntaxNode? VisitForEachVariableStatement(ForEachVariableStatementSyntax node)
    {
        var n = (ForEachVariableStatementSyntax)base.VisitForEachVariableStatement(node)!;
        return n.WithStatement(Ticked(Tracked(node.Statement, n.Statement)));
    }

    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        var n = (MethodDeclarationSyntax)base.VisitMethodDeclaration(node)!;
        if (n.Body is { } body) return n.WithBody(Wrapped(body));
        if (n.ExpressionBody is { } arrow)
        {
            var block = FromArrow(arrow, n.SemicolonToken, ReturnsValue(n.ReturnType, n.Modifiers), LineOf(node.ExpressionBody!.Expression));
            return n.WithExpressionBody(null).WithSemicolonToken(default).WithBody(block);
        }
        return n; // abstract, extern or partial: nothing to guard
    }

    public override SyntaxNode? VisitLocalFunctionStatement(LocalFunctionStatementSyntax node)
    {
        var n = (LocalFunctionStatementSyntax)base.VisitLocalFunctionStatement(node)!;
        if (n.Body is { } body) return n.WithBody(Wrapped(body));
        if (n.ExpressionBody is { } arrow)
        {
            var block = FromArrow(arrow, n.SemicolonToken, ReturnsValue(n.ReturnType, n.Modifiers), LineOf(node.ExpressionBody!.Expression));
            return n.WithExpressionBody(null).WithSemicolonToken(default).WithBody(block);
        }
        return n;
    }

    /// <summary><c>{ body }</c> → <c>{ Enter(); try { body } finally { Exit(); } }</c>.</summary>
    private static BlockSyntax Wrapped(BlockSyntax body) =>
        body.WithStatements(List<StatementSyntax>([
            Call("Enter"),
            TryStatement(
                Block(body.Statements),
                default,
                FinallyClause(Block(Call("Exit")))),
        ]));

    /// <summary>
    /// <c>=> expr;</c> → <c>{ Enter(); try { At(line); return expr; } finally { Exit(); } }</c>. The arrow's and the
    /// semicolon's trivia move onto the new braces, so any line breaks stay where they were.
    /// </summary>
    private BlockSyntax FromArrow(ArrowExpressionClauseSyntax arrow, SyntaxToken semicolon, bool returnsValue, int line)
    {
        var expr = arrow.Expression;
        StatementSyntax statement = expr switch
        {
            // `=> throw new X()` becomes `throw new X();`, not `return throw …`.
            ThrowExpressionSyntax t => ThrowStatement(t.ThrowKeyword, t.Expression, Token(SyntaxKind.SemicolonToken)),
            _ when returnsValue => ReturnStatement(Token(SyntaxKind.ReturnKeyword).WithTrailingTrivia(Space), expr, Token(SyntaxKind.SemicolonToken)),
            _ => ExpressionStatement(expr),
        };
        var inner = Block(
            Call("Enter"),
            TryStatement(Block(At(line), statement), default, FinallyClause(Block(Call("Exit")))));
        return inner
            .WithOpenBraceToken(Token(arrow.ArrowToken.LeadingTrivia, SyntaxKind.OpenBraceToken, arrow.ArrowToken.TrailingTrivia))
            .WithCloseBraceToken(Token(semicolon.LeadingTrivia, SyntaxKind.CloseBraceToken, semicolon.TrailingTrivia));
    }

    /// <summary>
    /// Whether <c>=> expr</c> means <c>return expr;</c>. Not for void, and not for async methods returning a
    /// plain Task or ValueTask (there <c>=> await X</c> is a statement).
    /// </summary>
    private static bool ReturnsValue(TypeSyntax returnType, SyntaxTokenList modifiers)
    {
        if (returnType is PredefinedTypeSyntax p && p.Keyword.IsKind(SyntaxKind.VoidKeyword)) return false;
        if (modifiers.Any(SyntaxKind.AsyncKeyword))
        {
            var name = returnType switch
            {
                IdentifierNameSyntax i => i.Identifier.ValueText,
                QualifiedNameSyntax q when q.Right is IdentifierNameSyntax i => i.Identifier.ValueText,
                _ => null,
            };
            if (name is "Task" or "ValueTask") return false;
        }
        return true;
    }
}

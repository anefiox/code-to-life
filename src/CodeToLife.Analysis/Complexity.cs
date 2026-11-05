using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CodeToLife.Analysis;

/// <summary>
/// Cyclomatic complexity, counted the same way as PR Buddy's PRB013: 1 plus one per branch point
/// (if, case label, loop, catch, ?:, ??, &amp;&amp;, ||, switch-expression arm). Lambdas and local
/// functions count toward the member they're in.
/// </summary>
public static class Complexity
{
    public static int Of(SyntaxNode body)
    {
        var complexity = 1;
        foreach (var node in body.DescendantNodesAndSelf())
        {
            complexity += node switch
            {
                IfStatementSyntax or WhileStatementSyntax or DoStatementSyntax or ForStatementSyntax or ForEachStatementSyntax
                    or CatchClauseSyntax or ConditionalExpressionSyntax or SwitchExpressionArmSyntax => 1,
                CaseSwitchLabelSyntax or CasePatternSwitchLabelSyntax => 1,
                BinaryExpressionSyntax b when b.IsKind(SyntaxKind.LogicalAndExpression) || b.IsKind(SyntaxKind.LogicalOrExpression)
                                             || b.IsKind(SyntaxKind.CoalesceExpression) => 1,
                _ => 0,
            };
        }
        return complexity;
    }
}

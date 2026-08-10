using System.Linq;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace HooSharper.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UseHashSetAddResultAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "HOO1012";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "Use the result of HashSet.Add",
        "Use the result of HashSet.Add instead of calling Contains first",
        "HooSharper.CodeStyle",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "HashSet.Add reports whether the value was newly added, avoiding a separate lookup.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(static compilationContext =>
        {
            var hashSetDefinition = compilationContext.Compilation.GetTypeByMetadataName(
                "System.Collections.Generic.HashSet`1");
            var equalityComparerDefinition = compilationContext.Compilation.GetTypeByMetadataName(
                "System.Collections.Generic.IEqualityComparer`1");
            if (hashSetDefinition is null || equalityComparerDefinition is null)
            {
                return;
            }

            compilationContext.RegisterSyntaxNodeAction(
                nodeContext => AnalyzeIfStatement(nodeContext, hashSetDefinition, equalityComparerDefinition),
                SyntaxKind.IfStatement);
        });
    }

    private static void AnalyzeIfStatement(
        SyntaxNodeAnalysisContext context,
        INamedTypeSymbol hashSetDefinition,
        INamedTypeSymbol equalityComparerDefinition)
    {
        var ifStatement = (IfStatementSyntax)context.Node;
        if (ifStatement.Else is not null ||
            ifStatement.Statement is not BlockSyntax { Statements.Count: > 0 } block ||
            block.Statements[0] is not ExpressionStatementSyntax
            {
                Expression: InvocationExpressionSyntax addInvocation,
            } ||
            HasDirective(ifStatement) ||
            !TryGetNegatedContains(ifStatement.Condition, out var containsInvocation, out var containsMember) ||
            containsInvocation.ArgumentList.Arguments.Count != 1 ||
            addInvocation.Expression is not MemberAccessExpressionSyntax
            {
                RawKind: (int)SyntaxKind.SimpleMemberAccessExpression,
                Name.Identifier.ValueText: "Add",
            } addMember ||
            addInvocation.ArgumentList.Arguments.Count != 1)
        {
            return;
        }

        var containsValue = containsInvocation.ArgumentList.Arguments[0].Expression;
        var addValue = addInvocation.ArgumentList.Arguments[0].Expression;
        if (!SyntaxFactory.AreEquivalent(containsMember.Expression, addMember.Expression) ||
            !SyntaxFactory.AreEquivalent(containsValue, addValue))
        {
            return;
        }

        var containsOperation = context.SemanticModel.GetOperation(containsInvocation, context.CancellationToken) as IInvocationOperation;
        var addOperation = context.SemanticModel.GetOperation(addInvocation, context.CancellationToken) as IInvocationOperation;
        if (!IsHashSetMethod(containsOperation?.TargetMethod, hashSetDefinition, "Contains") ||
            !IsHashSetMethod(addOperation?.TargetMethod, hashSetDefinition, "Add"))
        {
            return;
        }

        var receiverOperation = containsOperation!.Instance;
        var valueOperation = containsOperation.Arguments[0].Value;
        if (!HasProvablyPureComparer(
                receiverOperation,
                ifStatement,
                context.SemanticModel,
                hashSetDefinition,
                equalityComparerDefinition) ||
            !IsCallbackStableOperation(receiverOperation) ||
            !IsCallbackStableOperation(valueOperation) ||

            receiverOperation?.Type is not INamedTypeSymbol namedReceiver ||
            !SymbolEqualityComparer.Default.Equals(namedReceiver.OriginalDefinition, hashSetDefinition))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, containsMember.Name.GetLocation()));
    }
    private static bool HasProvablyPureComparer(
        IOperation? receiver,
        IfStatementSyntax ifStatement,
        SemanticModel semanticModel,
        INamedTypeSymbol hashSetDefinition,
        INamedTypeSymbol equalityComparerDefinition)
    {
        receiver = Unwrap(receiver);
        if (receiver?.Type is not INamedTypeSymbol hashSetType ||
            hashSetType.TypeArguments.Length != 1 ||
            !IsProvablyPureEqualityType(hashSetType.TypeArguments[0]))
        {
            return false;
        }

        return receiver switch
        {
            ILocalReferenceOperation localReference =>
                IsNeverReassigned(localReference.Local, ifStatement, semanticModel) &&
                HasDefaultComparerInitializer(
                    localReference.Local,
                    semanticModel,
                    hashSetDefinition,
                    equalityComparerDefinition),
            IFieldReferenceOperation fieldReference when fieldReference.Field.IsReadOnly &&
                !fieldReference.Field.IsVolatile =>
                IsNeverReassigned(fieldReference.Field, semanticModel) &&
                HasDefaultComparerInitializer(
                    fieldReference.Field,
                    semanticModel,
                    hashSetDefinition,
                    equalityComparerDefinition),
            _ => false,
        };
    }

    private static bool HasDefaultComparerInitializer(
        ISymbol symbol,
        SemanticModel semanticModel,
        INamedTypeSymbol collectionDefinition,
        INamedTypeSymbol equalityComparerDefinition)
    {
        if (symbol.DeclaringSyntaxReferences.Length != 1 ||
            symbol.DeclaringSyntaxReferences[0].GetSyntax() is not VariableDeclaratorSyntax
            {
                Initializer.Value: var initializer,
            })
        {
            return false;
        }

        if (initializer.SyntaxTree != semanticModel.SyntaxTree ||
            semanticModel.GetOperation(initializer) is not IObjectCreationOperation creation ||
            creation.Type is not INamedTypeSymbol createdType ||
            !SymbolEqualityComparer.Default.Equals(createdType.OriginalDefinition, collectionDefinition))
        {
            return false;
        }

        foreach (var argument in creation.Arguments)
        {
            if (argument.Parameter?.Type is INamedTypeSymbol parameterType &&
                SymbolEqualityComparer.Default.Equals(
                    parameterType.OriginalDefinition,
                    equalityComparerDefinition) &&
                !(argument.Value.ConstantValue is { HasValue: true, Value: null }))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsNeverReassigned(
        ILocalSymbol local,
        IfStatementSyntax ifStatement,
        SemanticModel semanticModel)
    {
        var scope = ifStatement.FirstAncestorOrSelf<MemberDeclarationSyntax>();
        if (scope is null)
        {
            return false;
        }

        foreach (var identifier in scope.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            if (SymbolEqualityComparer.Default.Equals(
                    semanticModel.GetSymbolInfo(identifier).Symbol,
                    local) &&
                IsWrittenReference(identifier))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsNeverReassigned(IFieldSymbol field, SemanticModel semanticModel)
    {
        if (field.ContainingType.DeclaringSyntaxReferences.Length != 1 ||
            field.ContainingType.DeclaringSyntaxReferences[0].GetSyntax() is not TypeDeclarationSyntax typeDeclaration ||
            typeDeclaration.SyntaxTree != semanticModel.SyntaxTree)
        {
            return false;
        }

        foreach (var identifier in typeDeclaration.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            if (SymbolEqualityComparer.Default.Equals(
                    semanticModel.GetSymbolInfo(identifier).Symbol,
                    field) &&
                IsWrittenReference(identifier))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsWrittenReference(IdentifierNameSyntax identifier)
    {
        for (SyntaxNode? node = identifier; node?.Parent is { } parent; node = parent)
        {
            if (parent is AssignmentExpressionSyntax assignment)
            {
                return assignment.Left.Span.Contains(identifier.Span);
            }

            if (parent is PrefixUnaryExpressionSyntax
                {
                    RawKind: (int)SyntaxKind.PreIncrementExpression or
                        (int)SyntaxKind.PreDecrementExpression,
                } ||
                parent is PostfixUnaryExpressionSyntax
                {
                    RawKind: (int)SyntaxKind.PostIncrementExpression or
                        (int)SyntaxKind.PostDecrementExpression,
                } ||
                parent is ArgumentSyntax
                {
                    RefKindKeyword.RawKind: (int)SyntaxKind.RefKeyword or
                        (int)SyntaxKind.OutKeyword,
                } ||
                parent is RefExpressionSyntax)
            {
                return true;
            }

            if (parent is StatementSyntax or MemberDeclarationSyntax)
            {
                return false;
            }
        }

        return false;
    }

    private static bool IsProvablyPureEqualityType(ITypeSymbol type)
    {
        if (type.TypeKind == TypeKind.Enum)
        {
            return true;
        }

        if (type is INamedTypeSymbol namedType &&
            namedType.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T &&
            namedType.TypeArguments.Length == 1)
        {
            return IsProvablyPureEqualityType(namedType.TypeArguments[0]);
        }

        return type.SpecialType is
            SpecialType.System_Boolean or
            SpecialType.System_Byte or
            SpecialType.System_SByte or
            SpecialType.System_Int16 or
            SpecialType.System_UInt16 or
            SpecialType.System_Int32 or
            SpecialType.System_UInt32 or
            SpecialType.System_Int64 or
            SpecialType.System_UInt64 or
            SpecialType.System_Char or
            SpecialType.System_Single or
            SpecialType.System_Double or
            SpecialType.System_Decimal or
            SpecialType.System_String;
    }

    private static bool TryGetNegatedContains(
        ExpressionSyntax condition,
        out InvocationExpressionSyntax invocation,
        out MemberAccessExpressionSyntax memberAccess)
    {
        condition = WalkDownParentheses(condition);
        if (condition is PrefixUnaryExpressionSyntax
            {
                RawKind: (int)SyntaxKind.LogicalNotExpression,
                Operand: var operand,
            })
        {
            operand = WalkDownParentheses(operand);
            if (operand is InvocationExpressionSyntax
                {
                    Expression: MemberAccessExpressionSyntax
                    {
                        RawKind: (int)SyntaxKind.SimpleMemberAccessExpression,
                        Name.Identifier.ValueText: "Contains",
                    } containsMember,
                } containsInvocation)
            {
                invocation = containsInvocation;
                memberAccess = containsMember;
                return true;
            }
        }

        invocation = null!;
        memberAccess = null!;
        return false;
    }

    private static bool IsHashSetMethod(IMethodSymbol? method, INamedTypeSymbol hashSetDefinition, string name) =>
        method is { IsStatic: false, Parameters.Length: 1 } &&
        method.Name == name &&
        SymbolEqualityComparer.Default.Equals(method.OriginalDefinition.ContainingType.OriginalDefinition, hashSetDefinition);


    private static bool IsCallbackStableOperation(IOperation? operation)
    {
        operation = Unwrap(operation);
        return operation switch
        {
            ILocalReferenceOperation => true,
            ILiteralOperation => true,
            IDefaultValueOperation => true,
            ITypeOfOperation => true,
            IParameterReferenceOperation => true,
            IInstanceReferenceOperation => true,
            IFieldReferenceOperation field when field.Field.IsReadOnly && !field.Field.IsVolatile =>
                IsCallbackStableOperation(field.Instance),
            _ => false,
        };
    }

    private static IOperation? Unwrap(IOperation? operation)
    {
        while (true)
        {
            switch (operation)
            {
                case IConversionOperation { IsImplicit: true, OperatorMethod: null } conversion:
                    operation = conversion.Operand;
                    break;
                case IParenthesizedOperation parenthesized:
                    operation = parenthesized.Operand;
                    break;
                default:
                    return operation;
            }
        }
    }

    private static ExpressionSyntax WalkDownParentheses(ExpressionSyntax expression)
    {
        while (expression is ParenthesizedExpressionSyntax parenthesized)
        {
            expression = parenthesized.Expression;
        }

        return expression;
    }

    private static bool HasDirective(IfStatementSyntax ifStatement)
    {
        foreach (var trivia in ifStatement.DescendantTrivia(descendIntoTrivia: true))
        {
            if (trivia.IsDirective)
            {
                return true;
            }
        }

        return false;
    }
}

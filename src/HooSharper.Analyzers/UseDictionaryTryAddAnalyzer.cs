using System.Linq;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace HooSharper.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UseDictionaryTryAddAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "HOO1011";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "Use Dictionary.TryAdd",
        "Use TryAdd instead of ContainsKey followed by Add",
        "HooSharper.CodeStyle",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Dictionary.TryAdd performs the existence check and insertion in one operation.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(static compilationContext =>
        {
            var dictionaryDefinition = compilationContext.Compilation.GetTypeByMetadataName(
                "System.Collections.Generic.Dictionary`2");
            var equalityComparerDefinition = compilationContext.Compilation.GetTypeByMetadataName(
                "System.Collections.Generic.IEqualityComparer`1");
            if (dictionaryDefinition is null || equalityComparerDefinition is null)
            {
                return;
            }

            compilationContext.RegisterSyntaxNodeAction(
                nodeContext => AnalyzeIfStatement(nodeContext, dictionaryDefinition, equalityComparerDefinition),
                SyntaxKind.IfStatement);
        });
    }

    private static void AnalyzeIfStatement(
        SyntaxNodeAnalysisContext context,
        INamedTypeSymbol dictionaryDefinition,
        INamedTypeSymbol equalityComparerDefinition)
    {
        var ifStatement = (IfStatementSyntax)context.Node;
        if (ifStatement.Else is not null || HasDirective(ifStatement) ||
            ifStatement.Condition is not PrefixUnaryExpressionSyntax
            {
                RawKind: (int)SyntaxKind.LogicalNotExpression,
                Operand: InvocationExpressionSyntax
                {
                    Expression: MemberAccessExpressionSyntax
                    {
                        RawKind: (int)SyntaxKind.SimpleMemberAccessExpression,
                        Name.Identifier.ValueText: "ContainsKey",
                    } containsMember,
                } containsInvocation,
            } ||
            containsInvocation.ArgumentList.Arguments.Count != 1 ||
            ifStatement.Statement is not BlockSyntax { Statements.Count: > 0 } block ||
            block.Statements[0] is not ExpressionStatementSyntax
            {
                Expression: InvocationExpressionSyntax
                {
                    Expression: MemberAccessExpressionSyntax
                    {
                        RawKind: (int)SyntaxKind.SimpleMemberAccessExpression,
                        Name.Identifier.ValueText: "Add",
                    } addMember,
                } addInvocation,
            } ||
            addInvocation.ArgumentList.Arguments.Count != 2)
        {
            return;
        }

        var containsKey = containsInvocation.ArgumentList.Arguments[0].Expression;
        var addKey = addInvocation.ArgumentList.Arguments[0].Expression;
        var value = addInvocation.ArgumentList.Arguments[1].Expression;
        if (!SyntaxFactory.AreEquivalent(containsMember.Expression, addMember.Expression) ||
            !SyntaxFactory.AreEquivalent(containsKey, addKey))
        {
            return;
        }

        var containsOperation = context.SemanticModel.GetOperation(containsInvocation, context.CancellationToken) as IInvocationOperation;
        var addOperation = context.SemanticModel.GetOperation(addInvocation, context.CancellationToken) as IInvocationOperation;
        if (!IsDictionaryMethod(containsOperation?.TargetMethod, dictionaryDefinition, "ContainsKey", 1) ||
            !IsDictionaryMethod(addOperation?.TargetMethod, dictionaryDefinition, "Add", 2))
        {
            return;
        }

        var receiverOperation = containsOperation!.Instance;
        var keyOperation = containsOperation.Arguments[0].Value;
        var valueOperation = addOperation!.Arguments[1].Value;
        if (!HasProvablyPureComparer(
                receiverOperation,
                ifStatement,
                context.SemanticModel,
                dictionaryDefinition,
                equalityComparerDefinition) ||
            !IsCallbackStableOperation(receiverOperation) ||
            !IsCallbackStableOperation(keyOperation) ||
            !IsCallbackStableOperation(valueOperation) ||
            !IsSideEffectFree(valueOperation) ||
            receiverOperation?.Type is not INamedTypeSymbol receiverNamedType ||
            !SymbolEqualityComparer.Default.Equals(receiverNamedType.OriginalDefinition, dictionaryDefinition) ||
            !HasSuitableTryAdd(receiverNamedType))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, containsMember.Name.GetLocation()));
    }

    private static bool IsDictionaryMethod(
        IMethodSymbol? method,
        INamedTypeSymbol dictionaryDefinition,
        string name,
        int parameterCount) =>
        method is { IsStatic: false } &&
        method.Name == name &&
        method.Parameters.Length == parameterCount &&
        SymbolEqualityComparer.Default.Equals(method.OriginalDefinition.ContainingType.OriginalDefinition, dictionaryDefinition);

    private static bool HasSuitableTryAdd(INamedTypeSymbol dictionaryType)
    {
        foreach (var member in dictionaryType.GetMembers("TryAdd"))
        {
            if (member is IMethodSymbol
                {
                    IsStatic: false,
                    DeclaredAccessibility: Accessibility.Public,
                    Parameters.Length: 2,
                    ReturnType.SpecialType: SpecialType.System_Boolean,
                } method &&
                SymbolEqualityComparer.Default.Equals(method.Parameters[0].Type, dictionaryType.TypeArguments[0]) &&
                SymbolEqualityComparer.Default.Equals(method.Parameters[1].Type, dictionaryType.TypeArguments[1]))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasProvablyPureComparer(
        IOperation? receiver,
        IfStatementSyntax ifStatement,
        SemanticModel semanticModel,
        INamedTypeSymbol dictionaryDefinition,
        INamedTypeSymbol equalityComparerDefinition)
    {
        receiver = Unwrap(receiver);
        if (receiver?.Type is not INamedTypeSymbol dictionaryType ||
            dictionaryType.TypeArguments.Length != 2 ||
            !IsProvablyPureEqualityType(dictionaryType.TypeArguments[0]))
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
                    dictionaryDefinition,
                    equalityComparerDefinition),
            IFieldReferenceOperation fieldReference when fieldReference.Field.IsReadOnly &&
                !fieldReference.Field.IsVolatile =>
                IsNeverReassigned(fieldReference.Field, semanticModel) &&
                HasDefaultComparerInitializer(
                    fieldReference.Field,
                    semanticModel,
                    dictionaryDefinition,
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

    private static bool IsSideEffectFree(IOperation? operation) => operation switch
    {
        ILiteralOperation => true,
        ILocalReferenceOperation => true,
        IParameterReferenceOperation => true,
        IInstanceReferenceOperation => true,
        IDefaultValueOperation => true,
        ITypeOfOperation => true,
        IFieldReferenceOperation fieldReference when !fieldReference.Field.IsVolatile &&
            (!fieldReference.Field.IsStatic || fieldReference.Field.IsConst) &&
            (fieldReference.Instance is null or IInstanceReferenceOperation) => true,
        IConversionOperation conversion when conversion.OperatorMethod is null => IsSideEffectFree(conversion.Operand),
        IParenthesizedOperation parenthesized => IsSideEffectFree(parenthesized.Operand),
        _ => false,
    };

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

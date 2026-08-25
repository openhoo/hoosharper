using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace HooSharper.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UseTryGetValueAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "HOO1007";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "Use TryGetValue",
        "Use TryGetValue instead of ContainsKey followed by an index access",
        "HooSharper.CodeStyle",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "TryGetValue performs a single dictionary lookup and provides the matching value.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(static compilationContext =>
        {
            var dictionaryDefinition = compilationContext.Compilation.GetTypeByMetadataName(
                "System.Collections.Generic.Dictionary`2");
            var dictionaryInterface = compilationContext.Compilation.GetTypeByMetadataName(
                "System.Collections.Generic.IDictionary`2");
            if (dictionaryDefinition is null && dictionaryInterface is null)
            {
                return;
            }

            compilationContext.RegisterSyntaxNodeAction(
                nodeContext => AnalyzeIfStatement(nodeContext, dictionaryDefinition, dictionaryInterface),
                SyntaxKind.IfStatement);
        });
    }

    private static void AnalyzeIfStatement(
        SyntaxNodeAnalysisContext context,
        INamedTypeSymbol? dictionaryDefinition,
        INamedTypeSymbol? dictionaryInterface)
    {
        var ifStatement = (IfStatementSyntax)context.Node;
        if (context.SemanticModel.SyntaxTree.Options is CSharpParseOptions
            {
                LanguageVersion: var languageVersion,
            } &&
            languageVersion != LanguageVersion.Default &&
            (int)languageVersion < (int)LanguageVersion.CSharp7)
        {
            return;
        }

        if (ifStatement.Else is not null || HasDirective(ifStatement) ||
            ifStatement.Condition is not InvocationExpressionSyntax
            {
                Expression: MemberAccessExpressionSyntax
                {
                    RawKind: (int)SyntaxKind.SimpleMemberAccessExpression,
                    Name.Identifier.ValueText: "ContainsKey",
                } memberAccess,
            } invocation ||
            invocation.ArgumentList.Arguments.Count != 1)
        {
            return;
        }

        var key = invocation.ArgumentList.Arguments[0].Expression;
        var dictionaryOperation = context.SemanticModel.GetOperation(memberAccess.Expression, context.CancellationToken);
        var invocationOperation = context.SemanticModel.GetOperation(invocation, context.CancellationToken) as IInvocationOperation;
        var keyOperation = invocationOperation?.Arguments.Length == 1
            ? invocationOperation.Arguments[0].Value
            : null;
        if (!IsCallbackStableOperation(dictionaryOperation) ||
            !IsCallbackStableOperation(keyOperation) ||
            !HasProvenDefaultComparer(
                dictionaryOperation,
                context.SemanticModel,
                context.CancellationToken,
                dictionaryDefinition) ||
            dictionaryOperation?.Type is not INamedTypeSymbol namedType ||
            (!SymbolEqualityComparer.Default.Equals(namedType.OriginalDefinition, dictionaryDefinition) &&
             !SymbolEqualityComparer.Default.Equals(namedType.OriginalDefinition, dictionaryInterface)) ||
            invocationOperation is null ||
            !IsDictionaryMember(
                invocationOperation.TargetMethod.OriginalDefinition.ContainingType,
                dictionaryDefinition,
                dictionaryInterface))
        {
            return;
        }

        if (MayMutateLookup(
                ifStatement.Statement,
                memberAccess.Expression,
                key,
                context.SemanticModel,
                keyOperation,
                context.CancellationToken))
        {
            return;
        }

        foreach (var node in ifStatement.Statement.DescendantNodes(ShouldDescendInto))
        {
            if (node is not ElementAccessExpressionSyntax { ArgumentList.Arguments.Count: 1 } elementAccess)
            {
                continue;
            }

            var indexedKey = elementAccess.ArgumentList.Arguments[0].Expression;
            if (!SyntaxFactory.AreEquivalent(memberAccess.Expression, elementAccess.Expression) ||
                !SyntaxFactory.AreEquivalent(key, indexedKey))
            {
                continue;
            }

            var elementOperation = Unwrap(context.SemanticModel.GetOperation(elementAccess, context.CancellationToken));
            if (elementOperation is not IPropertyReferenceOperation indexerOperation ||
                !IsDictionaryMember(
                    indexerOperation.Property.OriginalDefinition.ContainingType,
                    dictionaryDefinition,
                    dictionaryInterface) ||
                !IsValueRead(elementAccess, elementOperation))
            {
                continue;
            }

            context.ReportDiagnostic(Diagnostic.Create(Rule, memberAccess.Name.GetLocation()));
            return;
        }

    }
    private static bool HasProvenDefaultComparer(
        IOperation? operation,
        SemanticModel semanticModel,
        System.Threading.CancellationToken cancellationToken,
        INamedTypeSymbol? dictionaryDefinition)
    {
        operation = Unwrap(operation);
        if (operation?.Type is not INamedTypeSymbol dictionaryType ||
            dictionaryType.TypeArguments.Length != 2 ||
            !IsProvablyPureEqualityType(dictionaryType.TypeArguments[0]) ||
            operation is not IFieldReferenceOperation field ||
            !field.Field.IsReadOnly ||
            field.Field.IsVolatile ||
            !IsNeverReassigned(field.Field, semanticModel, cancellationToken))
        {
            return false;
        }

        foreach (var syntaxReference in field.Field.DeclaringSyntaxReferences)
        {
            if (syntaxReference.GetSyntax(cancellationToken) is VariableDeclaratorSyntax
                {
                    Initializer.Value: var initializer,
                } &&
                semanticModel.GetOperation(initializer, cancellationToken) is IObjectCreationOperation
                {
                    Type: INamedTypeSymbol createdType,
                    Arguments.Length: 0,
                } &&
                SymbolEqualityComparer.Default.Equals(
                    createdType.OriginalDefinition,
                    dictionaryDefinition))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsNeverReassigned(
        IFieldSymbol field,
        SemanticModel semanticModel,
        System.Threading.CancellationToken cancellationToken)
    {
        if (field.ContainingType.DeclaringSyntaxReferences.Length != 1 ||
            field.ContainingType.DeclaringSyntaxReferences[0].GetSyntax(cancellationToken) is not TypeDeclarationSyntax typeDeclaration ||
            typeDeclaration.SyntaxTree != semanticModel.SyntaxTree)
        {
            return false;
        }

        foreach (var node in typeDeclaration.DescendantNodes())
        {
            if (node is IdentifierNameSyntax identifier &&
                SymbolEqualityComparer.Default.Equals(
                    semanticModel.GetSymbolInfo(identifier, cancellationToken).Symbol,
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
    private static bool IsDictionaryMember(
        INamedTypeSymbol containingType,
        INamedTypeSymbol? dictionaryDefinition,
        INamedTypeSymbol? dictionaryInterface) =>
        SymbolEqualityComparer.Default.Equals(containingType.OriginalDefinition, dictionaryDefinition) ||
        SymbolEqualityComparer.Default.Equals(containingType.OriginalDefinition, dictionaryInterface);


    private static bool IsCallbackStableOperation(IOperation? operation)
    {
        operation = Unwrap(operation);
        return operation switch
        {
            ILiteralOperation => true,
            IDefaultValueOperation => true,
            ITypeOfOperation => true,
            ILocalReferenceOperation local when local.Local.IsConst => true,
            IInstanceReferenceOperation => true,
            IFieldReferenceOperation field when
                (field.Field.IsConst || field.Field.IsReadOnly) && !field.Field.IsVolatile =>
                field.Instance is null || IsCallbackStableOperation(field.Instance),
            _ => false,
        };
    }

    private static bool IsValueRead(
        ElementAccessExpressionSyntax elementAccess,
        IOperation operation)
    {
        var parent = operation.Parent;
        while (parent is IConversionOperation { IsImplicit: true } or IParenthesizedOperation)
        {
            parent = parent.Parent;
        }

        if (parent is ISimpleAssignmentOperation simpleAssignment &&
            ReferenceEquals(Unwrap(simpleAssignment.Target), operation) ||
            parent is ICompoundAssignmentOperation compoundAssignment &&
            ReferenceEquals(Unwrap(compoundAssignment.Target), operation) ||
            parent is IIncrementOrDecrementOperation increment &&
            ReferenceEquals(Unwrap(increment.Target), operation) ||
            parent is IArgumentOperation argument && argument.Parameter?.RefKind != RefKind.None)
        {
            return false;
        }

        for (SyntaxNode? node = elementAccess.Parent; node is not null; node = node.Parent)
        {
            if (node is AssignmentExpressionSyntax assignment &&
                assignment.Left.Span.Contains(elementAccess.Span))
            {
                return false;
            }

            if (node is RefExpressionSyntax)
            {
                return false;
            }

            if (node is ArgumentSyntax argumentSyntax && !argumentSyntax.RefKindKeyword.IsKind(SyntaxKind.None))
            {
                return false;
            }

            if (node is StatementSyntax or ArrowExpressionClauseSyntax)
            {
                break;
            }
        }

        return true;
    }


    private static bool MayMutateLookup(
        StatementSyntax statement,
        ExpressionSyntax dictionary,
        ExpressionSyntax key,
        SemanticModel semanticModel,
        IOperation? keyOperation,
        System.Threading.CancellationToken cancellationToken)
    {
        foreach (var node in statement.DescendantNodes(ShouldDescendInto))
        {
            if (node is AssignmentExpressionSyntax assignment &&
                (assignment.Left is ElementAccessExpressionSyntax ||
                 ContainsMatchingDictionaryIndexer(assignment.Left, dictionary, key) ||
                 IsSameExpression(assignment.Left, dictionary) ||
                 IsSameExpression(assignment.Left, key) ||
                 WritesKeyLocation(assignment.Left, key, keyOperation, semanticModel, cancellationToken)))
            {
                return true;
            }
            var mutatedOperand = node switch
            {
                PrefixUnaryExpressionSyntax prefix when
                    prefix.IsKind(SyntaxKind.PreIncrementExpression) ||
                    prefix.IsKind(SyntaxKind.PreDecrementExpression) => prefix.Operand,
                PostfixUnaryExpressionSyntax postfix when
                    postfix.IsKind(SyntaxKind.PostIncrementExpression) ||
                    postfix.IsKind(SyntaxKind.PostDecrementExpression) => postfix.Operand,
                _ => null,
            };
            if (mutatedOperand is not null &&
                (mutatedOperand is ElementAccessExpressionSyntax ||
                 IsSameExpression(mutatedOperand, dictionary) ||
                 IsSameExpression(mutatedOperand, key) ||
                 IsDictionaryIndexer(mutatedOperand, dictionary) ||
                 WritesKeyLocation(mutatedOperand, key, keyOperation, semanticModel, cancellationToken)))
            {
                return true;
            }

            if (node is InvocationExpressionSyntax or ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax or AwaitExpressionSyntax)
            {
                return true;
            }
        }

        return false;
    }

    private static bool WritesKeyLocation(
        ExpressionSyntax target,
        ExpressionSyntax key,
        IOperation? keyOperation,
        SemanticModel semanticModel,
        System.Threading.CancellationToken cancellationToken)
    {
        while (target is ParenthesizedExpressionSyntax parenthesizedTarget)
        {
            target = parenthesizedTarget.Expression;
        }

        while (key is ParenthesizedExpressionSyntax parenthesizedKey)
        {
            key = parenthesizedKey.Expression;
        }

        if (IsSameExpression(target, key))
        {
            return true;
        }

        if (target is not MemberAccessExpressionSyntax
            {
                Expression: ThisExpressionSyntax,
                Name: IdentifierNameSyntax memberName,
            } ||
            key is not IdentifierNameSyntax keyIdentifier ||
            keyIdentifier.Identifier.ValueText != memberName.Identifier.ValueText)
        {
            return false;
        }

        var targetRoot = Unwrap(semanticModel.GetOperation(target, cancellationToken));

        return ReferenceChainsMatch(targetRoot, keyOperation);
    }

    private static bool ReferenceChainsMatch(IOperation? left, IOperation? right)
    {
        while (true)
        {
            if (left is null || right is null)
            {
                return left is null && right is null;
            }

            if (left is IInstanceReferenceOperation || right is IInstanceReferenceOperation)
            {
                return left is IInstanceReferenceOperation && right is IInstanceReferenceOperation;
            }

            var leftMember = GetReferencedMember(left);
            if (leftMember is null ||
                !SymbolEqualityComparer.Default.Equals(leftMember, GetReferencedMember(right)))
            {
                return false;
            }

            left = GetReceiverInstance(left);
            right = GetReceiverInstance(right);
        }
    }

    private static ISymbol? GetReferencedMember(IOperation operation) => operation switch
    {
        IFieldReferenceOperation field => field.Field,
        IPropertyReferenceOperation property => property.Property,
        ILocalReferenceOperation local => local.Local,
        IParameterReferenceOperation parameter => parameter.Parameter,
        _ => null,
    };

    private static IOperation? GetReceiverInstance(IOperation operation) => operation switch
    {
        IFieldReferenceOperation field => field.Instance,
        IPropertyReferenceOperation property => property.Instance,
        _ => null,
    };

    private static bool ContainsMatchingDictionaryIndexer(
        ExpressionSyntax target,
        ExpressionSyntax dictionary,
        ExpressionSyntax key)
    {
        foreach (var node in target.DescendantNodesAndSelf())
        {
            if (node is ElementAccessExpressionSyntax
                {
                    ArgumentList.Arguments.Count: 1,
                } elementAccess &&
                IsSameExpression(elementAccess.Expression, dictionary) &&
                IsSameExpression(elementAccess.ArgumentList.Arguments[0].Expression, key))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsSameExpression(ExpressionSyntax first, ExpressionSyntax second) =>
        SyntaxFactory.AreEquivalent(first, second);

    private static bool IsDictionaryIndexer(
        ExpressionSyntax expression,
        ExpressionSyntax dictionary) =>
        expression is ElementAccessExpressionSyntax elementAccess &&
        IsSameExpression(elementAccess.Expression, dictionary);

    private static IOperation? Unwrap(IOperation? operation)
    {
        while (true)
        {
            operation = operation switch
            {
                IConversionOperation
                {
                    IsImplicit: true,
                    OperatorMethod: null,
                } conversion => conversion.Operand,
                IParenthesizedOperation parenthesized => parenthesized.Operand,
                _ => operation,
            };

            if (operation is not IConversionOperation { IsImplicit: true, OperatorMethod: null } and
                not IParenthesizedOperation)
            {
                return operation;
            }
        }
    }


    private static bool ShouldDescendInto(SyntaxNode node) =>
        node is not AnonymousFunctionExpressionSyntax and
        not LocalFunctionStatementSyntax and
        not TypeDeclarationSyntax;

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

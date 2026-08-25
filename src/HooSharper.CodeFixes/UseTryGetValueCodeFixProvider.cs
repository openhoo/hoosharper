using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HooSharper.Analyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Operations;

namespace HooSharper.CodeFixes;

[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(UseTryGetValueCodeFixProvider)), Shared]
public sealed class UseTryGetValueCodeFixProvider : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds => [UseTryGetValueAnalyzer.DiagnosticId];

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        var diagnostic = context.Diagnostics[0];
        var ifStatement = root?.FindToken(diagnostic.Location.SourceSpan.Start).Parent?
            .AncestorsAndSelf().OfType<IfStatementSyntax>().FirstOrDefault();
        if (ifStatement is null)
        {
            return;
        }


        context.RegisterCodeFix(
            CodeAction.Create(
                "Use TryGetValue",
                cancellationToken => ApplyFixAsync(context.Document, ifStatement, cancellationToken),
                nameof(UseTryGetValueCodeFixProvider)),
            diagnostic);
    }

    private static async Task<Document> ApplyFixAsync(
        Document document,
        IfStatementSyntax ifStatement,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (root is null || semanticModel is null ||
            semanticModel.SyntaxTree.Options is CSharpParseOptions
            {
                LanguageVersion: var languageVersion,
            } &&
            languageVersion != LanguageVersion.Default &&
            (int)languageVersion < (int)LanguageVersion.CSharp7 ||
            ifStatement.Condition is not InvocationExpressionSyntax
            {
                Expression: MemberAccessExpressionSyntax memberAccess,
            } invocation ||
            invocation.ArgumentList.Arguments.Count != 1)
        {
            return document;
        }

        var key = invocation.ArgumentList.Arguments[0].Expression;
        var dictionaryOperation = semanticModel.GetOperation(memberAccess.Expression, cancellationToken);
        var invocationOperation = semanticModel.GetOperation(invocation, cancellationToken) as IInvocationOperation;
        var keyOperation = invocationOperation?.Arguments.Length == 1
            ? invocationOperation.Arguments[0].Value
            : null;
        if (!IsCallbackStableOperation(dictionaryOperation) ||
            !IsCallbackStableOperation(keyOperation) ||
            !HasProvenDefaultComparer(dictionaryOperation, semanticModel, cancellationToken))
        {
            return document;
        }

        var dictionaryDefinition = semanticModel.Compilation.GetTypeByMetadataName(
            "System.Collections.Generic.Dictionary`2");
        var dictionaryInterface = semanticModel.Compilation.GetTypeByMetadataName(
            "System.Collections.Generic.IDictionary`2");
        var matchingAccesses = new List<ElementAccessExpressionSyntax>();
        if (MayMutateLookup(
                ifStatement.Statement,
                memberAccess.Expression,
                key,
                semanticModel,
                keyOperation,
                cancellationToken))
        {
            return document;
        }

        foreach (var elementAccess in ifStatement.Statement.DescendantNodes(ShouldDescendInto)
                     .OfType<ElementAccessExpressionSyntax>())
        {
            if (elementAccess.ArgumentList.Arguments.Count != 1)
            {
                continue;
            }

            var indexedKey = elementAccess.ArgumentList.Arguments[0].Expression;
            if (!SyntaxFactory.AreEquivalent(memberAccess.Expression, elementAccess.Expression) ||
                !SyntaxFactory.AreEquivalent(key, indexedKey))
            {
                continue;
            }

            var indexer = semanticModel.GetSymbolInfo(elementAccess, cancellationToken).Symbol as IPropertySymbol;
            if (indexer is not null && IsDictionaryMember(
                    indexer.OriginalDefinition.ContainingType,
                    dictionaryDefinition,
                    dictionaryInterface) &&
                IsValueRead(elementAccess, semanticModel, cancellationToken))
            {
                matchingAccesses.Add(elementAccess);
            }
        }

        if (matchingAccesses.Count == 0)
        {
            return document;
        }

        var valueName = CreateUniqueName(semanticModel, ifStatement);
        var valueIdentifier = SyntaxFactory.IdentifierName(valueName);
        var updatedBody = ifStatement.Statement.ReplaceNodes(
            matchingAccesses,
            (original, _) => RewriteAccess(original, valueIdentifier));

        var tryGetValueMember = memberAccess.WithName(
            SyntaxFactory.IdentifierName("TryGetValue").WithTriviaFrom(memberAccess.Name));
        var outArgument = SyntaxFactory.Argument(
                SyntaxFactory.DeclarationExpression(
                    SyntaxFactory.IdentifierName("var"),
                    SyntaxFactory.SingleVariableDesignation(SyntaxFactory.Identifier(valueName))))
            .WithRefKindKeyword(SyntaxFactory.Token(SyntaxKind.OutKeyword));
        var updatedCondition = invocation
            .WithExpression(tryGetValueMember)
            .WithArgumentList(invocation.ArgumentList.AddArguments(outArgument))
            .WithAdditionalAnnotations(Formatter.Annotation);
        var updatedIf = ifStatement
            .WithCondition(updatedCondition)
            .WithStatement(updatedBody)
            .WithAdditionalAnnotations(Formatter.Annotation);

        return document.WithSyntaxRoot(root.ReplaceNode(ifStatement, updatedIf));
    }

    private static ExpressionSyntax RewriteAccess(
        ElementAccessExpressionSyntax access,
        IdentifierNameSyntax valueIdentifier)
    {
        var rewritten = valueIdentifier.WithTriviaFrom(access);
        var internalTrivia = NormalizeInternalTrivia(CollectInternalTrivia(access));
        if (internalTrivia.Count == 0)
        {
            return rewritten;
        }

        // Exactly one space separates migrated internal trivia from the value
        // identifier unless an end-of-line or the surrounding whitespace already
        // provides that separation.
        var leading = rewritten.GetLeadingTrivia();
        var updatedLeading = internalTrivia.AddRange(leading);
        if (!internalTrivia[internalTrivia.Count - 1].IsKind(SyntaxKind.EndOfLineTrivia) &&
            (leading.Count == 0 ||
             (!leading[0].IsKind(SyntaxKind.WhitespaceTrivia) &&
              !leading[0].IsKind(SyntaxKind.EndOfLineTrivia))))
        {
            updatedLeading = updatedLeading.Insert(internalTrivia.Count, SyntaxFactory.Space);
        }

        return rewritten.WithLeadingTrivia(updatedLeading);
    }

    private static SyntaxTriviaList CollectInternalTrivia(ElementAccessExpressionSyntax access)
    {
        var internalTrivia = SyntaxFactory.TriviaList();
        var tokens = access.DescendantTokens().ToList();
        if (tokens.Count < 2)
        {
            return internalTrivia;
        }

        // The first token's leading trivia is the access's own leading trivia and the last
        // token's trailing trivia its own trailing trivia; both are migrated by WithTriviaFrom.
        for (var index = 0; index < tokens.Count - 1; index++)
        {
            internalTrivia = internalTrivia.AddRange(tokens[index].TrailingTrivia);
            internalTrivia = internalTrivia.AddRange(tokens[index + 1].LeadingTrivia);
        }

        return internalTrivia;
    }

    private static SyntaxTriviaList NormalizeInternalTrivia(SyntaxTriviaList internalTrivia)
    {
        var startIndex = 0;
        var endIndex = internalTrivia.Count - 1;
        while (startIndex <= endIndex && internalTrivia[startIndex].IsKind(SyntaxKind.WhitespaceTrivia))
        {
            startIndex++;
        }

        while (endIndex >= startIndex && internalTrivia[endIndex].IsKind(SyntaxKind.WhitespaceTrivia))
        {
            endIndex--;
        }

        if (startIndex > endIndex)
        {
            return SyntaxFactory.TriviaList();
        }

        var normalized = SyntaxFactory.TriviaList();
        for (var index = startIndex; index <= endIndex; index++)
        {
            normalized = normalized.Add(internalTrivia[index]);
        }

        return normalized;
    }

    private static string CreateUniqueName(
        SemanticModel semanticModel,
        IfStatementSyntax ifStatement)
    {
        var unavailableNames = new HashSet<string>(System.StringComparer.Ordinal);
        foreach (var symbol in semanticModel.LookupSymbols(ifStatement.SpanStart))
        {
            unavailableNames.Add(symbol.Name);
        }

        var enclosingScope = ifStatement.Parent ?? ifStatement;
        foreach (var token in enclosingScope.DescendantTokens())
        {
            if (token.IsKind(SyntaxKind.IdentifierToken))
            {
                unavailableNames.Add(token.ValueText);
            }
        }

        const string baseName = "value";
        var suffix = 0;
        while (unavailableNames.Contains(NameForSuffix(baseName, suffix)))
        {
            suffix++;
        }

        if (ifStatement.Parent is BlockSyntax block)
        {
            foreach (var statement in block.Statements)
            {
                if (statement == ifStatement)
                {
                    break;
                }

                if (statement is IfStatementSyntax
                    {
                        Condition: InvocationExpressionSyntax
                        {
                            Expression: MemberAccessExpressionSyntax
                            {
                                Name.Identifier.ValueText: "ContainsKey",
                            },
                        },
                    })
                {
                    suffix++;
                    while (unavailableNames.Contains(NameForSuffix(baseName, suffix)))
                    {
                        suffix++;
                    }
                }
            }
        }

        return NameForSuffix(baseName, suffix);
    }

    private static string NameForSuffix(string baseName, int suffix) =>
        suffix == 0 ? baseName : baseName + suffix;

    private static bool IsDictionaryMember(
        INamedTypeSymbol containingType,
        INamedTypeSymbol? dictionaryDefinition,
        INamedTypeSymbol? dictionaryInterface) =>
        SymbolEqualityComparer.Default.Equals(containingType.OriginalDefinition, dictionaryDefinition) ||
        SymbolEqualityComparer.Default.Equals(containingType.OriginalDefinition, dictionaryInterface);

    private static bool HasProvenDefaultComparer(
        IOperation? operation,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        operation = Unwrap(operation);
        if (operation?.Type is not INamedTypeSymbol dictionaryType ||
            dictionaryType.TypeArguments.Length != 2 ||
            !IsProvablyPureEqualityType(dictionaryType.TypeArguments[0]) ||
            operation is not IFieldReferenceOperation field ||
            !field.Field.IsReadOnly ||
            field.Field.IsVolatile)
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
                    Arguments.Length: 0,
                })
            {
                return true;
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

    private static bool IsCallbackStable(
        ExpressionSyntax expression,
        SemanticModel semanticModel,
        CancellationToken cancellationToken) =>
        IsCallbackStableOperation(semanticModel.GetOperation(expression, cancellationToken));

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
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        var operation = Unwrap(semanticModel.GetOperation(elementAccess, cancellationToken));
        var parent = operation?.Parent;
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

    private static bool MayMutateLookup(
        StatementSyntax statement,
        ExpressionSyntax dictionary,
        ExpressionSyntax key,
        SemanticModel semanticModel,
        IOperation? keyOperation,
        CancellationToken cancellationToken)
    {
        foreach (var node in statement.DescendantNodes(ShouldDescendInto))
        {
            if (node is AssignmentExpressionSyntax assignment &&
                (assignment.Left is ElementAccessExpressionSyntax ||
                 IsSameExpression(assignment.Left, dictionary) ||
                 IsSameExpression(assignment.Left, key) ||
                 IsDictionaryIndexer(assignment.Left, dictionary) ||
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
        CancellationToken cancellationToken)
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

    private static bool IsSameExpression(ExpressionSyntax first, ExpressionSyntax second) =>
        SyntaxFactory.AreEquivalent(first, second);

    private static bool IsDictionaryIndexer(
        ExpressionSyntax expression,
        ExpressionSyntax dictionary) =>
        expression is ElementAccessExpressionSyntax elementAccess &&
        IsSameExpression(elementAccess.Expression, dictionary);

    private static bool ShouldDescendInto(SyntaxNode node) =>
        node is not AnonymousFunctionExpressionSyntax and
        not LocalFunctionStatementSyntax and
        not TypeDeclarationSyntax;
}

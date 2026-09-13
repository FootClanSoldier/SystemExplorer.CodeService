// TEMPORARY THROWAWAY ROSLYN INSTRUMENTATION TEMPLATE. This file is not compiled into the probe.
using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis.LanguageService;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Shared.Extensions.ContextQuery;

namespace Microsoft.CodeAnalysis.Completion.Providers;

internal static class SystemExplorerCompletionSemanticOrigin
{
    internal const string OriginPropertyKey = "SystemExplorer.CompletionSemanticOrigin";
    internal const string InheritanceDepthPropertyKey = "SystemExplorer.CompletionInheritanceDepth";
    private const string EnabledEnvironmentVariable = "SYSTEMEXPLORER_COMPLETION_SEMANTIC_ORIGIN";

    public static CompletionItem Attach(
        CompletionItem item,
        ImmutableArray<ISymbol> symbols,
        SyntaxContext context)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(EnabledEnvironmentVariable), "1", StringComparison.Ordinal))
            return item;

        if (symbols.IsDefaultOrEmpty)
            return item.AddProperty(OriginPropertyKey, "Unknown");

        INamedTypeSymbol? semanticAnchor = ResolveExplicitReceiverAnchor(context, CancellationToken.None)
            ?? GetLexicalContainingType(context.SemanticModel, context.Position);

        OriginEvidence? aggregate = null;
        foreach (ISymbol symbol in symbols)
        {
            OriginEvidence current = Classify(symbol, semanticAnchor);
            if (aggregate is null)
            {
                aggregate = current;
                continue;
            }

            if (aggregate.Value.Kind != current.Kind || aggregate.Value.InheritanceDepth != current.InheritanceDepth)
                return item.AddProperty(OriginPropertyKey, "Unknown");
        }

        OriginEvidence evidence = aggregate ?? new("Unknown", null);
        item = item.AddProperty(OriginPropertyKey, evidence.Kind);
        return evidence.InheritanceDepth is int depth
            ? item.AddProperty(InheritanceDepthPropertyKey, depth.ToString(System.Globalization.CultureInfo.InvariantCulture))
            : item;
    }

    private static INamedTypeSymbol? ResolveExplicitReceiverAnchor(
        SyntaxContext context,
        CancellationToken cancellationToken)
    {
        if (!context.IsRightOfNameSeparator)
            return null;

        var syntaxFacts = context.GetRequiredLanguageService<ISyntaxFactsService>();
        var parentNode = context.TargetToken.Parent;
        if (syntaxFacts.IsSimpleMemberAccessExpression(parentNode))
        {
            var memberAccessReceiverExpression = syntaxFacts.GetExpressionOfMemberAccessExpression(
                parentNode, allowImplicitTarget: false);
            if (memberAccessReceiverExpression is null
                || syntaxFacts.IsThisExpression(memberAccessReceiverExpression)
                || syntaxFacts.IsBaseExpression(memberAccessReceiverExpression))
            {
                return null;
            }

            var receiverSymbolInfo = context.SemanticModel.GetSymbolInfo(memberAccessReceiverExpression, cancellationToken);
            if (TryResolveReceiverAnchorFromSymbolInfo(receiverSymbolInfo, out var receiverAnchor))
                return receiverAnchor;

            return ResolveValueReceiverAnchor(
                context.SemanticModel.GetTypeInfo(memberAccessReceiverExpression, cancellationToken));
        }

        if (!syntaxFacts.IsQualifiedName(parentNode))
            return null;

        syntaxFacts.GetPartsOfQualifiedName(
            parentNode, out var receiverExpression, out var dotToken, out var right);
        if (syntaxFacts.IsThisExpression(receiverExpression)
            || syntaxFacts.IsBaseExpression(receiverExpression))
        {
            return null;
        }

        var sourceText = parentNode.SyntaxTree.GetText(cancellationToken);
        if (sourceText.AreOnSameLine(dotToken, right.GetFirstToken()))
            return null;

        var speculativeReceiverSymbolInfo = context.SemanticModel.GetSpeculativeSymbolInfo(
            receiverExpression.SpanStart,
            receiverExpression,
            SpeculativeBindingOption.BindAsExpression);
        if (TryResolveReceiverAnchorFromSymbolInfo(speculativeReceiverSymbolInfo, out var speculativeReceiverAnchor))
            return speculativeReceiverAnchor;

        return ResolveValueReceiverAnchor(
            context.SemanticModel.GetSpeculativeTypeInfo(
                receiverExpression.SpanStart,
                receiverExpression,
                SpeculativeBindingOption.BindAsExpression));
    }

    private static OriginEvidence Classify(ISymbol symbol, INamedTypeSymbol? semanticAnchor)
    {
        if (symbol is ILocalSymbol or IParameterSymbol or IRangeVariableSymbol
            || symbol is IMethodSymbol { MethodKind: MethodKind.LocalFunction })
        {
            return new("Local", null);
        }

        if (symbol is IMethodSymbol { ReducedFrom: not null } reduced)
            return ClassifyDeclarationAuthority(reduced.ReducedFrom);

        ISymbol declaration = symbol;
        if (declaration.ContainingType is INamedTypeSymbol containingType && semanticAnchor is not null)
        {
            INamedTypeSymbol declarationType = containingType.OriginalDefinition;
            INamedTypeSymbol current = semanticAnchor.OriginalDefinition;
            if (SymbolEqualityComparer.Default.Equals(declarationType, current))
                return new("CurrentType", 0);

            int depth = 1;
            for (INamedTypeSymbol? baseType = semanticAnchor.BaseType; baseType is not null; baseType = baseType.BaseType, depth++)
            {
                if (SymbolEqualityComparer.Default.Equals(declarationType, baseType.OriginalDefinition))
                    return new("BaseType", depth);
            }
        }

        return ClassifyDeclarationAuthority(declaration);
    }

    private static bool TryResolveReceiverAnchorFromSymbolInfo(
        SymbolInfo receiverSymbolInfo,
        out INamedTypeSymbol? receiverAnchor)
    {
        if (ResolveNamedTypeAuthority(receiverSymbolInfo.Symbol) is { } namedTypeAuthority)
        {
            receiverAnchor = namedTypeAuthority;
            return true;
        }

        if (IsNamespaceOrTypeAuthority(receiverSymbolInfo.Symbol)
            || receiverSymbolInfo.CandidateSymbols.Any(IsNamespaceOrTypeAuthority))
        {
            receiverAnchor = null;
            return true;
        }

        receiverAnchor = null;
        return false;
    }

    private static INamedTypeSymbol? ResolveValueReceiverAnchor(TypeInfo receiverTypeInfo)
        => receiverTypeInfo.Type is INamedTypeSymbol { TypeKind: not TypeKind.Error } receiverType
            ? receiverType
            : null;

    private static INamedTypeSymbol? ResolveNamedTypeAuthority(ISymbol? symbol)
        => symbol switch
        {
            INamedTypeSymbol type when type.TypeKind != TypeKind.Error => type,
            IAliasSymbol { Target: INamedTypeSymbol target } when target.TypeKind != TypeKind.Error => target,
            _ => null,
        };

    private static bool IsNamespaceOrTypeAuthority(ISymbol? symbol)
        => symbol is INamespaceSymbol or INamedTypeSymbol
            || symbol is IAliasSymbol { Target: INamespaceOrTypeSymbol };

    private static OriginEvidence ClassifyDeclarationAuthority(ISymbol declaration)
    {
        if (declaration.DeclaringSyntaxReferences.Length > 0 || declaration.Locations.Any(static location => location.IsInSource))
            return new("OtherUserCode", null);
        if (declaration.Locations.Any(static location => location.IsInMetadata))
            return new("FrameworkOrOther", null);
        return new("Unknown", null);
    }

    private static INamedTypeSymbol? GetLexicalContainingType(SemanticModel semanticModel, int position)
    {
        for (ISymbol? enclosing = semanticModel.GetEnclosingSymbol(position); enclosing is not null; enclosing = enclosing.ContainingSymbol)
        {
            if (enclosing is INamedTypeSymbol type)
                return type;
        }
        return null;
    }

    private readonly record struct OriginEvidence(string Kind, int? InheritanceDepth);
}

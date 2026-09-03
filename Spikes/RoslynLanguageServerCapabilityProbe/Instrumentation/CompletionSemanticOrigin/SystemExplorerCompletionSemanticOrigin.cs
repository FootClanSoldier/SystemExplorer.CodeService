// TEMPORARY THROWAWAY ROSLYN INSTRUMENTATION TEMPLATE. This file is not compiled into the probe.
using System;
using System.Collections.Immutable;
using System.Linq;
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

        OriginEvidence? aggregate = null;
        foreach (ISymbol symbol in symbols)
        {
            OriginEvidence current = Classify(symbol, context);
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

    private static OriginEvidence Classify(ISymbol symbol, SyntaxContext context)
    {
        if (symbol is ILocalSymbol or IParameterSymbol or IRangeVariableSymbol
            || symbol is IMethodSymbol { MethodKind: MethodKind.LocalFunction })
        {
            return new("Local", null);
        }

        if (symbol is IMethodSymbol { ReducedFrom: not null } reduced)
            return ClassifyDeclarationAuthority(reduced.ReducedFrom);

        ISymbol declaration = symbol;
        INamedTypeSymbol? lexicalType = GetLexicalContainingType(context.SemanticModel, context.Position);
        if (declaration.ContainingType is INamedTypeSymbol containingType && lexicalType is not null)
        {
            INamedTypeSymbol declarationType = containingType.OriginalDefinition;
            INamedTypeSymbol current = lexicalType.OriginalDefinition;
            if (SymbolEqualityComparer.Default.Equals(declarationType, current))
                return new("CurrentType", 0);

            int depth = 1;
            for (INamedTypeSymbol? baseType = lexicalType.BaseType; baseType is not null; baseType = baseType.BaseType, depth++)
            {
                if (SymbolEqualityComparer.Default.Equals(declarationType, baseType.OriginalDefinition))
                    return new("BaseType", depth);
            }
        }

        return ClassifyDeclarationAuthority(declaration);
    }

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

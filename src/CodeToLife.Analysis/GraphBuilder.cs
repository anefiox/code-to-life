using System.Collections.Concurrent;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CodeToLife.Analysis;

/// <summary>Turns compiled projects into a <see cref="CodeGraph"/>: types declared in source and every way they touch.</summary>
public static class GraphBuilder
{
    public static CodeGraph Analyze(string path, CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(path);
        var projects = ProjectLoader.Discover(root);
        var compiled = ProjectLoader.Compile(projects, cancellationToken);
        var name = File.Exists(root) ? Path.GetFileNameWithoutExtension(root) : new DirectoryInfo(root).Name;
        return Build(File.Exists(root) ? Path.GetDirectoryName(root)! : root, name, compiled, cancellationToken);
    }

    public static CodeGraph Build(string root, string name, IReadOnlyList<(ProjectFile Project, CSharpCompilation Compilation)> compiled, CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>();
        var ids = new TypeIds();

        // Pass 1: every type declared in source, in every project, so edges can point across projects.
        var declared = new List<(ProjectFile Project, CSharpCompilation Compilation, INamedTypeSymbol Type)>();
        foreach (var (project, compilation) in compiled)
        {
            foreach (var type in SourceTypes(compilation.Assembly.GlobalNamespace))
            {
                cancellationToken.ThrowIfCancellationRequested();
                declared.Add((project, compilation, type));
                ids.Register(type);
            }
        }
        ids.Seal();

        // Pass 2: members, relationships and calls. Each project's semantic models are independent.
        var edges = new ConcurrentDictionary<(string, string, string), EdgeAccumulator>();
        var calls = new ConcurrentDictionary<(string, string), CallAccumulator>();
        var types = new ConcurrentBag<TypeNode>();
        var errors = new ConcurrentDictionary<string, int>();

        Parallel.ForEach(declared, new ParallelOptions { CancellationToken = cancellationToken }, item =>
        {
            var (project, compilation, type) = item;
            var collector = new Collector(ids, compilation, edges, calls);
            types.Add(collector.Describe(type, project.Name));
        });

        foreach (var (project, compilation) in compiled)
        {
            var count = compilation.GetDiagnostics(cancellationToken).Count(d => d.Severity == DiagnosticSeverity.Error);
            errors[project.Path] = count;
            if (count > 0)
                warnings.Add($"{project.Name}: {count} compile error(s) without a full build. Types still show; some calls into packages may be missing.");
        }

        var edgeList = edges.Values
            .Where(e => e.Source != e.Target)
            .Select(e => new TypeEdge(e.Source, e.Target, e.Kind, e.Count, e.File, e.Line))
            .OrderBy(e => e.Source, StringComparer.Ordinal).ThenBy(e => e.Target, StringComparer.Ordinal).ThenBy(e => e.Kind, StringComparer.Ordinal)
            .ToList();

        // Metrics that need the whole graph.
        var incoming = edgeList.Where(e => e.Kind != EdgeKind.Nested).GroupBy(e => e.Target).ToDictionary(g => g.Key, g => g.Select(e => e.Source).Distinct().Count());
        var outgoing = edgeList.Where(e => e.Kind != EdgeKind.Nested).GroupBy(e => e.Source).ToDictionary(g => g.Key, g => g.Select(e => e.Target).Distinct().Count());
        var typeList = types
            .Select(t => t with { Metrics = t.Metrics with { IncomingCount = incoming.GetValueOrDefault(t.Id), OutgoingCount = outgoing.GetValueOrDefault(t.Id) } })
            .OrderBy(t => t.Id, StringComparer.Ordinal)
            .ToList();

        var projectInfos = compiled.Select(c => new ProjectInfo(
            c.Project.Name, c.Project.Path, c.Project.OutputKind,
            c.Project.ProjectReferences.Select(r => compiled.FirstOrDefault(o => o.Project.Path.Equals(r, StringComparison.OrdinalIgnoreCase)).Project?.Name ?? Path.GetFileNameWithoutExtension(r)).ToList(),
            c.Project.SourceFiles.Count, errors.GetValueOrDefault(c.Project.Path), ProjectLoader.BuiltAssemblyIn(c.Project))).ToList();

        var callList = calls.Values
            .Select(c => new CallEdge(c.Source, c.Target, c.Count, c.Kind))
            .OrderBy(c => c.Source, StringComparer.Ordinal).ThenBy(c => c.Target, StringComparer.Ordinal)
            .ToList();

        return new CodeGraph(root, name, DateTimeOffset.Now, projectInfos, typeList, edgeList, callList, warnings);
    }

    static IEnumerable<INamedTypeSymbol> SourceTypes(INamespaceSymbol ns)
    {
        foreach (var member in ns.GetMembers())
        {
            if (member is INamespaceSymbol child)
                foreach (var t in SourceTypes(child)) yield return t;
            else if (member is INamedTypeSymbol type)
                foreach (var t in WithNested(type)) yield return t;
        }
    }

    static IEnumerable<INamedTypeSymbol> WithNested(INamedTypeSymbol type)
    {
        if (!IsUserCode(type)) yield break;
        yield return type;
        foreach (var nested in type.GetTypeMembers())
            foreach (var t in WithNested(nested)) yield return t;
    }

    static bool IsUserCode(ISymbol symbol) =>
        symbol.Locations.Any(l => l.IsInSource && l.SourceTree?.FilePath is { } f && !IsGeneratedFile(f))
        && !symbol.GetAttributes().Any(a => a.AttributeClass?.Name is "CompilerGeneratedAttribute" or "GeneratedCodeAttribute");

    internal static bool IsGeneratedFile(string path) =>
        path.StartsWith('<')
        || path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase);

    sealed class EdgeAccumulator(string source, string target, string kind, string? file, int line)
    {
        public string Source { get; } = source;
        public string Target { get; } = target;
        public string Kind { get; } = kind;
        public string? File { get; } = file;
        public int Line { get; } = line;
        int _count;
        public int Count => _count;
        public void Add() => Interlocked.Increment(ref _count);
    }

    sealed class CallAccumulator(string source, string target, string kind)
    {
        public string Source { get; } = source;
        public string Target { get; } = target;
        public string Kind { get; } = kind;
        int _count;
        public int Count => _count;
        public void Add() => Interlocked.Increment(ref _count);
    }

    /// <summary>Gives each source type a stable id, disambiguating same-named types in different projects.</summary>
    sealed class TypeIds
    {
        readonly Dictionary<(string Assembly, string Name), string> _ids = [];
        readonly Dictionary<string, int> _nameCounts = new(StringComparer.Ordinal);

        public void Register(INamedTypeSymbol type)
        {
            var name = FullName(type, '.');
            _ids[(type.ContainingAssembly.Name, name)] = name;
            _nameCounts[name] = _nameCounts.GetValueOrDefault(name) + 1;
        }

        public void Seal()
        {
            foreach (var key in _ids.Keys.ToList())
                if (_nameCounts[key.Name] > 1) _ids[key] = $"{key.Name} [{key.Assembly}]";
        }

        public string? IdOf(ITypeSymbol? symbol)
        {
            if (symbol is not INamedTypeSymbol named || named.ContainingAssembly is null) return null;
            named = named.OriginalDefinition;
            return _ids.GetValueOrDefault((named.ContainingAssembly.Name, FullName(named, '.')));
        }

        public static string FullName(INamedTypeSymbol type, char nestedSeparator)
        {
            var parts = new Stack<string>();
            for (var t = type; t is not null; t = t.ContainingType)
                parts.Push(t.MetadataName);
            var ns = type.ContainingNamespace is { IsGlobalNamespace: false } n ? n.ToDisplayString() + "." : "";
            return ns + string.Join(nestedSeparator, parts);
        }
    }

    /// <summary>Describes one type and records the edges and calls that start from it.</summary>
    sealed class Collector(
        TypeIds ids,
        CSharpCompilation compilation,
        ConcurrentDictionary<(string, string, string), EdgeAccumulator> edges,
        ConcurrentDictionary<(string, string), CallAccumulator> calls)
    {
        string _typeId = "";

        public TypeNode Describe(INamedTypeSymbol type, string project)
        {
            _typeId = ids.IdOf(type)!;
            var declarations = type.DeclaringSyntaxReferences
                .Where(r => !IsGeneratedFile(r.SyntaxTree.FilePath))
                .Select(r => r.GetSyntax())
                .ToList();
            var primary = declarations.FirstOrDefault();
            var (file, line, endLine) = primary is null ? (null, 0, 0) : Span(primary);

            Relationships(type, primary);

            var members = new List<MemberNode>();
            foreach (var member in type.GetMembers())
            {
                if (member.IsImplicitlyDeclared && !IsTopLevelMain(member)) continue;
                if (member is INamedTypeSymbol) continue;
                if (member is IMethodSymbol { AssociatedSymbol: not null }) continue; // accessors belong to their property
                if (member is IMethodSymbol { MethodKind: MethodKind.EventAdd or MethodKind.EventRemove or MethodKind.PropertyGet or MethodKind.PropertySet }) continue;
                if (!member.Locations.Any(l => l.IsInSource)) continue;
                var described = DescribeMember(type, member);
                if (described is not null) members.Add(described);
            }
            members.Sort((a, b) => string.CompareOrdinal(a.File, b.File) is var c && c != 0 ? c : a.Line.CompareTo(b.Line));

            if (type.ContainingType is { } outer && ids.IdOf(outer) is { } outerId)
                AddEdge(_typeId, outerId, EdgeKind.Nested, primary);

            var lines = declarations.Sum(d => { var s = d.GetLocation().GetLineSpan(); return s.EndLinePosition.Line - s.StartLinePosition.Line + 1; });
            return new TypeNode(
                _typeId,
                type.Name + (type.Arity > 0 ? "<" + string.Join(", ", type.TypeParameters.Select(p => p.Name)) + ">" : ""),
                type.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() : "",
                project,
                KindOf(type),
                Accessibility(type.DeclaredAccessibility),
                Modifiers(type),
                type.ContainingType is null ? null : ids.IdOf(type.ContainingType),
                file, line, endLine,
                SummaryOf(type),
                TypeIds.FullName(type, '+'),
                members,
                new TypeMetrics(lines, members.Count, members.Sum(m => m.Complexity), members.Count == 0 ? 0 : members.Max(m => m.Complexity), 0, 0),
                AttributeNames(type));
        }

        /// <summary>True when a primary constructor parameter is read inside a member body, which makes the compiler keep it as a field.</summary>
        static bool IsCaptured(IParameterSymbol parameter, TypeDeclarationSyntax type, SemanticModel model) =>
            type.Members.Any(member => member.DescendantNodes().OfType<IdentifierNameSyntax>()
                .Any(id => id.Identifier.ValueText == parameter.Name && SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(id).Symbol, parameter)));

        static bool IsTopLevelMain(ISymbol member) =>
            member is IMethodSymbol { Name: "<Main>$" } && member.Locations.Any(l => l.IsInSource);

        void Relationships(INamedTypeSymbol type, SyntaxNode? at)
        {
            if (type.BaseType is { } baseType && ids.IdOf(baseType) is { } baseId)
                AddEdge(_typeId, baseId, EdgeKind.Inherits, at);
            foreach (var arg in type.BaseType?.TypeArguments ?? [])
                foreach (var t in Mentioned(arg)) AddEdge(_typeId, t, EdgeKind.Uses, at);

            foreach (var iface in type.Interfaces)
            {
                if (ids.IdOf(iface) is { } ifaceId)
                    AddEdge(_typeId, ifaceId, type.TypeKind == TypeKind.Interface ? EdgeKind.Inherits : EdgeKind.Implements, at);
                foreach (var arg in iface.TypeArguments)
                    foreach (var t in Mentioned(arg)) AddEdge(_typeId, t, EdgeKind.Uses, at);
            }

            if (type.TypeKind == TypeKind.Delegate && type.DelegateInvokeMethod is { } invoke)
                foreach (var t in SignatureTypes(invoke)) AddEdge(_typeId, t, EdgeKind.Uses, at);
        }

        MemberNode? DescribeMember(INamedTypeSymbol type, ISymbol member)
        {
            var syntax = member.DeclaringSyntaxReferences.Select(r => r.GetSyntax()).FirstOrDefault(s => !IsGeneratedFile(s.SyntaxTree.FilePath));
            if (syntax is null) return null;
            var memberId = MemberId(member);
            var topLevel = IsTopLevelMain(member);

            // Fields are declared as `int a, b;`, but the interesting node is the whole declaration.
            SyntaxNode declaration = syntax is VariableDeclaratorSyntax { Parent.Parent: { } fieldDecl } ? fieldDecl : syntax;
            var (file, line, endLine) = Span(declaration);
            var bodyLine = line;
            var complexity = 0;
            string kind;
            string? typeName = null;

            switch (member)
            {
                case IFieldSymbol field:
                    kind = type.TypeKind == TypeKind.Enum ? "enum member" : field.IsConst ? "const" : "field";
                    typeName = Display(field.Type);
                    if (type.TypeKind != TypeKind.Enum)
                        foreach (var t in Mentioned(field.Type)) AddEdge(_typeId, t, EdgeKind.Has, declaration);
                    break;
                case IPropertySymbol property:
                    kind = property.IsIndexer ? "indexer" : "property";
                    typeName = Display(property.Type);
                    foreach (var t in Mentioned(property.Type)) AddEdge(_typeId, t, EdgeKind.Has, declaration);
                    foreach (var p in property.Parameters)
                        foreach (var t in Mentioned(p.Type)) AddEdge(_typeId, t, EdgeKind.Uses, declaration);
                    break;
                case IEventSymbol evt:
                    kind = "event";
                    typeName = Display(evt.Type);
                    foreach (var t in Mentioned(evt.Type)) AddEdge(_typeId, t, EdgeKind.Uses, declaration);
                    break;
                case IMethodSymbol method:
                    kind = topLevel ? "top-level statements" : method.MethodKind switch
                    {
                        MethodKind.Constructor => "constructor",
                        MethodKind.StaticConstructor => "static constructor",
                        MethodKind.Destructor => "finalizer",
                        MethodKind.UserDefinedOperator or MethodKind.Conversion => "operator",
                        _ => "method",
                    };
                    typeName = method.MethodKind is MethodKind.Constructor or MethodKind.StaticConstructor ? null : Display(method.ReturnType);
                    foreach (var t in SignatureTypes(method)) AddEdge(_typeId, t, EdgeKind.Uses, declaration);
                    // A record's primary constructor parameters become its properties.
                    if (syntax is TypeDeclarationSyntax)
                    {
                        kind = "primary constructor";
                        // A class's primary constructor parameters are captured as hidden fields when the body uses them.
                        if (!type.IsRecord && syntax is TypeDeclarationSyntax typeSyntax)
                        {
                            var model = compilation.GetSemanticModel(syntax.SyntaxTree);
                            foreach (var parameter in method.Parameters.Where(p => IsCaptured(p, typeSyntax, model)))
                                foreach (var t in Mentioned(parameter.Type)) AddEdge(_typeId, t, EdgeKind.Has, declaration);
                        }
                    }
                    break;
                default:
                    return null;
            }

            // Code that runs: walk it for calls, creations and type mentions.
            var bodies = BodiesOf(syntax, topLevel).ToList();
            if (bodies.Count > 0)
            {
                complexity = bodies.Sum(Complexity.Of) - (bodies.Count - 1);
                bodyLine = FirstExecutableLine(bodies[0]) ?? line;
                var model = compilation.GetSemanticModel(bodies[0].SyntaxTree);
                foreach (var body in bodies) Walk(body, model, memberId);
            }

            return new MemberNode(
                memberId,
                topLevel ? "<top-level statements>" : member.Name,
                kind,
                topLevel ? "Program entry point" : member.ToDisplayString(SignatureFormat),
                typeName,
                Accessibility(member.DeclaredAccessibility),
                member.IsStatic,
                member.IsAbstract,
                member is IMethodSymbol { IsAsync: true },
                file, line, endLine, bodyLine,
                complexity,
                SummaryOf(member),
                ImplementedMembers(type, member),
                AttributeNames(member));
        }

        static IEnumerable<SyntaxNode> BodiesOf(SyntaxNode syntax, bool topLevel)
        {
            if (topLevel)
            {
                // The synthesized Main's syntax is the compilation unit; its statements are the globals.
                foreach (var global in syntax.SyntaxTree.GetRoot().ChildNodes().OfType<GlobalStatementSyntax>()) yield return global;
                yield break;
            }
            switch (syntax)
            {
                case BaseMethodDeclarationSyntax m:
                    if (m.Body is not null) yield return m.Body;
                    if (m.ExpressionBody is not null) yield return m.ExpressionBody;
                    if (m is ConstructorDeclarationSyntax { Initializer: { } init }) yield return init;
                    break;
                case PropertyDeclarationSyntax p:
                    if (p.ExpressionBody is not null) yield return p.ExpressionBody;
                    if (p.Initializer is not null) yield return p.Initializer;
                    foreach (var a in p.AccessorList?.Accessors ?? default)
                    {
                        if (a.Body is not null) yield return a.Body;
                        if (a.ExpressionBody is not null) yield return a.ExpressionBody;
                    }
                    break;
                case IndexerDeclarationSyntax i:
                    if (i.ExpressionBody is not null) yield return i.ExpressionBody;
                    foreach (var a in i.AccessorList?.Accessors ?? default)
                    {
                        if (a.Body is not null) yield return a.Body;
                        if (a.ExpressionBody is not null) yield return a.ExpressionBody;
                    }
                    break;
                case EventDeclarationSyntax e:
                    foreach (var a in e.AccessorList?.Accessors ?? default)
                        if (a.Body is not null) yield return a.Body;
                    break;
                case VariableDeclaratorSyntax { Initializer: { } fieldInit }:
                    yield return fieldInit;
                    break;
                case LocalFunctionStatementSyntax:
                    break;
            }
        }

        static int? FirstExecutableLine(SyntaxNode body)
        {
            var first = body switch
            {
                BlockSyntax block => (SyntaxNode?)block.Statements.FirstOrDefault(),
                ArrowExpressionClauseSyntax arrow => arrow.Expression,
                _ => body,
            };
            return first is null ? null : first.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        }

        void Walk(SyntaxNode body, SemanticModel model, string memberId)
        {
            foreach (var node in body.DescendantNodesAndSelf())
            {
                switch (node)
                {
                    case BaseObjectCreationExpressionSyntax creation:
                    {
                        var info = model.GetSymbolInfo(creation);
                        var ctor = (info.Symbol ?? info.CandidateSymbols.FirstOrDefault()) as IMethodSymbol;
                        var created = ctor?.ContainingType ?? model.GetTypeInfo(creation).Type as INamedTypeSymbol;
                        if (ids.IdOf(created) is { } createdId)
                        {
                            AddEdge(_typeId, createdId, EdgeKind.Creates, creation);
                            if (ctor is not null && !ctor.IsImplicitlyDeclared) AddCall(memberId, MemberId(ctor), "creates");
                        }
                        foreach (var arg in (created as INamedTypeSymbol)?.TypeArguments ?? [])
                            foreach (var t in Mentioned(arg)) AddEdge(_typeId, t, EdgeKind.Uses, creation);
                        break;
                    }
                    case InvocationExpressionSyntax invocation:
                    {
                        var info = model.GetSymbolInfo(invocation);
                        if ((info.Symbol ?? info.CandidateSymbols.FirstOrDefault()) is IMethodSymbol method)
                        {
                            method = method.ReducedFrom ?? method;
                            if (method.MethodKind == MethodKind.LocalFunction) break;
                            if (ids.IdOf(method.ContainingType) is { } targetType)
                            {
                                AddEdge(_typeId, targetType, EdgeKind.Calls, invocation);
                                var target = method.OriginalDefinition;
                                AddCall(memberId, MemberId(target), target.IsAbstract || target.IsVirtual || target.ContainingType.TypeKind == TypeKind.Interface ? "virtual" : "direct");
                            }
                            foreach (var arg in method.TypeArguments)
                                foreach (var t in Mentioned(arg)) AddEdge(_typeId, t, EdgeKind.Uses, invocation);
                        }
                        break;
                    }
                    case MemberAccessExpressionSyntax or IdentifierNameSyntax when IsStandaloneReference(node):
                    {
                        var symbol = model.GetSymbolInfo(node).Symbol;
                        switch (symbol)
                        {
                            case IFieldSymbol { IsConst: true } or IFieldSymbol { ContainingType.TypeKind: TypeKind.Enum }:
                                // Reading a constant or enum value isn't a call: it's a mention.
                                if (ids.IdOf(symbol.ContainingType) is { } constOwner) AddEdge(_typeId, constOwner, EdgeKind.Uses, node);
                                break;
                            case IPropertySymbol or IEventSymbol or IFieldSymbol when !symbol.IsImplicitlyDeclared:
                                if (ids.IdOf(symbol.ContainingType) is { } owner && owner != _typeId)
                                {
                                    AddEdge(_typeId, owner, EdgeKind.Calls, node);
                                    if (symbol is IPropertySymbol or IEventSymbol) AddCall(memberId, MemberId(symbol.OriginalDefinition), "property");
                                }
                                else if (symbol is IPropertySymbol && ids.IdOf(symbol.ContainingType) is not null)
                                    AddCall(memberId, MemberId(symbol.OriginalDefinition), "property");
                                break;
                            case IMethodSymbol { MethodKind: MethodKind.Ordinary } group:
                                // A method passed as a delegate: it will be called, just not here.
                                if (ids.IdOf(group.ContainingType) is { } groupOwner)
                                {
                                    AddEdge(_typeId, groupOwner, EdgeKind.Calls, node);
                                    AddCall(memberId, MemberId(group.OriginalDefinition), "delegate");
                                }
                                break;
                            case INamedTypeSymbol named when node is IdentifierNameSyntax:
                                foreach (var t in Mentioned(named)) AddEdge(_typeId, t, EdgeKind.Uses, node);
                                break;
                        }
                        break;
                    }
                    case GenericNameSyntax or PredefinedTypeSyntax or ArrayTypeSyntax or NullableTypeSyntax when node.Parent is not (MemberAccessExpressionSyntax or InvocationExpressionSyntax):
                    {
                        if (model.GetTypeInfo(node).Type is { } typeSymbol)
                            foreach (var t in Mentioned(typeSymbol)) AddEdge(_typeId, t, EdgeKind.Uses, node);
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// A name that isn't being invoked and isn't the right half of <c>a.B</c> (the whole
        /// <c>a.B</c> is looked at instead), so each reference is counted once.
        /// </summary>
        static bool IsStandaloneReference(SyntaxNode node) =>
            !(node.Parent is InvocationExpressionSyntax invocation && invocation.Expression == node)
            && !(node.Parent is MemberAccessExpressionSyntax access && access.Name == node)
            && !(node.Parent is MemberBindingExpressionSyntax);

        IEnumerable<string> SignatureTypes(IMethodSymbol method)
        {
            if (!method.ReturnsVoid)
                foreach (var t in Mentioned(method.ReturnType)) yield return t;
            foreach (var p in method.Parameters)
                foreach (var t in Mentioned(p.Type)) yield return t;
        }

        /// <summary>Source types a type expression mentions: <c>Dictionary&lt;string, List&lt;Order&gt;&gt;</c> mentions Order.</summary>
        IEnumerable<string> Mentioned(ITypeSymbol type)
        {
            switch (type)
            {
                case IArrayTypeSymbol array:
                    foreach (var t in Mentioned(array.ElementType)) yield return t;
                    break;
                case IPointerTypeSymbol pointer:
                    foreach (var t in Mentioned(pointer.PointedAtType)) yield return t;
                    break;
                case INamedTypeSymbol named:
                    if (ids.IdOf(named) is { } id) yield return id;
                    foreach (var arg in named.TypeArguments)
                        foreach (var t in Mentioned(arg)) yield return t;
                    break;
            }
        }

        IReadOnlyList<string> ImplementedMembers(INamedTypeSymbol type, ISymbol member)
        {
            var result = new List<string>();
            var overridden = member switch
            {
                IMethodSymbol m => (ISymbol?)m.OverriddenMethod,
                IPropertySymbol p => p.OverriddenProperty,
                IEventSymbol e => e.OverriddenEvent,
                _ => null,
            };
            if (overridden is not null && ids.IdOf(overridden.ContainingType) is not null) result.Add(MemberId(overridden.OriginalDefinition));

            foreach (var iface in type.AllInterfaces)
            {
                if (ids.IdOf(iface) is null) continue;
                foreach (var candidate in iface.GetMembers())
                    if (SymbolEqualityComparer.Default.Equals(type.FindImplementationForInterfaceMember(candidate), member))
                        result.Add(MemberId(candidate.OriginalDefinition));
            }
            return result;
        }

        string MemberId(ISymbol member)
        {
            var owner = ids.IdOf(member.ContainingType) ?? member.ContainingType?.ToDisplayString() ?? "";
            return owner + "|" + (member.GetDocumentationCommentId() ?? member.Name);
        }

        void AddEdge(string source, string target, string kind, SyntaxNode? at)
        {
            if (source == target) return;
            var (file, line, _) = at is null ? (null, 0, 0) : Span(at);
            edges.GetOrAdd((source, target, kind), _ => new EdgeAccumulator(source, target, kind, file, line)).Add();
        }

        void AddCall(string source, string target, string kind)
        {
            if (source == target) return;
            calls.GetOrAdd((source, target), _ => new CallAccumulator(source, target, kind)).Add();
        }
    }

    static (string? File, int Line, int EndLine) Span(SyntaxNode node)
    {
        var span = node.GetLocation().GetLineSpan();
        // Start at the declaration, not at the doc comment or attributes above it.
        var start = node is MemberDeclarationSyntax member && member.Modifiers.Any()
            ? member.Modifiers.First().GetLocation().GetLineSpan().StartLinePosition.Line
            : span.StartLinePosition.Line;
        return (node.SyntaxTree.FilePath, start + 1, span.EndLinePosition.Line + 1);
    }

    static readonly SymbolDisplayFormat SignatureFormat = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameOnly,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeParameters | SymbolDisplayMemberOptions.IncludeType | SymbolDisplayMemberOptions.IncludeRef,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeName | SymbolDisplayParameterOptions.IncludeParamsRefOut | SymbolDisplayParameterOptions.IncludeDefaultValue,
        propertyStyle: SymbolDisplayPropertyStyle.ShowReadWriteDescriptor,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    static string Display(ITypeSymbol type) => type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

    static string KindOf(INamedTypeSymbol type) => type switch
    {
        { IsRecord: true, TypeKind: TypeKind.Struct } => "record struct",
        { IsRecord: true } => "record",
        { TypeKind: TypeKind.Interface } => "interface",
        { TypeKind: TypeKind.Struct } => "struct",
        { TypeKind: TypeKind.Enum } => "enum",
        { TypeKind: TypeKind.Delegate } => "delegate",
        { IsStatic: true } => "static class",
        _ => "class",
    };

    static string Accessibility(Microsoft.CodeAnalysis.Accessibility accessibility) => accessibility switch
    {
        Microsoft.CodeAnalysis.Accessibility.Public => "public",
        Microsoft.CodeAnalysis.Accessibility.Internal => "internal",
        Microsoft.CodeAnalysis.Accessibility.Protected => "protected",
        Microsoft.CodeAnalysis.Accessibility.ProtectedOrInternal => "protected internal",
        Microsoft.CodeAnalysis.Accessibility.ProtectedAndInternal => "private protected",
        Microsoft.CodeAnalysis.Accessibility.Private => "private",
        _ => "internal",
    };

    static IReadOnlyList<string> Modifiers(INamedTypeSymbol type)
    {
        var list = new List<string>();
        if (type.IsAbstract && type.TypeKind == TypeKind.Class) list.Add("abstract");
        if (type.IsSealed && type.TypeKind == TypeKind.Class && !type.IsStatic) list.Add("sealed");
        if (type.IsStatic) list.Add("static");
        if (type.DeclaringSyntaxReferences.Length > 1) list.Add("partial");
        if (type.IsGenericType) list.Add("generic");
        return list;
    }

    static IReadOnlyList<string> AttributeNames(ISymbol symbol) =>
        symbol.GetAttributes()
            .Select(a => a.AttributeClass?.Name)
            .Where(n => n is not null && n != "CompilerGeneratedAttribute" && n != "NullableContextAttribute" && n != "NullableAttribute")
            .Select(n => n!.EndsWith("Attribute", StringComparison.Ordinal) ? n[..^"Attribute".Length] : n!)
            .Distinct()
            .ToList();

    static string? SummaryOf(ISymbol symbol)
    {
        var xml = symbol.GetDocumentationCommentXml();
        if (string.IsNullOrWhiteSpace(xml)) return null;
        try
        {
            var summary = XElement.Parse(xml).Element("summary");
            if (summary is null) return null;
            var text = string.Concat(summary.Nodes().Select(n => n switch
            {
                XText t => t.Value,
                XElement e when e.Attribute("cref") is { } cref => cref.Value.Split('.').Last(),
                XElement e when e.Attribute("langword") is { } word => word.Value,
                XElement e => e.Value,
                _ => "",
            }));
            return string.Join(" ", text.Split((char[])[' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries));
        }
        catch { return null; }
    }
}

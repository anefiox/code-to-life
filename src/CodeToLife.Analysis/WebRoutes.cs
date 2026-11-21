using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CodeToLife.Analysis;

/// <summary>
/// Finds the web addresses an ASP.NET Core app answers, and the Razor views its handlers render:
/// minimal APIs (<c>app.MapGet("/x", ...)</c>, including <c>MapGroup</c> prefixes), controller
/// actions (attribute routes, or the default <c>/{controller}/{action}</c> route) and Razor Pages.
///
/// It reads the syntax first and only uses the semantic model to confirm or fill in values, so it
/// still works when the ASP.NET Core assemblies can't be resolved (a project that hasn't been built).
/// </summary>
sealed partial class WebRoutes(string root, ConcurrentBag<RouteInfo> routes, ConcurrentDictionary<string, List<string>> views)
{
    void AddRoute(RouteInfo route) => routes.Add(route);

    static readonly Dictionary<string, string> Verbs = new(StringComparer.Ordinal)
    {
        ["HttpGet"] = "GET", ["HttpPost"] = "POST", ["HttpPut"] = "PUT", ["HttpDelete"] = "DELETE",
        ["HttpPatch"] = "PATCH", ["HttpHead"] = "HEAD", ["HttpOptions"] = "OPTIONS",
    };

    static readonly Dictionary<string, string> MapVerbs = new(StringComparer.Ordinal)
    {
        ["MapGet"] = "GET", ["MapPost"] = "POST", ["MapPut"] = "PUT", ["MapDelete"] = "DELETE", ["MapPatch"] = "PATCH", ["MapMethods"] = "*",
    };

    public TypeContext For(INamedTypeSymbol type, SyntaxNode? declaration, CSharpCompilation compilation) =>
        new(this, type, declaration as TypeDeclarationSyntax, compilation);

    void AddView(string memberId, IEnumerable<string> names)
    {
        var list = views.GetOrAdd(memberId, _ => []);
        lock (list) list.AddRange(names);
    }

    string Relative(string path) => System.IO.Path.GetRelativePath(root, path).Replace('\\', '/');

    /// <summary>What one type contributes: its controller actions or page handlers, and any minimal APIs it maps.</summary>
    public sealed class TypeContext
    {
        readonly WebRoutes _owner;
        readonly INamedTypeSymbol _type;
        readonly CSharpCompilation _compilation;
        readonly string? _controller;          // "Orders" for OrdersController
        readonly bool _conventional;           // an MVC controller without attribute routes on the class
        readonly List<(string Template, bool Fixed)> _prefixes = [];
        readonly string? _page;                // a Razor Page's address, e.g. "/Products/Edit"
        readonly string? _pageView;            // its .cshtml, relative to the root and without the extension

        internal TypeContext(WebRoutes owner, INamedTypeSymbol type, TypeDeclarationSyntax? declaration, CSharpCompilation compilation)
        {
            _owner = owner;
            _type = type;
            _compilation = compilation;
            if (declaration is null || type.IsAbstract || type.IsStatic || type.TypeKind != TypeKind.Class) return;

            var model = compilation.GetSemanticModel(declaration.SyntaxTree);
            var attributes = Attributes(declaration.AttributeLists).ToList();
            var isController = InheritsFrom(type, "Controller", "ControllerBase")
                || attributes.Any(a => a.Name is "ApiController" or "Controller")
                || (type.Name.EndsWith("Controller", StringComparison.Ordinal) && declaration.Members.OfType<MethodDeclarationSyntax>()
                    .Any(m => Attributes(m.AttributeLists).Any(a => Verbs.ContainsKey(a.Name))));
            if (isController && !attributes.Any(a => a.Name == "NonController"))
            {
                _controller = type.Name.EndsWith("Controller", StringComparison.Ordinal) && type.Name.Length > "Controller".Length
                    ? type.Name[..^"Controller".Length]
                    : type.Name;
                foreach (var route in attributes.Where(a => a.Name == "Route"))
                    if (Template(route.Syntax, model) is { } template) _prefixes.Add(template);
                _conventional = _prefixes.Count == 0 && InheritsFrom(type, "Controller");
                return;
            }

            // A Razor Page: Pages/Products/Edit.cshtml.cs holds the PageModel for Pages/Products/Edit.cshtml.
            var file = declaration.SyntaxTree.FilePath;
            if (InheritsFrom(type, "PageModel") && file.EndsWith(".cshtml.cs", StringComparison.OrdinalIgnoreCase))
            {
                var view = file[..^".cs".Length];
                var relative = owner.Relative(view);
                var pages = relative.LastIndexOf("Pages/", StringComparison.Ordinal);
                if (pages < 0 || (pages > 0 && relative[pages - 1] != '/')) return;
                var address = relative[(pages + "Pages/".Length)..^".cshtml".Length];
                if (address == "Index") address = "";
                else if (address.EndsWith("/Index", StringComparison.Ordinal)) address = address[..^"/Index".Length];
                address = "/" + address;
                if (File.Exists(view) && PageDirective(view) is { } directive)
                {
                    if (directive.StartsWith('/')) address = directive;
                    else if (directive.Length > 0) address = address.TrimEnd('/') + "/" + directive;
                }
                _page = address;
                _pageView = relative[..^".cshtml".Length];
            }
        }

        /// <summary>A method declared in this type: a controller action or a page handler becomes a route.</summary>
        public void Action(IMethodSymbol method, MethodDeclarationSyntax syntax, string memberId)
        {
            if (method.IsStatic || method.MethodKind != MethodKind.Ordinary || method.DeclaredAccessibility != Accessibility.Public) return;
            var (file, line) = (syntax.SyntaxTree.FilePath, syntax.Identifier.GetLocation().GetLineSpan().StartLinePosition.Line + 1);

            if (_page is not null && _pageView is not null)
            {
                var match = PageHandler().Match(method.Name);
                if (!match.Success) return;
                var verb = match.Groups[1].Value.ToUpperInvariant();
                var named = match.Groups[2].Value;
                _owner.AddRoute(new RouteInfo(verb, _page, true, memberId, file, line, "page", [],
                    named.Length > 0 ? $"Runs when the request asks for ?handler={named}." : null));
                _owner.AddView(memberId, [_pageView]);
                return;
            }

            if (_controller is null) return;
            var attributes = Attributes(syntax.AttributeLists).ToList();
            if (attributes.Any(a => a.Name == "NonAction")) return;
            var model = _compilation.GetSemanticModel(syntax.SyntaxTree);
            var action = ActionName(method.Name);

            var entries = new List<(string Verb, (string Template, bool Fixed)? Template)>();
            foreach (var a in attributes)
            {
                if (Verbs.TryGetValue(a.Name, out var verb)) entries.Add((verb, Template(a.Syntax, model)));
                else if (a.Name == "AcceptVerbs")
                    entries.Add((string.Join(", ", Arguments(a.Syntax).Select(arg => Value(arg, model).Text.ToUpperInvariant())), null));
            }
            var routeTemplates = attributes.Where(a => a.Name == "Route").Select(a => Template(a.Syntax, model)).OfType<(string, bool)>().ToList();
            if (routeTemplates.Count > 0)
            {
                var combined = new List<(string Verb, (string Template, bool Fixed)? Template)>();
                if (entries.Count == 0) combined.AddRange(routeTemplates.Select(t => ("*", ((string, bool)?)t)));
                foreach (var e in entries)
                    if (e.Template is null) combined.AddRange(routeTemplates.Select(t => (e.Verb, ((string, bool)?)t)));
                    else combined.Add(e);
                entries = combined;
            }

            string? note = null;
            if (entries.Count == 0)
            {
                if (_prefixes.Count > 0) entries.Add(("*", null));
                else if (_conventional)
                {
                    entries.Add(("*", ($"{_controller}/{action}", true)));
                    note = "Reached through the default {controller}/{action} route, if the app sets one up with MapControllerRoute or MapDefaultControllerRoute.";
                }
                else return;
            }

            var prefixes = _prefixes.Count > 0 ? _prefixes : [("", true)];
            foreach (var (verb, template) in entries)
                foreach (var prefix in prefixes)
                {
                    var (path, isFixed) = Combine(prefix, template);
                    path = Tokens().Replace(path, m => m.Groups[1].Value.ToLowerInvariant() switch
                    {
                        "controller" => _controller,
                        "action" => action,
                        _ => m.Value,
                    });
                    _owner.AddRoute(new RouteInfo(verb, path, isFixed, memberId, file, line, "controller", [], note));
                }
        }

        /// <summary>Any call in a method body: a minimal API mapping, or a controller returning a view.</summary>
        public void Invocation(InvocationExpressionSyntax invocation, SemanticModel model, string memberId, Func<ISymbol?, string?> graphId)
        {
            if (invocation.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: var map } access && MapVerbs.TryGetValue(map, out var verb))
                MinimalApi(invocation, access, verb, model, memberId, graphId);
            else if (_controller is not null && invocation.Expression is IdentifierNameSyntax { Identifier.ValueText: "View" or "PartialView" } or
                     MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax, Name.Identifier.ValueText: "View" or "PartialView" })
                ViewCall(invocation, model, memberId);
        }

        void MinimalApi(InvocationExpressionSyntax invocation, MemberAccessExpressionSyntax access, string verb, SemanticModel model, string memberId, Func<ISymbol?, string?> graphId)
        {
            var args = invocation.ArgumentList.Arguments;
            var handlerIndex = verb == "*" ? 2 : 1;
            if (args.Count <= handlerIndex) return;
            // A MapGet from some other library isn't a route; one that doesn't resolve probably is.
            var symbol = model.GetSymbolInfo(invocation) is var info ? info.Symbol ?? info.CandidateSymbols.FirstOrDefault() : null;
            if (symbol is not null && !(symbol.ContainingNamespace?.ToDisplayString() ?? "").StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)) return;

            var pattern = Value(args[0].Expression, model);
            if (pattern.Fixed && (pattern.Text.Contains(' ') || pattern.Text.Contains("://", StringComparison.Ordinal))) return;
            if (verb == "*") verb = Methods(args[1].Expression, model) ?? "*";
            var (prefix, prefixFixed, note) = Prefix(access.Expression, model, 0);
            var path = Join(prefix, pattern.Text);

            var handler = memberId;
            var inline = new List<string>();
            var expression = args[handlerIndex].Expression;
            if (expression is AnonymousFunctionExpressionSyntax lambda)
            {
                foreach (var node in lambda.Body.DescendantNodesAndSelf())
                {
                    var target = node switch
                    {
                        InvocationExpressionSyntax call => Resolve(model, call),
                        BaseObjectCreationExpressionSyntax creation => Resolve(model, creation),
                        _ => null,
                    };
                    if (graphId(target is IMethodSymbol m ? m.ReducedFrom ?? m : target) is { } id && !inline.Contains(id)) inline.Add(id);
                }
            }
            else if (graphId(Resolve(model, expression)) is { } method) handler = method;

            var line = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            _owner.AddRoute(new RouteInfo(verb, path, pattern.Fixed && prefixFixed, handler, invocation.SyntaxTree.FilePath, line, "minimal", inline, note,
                Inline: expression is AnonymousFunctionExpressionSyntax));
        }

        /// <summary>The prefixes added by <c>MapGroup</c> calls in front of a mapping.</summary>
        static (string Prefix, bool Fixed, string? Note) Prefix(ExpressionSyntax receiver, SemanticModel model, int depth)
        {
            if (depth > 16) return ("", true, null);
            switch (receiver)
            {
                case InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax inner } call:
                {
                    var outer = Prefix(inner.Expression, model, depth + 1);
                    if (inner.Name.Identifier.ValueText != "MapGroup" || call.ArgumentList.Arguments.Count == 0) return outer;
                    var group = Value(call.ArgumentList.Arguments[0].Expression, model);
                    return (Join(outer.Prefix, group.Text), outer.Fixed && group.Fixed, outer.Note);
                }
                case IdentifierNameSyntax name:
                    switch (model.GetSymbolInfo(name).Symbol)
                    {
                        case ILocalSymbol local:
                            var declarator = local.DeclaringSyntaxReferences.Select(r => r.GetSyntax()).OfType<VariableDeclaratorSyntax>().FirstOrDefault();
                            return declarator?.Initializer?.Value is { } value ? Prefix(value, model, depth + 1) : ("", true, null);
                        case IParameterSymbol:
                            return ("", true, "Mapped through a parameter, so a MapGroup prefix added by the caller isn't shown.");
                    }
                    break;
            }
            return ("", true, null);
        }

        void ViewCall(InvocationExpressionSyntax invocation, SemanticModel model, string memberId)
        {
            var method = model.GetEnclosingSymbol(invocation.SpanStart) is IMethodSymbol m ? m : null;
            while (method is { MethodKind: MethodKind.LocalFunction or MethodKind.AnonymousFunction }) method = method.ContainingSymbol as IMethodSymbol;
            var name = method is null ? null : ActionName(method.Name);
            var first = invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression;
            if (first is not null && (first is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression }
                || model.GetTypeInfo(first).Type?.SpecialType == SpecialType.System_String))
            {
                var value = Value(first, model);
                if (!value.Fixed) return;
                name = value.Text;
            }
            if (string.IsNullOrEmpty(name)) return;
            if (name.StartsWith("~/", StringComparison.Ordinal) || name.StartsWith('/') || name.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase))
                _owner.AddView(memberId, [name.TrimStart('~').TrimStart('/')[..^(name.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase) ? ".cshtml".Length : 0)]]);
            else
                _owner.AddView(memberId, [$"Views/{_controller}/{name}", $"Views/Shared/{name}", $"Pages/Shared/{name}"]);
        }

        static ISymbol? Resolve(SemanticModel model, SyntaxNode node) =>
            model.GetSymbolInfo(node) is var info ? info.Symbol ?? info.CandidateSymbols.FirstOrDefault() : null;

        static string? Methods(ExpressionSyntax expression, SemanticModel model)
        {
            var values = expression.DescendantNodesAndSelf().OfType<LiteralExpressionSyntax>()
                .Where(l => l.IsKind(SyntaxKind.StringLiteralExpression)).Select(l => l.Token.ValueText.ToUpperInvariant()).ToList();
            return values.Count > 0 ? string.Join(", ", values) : null;
        }

        static string ActionName(string method) =>
            method.EndsWith("Async", StringComparison.Ordinal) && method.Length > 5 ? method[..^5] : method;

        static (string Text, bool Fixed) Combine((string Template, bool Fixed) prefix, (string Template, bool Fixed)? template)
        {
            if (template is { } t && (t.Template.StartsWith('/') || t.Template.StartsWith("~/", StringComparison.Ordinal)))
                return (Join("", t.Template.TrimStart('~')), t.Fixed);
            return (Join(prefix.Template, template?.Template ?? ""), prefix.Fixed && (template?.Fixed ?? true));
        }

        static string Join(string prefix, string path)
        {
            var parts = (prefix.Trim('/') + "/" + path.Trim('/')).Trim('/');
            return "/" + parts;
        }

        static (string Template, bool Fixed)? Template(AttributeSyntax attribute, SemanticModel model)
        {
            var first = Arguments(attribute).FirstOrDefault();
            if (first is null) return null;
            var value = Value(first, model);
            return (value.Text, value.Fixed);
        }

        /// <summary>Positional arguments only: <c>Name = "x"</c> doesn't change the address.</summary>
        static IEnumerable<ExpressionSyntax> Arguments(AttributeSyntax attribute) =>
            attribute.ArgumentList?.Arguments.Where(a => a.NameEquals is null && a.NameColon is null).Select(a => a.Expression) ?? [];

        static (string Text, bool Fixed) Value(ExpressionSyntax expression, SemanticModel model)
        {
            if (expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression)) return (literal.Token.ValueText, true);
            try
            {
                if (model.GetConstantValue(expression) is { HasValue: true, Value: string constant }) return (constant, true);
            }
            catch (ArgumentException) { } // the node belongs to another tree
            return (expression.ToString(), false);
        }

        static IEnumerable<(string Name, AttributeSyntax Syntax)> Attributes(SyntaxList<AttributeListSyntax> lists) =>
            lists.SelectMany(l => l.Attributes).Select(a => (AttributeName(a.Name), a));

        static string AttributeName(NameSyntax name)
        {
            var text = name switch
            {
                QualifiedNameSyntax q => q.Right.Identifier.ValueText,
                AliasQualifiedNameSyntax a => a.Name.Identifier.ValueText,
                SimpleNameSyntax s => s.Identifier.ValueText,
                _ => name.ToString(),
            };
            return text.EndsWith("Attribute", StringComparison.Ordinal) ? text[..^"Attribute".Length] : text;
        }

        /// <summary>By name, so it works when ASP.NET Core can't be resolved (the base is then an error type with the written name).</summary>
        static bool InheritsFrom(INamedTypeSymbol type, params string[] names)
        {
            for (var t = type.BaseType; t is not null; t = t.BaseType)
                if (names.Contains(t.Name)) return true;
            return false;
        }

        /// <summary>The template in a page's <c>@page "{id:int}"</c> directive: "" when it has none, null when it isn't a page.</summary>
        static string? PageDirective(string cshtml)
        {
            try
            {
                foreach (var line in File.ReadLines(cshtml).Take(20))
                {
                    var match = PageLine().Match(line);
                    if (match.Success) return match.Groups[1].Value;
                }
            }
            catch (IOException) { }
            return null;
        }
    }

    [GeneratedRegex(@"^On(Get|Post|Put|Delete|Patch)(\w*?)(?:Async)?$")]
    private static partial Regex PageHandler();

    [GeneratedRegex(@"\[(controller|action|area)\]", RegexOptions.IgnoreCase)]
    private static partial Regex Tokens();

    [GeneratedRegex(@"^\s*@page(?:\s+""([^""]*)"")?\s*$")]
    private static partial Regex PageLine();
}

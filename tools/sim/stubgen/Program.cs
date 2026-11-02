using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

// args: repo out.cs
static class P
{
    static HashSet<string> LibNames;
    static readonly Dictionary<string, HashSet<string>> Hand = new Dictionary<string, HashSet<string>>();
    static bool ByHand(INamedTypeSymbol t, ISymbol m)
    {
        if (!Hand.TryGetValue(t.Name, out var h)) return false;
        if (m is IMethodSymbol me)
        {
            if (me.MethodKind == MethodKind.Constructor) return h.Contains("ctor:" + me.Parameters.Length);
            if (me.MethodKind == MethodKind.UserDefinedOperator) return h.Contains("op:" + OpName(me.Name));
            if (me.MethodKind == MethodKind.Conversion) return h.Contains("conv");
        }
        if (m is IPropertySymbol p && p.IsIndexer) return h.Contains("this[]");
        return h.Contains(m.Name);
    }
    static readonly HashSet<ISymbol> Used = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
    static readonly HashSet<INamedTypeSymbol> Types = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
    static readonly Queue<ISymbol> Work = new Queue<ISymbol>();
    static readonly SymbolDisplayFormat Fq = SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers | SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    static bool IsLib(ISymbol s) => s != null && s.ContainingAssembly != null && LibNames.Contains(s.ContainingAssembly.Name);

    static void AddType(ITypeSymbol t)
    {
        switch (t)
        {
            case null: return;
            case IArrayTypeSymbol a: AddType(a.ElementType); return;
            case IPointerTypeSymbol p: AddType(p.PointedAtType); return;
            case INamedTypeSymbol n:
                foreach (var ta in n.TypeArguments) AddType(ta);
                var d = n.OriginalDefinition;
                if (!IsLib(d)) return;
                if (Types.Add(d))
                {
                    if (d.ContainingType != null) AddType(d.ContainingType);
                    if (d.BaseType != null) AddType(d.BaseType);
                    foreach (var tp in d.TypeParameters) foreach (var c in tp.ConstraintTypes) AddType(c);
                    if (d.TypeKind == TypeKind.Delegate) AddMember(d.DelegateInvokeMethod);
                    if (d.TypeKind == TypeKind.Enum) foreach (var f in d.GetMembers().OfType<IFieldSymbol>()) AddMember(f);
                }
                return;
        }
    }
    static void AddMember(ISymbol m)
    {
        if (m == null) return;
        m = m.OriginalDefinition;
        if (m is IMethodSymbol red && red.ReducedFrom != null) m = red.ReducedFrom.OriginalDefinition;
        if (m is IMethodSymbol acc && acc.AssociatedSymbol != null && acc.AssociatedSymbol is IPropertySymbol or IEventSymbol) m = acc.AssociatedSymbol;
        if (m is INamedTypeSymbol nt) { AddType(nt); return; }
        if (!IsLib(m) || m is INamespaceSymbol || m is ITypeParameterSymbol || m is IAssemblySymbol) return;
        if (!Used.Add(m)) return;
        AddType(m.ContainingType);
        switch (m)
        {
            case IFieldSymbol f: AddType(f.Type); break;
            case IPropertySymbol p: AddType(p.Type); foreach (var a in p.Parameters) AddType(a.Type); break;
            case IEventSymbol e: AddType(e.Type); break;
            case IMethodSymbol me:
                AddType(me.ReturnType); foreach (var a in me.Parameters) AddType(a.Type);
                foreach (var tp in me.TypeParameters) foreach (var c in tp.ConstraintTypes) AddType(c);
                break;
        }
    }
    static void Sym(SymbolInfo i)
    {
        Add(i.Symbol); foreach (var c in i.CandidateSymbols) Add(c);
    }
    static void Add(ISymbol s)
    {
        if (s == null) return;
        if (s is ITypeSymbol t) { AddType(t); return; }
        if (s is IMethodSymbol m) foreach (var ta in m.TypeArguments) AddType(ta);
        if (s is IAliasSymbol al) { Add(al.Target); return; }
        AddMember(s);
    }

    static void Main(string[] args)
    {
        string repo = args[0], outPath = args[1];
        foreach (var c in CSharpSyntaxTree.ParseText(File.ReadAllText(args[2])).GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>())
        {
            string tn = c.Identifier.Text; if (!Hand.ContainsKey(tn)) Hand[tn] = new HashSet<string>();
            foreach (var mem in c.Members)
                switch (mem)
                {
                    case MethodDeclarationSyntax x: Hand[tn].Add(x.Identifier.Text); break;
                    case PropertyDeclarationSyntax x: Hand[tn].Add(x.Identifier.Text); break;
                    case FieldDeclarationSyntax x: foreach (var v in x.Declaration.Variables) Hand[tn].Add(v.Identifier.Text); break;
                    case OperatorDeclarationSyntax x: Hand[tn].Add("op:" + x.OperatorToken.Text); break;
                    case ConversionOperatorDeclarationSyntax x: Hand[tn].Add("conv"); break;
                    case ConstructorDeclarationSyntax x: Hand[tn].Add("ctor:" + x.ParameterList.Parameters.Count); break;
                    case IndexerDeclarationSyntax x: Hand[tn].Add("this[]"); break;
                }
        }
        var libFiles = Directory.GetFiles(Path.Combine(repo, "lib"), "*.dll");
        LibNames = new HashSet<string>(libFiles.Select(f => Path.GetFileNameWithoutExtension(f)));
        var std = Directory.GetDirectories(Path.Combine(Environment.GetEnvironmentVariable("HOME"), ".nuget/packages/netstandard.library.ref")).OrderBy(x => x).Last();
        var refs = libFiles.Concat(Directory.GetFiles(Path.Combine(std, "ref/netstandard2.1"), "*.dll")).Select(f => (MetadataReference)MetadataReference.CreateFromFile(f)).ToList();
        var parse = new CSharpParseOptions(LanguageVersion.Latest);
        var files = new[] { "src/TLDRevamp", "src/TLDRevamp.Multiplayer" }.SelectMany(d => Directory.GetFiles(Path.Combine(repo, d), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains("/obj/") && !f.Contains("/bin/")).ToList();
        var trees = files.Select(f => CSharpSyntaxTree.ParseText(File.ReadAllText(f), parse, f)).ToList();
        var comp = CSharpCompilation.Create("mod", trees, refs, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var errs = comp.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Console.WriteLine($"mod: {files.Count} files, against the real DLLs: {errs.Count} errors");
        foreach (var e in errs.Take(5)) Console.WriteLine("  " + e.ToString().Replace(repo, ""));
        foreach (var tree in trees)
        {
            var sm = comp.GetSemanticModel(tree);
            foreach (var n in tree.GetRoot().DescendantNodes())
            {
                switch (n)
                {
                    case ExpressionSyntax ex:
                        Sym(sm.GetSymbolInfo(ex));
                        var ti = sm.GetTypeInfo(ex); AddType(ti.Type); AddType(ti.ConvertedType);
                        var cv = sm.GetConversion(ex); if (cv.MethodSymbol != null) Add(cv.MethodSymbol);
                        break;
                    case ForEachStatementSyntax fe:
                        var fi = sm.GetForEachStatementInfo(fe); Add(fi.GetEnumeratorMethod); Add(fi.MoveNextMethod); Add(fi.CurrentProperty); AddType(fi.ElementType); break;
                    case AttributeSyntax at: Sym(sm.GetSymbolInfo(at)); break;
                    case ConstructorInitializerSyntax ci: Sym(sm.GetSymbolInfo(ci)); break;
                }
                var decl = (n is MemberDeclarationSyntax || n is VariableDeclaratorSyntax || n is ParameterSyntax || n is TypeParameterConstraintClauseSyntax) ? sm.GetDeclaredSymbol(n) : null;
                if (decl is IMethodSymbol dm && dm.OverriddenMethod != null) Add(dm.OverriddenMethod);
                if (decl is IPropertySymbol dp && dp.OverriddenProperty != null) Add(dp.OverriddenProperty);
                if (decl is INamedTypeSymbol dt)
                {
                    AddType(dt.BaseType);
                    foreach (var i in dt.AllInterfaces) { AddType(i); }
                }
                if (decl is IFieldSymbol df) AddType(df.Type);
                if (decl is IPropertySymbol dq) AddType(dq.Type);
                if (decl is IMethodSymbol dr) { AddType(dr.ReturnType); foreach (var a in dr.Parameters) AddType(a.Type); }
                if (decl is ILocalSymbol dl) AddType(dl.Type);
            }
        }
        foreach (var m in Used.OfType<IMethodSymbol>().Where(x => x.MethodKind == MethodKind.UserDefinedOperator).ToList())
        {
            var pair = m.Name switch { "op_Equality" => "op_Inequality", "op_Inequality" => "op_Equality", "op_LessThan" => "op_GreaterThan", "op_GreaterThan" => "op_LessThan", "op_True" => "op_False", "op_False" => "op_True", _ => null };
            if (pair != null) AddMember(m.ContainingType.GetMembers(pair).FirstOrDefault());
        }
        // what a used type's interfaces need is left to default members; abstract members of bases the mod derives from are kept virtual
        var byAsm = Types.GroupBy(t => t.ContainingAssembly.Name).OrderByDescending(g => g.Count()).Select(g => g.Key + ":" + g.Count());
        Console.WriteLine($"used: {Types.Count} types, {Used.Count} members ({string.Join(", ", byAsm)})");
        var memAsm = Used.GroupBy(t => t.ContainingAssembly.Name).OrderByDescending(g => g.Count()).Select(g => g.Key + ":" + g.Count());
        Console.WriteLine($"members by assembly: {string.Join(", ", memAsm)}");
        var sb = new StringBuilder("// generated: the game's and the libraries' API as far as the mod uses it. No game code, every body empty.\n#pragma warning disable\n");
        foreach (var ns in Types.Where(t => t.ContainingType == null).GroupBy(t => t.ContainingNamespace.IsGlobalNamespace ? "" : t.ContainingNamespace.ToDisplayString()).OrderBy(g => g.Key))
        {
            if (ns.Key != "") sb.Append("namespace ").Append(ns.Key).Append(" {\n");
            foreach (var t in ns.OrderBy(x => x.Name)) Emit(sb, t);
            if (ns.Key != "") sb.Append("}\n");
        }
        File.WriteAllText(outPath, sb.ToString());
        Console.WriteLine($"stubs: {sb.ToString().Count(c => c == '\n')} lines, {sb.Length / 1024} KB");
    }

    static string Name(ITypeSymbol t) => t.ToDisplayString(Fq);
    static string Esc(string n) => SyntaxFacts.GetKeywordKind(n) != SyntaxKind.None ? "@" + n : n;
    static string Acc(ISymbol s) => s.DeclaredAccessibility switch { Accessibility.Protected => "protected", Accessibility.ProtectedOrInternal => "protected", _ => "public" };
    static string Tps(IEnumerable<ITypeParameterSymbol> ps) { var l = ps.Select(p => p.Name).ToList(); return l.Count == 0 ? "" : "<" + string.Join(",", l) + ">"; }
    static string Cons(IEnumerable<ITypeParameterSymbol> ps)
    {
        var sb = new StringBuilder();
        foreach (var p in ps)
        {
            var c = new List<string>();
            if (p.HasReferenceTypeConstraint) c.Add("class");
            if (p.HasValueTypeConstraint) c.Add("struct");
            c.AddRange(p.ConstraintTypes.Select(Name));
            if (p.HasConstructorConstraint) c.Add("new()");
            if (c.Count > 0) sb.Append(" where ").Append(p.Name).Append(" : ").Append(string.Join(", ", c));
        }
        return sb.ToString();
    }
    static string Lit(object v, ITypeSymbol t)
    {
        if (v == null) return t.IsValueType || t.TypeKind == TypeKind.TypeParameter ? "default" : "null";
        if (t.TypeKind == TypeKind.Enum) return "(" + Name(t) + ")" + Convert.ToInt64(v);
        return SymbolDisplay.FormatPrimitive(v, true, false) is string s ? (v is float ? s + "f" : v is long ? s + "L" : v is uint ? s + "u" : s) : "default";
    }
    static string Pars(IEnumerable<IParameterSymbol> ps, bool ext = false)
    {
        return string.Join(", ", ps.Select((p, i) =>
        {
            var b = new StringBuilder();
            if (ext && i == 0) b.Append("this ");
            if (p.IsParams) b.Append("params ");
            if (p.RefKind == RefKind.Ref) b.Append("ref "); if (p.RefKind == RefKind.Out) b.Append("out "); if (p.RefKind == RefKind.In) b.Append("in ");
            b.Append(Name(p.Type)).Append(' ').Append(Esc(p.Name));
            if (p.HasExplicitDefaultValue) b.Append(" = ").Append(Lit(p.ExplicitDefaultValue, p.Type));
            return b.ToString();
        }));
    }
    static string Outs(IMethodSymbol m) => string.Concat(m.Parameters.Where(p => p.RefKind == RefKind.Out).Select(p => Esc(p.Name) + " = default; "));

    static void Emit(StringBuilder sb, INamedTypeSymbol t)
    {
        string acc = "public";
        if (t.TypeKind == TypeKind.Enum)
        {
            sb.Append($"{acc} enum {Esc(t.Name)} : {Name(t.EnumUnderlyingType)} {{ ");
            foreach (var f in t.GetMembers().OfType<IFieldSymbol>()) sb.Append(Esc(f.Name)).Append(" = unchecked((").Append(Name(t.EnumUnderlyingType)).Append(")").Append(f.ConstantValue).Append("), ");
            sb.Append("}\n"); return;
        }
        if (t.TypeKind == TypeKind.Delegate)
        {
            var inv = t.DelegateInvokeMethod;
            sb.Append($"{acc} delegate {(inv.RefKind == RefKind.Ref ? "ref " : "")}{Name(inv.ReturnType)} {Esc(t.Name)}{Tps(t.TypeParameters)}({Pars(inv.Parameters)}){Cons(t.TypeParameters)};\n"); return;
        }
        string kind = t.TypeKind == TypeKind.Struct ? "struct" : t.TypeKind == TypeKind.Interface ? "interface" : "class";
        string mod = t.IsStatic ? "static " : "";
        var bases = new List<string>();
        if (t.TypeKind == TypeKind.Class && t.BaseType != null && t.BaseType.SpecialType != SpecialType.System_Object) bases.Add(Name(t.BaseType));
        foreach (var i in t.Interfaces) if (IsLib(i) && Types.Contains(i.OriginalDefinition)) bases.Add(Name(i));
        bool disposable = t.TypeKind != TypeKind.Interface && t.Interfaces.Any(i => i.ToDisplayString() == "System.IDisposable") && !(t.BaseType != null && t.BaseType.AllInterfaces.Any(i => i.ToDisplayString() == "System.IDisposable"));
        if (disposable) bases.Add("System.IDisposable");
        for (var b = t.BaseType; b != null; b = b.BaseType) if (b.ToDisplayString() == "System.Attribute") { sb.Append("[System.AttributeUsage(System.AttributeTargets.All, AllowMultiple = true)]\n"); break; }
        sb.Append($"{acc} {mod}partial {kind} {Esc(t.Name)}{Tps(t.TypeParameters)}");
        if (bases.Count > 0) sb.Append(" : ").Append(string.Join(", ", bases));
        sb.Append(Cons(t.TypeParameters)).Append(" {\n");
        bool hasCtor = false;
        foreach (var m in t.GetMembers().Where(Used.Contains).OrderBy(x => x.Name, StringComparer.Ordinal))
        {
            if (ByHand(t, m)) continue;
            switch (m)
            {
                case IFieldSymbol f:
                    if (f.HasConstantValue) sb.Append($"  public const {Name(f.Type)} {Esc(f.Name)} = {Lit(f.ConstantValue, f.Type)};\n");
                    else sb.Append($"  {Acc(f)} {(f.IsStatic ? "static " : "")}{Name(f.Type)} {Esc(f.Name)};\n");
                    break;
                case IPropertySymbol p:
                    {
                        string st = p.IsStatic ? "static " : "";
                        string virt = !p.IsStatic && t.TypeKind == TypeKind.Class && (p.IsVirtual || p.IsAbstract) ? "virtual " : p.IsOverride ? "" : "";
                        string acs = t.TypeKind == TypeKind.Interface || p.IsIndexer
                            ? (p.GetMethod != null ? "get => default; " : "") + (p.SetMethod != null ? "set { } " : "")
                            : t.TypeKind == TypeKind.Struct ? (p.GetMethod != null ? "get => default; " : "") + (p.SetMethod != null ? "set { } " : "")
                            : (p.GetMethod != null || p.SetMethod != null ? "get; set; " : "");
                        string name = p.IsIndexer ? $"this[{Pars(p.Parameters)}]" : Esc(p.Name);
                        sb.Append($"  {Acc(p)} {st}{virt}{Name(p.Type)} {name} {{ {acs}}}\n");
                        break;
                    }
                case IEventSymbol e:
                    sb.Append($"  public {(e.IsStatic ? "static " : "")}event {Name(e.Type)} {Esc(e.Name)};\n"); break;
                case IMethodSymbol me:
                    if (me.MethodKind == MethodKind.StaticConstructor || me.MethodKind == MethodKind.Destructor) break;
                    if (me.MethodKind == MethodKind.Constructor)
                    {
                        if (me.Parameters.Length == 0) hasCtor = true;
                        if (t.TypeKind == TypeKind.Struct && me.Parameters.Length == 0) break;
                        sb.Append($"  public {Esc(t.Name)}({Pars(me.Parameters)}) {{ {(t.TypeKind == TypeKind.Struct ? "this = default; " : "")}{Outs(me)}}}\n");
                        break;
                    }
                    string body = (me.ReturnsVoid ? "{ " + Outs(me) + "}" : "{ " + Outs(me) + (me.ReturnType is IArrayTypeSymbol ar0 && ar0.Rank == 1 ? "return new " + Name(ar0.ElementType) + "[0]; }" : "return default; }"));
                    if (me.MethodKind == MethodKind.UserDefinedOperator && (me.Name == "op_Equality" || me.Name == "op_Inequality") && me.Parameters.Length == 2)
                    {
                        string aa = Esc(me.Parameters[0].Name), bb = Esc(me.Parameters[1].Name), not = me.Name == "op_Inequality" ? "!" : "";
                        body = t.TypeKind == TypeKind.Struct ? $"{{ return {not}{aa}.Equals({bb}); }}" : $"{{ return {not}ReferenceEquals({aa}, {bb}); }}";
                    }
                    if (me.MethodKind == MethodKind.UserDefinedOperator || me.MethodKind == MethodKind.Conversion)
                    {
                        string op = me.MethodKind == MethodKind.Conversion
                            ? $"{(me.Name == "op_Implicit" ? "implicit" : "explicit")} operator {Name(me.ReturnType)}"
                            : $"{Name(me.ReturnType)} operator {OpName(me.Name)}";
                        sb.Append($"  public static {op}({Pars(me.Parameters)}) {body}\n");
                        break;
                    }
                    string vr = me.IsStatic || t.TypeKind != TypeKind.Class ? "" : me.IsOverride ? "public new virtual ".Substring(0, 0) + "virtual " : (me.IsVirtual || me.IsAbstract) ? "virtual " : "";
                    if (t.TypeKind == TypeKind.Struct && me.IsOverride) vr = "override ";
                    if (t.TypeKind == TypeKind.Class && me.IsOverride && me.OverriddenMethod != null && !IsLib(me.OverriddenMethod)) vr = "override ";
                    sb.Append($"  {Acc(me)} {(me.IsStatic ? "static " : "")}{vr}{Name(me.ReturnType)} {Esc(me.Name)}{Tps(me.TypeParameters)}({Pars(me.Parameters, me.IsExtensionMethod)}){Cons(me.TypeParameters)} {body}\n");
                    break;
            }
        }
        if (disposable && !t.GetMembers("Dispose").Any(Used.Contains)) sb.Append("  public void Dispose() { }\n");
        if (!hasCtor && t.TypeKind == TypeKind.Class && !t.IsStatic && !(Hand.TryGetValue(t.Name, out var hh) && hh.Contains("ctor:0"))) sb.Append($"  public {Esc(t.Name)}() {{ }}\n");
        foreach (var n in t.GetTypeMembers().Where(x => Types.Contains(x)).OrderBy(x => x.Name)) Emit(sb, n);
        sb.Append("}\n");
    }
    static string OpName(string n) => n switch
    {
        "op_Addition" => "+", "op_Subtraction" => "-", "op_Multiply" => "*", "op_Division" => "/", "op_Equality" => "==", "op_Inequality" => "!=", "op_UnaryNegation" => "-",
        "op_LessThan" => "<", "op_GreaterThan" => ">", "op_LessThanOrEqual" => "<=", "op_GreaterThanOrEqual" => ">=", "op_True" => "true", "op_False" => "false", "op_LogicalNot" => "!", _ => n
    };
}

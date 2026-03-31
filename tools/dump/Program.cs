// Metadata-only dump of sts2.dll using System.Reflection.Metadata
// No assembly loading — reads PE/IL metadata directly, no deps required.

using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: dump <path-to-sts2.dll> [output.txt]");
    return 1;
}

string dllPath = args[0];
string outPath = args.Length > 1 ? args[1] : Path.ChangeExtension(dllPath, ".dump.txt");

using var fs = File.OpenRead(dllPath);
using var pe = new PEReader(fs);
var mr = pe.GetMetadataReader();

var sb = new System.Text.StringBuilder();

// ── Helper: decode a TypeDefinition's full name ──────────────────────────
string TypeName(TypeDefinitionHandle h)
{
    var t = mr.GetTypeDefinition(h);
    var ns = mr.GetString(t.Namespace);
    var name = mr.GetString(t.Name);
    return ns.Length > 0 ? $"{ns}.{name}" : name;
}

// ── Helper: decode a TypeReference full name ─────────────────────────────
string TypeRefName(EntityHandle h)
{
    if (h.IsNil) return "<nil>";
    if (h.Kind == HandleKind.TypeDefinition)
        return TypeName((TypeDefinitionHandle)h);
    if (h.Kind == HandleKind.TypeReference)
    {
        var tr = mr.GetTypeReference((TypeReferenceHandle)h);
        var ns = mr.GetString(tr.Namespace);
        var name = mr.GetString(tr.Name);
        return ns.Length > 0 ? $"{ns}.{name}" : name;
    }
    return h.Kind.ToString();
}

// ── Helper: decode a generic parameter list ──────────────────────────────
string GenericParams(GenericParameterHandleCollection gps)
{
    if (gps.Count == 0) return "";
    var names = gps.Select(gph => mr.GetString(mr.GetGenericParameter(gph).Name));
    return $"<{string.Join(", ", names)}>";
}

// ── Helper: decode a method signature (param count + return type token) ──
string MethodSig(MethodDefinition md)
{
    try
    {
        var sig = mr.GetBlobBytes(md.Signature);
        // byte 0 = calling convention flags, byte 1 = param count
        int paramCount = sig.Length > 1 ? sig[1] : 0;
        return $"({paramCount} params)";
    }
    catch { return "(?)"; }
}

// ── Pass 1: sort all types by namespace then name ────────────────────────
var types = mr.TypeDefinitions
    .Select(h => mr.GetTypeDefinition(h))
    .Where(t => !mr.GetString(t.Name).StartsWith("<"))   // skip compiler-generated
    .OrderBy(t => mr.GetString(t.Namespace))
    .ThenBy(t => mr.GetString(t.Name))
    .ToList();

sb.AppendLine($"=== sts2.dll — {types.Count} types ===");
sb.AppendLine();

foreach (var typeDef in types)
{
    var ns = mr.GetString(typeDef.Namespace);
    var name = mr.GetString(typeDef.Name);
    var fullName = ns.Length > 0 ? $"{ns}.{name}" : name;
    var gp = GenericParams(typeDef.GetGenericParameters());
    var attrs = typeDef.Attributes;

    // Visibility
    var vis = (attrs & System.Reflection.TypeAttributes.VisibilityMask) switch
    {
        System.Reflection.TypeAttributes.Public => "public",
        System.Reflection.TypeAttributes.NestedPublic => "public nested",
        System.Reflection.TypeAttributes.NestedPrivate => "private nested",
        System.Reflection.TypeAttributes.NestedFamily => "protected nested",
        System.Reflection.TypeAttributes.NestedAssembly => "internal nested",
        System.Reflection.TypeAttributes.NestedFamORAssem => "protected internal nested",
        _ => "internal"
    };

    // Kind
    bool isInterface = attrs.HasFlag(System.Reflection.TypeAttributes.Interface);
    bool isAbstract = attrs.HasFlag(System.Reflection.TypeAttributes.Abstract);
    bool isSealed = attrs.HasFlag(System.Reflection.TypeAttributes.Sealed);
    string kind = isInterface ? "interface" : (isAbstract && isSealed) ? "static class" : isAbstract ? "abstract class" : isSealed ? "sealed class" : "class";

    // Base type
    string baseStr = "";
    if (!typeDef.BaseType.IsNil)
    {
        string baseName = TypeRefName(typeDef.BaseType);
        if (baseName != "System.Object" && baseName != "System.ValueType" && baseName != "System.Enum")
            baseStr = $" : {baseName}";
    }

    // Interfaces
    var ifaces = typeDef.GetInterfaceImplementations()
        .Select(ih => TypeRefName(mr.GetInterfaceImplementation(ih).Interface))
        .ToList();
    if (ifaces.Count > 0)
        baseStr += (baseStr.Length > 0 ? ", " : " : ") + string.Join(", ", ifaces);

    sb.AppendLine($"[{vis}] {kind} {fullName}{gp}{baseStr}");

    // ── Fields ────────────────────────────────────────────────────────────
    foreach (var fh in typeDef.GetFields())
    {
        var f = mr.GetFieldDefinition(fh);
        var fname = mr.GetString(f.Name);
        if (fname.StartsWith("<")) continue; // backing fields
        var fa = f.Attributes;
        bool fstatic = fa.HasFlag(System.Reflection.FieldAttributes.Static);
        var fvis = (fa & System.Reflection.FieldAttributes.FieldAccessMask) switch
        {
            System.Reflection.FieldAttributes.Public => "public",
            System.Reflection.FieldAttributes.Family => "protected",
            System.Reflection.FieldAttributes.Assembly => "internal",
            System.Reflection.FieldAttributes.FamORAssem => "protected internal",
            _ => "private"
        };
        sb.AppendLine($"  field  {fvis}{(fstatic ? " static" : "")} {fname}");
    }

    // ── Properties ───────────────────────────────────────────────────────
    foreach (var ph in typeDef.GetProperties())
    {
        var p = mr.GetPropertyDefinition(ph);
        var pname = mr.GetString(p.Name);
        sb.AppendLine($"  prop   {pname}");
    }

    // ── Methods ──────────────────────────────────────────────────────────
    foreach (var mh in typeDef.GetMethods())
    {
        var m = mr.GetMethodDefinition(mh);
        var mname = mr.GetString(m.Name);
        if (mname.StartsWith("<")) continue; // lambda/compiler methods
        var ma = m.Attributes;
        bool mstatic = ma.HasFlag(System.Reflection.MethodAttributes.Static);
        bool mabstract = ma.HasFlag(System.Reflection.MethodAttributes.Abstract);
        bool mvirtual = ma.HasFlag(System.Reflection.MethodAttributes.Virtual);
        var mvis = (ma & System.Reflection.MethodAttributes.MemberAccessMask) switch
        {
            System.Reflection.MethodAttributes.Public => "public",
            System.Reflection.MethodAttributes.Family => "protected",
            System.Reflection.MethodAttributes.Assembly => "internal",
            System.Reflection.MethodAttributes.FamORAssem => "protected internal",
            _ => "private"
        };
        var mgp = GenericParams(m.GetGenericParameters());
        var msig = MethodSig(m);
        string modifiers = "";
        if (mstatic) modifiers += " static";
        else if (mabstract) modifiers += " abstract";
        else if (mvirtual) modifiers += " virtual";
        sb.AppendLine($"  method {mvis}{modifiers} {mname}{mgp}{msig}");
    }

    sb.AppendLine();
}

File.WriteAllText(outPath, sb.ToString());
Console.WriteLine($"Dumped {types.Count} types → {outPath}");
return 0;

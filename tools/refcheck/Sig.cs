using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Text;

namespace RefCheck;

/// <summary>
/// Produces assembly-independent canonical type strings from signature blobs.
/// Generic context = optional array of already-canonical type-argument strings that
/// replace !0, !1, ... (used when walking into a generic base type instantiation).
/// Without a context, class type parameters print as !N and method type parameters as !!N.
/// </summary>
public sealed class SigProvider : ISignatureTypeProvider<string, string[]?>, ICustomAttributeTypeProvider<string>
{
    public static readonly SigProvider Instance = new();

    public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch
    {
        PrimitiveTypeCode.Boolean => "System.Boolean",
        PrimitiveTypeCode.Byte => "System.Byte",
        PrimitiveTypeCode.Char => "System.Char",
        PrimitiveTypeCode.Double => "System.Double",
        PrimitiveTypeCode.Int16 => "System.Int16",
        PrimitiveTypeCode.Int32 => "System.Int32",
        PrimitiveTypeCode.Int64 => "System.Int64",
        PrimitiveTypeCode.IntPtr => "System.IntPtr",
        PrimitiveTypeCode.Object => "System.Object",
        PrimitiveTypeCode.SByte => "System.SByte",
        PrimitiveTypeCode.Single => "System.Single",
        PrimitiveTypeCode.String => "System.String",
        PrimitiveTypeCode.TypedReference => "System.TypedReference",
        PrimitiveTypeCode.UInt16 => "System.UInt16",
        PrimitiveTypeCode.UInt32 => "System.UInt32",
        PrimitiveTypeCode.UInt64 => "System.UInt64",
        PrimitiveTypeCode.UIntPtr => "System.UIntPtr",
        PrimitiveTypeCode.Void => "System.Void",
        _ => "?prim" + typeCode,
    };

    public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => Names.TypeDefFullName(reader, handle);
    public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => Names.TypeRefFullName(reader, handle);
    public string GetTypeFromSpecification(MetadataReader reader, string[]? ctx, TypeSpecificationHandle handle, byte rawTypeKind)
        => reader.GetTypeSpecification(handle).DecodeSignature(this, ctx);

    public string GetSZArrayType(string elementType) => elementType + "[]";
    public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[" + new string(',', Math.Max(0, shape.Rank - 1)) + "]";
    public string GetByReferenceType(string elementType) => elementType + "&";
    public string GetPointerType(string elementType) => elementType + "*";
    public string GetPinnedType(string elementType) => elementType + " pinned";
    public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) => genericType + "<" + string.Join(",", typeArguments) + ">";
    public string GetGenericTypeParameter(string[]? ctx, int index) => ctx != null && index < ctx.Length ? ctx[index] : "!" + index;
    public string GetGenericMethodParameter(string[]? ctx, int index) => "!!" + index;
    public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType + (isRequired ? " modreq(" : " modopt(") + modifier + ")";
    public string GetFunctionPointerType(MethodSignature<string> signature) => "method " + signature.ReturnType + " *(" + string.Join(",", signature.ParameterTypes) + ")";

    // ICustomAttributeTypeProvider
    public string GetSystemType() => "System.Type";
    public bool IsSystemType(string type) => type == "System.Type";
    public string GetTypeFromSerializedName(string name) => Names.ReflectionNameToCanonical(name);
    public PrimitiveTypeCode GetUnderlyingEnumType(string type)
    {
        // All enums used in this mod's attribute blobs (HarmonyLib.MethodType, ArgumentType,
        // HarmonyPatchType, System.AttributeTargets, ...) are int-backed. A non-int enum would
        // mis-decode; callers wrap decoding in try/catch and report it as undecodable.
        return PrimitiveTypeCode.Int32;
    }
}

/// <summary>Collects every TypeReference mentioned in a signature blob (mod reader only).</summary>
public sealed class TypeRefCollector : ISignatureTypeProvider<int, object?>
{
    public readonly HashSet<TypeReferenceHandle> Found = new();
    public int GetPrimitiveType(PrimitiveTypeCode typeCode) => 0;
    public int GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => 0;
    public int GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
    {
        // Record the TypeRef and its enclosing TypeRefs (a nested type ref implies the outer).
        EntityHandle h = handle;
        while (h.Kind == HandleKind.TypeReference)
        {
            var trh = (TypeReferenceHandle)h;
            Found.Add(trh);
            h = reader.GetTypeReference(trh).ResolutionScope;
        }
        return 0;
    }
    public int GetTypeFromSpecification(MetadataReader reader, object? ctx, TypeSpecificationHandle handle, byte rawTypeKind)
        => reader.GetTypeSpecification(handle).DecodeSignature(this, ctx);
    public int GetSZArrayType(int elementType) => 0;
    public int GetArrayType(int elementType, ArrayShape shape) => 0;
    public int GetByReferenceType(int elementType) => 0;
    public int GetPointerType(int elementType) => 0;
    public int GetPinnedType(int elementType) => 0;
    public int GetGenericInstantiation(int genericType, ImmutableArray<int> typeArguments) => 0;
    public int GetGenericTypeParameter(object? ctx, int index) => 0;
    public int GetGenericMethodParameter(object? ctx, int index) => 0;
    public int GetModifiedType(int modifier, int unmodifiedType, bool isRequired) => 0;
    public int GetFunctionPointerType(MethodSignature<int> signature) => 0;
}

public static class Names
{
    public static string TypeDefFullName(MetadataReader r, TypeDefinitionHandle h)
    {
        var td = r.GetTypeDefinition(h);
        var name = r.GetString(td.Name);
        var decl = td.GetDeclaringType();
        if (!decl.IsNil) return TypeDefFullName(r, decl) + "/" + name;
        var ns = r.GetString(td.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }

    public static string TypeRefFullName(MetadataReader r, TypeReferenceHandle h)
    {
        var tr = r.GetTypeReference(h);
        var name = r.GetString(tr.Name);
        var ns = r.GetString(tr.Namespace);
        if (tr.ResolutionScope.Kind == HandleKind.TypeReference)
            return TypeRefFullName(r, (TypeReferenceHandle)tr.ResolutionScope) + "/" + (ns.Length == 0 ? name : ns + "." + name);
        return ns.Length == 0 ? name : ns + "." + name;
    }

    /// <summary>Assembly name of the outermost resolution scope of a TypeRef ("" if not an AssemblyRef).</summary>
    public static string TypeRefScopeAssembly(MetadataReader r, TypeReferenceHandle h)
    {
        EntityHandle scope = r.GetTypeReference(h).ResolutionScope;
        while (scope.Kind == HandleKind.TypeReference) scope = r.GetTypeReference((TypeReferenceHandle)scope).ResolutionScope;
        if (scope.Kind == HandleKind.AssemblyReference) return r.GetString(r.GetAssemblyReference((AssemblyReferenceHandle)scope).Name);
        return "";
    }

    /// <summary>
    /// Converts a reflection-style (serialized) type name, e.g.
    /// "System.Collections.Generic.List`1[[Foo.Bar, sts2, Version=...]], System.Private.CoreLib"
    /// or "Outer+Inner, sts2", into the canonical form used here ("List`1&lt;Foo.Bar&gt;", "Outer/Inner").
    /// </summary>
    public static string ReflectionNameToCanonical(string s)
    {
        int pos = 0;
        var result = ParseReflType(s, ref pos, topLevel: true);
        return result;
    }

    static string ParseReflType(string s, ref int pos, bool topLevel)
    {
        var sb = new StringBuilder();
        // name part until '[' ',' ']' or end
        while (pos < s.Length && s[pos] != '[' && s[pos] != ',' && s[pos] != ']' && s[pos] != '&' && s[pos] != '*')
        {
            sb.Append(s[pos] == '+' ? '/' : s[pos]);
            pos++;
        }
        var name = sb.ToString().Trim();
        var suffix = new StringBuilder();
        // generic args: "[[A, asm],[B, asm]]" or "[A,B]"
        while (pos < s.Length && s[pos] == '[')
        {
            if (pos + 1 < s.Length && (s[pos + 1] == ']' || s[pos + 1] == ','))
            {
                // array rank spec "[]" or "[,]"
                int start = pos;
                while (pos < s.Length && s[pos] != ']') pos++;
                pos++; // ']'
                suffix.Append(s, start, pos - start);
                continue;
            }
            // generic argument list
            pos++; // '['
            var args = new List<string>();
            while (pos < s.Length && s[pos] != ']')
            {
                if (s[pos] == ',') { pos++; continue; }
                if (s[pos] == ' ') { pos++; continue; }
                bool bracketed = s[pos] == '[';
                if (bracketed) pos++;
                var arg = ParseReflType(s, ref pos, topLevel: false);
                if (bracketed)
                {
                    // skip assembly qualification up to matching ']'
                    int depth = 0;
                    while (pos < s.Length)
                    {
                        if (s[pos] == '[') depth++;
                        else if (s[pos] == ']') { if (depth == 0) break; depth--; }
                        pos++;
                    }
                    pos++; // closing ']'
                }
                args.Add(arg);
            }
            pos++; // ']'
            name = name + "<" + string.Join(",", args) + ">";
        }
        while (pos < s.Length && (s[pos] == '&' || s[pos] == '*')) { suffix.Append(s[pos]); pos++; }
        // array suffix after generics
        while (pos < s.Length && s[pos] == '[' )
        {
            int start = pos;
            while (pos < s.Length && s[pos] != ']') pos++;
            pos++;
            suffix.Append(s, start, pos - start);
        }
        if (topLevel)
        {
            // ignore ", assembly" qualification
            pos = s.Length;
        }
        return name + suffix.ToString();
    }
}

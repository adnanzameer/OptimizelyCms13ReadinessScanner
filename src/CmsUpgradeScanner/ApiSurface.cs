using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace OptimizelyCms13ReadinessScanner;

public enum MemberLookup { Found, NotFound, Unknown }

/// <summary>An [Obsolete] attribute read from metadata. IsError = [Obsolete(..., error: true)]: using it does not compile.</summary>
public sealed record ObsoleteInfo(string? Message, bool IsError);

public readonly record struct MemberInspection(MemberLookup Lookup, ObsoleteInfo? Obsolete);

/// <summary>
/// The public/protected API of a set of assemblies, read straight from their metadata
/// (System.Reflection.Metadata): type names, member names, base types, interfaces and [Obsolete]
/// attributes. Assemblies are never loaded or executed, so indexing a downloaded package cannot run
/// its code, and no dependency resolution is needed.
///
/// Deliberately name-based. A member is "present" if a member of that name exists on the type or
/// anything it inherits, so a changed signature (different parameters, same name) is NOT detected.
/// That keeps false positives near zero, which matters more here than catching every break.
///
/// CMS 13 mostly retires APIs by marking them [Obsolete(error: true)] rather than deleting them, so
/// the obsolete attribute is recorded alongside existence. For an overloaded name, a member only
/// counts as obsolete when every overload with that name is.
/// </summary>
public sealed class ApiSurface
{
    private sealed class MemberEntry
    {
        public bool AnyNotObsolete;
        public ObsoleteInfo? Obsolete;
    }

    private sealed class TypeEntry
    {
        public readonly Dictionary<string, MemberEntry> Members = new(StringComparer.Ordinal);
        public readonly List<string> Parents = new();   // base type + interfaces, by full name
        public bool ParentsIncomplete;                  // a parent we could not name
        public bool ForwardedOnly;                      // seen only as a type forwarder: members unknown
        public ObsoleteInfo? Obsolete;
    }

    private readonly Dictionary<string, TypeEntry> _types = new(StringComparer.Ordinal);
    private readonly HashSet<string> _assemblies = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<string> AssemblyNames => _assemblies;
    public int TypeCount => _types.Count;

    /// <summary>Adds one assembly image. Returns false if it is not a managed assembly.</summary>
    public bool AddAssembly(byte[] image)
    {
        try
        {
            using var pe = new PEReader(new MemoryStream(image, writable: false));
            if (!pe.HasMetadata) return false;
            var md = pe.GetMetadataReader();
            if (!md.IsAssembly) return false;

            _assemblies.Add(md.GetString(md.GetAssemblyDefinition().Name));
            foreach (var handle in md.TypeDefinitions) AddType(md, handle);
            foreach (var handle in md.ExportedTypes)
            {
                var exported = md.GetExportedType(handle);
                if (!exported.IsForwarder || exported.Implementation.Kind == HandleKind.ExportedType) continue;
                var name = Join(md.GetString(exported.Namespace), md.GetString(exported.Name));
                _types.TryAdd(name, new TypeEntry { ForwardedOnly = true });
            }
            return true;
        }
        catch (BadImageFormatException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    public bool HasType(string fullName) => _types.ContainsKey(fullName);

    /// <summary>The [Obsolete] attribute on the type itself, if any.</summary>
    public ObsoleteInfo? TypeObsolete(string fullName) => _types.TryGetValue(fullName, out var t) ? t.Obsolete : null;

    /// <summary>
    /// Whether <paramref name="member"/> exists on <paramref name="typeName"/> or anything it inherits
    /// or implements, and whether it is obsolete there. Unknown whenever the answer depends on
    /// something not indexed.
    /// </summary>
    public MemberLookup LookupMember(string typeName, string member) => Inspect(typeName, member).Lookup;

    public MemberInspection Inspect(string typeName, string member)
    {
        if (!_types.ContainsKey(typeName)) return new(MemberLookup.Unknown, null);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        queue.Enqueue(typeName);
        var sawUnknown = false;

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!seen.Add(current)) continue;

            if (!_types.TryGetValue(current, out var entry))
            {
                // A parent outside the index. The BCL cannot have gained a member that a CMS type
                // used to declare, but any other missing parent makes the answer unknowable.
                if (!current.StartsWith("System.", StringComparison.Ordinal) && current != "System.Object") sawUnknown = true;
                continue;
            }

            if (entry.ForwardedOnly) { sawUnknown = true; continue; }
            if (entry.Members.TryGetValue(member, out var m))
                return new(MemberLookup.Found, m.AnyNotObsolete ? null : m.Obsolete);
            if (entry.ParentsIncomplete) sawUnknown = true;
            foreach (var parent in entry.Parents) queue.Enqueue(parent);
        }

        return new(sawUnknown ? MemberLookup.Unknown : MemberLookup.NotFound, null);
    }

    // ---- reading -------------------------------------------------------------------------------

    private void AddType(MetadataReader md, TypeDefinitionHandle handle)
    {
        var type = md.GetTypeDefinition(handle);
        if (!IsExposed(md, type)) return;

        var name = FullName(md, type);
        if (!_types.TryGetValue(name, out var entry) || entry.ForwardedOnly)
            _types[name] = entry = new TypeEntry();

        entry.Obsolete = ReadObsolete(md, type.GetCustomAttributes());
        if (!type.BaseType.IsNil) AddParent(md, entry, type.BaseType);
        foreach (var implementation in type.GetInterfaceImplementations())
            AddParent(md, entry, md.GetInterfaceImplementation(implementation).Interface);

        foreach (var m in type.GetMethods())
        {
            var method = md.GetMethodDefinition(m);
            if (IsAccessible(method.Attributes & MethodAttributes.MemberAccessMask))
                AddMember(entry, md.GetString(method.Name), ReadObsolete(md, method.GetCustomAttributes()));
        }
        foreach (var f in type.GetFields())
        {
            var field = md.GetFieldDefinition(f);
            if (IsAccessible((MethodAttributes)(field.Attributes & FieldAttributes.FieldAccessMask)))
                AddMember(entry, md.GetString(field.Name), ReadObsolete(md, field.GetCustomAttributes()));
        }
        foreach (var p in type.GetProperties())
        {
            var property = md.GetPropertyDefinition(p);
            AddMember(entry, md.GetString(property.Name), ReadObsolete(md, property.GetCustomAttributes()));
        }
        foreach (var e in type.GetEvents())
        {
            var evt = md.GetEventDefinition(e);
            AddMember(entry, md.GetString(evt.Name), ReadObsolete(md, evt.GetCustomAttributes()));
        }
    }

    private static void AddMember(TypeEntry entry, string name, ObsoleteInfo? obsolete)
    {
        if (!entry.Members.TryGetValue(name, out var member)) entry.Members[name] = member = new MemberEntry();
        if (obsolete == null) member.AnyNotObsolete = true;
        else member.Obsolete ??= obsolete;
    }

    // System.ObsoleteAttribute has three constructors: (), (string) and (string, bool error). The
    // constructor's own signature tells how many fixed arguments the attribute blob carries.
    private static ObsoleteInfo? ReadObsolete(MetadataReader md, CustomAttributeHandleCollection attributes)
    {
        foreach (var handle in attributes)
        {
            var attribute = md.GetCustomAttribute(handle);
            if (attribute.Constructor.Kind != HandleKind.MemberReference) continue;

            var ctor = md.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
            if (ctor.Parent.Kind != HandleKind.TypeReference) continue;
            var parent = md.GetTypeReference((TypeReferenceHandle)ctor.Parent);
            if (md.GetString(parent.Name) != "ObsoleteAttribute" || md.GetString(parent.Namespace) != "System") continue;

            var signature = md.GetBlobReader(ctor.Signature);
            signature.ReadByte();                                   // calling convention
            var parameterCount = signature.ReadCompressedInteger();

            string? message = null;
            var isError = false;
            try
            {
                var value = md.GetBlobReader(attribute.Value);
                if (value.ReadUInt16() == 1)                        // blob prolog
                {
                    if (parameterCount >= 1) message = value.ReadSerializedString();
                    if (parameterCount >= 2) isError = value.ReadBoolean();
                }
            }
            catch (BadImageFormatException) { /* keep what was read: it is still marked obsolete */ }

            return new ObsoleteInfo(message, isError);
        }
        return null;
    }

    // Public, family (protected) and family-or-assembly members are API a consumer can use.
    private static bool IsAccessible(MethodAttributes access) =>
        access is MethodAttributes.Public or MethodAttributes.Family or MethodAttributes.FamORAssem;

    private static bool IsExposed(MetadataReader md, TypeDefinition type)
    {
        for (var current = type; ; current = md.GetTypeDefinition(current.GetDeclaringType()))
        {
            var visibility = current.Attributes & TypeAttributes.VisibilityMask;
            var visible = visibility is TypeAttributes.Public or TypeAttributes.NestedPublic
                or TypeAttributes.NestedFamily or TypeAttributes.NestedFamORAssem;
            if (!visible) return false;
            if (current.GetDeclaringType().IsNil) return true;
        }
    }

    private static string FullName(MetadataReader md, TypeDefinition type)
    {
        var declaring = type.GetDeclaringType();
        var name = md.GetString(type.Name);
        return declaring.IsNil
            ? Join(md.GetString(type.Namespace), name)
            : FullName(md, md.GetTypeDefinition(declaring)) + "+" + name;
    }

    private static void AddParent(MetadataReader md, TypeEntry entry, EntityHandle parent)
    {
        var name = TryName(md, parent);
        if (name == null) entry.ParentsIncomplete = true;
        else entry.Parents.Add(name);
    }

    private static string? TryName(MetadataReader md, EntityHandle handle)
    {
        switch (handle.Kind)
        {
            case HandleKind.TypeDefinition:
                return FullName(md, md.GetTypeDefinition((TypeDefinitionHandle)handle));
            case HandleKind.TypeReference:
            {
                var reference = md.GetTypeReference((TypeReferenceHandle)handle);
                var name = md.GetString(reference.Name);
                if (reference.ResolutionScope.Kind == HandleKind.TypeReference)
                {
                    var outer = TryName(md, (TypeReferenceHandle)reference.ResolutionScope);
                    return outer == null ? null : outer + "+" + name;
                }
                return Join(md.GetString(reference.Namespace), name);
            }
            case HandleKind.TypeSpecification:
            {
                // Generic instantiation (Base<T>): GENERICINST, CLASS|VALUETYPE, then the open type.
                var reader = md.GetBlobReader(md.GetTypeSpecification((TypeSpecificationHandle)handle).Signature);
                if (reader.ReadByte() != (byte)SignatureTypeCode.GenericTypeInstance) return null;
                reader.ReadByte();
                return TryName(md, reader.ReadTypeHandle());
            }
            default:
                return null;
        }
    }

    private static string Join(string ns, string name) => ns.Length == 0 ? name : ns + "." + name;
}

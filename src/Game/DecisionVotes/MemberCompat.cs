using System;
using System.Collections.Generic;
using System.Reflection;

namespace SlayTheStreamer2.Game.DecisionVotes;

/// <summary>
/// Version-tolerant member lookup for game types whose shape differs between the
/// two live game branches. One mod DLL serves both, and build.ps1 compiles against
/// whichever branch is installed, so a member that was renamed, re-typed, or given
/// an extra optional parameter cannot be referenced directly: the compiled
/// reference binds on one branch and throws MissingMethodException /
/// MissingFieldException at JIT time on the other. Callers pass names (and
/// signatures) in preference order and get back whichever exists.
///
/// Pure BCL reflection, no game types, so it is unit-tested (MemberCompatTests).
/// <see cref="SeedCompat"/> is the older, seed-specific sibling.
/// </summary>
internal static class MemberCompat {
    private const BindingFlags Declared = BindingFlags.Instance | BindingFlags.Static
        | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    /// <summary>The first method, trying <paramref name="names"/> in order, whose
    /// parameter types are exactly <paramref name="parameterTypes"/>. Return type is
    /// ignored (it is part of a compiled reference but not of this lookup). Private
    /// methods on base types are found. ORDER MATTERS when an old build keeps a dead
    /// method under the new name: list the name that is live on the old build first.</summary>
    internal static MethodInfo? FirstMethod(Type type, Type[] parameterTypes, params string[] names) {
        foreach (var name in names) {
            for (var t = type; t is not null; t = t.BaseType) {
                foreach (var m in t.GetMethods(Declared)) {
                    if (m.Name == name && SameTypes(m.GetParameters(), parameterTypes)) return m;
                }
            }
        }
        return null;
    }

    /// <summary>The first field, trying <paramref name="names"/> in order. Private
    /// fields on base types are found.</summary>
    internal static FieldInfo? FirstField(Type type, params string[] names) {
        foreach (var name in names) {
            for (var t = type; t is not null; t = t.BaseType) {
                var f = t.GetField(name, Declared);
                if (f is not null) return f;
            }
        }
        return null;
    }

    /// <summary>Every instance constructor that has a parameter with this name and
    /// type. For Harmony constructor patches: target these instead of naming an exact
    /// argument list, which stops matching the moment the game appends a parameter.</summary>
    internal static IReadOnlyList<ConstructorInfo> ConstructorsWithParameter(Type type, string parameterName, Type parameterType) {
        var found = new List<ConstructorInfo>();
        foreach (var c in type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)) {
            foreach (var p in c.GetParameters()) {
                if (p.Name == parameterName && p.ParameterType == parameterType) { found.Add(c); break; }
            }
        }
        return found;
    }

    /// <summary>Constructs <paramref name="type"/> through the constructor whose
    /// leading parameters are exactly <paramref name="leadingTypes"/> and whose
    /// remaining parameters, if any, are all optional (filled with their declared
    /// defaults). An exact match wins over one with an optional tail. This is the
    /// shape of "the game appended an optional parameter": source still compiles,
    /// but the compiled constructor reference changes.</summary>
    /// <exception cref="MissingMethodException">No such constructor.</exception>
    internal static object Construct(Type type, Type[] leadingTypes, object?[] leadingArgs) {
        ConstructorInfo? best = null;
        int bestExtra = int.MaxValue;
        foreach (var c in type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)) {
            var ps = c.GetParameters();
            if (ps.Length < leadingTypes.Length) continue;
            bool ok = true;
            for (int i = 0; ok && i < leadingTypes.Length; i++) ok = ps[i].ParameterType == leadingTypes[i];
            for (int i = leadingTypes.Length; ok && i < ps.Length; i++) ok = ps[i].HasDefaultValue;
            if (!ok) continue;
            int extra = ps.Length - leadingTypes.Length;
            if (extra < bestExtra) { best = c; bestExtra = extra; }
        }
        if (best is null) {
            throw new MissingMethodException(
                $"{type.FullName}: no constructor starting with ({string.Join(", ", Array.ConvertAll(leadingTypes, t => t.Name))}) whose remaining parameters are optional");
        }
        var all = best.GetParameters();
        var args = new object?[all.Length];
        Array.Copy(leadingArgs, args, leadingTypes.Length);
        for (int i = leadingTypes.Length; i < all.Length; i++) args[i] = all[i].DefaultValue;
        return best.Invoke(args);
    }

    /// <summary>True when <paramref name="method"/> has a parameter with this name
    /// and type. Harmony injects original parameters into patch methods BY NAME and
    /// throws during patching when the name is gone, so a patch that injects one must
    /// gate on this before registering.</summary>
    internal static bool HasParameter(MethodBase? method, string name, Type type) {
        if (method is null) return false;
        foreach (var p in method.GetParameters()) {
            if (p.Name == name && p.ParameterType == type) return true;
        }
        return false;
    }

    private static bool SameTypes(ParameterInfo[] parameters, Type[] types) {
        if (parameters.Length != types.Length) return false;
        for (int i = 0; i < types.Length; i++) {
            if (parameters[i].ParameterType != types[i]) return false;
        }
        return true;
    }
}

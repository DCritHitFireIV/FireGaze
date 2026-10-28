using dnlib.DotNet;

namespace FireGaze.UIText;

// These contracts only preserve provenance; the eventual display/IPC consumer
// decides whether a literal can be changed. No target assembly is executed.
internal static class UITextCollectionContracts
{
    internal enum Kind { None, Rejected, Dictionary, Add, Values, Keys, Count, ToArray, Span, Localize }

    internal static bool IsStringDictionary(TypeSig? type) => type is GenericInstSig owner
        && owner.GenericType.FullName == "System.Collections.Generic.Dictionary`2"
        && FrameworkOwner(owner.GenericType.TypeDefOrRef, "System.Collections")
        && owner.GenericArguments.Count == 2 && owner.GenericArguments[1].ElementType == ElementType.String;

    internal static Kind Classify(IMethod method)
    {
        var owner = method.DeclaringType;
        var type = owner?.ToTypeSig();
        var generic = type as GenericInstSig;
        var name = method.Name.String;
        var signature = method.MethodSig;
        if (owner?.FullName == "CheapLoc.Loc" && name == "Localize")
            return owner.DefinitionAssembly?.Name == "CheapLoc"
                && signature is { HasThis: false, GenParamCount: 0 }
                && signature.Params.Count is 2 or 3
                && signature.RetType.ElementType == ElementType.String
                && signature.Params[0].ElementType == ElementType.String && signature.Params[1].ElementType == ElementType.String
                && (signature.Params.Count == 2 || signature.Params[2].FullName == "System.Reflection.Assembly"
                    && FrameworkOwner(signature.Params[2].ToTypeDefOrRef(), "System.Runtime"))
                ? Kind.Localize : Kind.Rejected;

        if (generic?.GenericType.FullName == "System.Collections.Generic.Dictionary`2"
            && name is ".ctor" or "Add" or "get_Values" or "get_Keys" or "get_Count")
        {
            if (!FrameworkOwner(owner, "System.Collections") || generic.GenericArguments.Count != 2
                || generic.GenericArguments[1].ElementType != ElementType.String
                || signature is not { HasThis: true, GenParamCount: 0 }) return Kind.Rejected;
            return name switch
            {
                ".ctor" when signature.Params.Count == 0 && signature.RetType.ElementType == ElementType.Void => Kind.Dictionary,
                "Add" when signature.Params.Count == 2 && signature.RetType.ElementType == ElementType.Void
                    && SameType(Closed(method, signature.Params[0]), generic.GenericArguments[0])
                    && Closed(method, signature.Params[1]).ElementType == ElementType.String => Kind.Add,
                "get_Count" when signature.Params.Count == 0 && signature.RetType.ElementType == ElementType.I4 => Kind.Count,
                "get_Values" when signature.Params.Count == 0 && IsDictionaryProjection(method, signature.RetType, generic, "ValueCollection") => Kind.Values,
                "get_Keys" when signature.Params.Count == 0 && IsDictionaryProjection(method, signature.RetType, generic, "KeyCollection") => Kind.Keys,
                _ => Kind.Rejected,
            };
        }

        if (owner?.FullName == "System.Linq.Enumerable" && name == "ToArray")
            return FrameworkOwner(owner, "System.Linq")
                && method is MethodSpec specification && specification.GenericInstMethodSig.GenericArguments.Count == 1
                && specification.GenericInstMethodSig.GenericArguments[0].ElementType == ElementType.String
                && signature is { HasThis: false, GenParamCount: 1, Params.Count: 1 }
                && signature.Params[0] is GenericInstSig enumerable
                && enumerable.GenericType.FullName == "System.Collections.Generic.IEnumerable`1"
                && FrameworkOwner(enumerable.GenericType.TypeDefOrRef, "System.Runtime")
                && enumerable.GenericArguments.Count == 1
                && Closed(method, enumerable.GenericArguments[0]).ElementType == ElementType.String
                && signature.RetType is SZArraySig array && Closed(method, array.Next).ElementType == ElementType.String
                ? Kind.ToArray : Kind.Rejected;

        if (generic?.GenericType.FullName == "System.ReadOnlySpan`1" && name == "op_Implicit")
            return FrameworkOwner(owner, "System.Runtime") && generic.GenericArguments.Count == 1
                && generic.GenericArguments[0].ElementType == ElementType.String
                && signature is { HasThis: false, GenParamCount: 0, Params.Count: 1 }
                && signature.Params[0] is SZArraySig source && Closed(method, source.Next).ElementType == ElementType.String
                && signature.RetType is GenericInstSig target && target.GenericType.FullName == "System.ReadOnlySpan`1"
                && FrameworkOwner(target.GenericType.TypeDefOrRef, "System.Runtime")
                && target.GenericArguments.Count == 1 && Closed(method, target.GenericArguments[0]).ElementType == ElementType.String
                ? Kind.Span : Kind.Rejected;
        return Kind.None;
    }

    private static bool IsDictionaryProjection(IMethod method, TypeSig result, GenericInstSig owner, string projection) =>
        result is GenericInstSig projected
        && projected.GenericType.FullName == "System.Collections.Generic.Dictionary`2/" + projection
        && FrameworkOwner(projected.GenericType.TypeDefOrRef, "System.Collections")
        && projected.GenericArguments.Count == 2
        && SameType(Closed(method, projected.GenericArguments[0]), owner.GenericArguments[0])
        && Closed(method, projected.GenericArguments[1]).ElementType == ElementType.String;

    private static bool FrameworkOwner(ITypeDefOrRef? owner, string assembly) =>
        owner?.DefinitionAssembly?.Name == assembly
        && owner.DefinitionAssembly.FullName.EndsWith(", PublicKeyToken=b03f5f7f11d50a3a", StringComparison.OrdinalIgnoreCase);

    private static bool SameType(TypeSig left, TypeSig right) => new SigComparer().Equals(left, right);

    private static TypeSig Closed(IMethod method, TypeSig type) => type switch
    {
        GenericMVar parameter when method is MethodSpec specification
            && parameter.Number < specification.GenericInstMethodSig.GenericArguments.Count =>
                specification.GenericInstMethodSig.GenericArguments[(int)parameter.Number],
        GenericVar parameter when method.DeclaringType.ToTypeSig() is GenericInstSig owner
            && parameter.Number < owner.GenericArguments.Count => owner.GenericArguments[(int)parameter.Number],
        _ => type,
    };
}

using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace StripperClips.Framework;

public sealed class PatchSet : IDisposable
{
    private const BindingFlags AnyMember =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private readonly Harmony _harmony;

    public PatchSet(string harmonyId) => _harmony = new Harmony(harmonyId);

    public string Id => _harmony.Id;

    public static MethodInfo Method(Type type, string name, params Type[] parameters)
    {
        var named = type.GetMethods(AnyMember).Where(method => method.Name == name).ToArray();
        var match = named.FirstOrDefault(method =>
            method.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(parameters));
        if (match != null) return match;
        var candidates = named.Length == 0 ? "none" : string.Join("; ", named.Select(method => method.ToString()));
        throw new MissingMethodException(
            $"{type.FullName}.{name}({string.Join(", ", parameters.Select(parameter => parameter.Name))}) not found. Declared overloads: {candidates}");
    }

    public static MethodInfo Only(Type type, string name)
    {
        var named = type.GetMethods(AnyMember).Where(method => method.Name == name).ToArray();
        if (named.Length == 1) return named[0];
        throw new MissingMethodException($"{type.FullName}.{name}: expected one declaration, found {named.Length}.");
    }

    public void Prefix(MethodBase target, Delegate patch, int priority = Priority.Normal) =>
        _harmony.Patch(target, prefix: new HarmonyMethod(patch.Method, priority));

    public void Postfix(MethodBase target, Delegate patch, int priority = Priority.Normal) =>
        _harmony.Patch(target, postfix: new HarmonyMethod(patch.Method, priority));

    public void Dispose() => _harmony.UnpatchSelf();
}

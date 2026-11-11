using System.Reflection;

namespace OnlyWorkspace.Tests;

/// <summary>The one accessor for private fields of forms and panels under test, read by name.</summary>
internal static class FormFields
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;

    public static T Field<T>(object owner, string name) => (T)Find(owner, name).GetValue(owner)!;

    public static void Set(object owner, string name, object value) => Find(owner, name).SetValue(owner, value);

    /// <summary>Walks the base types too, so a field declared on a base form is found from a derived instance.</summary>
    private static FieldInfo Find(object owner, string name)
    {
        for (var type = owner.GetType(); type is not null; type = type.BaseType)
        {
            var field = type.GetField(name, Flags | BindingFlags.DeclaredOnly);
            if (field is not null)
                return field;
        }
        throw new MissingFieldException(owner.GetType().FullName, name);
    }
}

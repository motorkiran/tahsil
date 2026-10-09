using System.Reflection;

namespace Tahsil.Domain.Tests;

/// <summary>
/// Binary floating point cannot represent minor units and tax rates exactly, so the domain model never uses it
/// (types cannot be banned by the analyzer, hence this test).
/// </summary>
public sealed class FloatingPointTests
{
    private const BindingFlags Declared =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    [Fact]
    [Trait("Requirement", "FND-6")]
    [Trait("Requirement", "MNY-4")]
    public void TheDomainModelHasNoFloatingPointMembers()
    {
        var members = Assembly.Load("Tahsil.Domain").GetTypes()
            .SelectMany(type => FloatingPointMembers(type).Select(member => $"{type.FullName}.{member}"));

        members.ShouldBeEmpty();
    }

    private static IEnumerable<string> FloatingPointMembers(Type type)
    {
        foreach (var field in type.GetFields(Declared).Where(field => IsFloatingPoint(field.FieldType)))
        {
            yield return field.Name;
        }

        foreach (var property in type.GetProperties(Declared).Where(property => IsFloatingPoint(property.PropertyType)))
        {
            yield return property.Name;
        }

        foreach (var method in type.GetMethods(Declared).Cast<MethodBase>().Concat(type.GetConstructors(Declared)))
        {
            var returnsFloatingPoint = method is MethodInfo info && IsFloatingPoint(info.ReturnType);
            if (returnsFloatingPoint || method.GetParameters().Any(parameter => IsFloatingPoint(parameter.ParameterType)))
            {
                yield return method.Name;
            }
        }
    }

    private static bool IsFloatingPoint(Type type)
    {
        var element = type.HasElementType ? type.GetElementType()! : type;
        element = Nullable.GetUnderlyingType(element) ?? element;

        return element == typeof(float) || element == typeof(double) || element == typeof(Half)
            || (element.IsGenericType && element.GetGenericArguments().Any(IsFloatingPoint));
    }
}

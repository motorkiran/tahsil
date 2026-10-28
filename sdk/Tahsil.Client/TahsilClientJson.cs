using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Tahsil.Contracts.Serialization;

namespace Tahsil.Client;

/// <summary>
/// The API's JSON conventions for requests and responses. A property that a request leaves unset is not sent: for
/// changes, a property with a <c>Has…</c> companion is sent when it was set, also to <c>null</c>, which clears it (SDK-2).
/// </summary>
internal static class TahsilClientJson
{
    public static readonly JsonSerializerOptions Options = TahsilJson.Configure(new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { SendWhatWasSet } },
    });

    private static void SendWhatWasSet(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Kind != JsonTypeInfoKind.Object)
        {
            return;
        }

        foreach (var property in typeInfo.Properties)
        {
            if (property.AttributeProvider is not PropertyInfo clr
                || clr.DeclaringType?.GetProperty($"Has{clr.Name}", BindingFlags.Public | BindingFlags.Instance) is not { PropertyType: var type } presence
                || type != typeof(bool))
            {
                continue;
            }

            property.ShouldSerialize = (owner, _) => (bool)presence.GetValue(owner)!;
        }
    }
}

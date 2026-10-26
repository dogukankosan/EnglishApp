using System.Text.Json;
using System.Text.Json.Serialization;

namespace EnglishApp.Api.Infrastructure;

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = Configure(new JsonSerializerOptions(JsonSerializerDefaults.Web));

    public static JsonSerializerOptions Configure(JsonSerializerOptions o)
    {
        o.Converters.Add(new JsonStringEnumConverter());
        // Türkçe karakterler ç gibi kaçışlanmasın (HTML'e basarken UI zaten encode ediyor)
        o.Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
        o.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        return o;
    }
}

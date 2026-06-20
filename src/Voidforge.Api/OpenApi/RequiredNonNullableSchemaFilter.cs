using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Voidforge.Api.OpenApi;

/// <summary>
/// Marks every non-nullable property as <c>required</c> in the emitted OpenAPI schema.
/// With nullable reference types enabled, a non-nullable property is always present on the
/// wire, so the generated frontend client should treat it as required (not optional).
/// </summary>
public sealed class RequiredNonNullableSchemaFilter : ISchemaFilter
{
    public void Apply(IOpenApiSchema schema, SchemaFilterContext context)
    {
        if (schema is not OpenApiSchema concrete || concrete.Properties is null)
        {
            return;
        }

        concrete.Required ??= new HashSet<string>(StringComparer.Ordinal);

        foreach (var (propertyName, propertySchema) in concrete.Properties)
        {
            var isNullable = propertySchema.Type?.HasFlag(JsonSchemaType.Null) ?? false;
            if (!isNullable)
            {
                concrete.Required.Add(propertyName);
            }
        }
    }
}

using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace P2FK.IO.Services
{
    public sealed class AllModelSchemasDocumentFilter : IDocumentFilter
    {
        public void Apply(OpenApiDocument swaggerDoc, DocumentFilterContext context)
        {
            IEnumerable<Type> modelTypes = typeof(P2FK.IO.Models.KuboAddResult).Assembly
                .GetTypes()
                .Where(type =>
                    type.IsClass
                    && type.IsPublic
                    && !type.IsAbstract
                    && !type.IsGenericTypeDefinition
                    && type.Namespace == "P2FK.IO.Models");

            foreach (Type modelType in modelTypes)
                context.SchemaGenerator.GenerateSchema(modelType, context.SchemaRepository);
        }
    }
}

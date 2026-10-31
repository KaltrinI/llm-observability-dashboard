using LlmObservability.Api.Models;
using Microsoft.OData.Edm;
using Microsoft.OData.ModelBuilder;

namespace LlmObservability.Api.OData;

public static class EdmModelBuilder
{
    public static IEdmModel Build()
    {
        var builder = new ODataConventionModelBuilder();

        builder.EntitySet<LlmInteraction>("LlmInteractions");
        builder.EntitySet<ModelConfiguration>("ModelConfigurations");
        builder.EntitySet<PromptTemplate>("PromptTemplates");
        builder.EntitySet<UserFeedback>("UserFeedbacks");

        return builder.GetEdmModel();
    }
}

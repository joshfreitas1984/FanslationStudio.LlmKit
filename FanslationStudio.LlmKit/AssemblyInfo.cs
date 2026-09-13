using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("FanslationStudio.LlmKit.Tests")]

// DragonHierOverLlm's own Tests project (Tests.csproj, no explicit AssemblyName so it defaults
// to "Tests") - needed so its QcOmittedSubjectRegression fact can call
// QualityReviewWorkflow.GetLlmVerdictAsync directly against a known SOURCE/TRANSLATION pair
// instead of going through the corpus.
[assembly: InternalsVisibleTo("Tests")]

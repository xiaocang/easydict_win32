using Easydict.TranslationService.Models;

namespace Easydict.TranslationService;

/// <summary>
/// Optional interface for services that need to constrain the host's automatic behavior
/// (retry, caching, phonetic enrichment). Services that do not implement it get
/// <see cref="ServiceExecutionPolicy.Default"/>.
/// </summary>
public interface IServiceExecutionPolicyProvider
{
    /// <summary>The execution policy the host must honor for this service.</summary>
    ServiceExecutionPolicy ExecutionPolicy { get; }
}

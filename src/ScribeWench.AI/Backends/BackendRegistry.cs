using Microsoft.Extensions.DependencyInjection;
using ScribeWench.Core.Models;

namespace ScribeWench.AI.Backends;

// Picks the backend strategy that config.json names.
public sealed class BackendRegistry(IEnumerable<IBackend> backends, AiConfig ai)
{
    public IBackend Current => Resolve(ai.Backend);

    public IBackend Resolve(string key) =>
        backends.FirstOrDefault(b => b.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException(
            $"Unknown AI backend: '{key}'. Valid values: {string.Join(", ", backends.Select(b => b.Key))}.");
}

public static class BackendServiceCollectionExtensions
{
    public static IServiceCollection AddBackend<TBackend>(this IServiceCollection services)
        where TBackend : class, IBackend =>
        services.AddSingleton<IBackend, TBackend>();
}

using ScribeWench.Core.Models;

namespace ScribeWench.Core.Interfaces;

public interface IConfigStore
{
    string ConfigPath { get; }
    Task<AppConfig?> LoadAsync();
    Task SaveAsync(AppConfig config);
}

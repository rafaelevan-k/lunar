namespace Lunar.Infrastructure.Startup;

/// <summary>The per-user Windows "Run" list, reduced to one named value.</summary>
internal interface IRunKeyStore
{
    string? Read(string name);

    void Write(string name, string command);

    void Delete(string name);
}

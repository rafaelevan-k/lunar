namespace Lunar.Infrastructure.Startup;

/// <summary>The per-user Windows "Run" list, reduced to one named value, plus the user's on/off choice for it.</summary>
internal interface IRunKeyStore
{
    string? Read(string name);

    void Write(string name, string command);

    void Delete(string name);

    /// <summary>
    /// True when the user switched this entry off in Task Manager or in Windows Settings &gt; Apps &gt; Startup.
    /// Windows keeps that choice apart from the Run value and then skips the entry at sign-in.
    /// </summary>
    bool IsTurnedOffByUser(string name);

    /// <summary>Forgets that choice, so Windows starts the entry again (a no-op when there is none).</summary>
    void ClearUserChoice(string name);
}

namespace TeXeed.Tests;
internal static class Check
{
    public static void True(bool value, string message = "Assertion failed") { if (!value) throw new Exception(message); }
    public static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}"); }
    public static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}");
    }
}

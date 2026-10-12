using TeXeed.Tests;

if (Environment.GetEnvironmentVariable("TEXEED_TEST_FAKE_ENGINE") is { Length: > 0 } mode)
    return await CompilerTests.RunFakeEngineAsync(args, mode);
if (args.Length == 3 && args[0] == "--real-tex")
    return await CompilerTests.RunRealEngineAsync(args[1], args[2]);
try
{
    IndexCacheTests.Run();
    if (args.Contains("--index-only"))
    {
        await TitleTests.RunAsync(); await ProjectTests.RunAsync();
        Console.WriteLine("PASS: indexing, project discovery, title semantics and save/conflict regressions"); return 0;
    }
    LayoutTests.Run();
    await TitleTests.RunAsync();
    await ProjectTests.RunAsync();
    Console.WriteLine("PASS: project discovery, indexing, safe save, formatting");
    await CompilerTests.RunAsync();
    Console.WriteLine("PASS: compilation, cancellation, diagnostics, SyncTeX");
    Console.WriteLine("All tests passed.");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    return 1;
}

using System.Diagnostics;

namespace Easydict.CompatHost;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Trace.Listeners.Clear();
        Trace.Listeners.Add(new TextWriterTraceListener(Console.Error));
        Trace.AutoFlush = true;

        var runtimeState = new CompatHostRuntimeState();
        await using var translator = new TranslationManagerCompatTranslator();
        var dispatcher = new CompatHostDispatcher(
            translator,
            new OcrWorkerCompatRecognizer(),
            new LongDocWorkerCompatTranslator(),
            new LocalAiWorkerCompatService(),
            new MdxCompatLookupService(),
            new FileSettingsCompatMigrator(),
            runtimeState);
        return await CompatHostApplication.RunAsync(Console.In, Console.Out, dispatcher);
    }
}

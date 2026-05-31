namespace Easydict.CompatHost;

public static class CompatHostApplication
{
    public static async Task<int> RunAsync(
        TextReader input,
        TextWriter output,
        CompatHostDispatcher dispatcher,
        CancellationToken cancellationToken = default)
    {
        string? line;
        while ((line = await input.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var shouldExit = await dispatcher.DispatchAsync(line, output, cancellationToken)
                .ConfigureAwait(false);
            if (shouldExit)
            {
                break;
            }
        }

        return 0;
    }
}

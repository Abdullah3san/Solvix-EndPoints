namespace Solvix.Infrastructure.Email;

using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Solvix.SharedKernel.Email;

/// <summary>
/// In-process mail queue. Requests enqueue and return; a background worker
/// delivers. A response therefore never waits for (or reveals the outcome of)
/// an SMTP conversation. The queue is bounded: under a flood, new messages are
/// dropped rather than letting memory grow, and the drop is logged.
/// </summary>
internal sealed partial class BackgroundEmailQueue(ILogger<BackgroundEmailQueue> logger) : IEmailQueue
{
    private readonly Channel<EmailMessage> _channel = Channel.CreateBounded<EmailMessage>(
        new BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    public ChannelReader<EmailMessage> Reader => _channel.Reader;

    public void Enqueue(EmailMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!_channel.Writer.TryWrite(message))
        {
            LogDropped(logger, message.Subject);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "E-mail queue is full: message with subject '{Subject}' was dropped")]
    private static partial void LogDropped(ILogger logger, string subject);
}

internal sealed partial class EmailDispatcher(BackgroundEmailQueue queue, IServiceScopeFactory scopes, ILogger<EmailDispatcher> logger)
    : BackgroundService
{
    private const int MaxAttempts = 3;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var message in queue.Reader.ReadAllAsync(stoppingToken))
        {
            await DeliverAsync(message, stoppingToken);
        }
    }

    private async Task DeliverAsync(EmailMessage message, CancellationToken stoppingToken)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<IEmailSender>().SendAsync(message, stoppingToken);
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // A failed delivery must never take the dispatcher down; it is logged and retried.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                // The recipient is deliberately not logged at error level together with the subject more than needed.
                LogDeliveryFailed(logger, ex, message.Subject, attempt, MaxAttempts);
                if (attempt < MaxAttempts)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2 * attempt), stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                }
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Delivering e-mail '{Subject}' failed (attempt {Attempt} of {MaxAttempts})")]
    private static partial void LogDeliveryFailed(ILogger logger, Exception exception, string subject, int attempt, int maxAttempts);
}

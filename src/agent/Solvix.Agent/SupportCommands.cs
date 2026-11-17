namespace Solvix.Agent
{
    using System.Globalization;
    using Solvix.Agent.Contracts;
    using Solvix.Agent.Core.LocalApi;

    /// <summary>
    /// What a person at the computer can do: report a problem and chat with a technician. These talk to
    /// the running agent service over a local pipe, so they work without administrator rights.
    /// </summary>
    internal static class SupportCommands
    {
        public static async Task<int> TicketAsync(string[] args, string pipeName)
        {
            var subject = Option(args, "--subject") ?? Option(args, "-s");
            var description = Option(args, "--description") ?? Option(args, "-d");
            var priorityText = Option(args, "--priority") ?? "normal";
            if (!Enum.TryParse<TicketPriority>(priorityText, ignoreCase: true, out var priority) || !Enum.IsDefined(priority))
            {
                Console.Error.WriteLine("Priority must be low, normal, high or urgent.");
                return 2;
            }

            if (string.IsNullOrWhiteSpace(subject))
            {
                if (Console.IsInputRedirected)
                {
                    Console.Error.WriteLine("Usage: Solvix.Agent ticket --subject \"<what is wrong>\" [--description \"<details>\"] [--priority normal]");
                    return 2;
                }

                Console.Write("What is the problem (a few words)? ");
                subject = Console.ReadLine();
                if (string.IsNullOrWhiteSpace(subject))
                {
                    Console.Error.WriteLine("Cancelled: no description given.");
                    return 2;
                }

                Console.Write("Any details that would help (optional)? ");
                description = Console.ReadLine();
            }

            var response = await new LocalApiClient(pipeName).CallAsync(
                new LocalRequest
                {
                    Op = LocalOps.SubmitTicket,
                    Ticket = new SubmitTicketRequest { Subject = subject.Trim(), Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(), Priority = priority },
                },
                CancellationToken.None);
            if (!response.Ok || response.Ticket is null)
            {
                Console.Error.WriteLine(response.Error ?? "The ticket could not be submitted.");
                return 1;
            }

            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Ticket #{response.Ticket.Number} was sent to IT. You can follow it with: Solvix.Agent tickets"));
            return 0;
        }

        public static async Task<int> TicketsAsync(string pipeName)
        {
            var response = await new LocalApiClient(pipeName).CallAsync(new LocalRequest { Op = LocalOps.ListTickets }, CancellationToken.None);
            if (!response.Ok)
            {
                Console.Error.WriteLine(response.Error);
                return 1;
            }

            if (response.Tickets is not { Count: > 0 })
            {
                Console.WriteLine("No tickets have been raised from this computer.");
                return 0;
            }

            foreach (var t in response.Tickets)
            {
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"#{t.Number,-6} {t.Status,-10} {t.Priority,-7} {t.CreatedAtUtc.LocalDateTime:g}  {t.Subject}"));
            }

            return 0;
        }

        public static async Task<int> ChatAsync(string pipeName)
        {
            var client = new LocalApiClient(pipeName);
            using var stop = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                stop.Cancel();
            };

            var first = await client.CallAsync(new LocalRequest { Op = LocalOps.PollChat, After = 0 }, stop.Token);
            if (!first.Ok)
            {
                Console.Error.WriteLine(first.Error);
                return 1;
            }

            Console.WriteLine("Chat with IT. Type a message and press Enter; /quit to leave.");
            long last = 0;
            foreach (var m in first.Messages ?? [])
            {
                Print(m);
                last = Math.Max(last, m.Sequence);
            }

            var listener = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    LocalResponse? poll = null;
                    try
                    {
                        poll = await client.CallAsync(new LocalRequest { Op = LocalOps.PollChat, After = last, WaitSeconds = 20 }, stop.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    if (!poll.Ok)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5), stop.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                        continue;
                    }

                    foreach (var m in poll.Messages ?? [])
                    {
                        last = Math.Max(last, m.Sequence);
                        if (m.Sender == ChatSender.Technician)
                        {
                            Print(m);
                        }
                    }
                }
            });

            while (!stop.IsCancellationRequested)
            {
                var line = await Task.Run(Console.ReadLine);
                if (line is null || string.Equals(line.Trim(), "/quit", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var sent = await client.CallAsync(new LocalRequest { Op = LocalOps.SendChat, Body = line }, CancellationToken.None);
                if (!sent.Ok)
                {
                    Console.Error.WriteLine(sent.Error);
                }
                else if (sent.Message is { } message)
                {
                    last = Math.Max(last, message.Sequence);
                }
            }

            await stop.CancelAsync();
            await listener.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            return 0;
        }

        private static void Print(ChatMessage m) =>
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[{m.SentAtUtc.LocalDateTime:t}] {(m.Sender == ChatSender.Technician ? m.SenderName ?? "IT" : "You")}: {m.Body}"));

        private static string? Option(string[] args, string name)
        {
            var index = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }
    }
}

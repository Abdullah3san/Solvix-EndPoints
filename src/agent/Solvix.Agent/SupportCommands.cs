namespace Solvix.Agent
{
    using System.Globalization;
    using Solvix.Agent.Contracts;
    using Solvix.Agent.Core.Enrollment;
    using Solvix.Agent.Core.LocalApi;

    /// <summary>
    /// What a person at the computer can do: report a problem and chat with a technician. These talk to
    /// the running agent service over a local pipe, so they work without administrator rights.
    /// </summary>
    internal static class RemoteAccessCommandLine
    {
        /// <summary>Adds <c>--remote-access</c>, <c>--remote-features</c> and <c>--files-folder</c> (all local decisions) to the settings.</summary>
        public static EnrollmentRequestSettings? Apply(EnrollmentRequestSettings settings, string[] args, out string? error)
        {
            error = null;
            if (!RemoteAccessChoice.TryParse(ArgValue(args, "--remote-access"), out var mode))
            {
                error = $"Unknown remote access choice. Use {RemoteAccessChoice.Usage} (or leave it out to keep remote access off).";
                return null;
            }

            if (!RemoteFeatureChoice.TryParse(ArgValue(args, "--remote-features"), out var features, out error))
            {
                return null;
            }

            var folders = new List<string>();
            for (var i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], "--files-folder", StringComparison.OrdinalIgnoreCase))
                {
                    if (!RemoteFeatureChoice.TryValidateFolder(args[i + 1], out var full, out error))
                    {
                        return null;
                    }

                    folders.Add(full);
                }
            }

            if (mode == Solvix.Agent.Contracts.RemoteAccessMode.Disabled && (features != Solvix.Agent.Contracts.RemoteFeatures.Shell || folders.Count > 0))
            {
                error = $"--remote-features and --files-folder need remote access to be switched on: add {RemoteAccessChoice.Usage}.";
                return null;
            }

            return settings with { RemoteAccess = mode, RemoteFeatures = features, FileFolders = folders };
        }

        private static string? ArgValue(string[] args, string name)
        {
            var index = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }
    }

    internal static class SupportCommands
    {
        /// <summary>
        /// Runs in the signed-in user's own session (started at sign-in) and lets the service capture
        /// that user's screen. It holds no credential and talks only to the service's local pipe.
        /// </summary>
        public static async Task<int> ScreenHelperAsync(string[] args)
        {
            if (!OperatingSystem.IsWindows())
            {
                Console.Error.WriteLine("The screen helper is only available on Windows.");
                return 2;
            }

            return await WindowsHelper.RunAsync(ArgumentAfter(args, "--pipe") ?? new Solvix.Agent.Core.Configuration.RemoteAccessOptions().ScreenHelperPipeName);
        }

        private static string? ArgumentAfter(string[] args, string name)
        {
            var index = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }

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

namespace Solvix.Agent
{
    using System.Runtime.Versioning;
    using Solvix.Agent.Core.Remote;
    using Solvix.Agent.Windows.Remote;

    [SupportedOSPlatform("windows")]
    internal static class WindowsHelper
    {
        public static async Task<int> RunAsync(string pipeName)
        {
            GdiScreenCapture.PrepareHelperProcess(); // no console window on the user's screen; real pixel sizes
            using var stop = new CancellationTokenSource();
            await new ScreenHelperLoop(new GdiScreenCapture(), new SendInputInjector(), pipeName).RunAsync(stop.Token);
            return 0;
        }
    }
}

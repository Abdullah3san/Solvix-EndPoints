namespace Solvix.Agent.Core.Monitoring;

using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using Solvix.Agent.Contracts;
using Solvix.Agent.Core.Configuration;

/// <summary>Measures local network reachability (the network signal), separately from server connectivity.</summary>
public interface INetworkReachabilityProbe
{
    Task<NetworkReachabilitySnapshot?> ProbeAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Built-in probes only: an operational adapter, the default gateway (ICMP),
/// DNS resolution and TCP connects to targets from LOCAL configuration. The
/// server can neither choose targets nor trigger probes.
/// </summary>
public sealed class NetworkReachabilityProbe(IOptions<AgentOptions> options, TimeProvider time) : INetworkReachabilityProbe
{
    public async Task<NetworkReachabilitySnapshot?> ProbeAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value.Network;
        if (!settings.Enabled)
        {
            return null;
        }

        var timeout = TimeSpan.FromMilliseconds(Math.Clamp(settings.TimeoutMilliseconds, 200, 10_000));
        var checks = new List<Task<NetworkCheck>>();

        var adapters = OperationalAdapters();
        var interfaceCheck = new NetworkCheck
        {
            Kind = NetworkCheckKind.Interface,
            Target = adapters.Count > 0 ? string.Join(", ", adapters.Select(a => a.Name).Take(4)) : "none",
            Success = adapters.Count > 0,
            Error = adapters.Count > 0 ? null : "No operational network adapter.",
        };

        if (settings.PingGateway)
        {
            var gateway = adapters
                .SelectMany(a => a.GetIPProperties().GatewayAddresses)
                .Select(g => g.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));
            checks.Add(gateway is null
                ? Task.FromResult(new NetworkCheck { Kind = NetworkCheckKind.Gateway, Target = "default gateway", Success = false, Error = "No default gateway configured." })
                : PingAsync(gateway, timeout));
        }

        foreach (var host in DnsTargets(settings).Take(5))
        {
            checks.Add(ResolveAsync(host, timeout, cancellationToken));
        }

        foreach (var target in settings.TcpTargets.Where(t => !string.IsNullOrWhiteSpace(t)).Take(10))
        {
            checks.Add(ConnectAsync(target.Trim(), timeout, cancellationToken));
        }

        var results = await Task.WhenAll(checks);
        return new NetworkReachabilitySnapshot { CheckedAtUtc = time.GetUtcNow(), Checks = [interfaceCheck, .. results] };
    }

    private IEnumerable<string> DnsTargets(NetworkProbeOptions settings)
    {
        if (settings.DnsHostNames.Count > 0)
        {
            return settings.DnsHostNames.Where(h => !string.IsNullOrWhiteSpace(h)).Select(h => h.Trim());
        }

        var server = options.Value.ServerUrl;
        return server is not null && server.HostNameType == UriHostNameType.Dns ? [server.Host] : [];
    }

    private static List<NetworkInterface> OperationalAdapters()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(a => a.OperationalStatus == OperationalStatus.Up
                    && a.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                .ToList();
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }

    private static async Task<NetworkCheck> PingAsync(IPAddress gateway, TimeSpan timeout)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(gateway, timeout);
            return new NetworkCheck
            {
                Kind = NetworkCheckKind.Gateway,
                Target = gateway.ToString(),
                Success = reply.Status == IPStatus.Success,
                LatencyMs = reply.Status == IPStatus.Success ? (int)reply.RoundtripTime : null,
                Error = reply.Status == IPStatus.Success ? null : reply.Status.ToString(),
            };
        }
        catch (Exception ex) when (ex is PingException or InvalidOperationException or PlatformNotSupportedException)
        {
            return new NetworkCheck { Kind = NetworkCheckKind.Gateway, Target = gateway.ToString(), Success = false, Error = Short(ex) };
        }
    }

    private static async Task<NetworkCheck> ResolveAsync(string host, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);
            var addresses = await Dns.GetHostAddressesAsync(host, cts.Token);
            return new NetworkCheck { Kind = NetworkCheckKind.Dns, Target = host, Success = addresses.Length > 0, LatencyMs = (int)watch.ElapsedMilliseconds };
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ArgumentException)
        {
            return new NetworkCheck { Kind = NetworkCheckKind.Dns, Target = host, Success = false, Error = ex is OperationCanceledException ? "Timed out." : Short(ex) };
        }
    }

    private static async Task<NetworkCheck> ConnectAsync(string target, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var separator = target.LastIndexOf(':');
        if (separator <= 0 || !int.TryParse(target[(separator + 1)..], System.Globalization.CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
        {
            return new NetworkCheck { Kind = NetworkCheckKind.Tcp, Target = target, Success = false, Error = "Invalid target (expected host:port)." };
        }

        var watch = Stopwatch.StartNew();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);
            using var client = new TcpClient();
            await client.ConnectAsync(target[..separator].Trim('[', ']'), port, cts.Token);
            return new NetworkCheck { Kind = NetworkCheckKind.Tcp, Target = target, Success = true, LatencyMs = (int)watch.ElapsedMilliseconds };
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return new NetworkCheck { Kind = NetworkCheckKind.Tcp, Target = target, Success = false, Error = ex is OperationCanceledException ? "Timed out." : Short(ex) };
        }
    }

    private static string Short(Exception ex) => ex.Message.Length > 200 ? ex.Message[..200] : ex.Message;
}

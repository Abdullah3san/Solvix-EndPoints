namespace Solvix.SharedKernel.Telemetry;

using System.Diagnostics.Metrics;

/// <summary>
/// Business metrics shared by all modules (exported through OpenTelemetry when an exporter is configured).
/// Tags never carry tenant names or user data; tenant id is intentionally not a tag (unbounded cardinality).
/// </summary>
public static class SolvixMetrics
{
    public const string MeterName = "Solvix";

    private static readonly Meter Meter = new(MeterName, "1.0");

    public static readonly Counter<long> DeviceEnrollmentsDenied = Meter.CreateCounter<long>("solvix.devices.enrollments_denied", description: "Enrollments refused by the subscription (limit reached, lapsed).");

    public static readonly Counter<long> InvoicesIssued = Meter.CreateCounter<long>("solvix.billing.invoices_issued");

    public static readonly Counter<long> PaymentsSucceeded = Meter.CreateCounter<long>("solvix.billing.payments_succeeded");

    public static readonly Counter<long> PaymentMismatches = Meter.CreateCounter<long>("solvix.billing.payment_mismatches", description: "Provider payments whose amount/currency/tenant did not match the invoice.");

    public static readonly Counter<long> WebhooksRejected = Meter.CreateCounter<long>("solvix.billing.webhooks_rejected", description: "Webhook deliveries that failed signature verification.");

    public static readonly Counter<long> BillingJobFailures = Meter.CreateCounter<long>("solvix.billing.job_failures");

    public static readonly Counter<long> TicketsCreated = Meter.CreateCounter<long>("solvix.helpdesk.tickets_created");

    public static readonly Counter<long> TicketsEscalated = Meter.CreateCounter<long>("solvix.helpdesk.tickets_escalated");

    public static readonly Counter<long> SlaBreaches = Meter.CreateCounter<long>("solvix.helpdesk.sla_breaches");

    public static readonly Counter<long> InboundEmails = Meter.CreateCounter<long>("solvix.helpdesk.inbound_emails");

    public static readonly Counter<long> AiSummaries = Meter.CreateCounter<long>("solvix.helpdesk.ai_summaries");
}

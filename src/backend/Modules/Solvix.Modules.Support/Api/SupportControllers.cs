namespace Solvix.Modules.Support.Api;

using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Solvix.Agent.Contracts;
using Solvix.Modules.Support.Application;
using Solvix.SharedKernel.Results;
using Solvix.SharedKernel.Web;

/// <summary>Tickets, for technicians in the web console. Any member may raise a ticket; handling them needs an administrator.</summary>
[ApiVersion(1.0)]
[Route("api/v{version:apiVersion}/tickets")]
public sealed class TicketsController(ITicketService service) : ApiControllerBase
{
    [HttpGet]
    [Authorize(Policy = Policies.TenantAdmin)]
    public async Task<ActionResult<PagedResult<TicketSummaryDto>>> List([FromQuery] TicketListQuery query, CancellationToken cancellationToken) =>
        Ok(await service.ListAsync(query, cancellationToken));

    [HttpGet("{id:guid}")]
    [Authorize(Policy = Policies.TenantAdmin)]
    public async Task<ActionResult<TicketDetailsDto>> Get(Guid id, CancellationToken cancellationToken) =>
        Ok(await service.GetAsync(id, cancellationToken));

    [HttpPost]
    [ProducesResponseType<TicketDetailsDto>(StatusCodes.Status201Created)]
    public async Task<ActionResult<TicketDetailsDto>> Create([FromBody] CreateTicketRequest request, CancellationToken cancellationToken)
    {
        var created = await service.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = created.Summary.Id, version = "1" }, created);
    }

    [HttpPatch("{id:guid}")]
    [Authorize(Policy = Policies.TenantAdmin)]
    public async Task<ActionResult<TicketDetailsDto>> Update(Guid id, [FromBody] UpdateTicketRequest request, CancellationToken cancellationToken) =>
        Ok(await service.UpdateAsync(id, request, cancellationToken));

    [HttpPost("{id:guid}/comments")]
    [Authorize(Policy = Policies.TenantAdmin)]
    public async Task<ActionResult<TicketDetailsDto>> AddComment(Guid id, [FromBody] AddTicketCommentRequest request, CancellationToken cancellationToken) =>
        Ok(await service.AddCommentAsync(id, request, cancellationToken));
}

/// <summary>Live chat with the person using a device.</summary>
[ApiVersion(1.0)]
[Authorize(Policy = Policies.TenantAdmin)]
public sealed class ChatController(IChatService service) : ApiControllerBase
{
    [HttpGet("~/api/v{version:apiVersion}/chat/conversations")]
    public async Task<ActionResult<IReadOnlyList<ChatConversationDto>>> Conversations(CancellationToken cancellationToken) =>
        Ok(await service.ListConversationsAsync(cancellationToken));

    [HttpGet("~/api/v{version:apiVersion}/devices/{deviceId:guid}/chat")]
    public async Task<ActionResult<IReadOnlyList<ChatMessageDto>>> List(Guid deviceId, [FromQuery] long after, CancellationToken cancellationToken) =>
        Ok(await service.ListAsync(deviceId, after, cancellationToken));

    [HttpPost("~/api/v{version:apiVersion}/devices/{deviceId:guid}/chat")]
    [ProducesResponseType<ChatMessageDto>(StatusCodes.Status201Created)]
    public async Task<ActionResult<ChatMessageDto>> Send(Guid deviceId, [FromBody] SendChatRequest request, CancellationToken cancellationToken) =>
        StatusCode(StatusCodes.Status201Created, await service.SendAsync(deviceId, request, cancellationToken));
}

/// <summary>Called by the agent on behalf of the person using the computer.</summary>
[ApiController]
[ApiVersion(1.0)]
[Produces("application/json")]
[Route("api/v{version:apiVersion}/agent")]
[Authorize(Policy = Policies.Device)]
public sealed class AgentSupportController(IAgentSupportService service) : ControllerBase
{
    [HttpPost("tickets")]
    [RequestSizeLimit(64 * 1024)]
    public async Task<ActionResult<AgentTicket>> SubmitTicket([FromBody] SubmitTicketRequest request, CancellationToken cancellationToken) =>
        StatusCode(StatusCodes.Status201Created, await service.SubmitTicketAsync(request, cancellationToken));

    [HttpGet("tickets")]
    public async Task<ActionResult<AgentTicketList>> Tickets(CancellationToken cancellationToken) =>
        Ok(await service.ListTicketsAsync(cancellationToken));

    [HttpGet("chat")]
    public async Task<ActionResult<ChatMessageList>> PollChat([FromQuery] long after, [FromQuery] int wait, CancellationToken cancellationToken) =>
        Ok(await service.PollChatAsync(after, wait, cancellationToken));

    [HttpPost("chat")]
    [RequestSizeLimit(16 * 1024)]
    public async Task<ActionResult<ChatMessage>> SendChat([FromBody] SendChatMessageRequest request, CancellationToken cancellationToken) =>
        StatusCode(StatusCodes.Status201Created, await service.SendChatAsync(request, cancellationToken));
}

/// <summary>Remote command sessions on a laptop. Administrators only; every session is audited.</summary>
[ApiVersion(1.0)]
[Authorize(Policy = Policies.TenantAdmin)]
public sealed class RemoteSessionsController(IRemoteSessionService service) : ApiControllerBase
{
    [HttpPost("~/api/v{version:apiVersion}/devices/{deviceId:guid}/remote-sessions")]
    [ProducesResponseType<RemoteSessionDto>(StatusCodes.Status201Created)]
    public async Task<ActionResult<RemoteSessionDto>> Start(Guid deviceId, [FromBody] StartRemoteSessionRequest request, CancellationToken cancellationToken) =>
        StatusCode(StatusCodes.Status201Created, await service.StartAsync(deviceId, request, cancellationToken));

    [HttpGet("~/api/v{version:apiVersion}/remote-sessions")]
    public async Task<ActionResult<PagedResult<RemoteSessionDto>>> List([FromQuery] RemoteSessionListQuery query, CancellationToken cancellationToken) =>
        Ok(await service.ListAsync(query, cancellationToken));

    [HttpGet("~/api/v{version:apiVersion}/remote-sessions/{id:guid}")]
    public async Task<ActionResult<RemoteSessionDto>> Get(Guid id, CancellationToken cancellationToken) =>
        Ok(await service.GetAsync(id, cancellationToken));

    [HttpGet("~/api/v{version:apiVersion}/remote-sessions/{id:guid}/output")]
    public async Task<ActionResult<RemoteOutputDto>> Output(Guid id, [FromQuery] long after, [FromQuery] int wait, CancellationToken cancellationToken) =>
        Ok(await service.OutputAsync(id, after, wait, cancellationToken));

    [HttpPost("~/api/v{version:apiVersion}/remote-sessions/{id:guid}/input")]
    [RequestSizeLimit(64 * 1024)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Input(Guid id, [FromBody] RemoteInputRequest request, CancellationToken cancellationToken)
    {
        await service.SendInputAsync(id, request, cancellationToken);
        return NoContent();
    }

    [HttpPost("~/api/v{version:apiVersion}/remote-sessions/{id:guid}/end")]
    public async Task<ActionResult<RemoteSessionDto>> End(Guid id, CancellationToken cancellationToken) =>
        Ok(await service.EndAsync(id, cancellationToken));
}

/// <summary>Agent side of remote sessions (device certificate).</summary>
[ApiController]
[ApiVersion(1.0)]
[Produces("application/json")]
[Route("api/v{version:apiVersion}/agent/remote")]
[Authorize(Policy = Policies.Device)]
public sealed class AgentRemoteController(IAgentRemoteService service) : ControllerBase
{
    [HttpGet("pending")]
    public async Task<ActionResult<PendingRemoteResponse>> Pending([FromQuery] int wait, CancellationToken cancellationToken) =>
        Ok(await service.PendingAsync(wait, cancellationToken));

    [HttpPost("{id:guid}/accept")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Accept(Guid id, [FromBody] AcceptRemoteRequest request, CancellationToken cancellationToken)
    {
        await service.AcceptAsync(id, request, cancellationToken);
        return NoContent();
    }

    [HttpPost("{id:guid}/deny")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Deny(Guid id, [FromBody] DenyRemoteRequest request, CancellationToken cancellationToken)
    {
        await service.DenyAsync(id, request, cancellationToken);
        return NoContent();
    }

    [HttpPost("{id:guid}/end")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> End(Guid id, [FromBody] EndRemoteRequest request, CancellationToken cancellationToken)
    {
        await service.EndAsync(id, request, cancellationToken);
        return NoContent();
    }

    [HttpPost("{id:guid}/output")]
    [RequestSizeLimit(64 * 1024)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Output(Guid id, [FromBody] RemoteOutputRequest request, CancellationToken cancellationToken)
    {
        await service.PostOutputAsync(id, request, cancellationToken);
        return NoContent();
    }

    [HttpGet("{id:guid}/input")]
    public async Task<ActionResult<RemoteInputResponse>> Input(Guid id, [FromQuery] long after, [FromQuery] int wait, CancellationToken cancellationToken) =>
        Ok(await service.InputAsync(id, after, wait, cancellationToken));
}

/// <summary>Screen viewer and file transfer for technicians (inside a screen / files session).</summary>
[ApiVersion(1.0)]
[Authorize(Policy = Policies.TenantAdmin)]
public sealed class RemoteMediaController(IRemoteScreenService screen, IRemoteFileService files) : ApiControllerBase
{
    /// <summary>200 with the newest JPEG frame, or 204 when nothing newer arrived. Both carry the session state.</summary>
    [HttpGet("~/api/v{version:apiVersion}/remote-sessions/{id:guid}/screen/frame")]
    public async Task<IActionResult> Frame(Guid id, [FromQuery] long after, [FromQuery] int wait, CancellationToken cancellationToken)
    {
        var result = await screen.GetFrameAsync(id, after, wait, cancellationToken);
        Response.Headers["X-Session-Status"] = result.Status.ToString();
        Response.Headers.CacheControl = "no-store";
        if (result.Jpeg is null)
        {
            return NoContent();
        }

        Response.Headers["X-Frame-Sequence"] = result.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Response.Headers["X-Frame-Width"] = result.Width.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Response.Headers["X-Frame-Height"] = result.Height.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return File(result.Jpeg, "image/jpeg");
    }

    [HttpPost("~/api/v{version:apiVersion}/remote-sessions/{id:guid}/files/list")]
    public async Task<ActionResult<RemoteFileOperationDto>> List(Guid id, [FromBody] ListRemoteFilesRequest request, CancellationToken cancellationToken) =>
        Ok(await files.ListAsync(id, request.Path, 20, cancellationToken));

    [HttpGet("~/api/v{version:apiVersion}/remote-sessions/{id:guid}/files/download")]
    public async Task Download(Guid id, [FromQuery] string? path, CancellationToken cancellationToken)
    {
        var download = await files.DownloadAsync(id, path, cancellationToken);
        Response.ContentType = "application/octet-stream";
        Response.ContentLength = download.SizeBytes;
        Response.Headers.ContentDisposition = new System.Net.Mime.ContentDisposition { FileName = download.FileName, DispositionType = "attachment" }.ToString();
        Response.Headers.CacheControl = "no-store";
        await foreach (var chunk in download.Chunks.WithCancellation(cancellationToken))
        {
            await Response.Body.WriteAsync(chunk, cancellationToken);
        }
    }

    [HttpPost("~/api/v{version:apiVersion}/remote-sessions/{id:guid}/files/upload")]
    [RequestSizeLimit(FileLimits.MaxFileBytes + 1024 * 1024)]
    [DisableRequestSizeLimit]
    [ProducesResponseType<RemoteFileOperationDto>(StatusCodes.Status202Accepted)]
    public async Task<ActionResult<RemoteFileOperationDto>> Upload(Guid id, [FromQuery] string? path, [FromQuery] bool overwrite, CancellationToken cancellationToken) =>
        StatusCode(StatusCodes.Status202Accepted, await files.UploadAsync(id, path, overwrite, Request.Body, cancellationToken));

    [HttpGet("~/api/v{version:apiVersion}/remote-sessions/{id:guid}/files/operations")]
    public async Task<ActionResult<IReadOnlyList<RemoteFileOperationDto>>> Operations(Guid id, CancellationToken cancellationToken) =>
        Ok(await files.ListOperationsAsync(id, cancellationToken));

    [HttpGet("~/api/v{version:apiVersion}/remote-sessions/{id:guid}/files/operations/{operationId:guid}")]
    public async Task<ActionResult<RemoteFileOperationDto>> Operation(Guid id, Guid operationId, [FromQuery] int wait, CancellationToken cancellationToken) =>
        Ok(await files.GetAsync(id, operationId, wait, cancellationToken));
}

/// <summary>Agent side of screen frames and file operations (device certificate).</summary>
[ApiController]
[ApiVersion(1.0)]
[Produces("application/json")]
[Route("api/v{version:apiVersion}/agent/remote/{sessionId:guid}")]
[Authorize(Policy = Policies.Device)]
public sealed class AgentRemoteMediaController(IAgentScreenService screen, IAgentRemoteFileService files) : ControllerBase
{
    [HttpPost("frame")]
    [RequestSizeLimit(ScreenLimits.MaxFrameBytes + 1024)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Frame(Guid sessionId, [FromQuery] int width, [FromQuery] int height, CancellationToken cancellationToken)
    {
        await screen.PostFrameAsync(sessionId, width, height, await ReadBodyAsync(ScreenLimits.MaxFrameBytes, cancellationToken), cancellationToken);
        return NoContent();
    }

    [HttpGet("files/next")]
    public async Task<ActionResult<PendingFileOperationResponse>> Next(Guid sessionId, [FromQuery] int wait, CancellationToken cancellationToken) =>
        Ok(await files.NextAsync(sessionId, wait, cancellationToken));

    [HttpPost("files/{operationId:guid}/begin")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Begin(Guid sessionId, Guid operationId, [FromBody] BeginFileDownloadRequest request, CancellationToken cancellationToken)
    {
        await files.BeginDownloadAsync(sessionId, operationId, request.SizeBytes, cancellationToken);
        return NoContent();
    }

    [HttpPost("files/{operationId:guid}/chunk")]
    [RequestSizeLimit(FileLimits.ChunkBytes + 1024)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> PostChunk(Guid sessionId, Guid operationId, [FromQuery] int index, CancellationToken cancellationToken)
    {
        await files.PostChunkAsync(sessionId, operationId, index, await ReadBodyAsync(FileLimits.ChunkBytes, cancellationToken), cancellationToken);
        return NoContent();
    }

    [HttpGet("files/{operationId:guid}/chunk/{index:int}")]
    public async Task<IActionResult> GetChunk(Guid sessionId, Guid operationId, int index, CancellationToken cancellationToken) =>
        File(await files.GetUploadChunkAsync(sessionId, operationId, index, cancellationToken), "application/octet-stream");

    [HttpPost("files/{operationId:guid}/result")]
    [RequestSizeLimit(4 * 1024 * 1024)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Result(Guid sessionId, Guid operationId, [FromBody] FileOperationResult result, CancellationToken cancellationToken)
    {
        await files.CompleteAsync(sessionId, operationId, result, cancellationToken);
        return NoContent();
    }

    private async Task<byte[]> ReadBodyAsync(int max, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await Request.Body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > max)
            {
                throw new Solvix.SharedKernel.Exceptions.ValidationException("body", "The request body is too large.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}

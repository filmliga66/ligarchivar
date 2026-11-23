using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using FL.LigArchivar.Api.Models;
using FL.LigArchivar.Api.Services;

namespace FL.LigArchivar.Api.Controllers;

[ApiController]
[Route("api/events")]
[Authorize]
public class EventsController : ControllerBase
{
    private readonly ArchiveService _archiveService;

    public EventsController(ArchiveService archiveService)
    {
        _archiveService = archiveService;
    }

    /// <summary>
    /// Returns event details including file list.
    /// GET /api/events?path=Digitalfoto/2018/A-Albverein/A_2018-05-01_Maiwanderung
    /// </summary>
    [HttpGet]
    public ActionResult<EventDetailDto> GetEvent([FromQuery] string path)
    {
        if (!ArchiveService.ValidatePath(path))
            return BadRequest("Invalid path.");

        var dto = _archiveService.GetEvent(path);
        if (dto == null)
            return NotFound();

        return Ok(dto);
    }

    /// <summary>
    /// Sequential rename. Acquires write lock; returns 409 if locked.
    /// POST /api/events/rename?path=Digitalfoto/2018/A-Albverein/A_2018-05-01_Maiwanderung
    /// </summary>
    [HttpPost("rename")]
    public IActionResult Rename([FromQuery] string path, [FromBody] RenameRequestDto request) =>
        ToActionResult(_archiveService.Rename(path, request));

    /// <summary>
    /// DateTime-based rename. Acquires write lock; returns 409 if locked.
    /// POST /api/events/rename-by-datetime?path=Digitalfoto/2018/A-Albverein/A_2018-05-01_Maiwanderung
    /// </summary>
    [HttpPost("rename-by-datetime")]
    public IActionResult RenameByDateTime([FromQuery] string path,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] RenameByDateTimeRequestDto? request) =>
        ToActionResult(_archiveService.RenameByDateTime(path, request?.ConfirmationToken));

    private IActionResult ToActionResult(RenameOutcome outcome) => outcome switch
    {
        RenameCompleted completed => Ok(completed.Event),
        RenameNeedsConfirmation confirmation => Accepted(confirmation.Preview),
        RenameFailure failure => StatusCode(failure.StatusCode, new { message = failure.Message }),
        _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
    };
}

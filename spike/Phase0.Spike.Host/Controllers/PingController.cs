using Microsoft.AspNetCore.Mvc;

namespace Phase0.Spike.Host.Controllers;

/// <summary>An ordinary MVC controller, present so A4 can assert it is unaffected by the negotiate policy.</summary>
[ApiController]
[Route("api/mvc-ping")]
public class PingController : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => Ok(new { pong = true, via = "mvc" });
}

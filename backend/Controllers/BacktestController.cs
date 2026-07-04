using Microsoft.AspNetCore.Mvc;
using System.Threading.Tasks;
using backend.Models;
using backend.Services;

namespace backend.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class BacktestController : ControllerBase
    {
        private readonly BacktestService _backtestService;

        public BacktestController(BacktestService backtestService)
        {
            _backtestService = backtestService;
        }

        [HttpPost("run")]
        public async Task<IActionResult> RunBacktest([FromBody] BacktestRequest request)
        {
            if (string.IsNullOrEmpty(request.Ticker)) return BadRequest("Ticker is required");
            var result = await _backtestService.RunBacktestAsync(request.Ticker, request.StopLossPct, request.TargetPct, request.UseDynamicExits);
            return Ok(result);
        }

        [HttpPost("run-all")]
        public async Task<IActionResult> RunAllBacktests([FromBody] BacktestRequest request)
        {
            var result = await _backtestService.RunAllBacktestsAsync(request.StopLossPct, request.TargetPct, request.UseDynamicExits);
            return Ok(result);
        }
    }
}

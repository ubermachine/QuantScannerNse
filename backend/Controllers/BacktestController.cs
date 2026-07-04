using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using backend.Models;
using backend.Services;

namespace backend.Controllers
{
    [ApiController]
    [Route("api/backtest")]
    public class BacktestController : ControllerBase
    {
        private readonly BacktestService _backtestService;

        public BacktestController(BacktestService backtestService)
        {
            _backtestService = backtestService;
        }

        [HttpPost("portfolio")]
        public async Task<ActionResult<PortfolioSimulationResult>> RunPortfolioSimulation([FromBody] PortfolioRequest request)
        {
            try
            {
                if (request == null)
                {
                    return BadRequest("PortfolioRequest request body is required.");
                }

                var result = await _backtestService.RunPortfolioSimulationAsync(request);
                return Ok(result);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        [HttpPost("compare-all")]
        public async Task<ActionResult<MultiStrategySimulationResult>> CompareAllStrategies([FromBody] PortfolioRequest request)
        {
            try
            {
                if (request == null) return BadRequest("Request body is required.");
                var result = await _backtestService.RunAllStrategiesAsync(request);
                return Ok(result);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }
    }
}

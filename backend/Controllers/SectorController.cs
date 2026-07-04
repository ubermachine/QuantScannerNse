using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using backend.Models;
using backend.Services;

namespace backend.Controllers
{
    [ApiController]
    [Route("api/sector")]
    public class SectorController : ControllerBase
    {
        private readonly SectorService _sectorService;

        public SectorController(SectorService sectorService)
        {
            _sectorService = sectorService;
        }

        [HttpPost("sync")]
        public async Task<ActionResult<object>> SyncSectors()
        {
            try
            {
                int count = await _sectorService.SyncSectorDataAsync();
                return Ok(new { message = $"Synced {count} bars across sector indices", barsCount = count });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        [HttpGet("rotation")]
        public ActionResult<SectorRotationResult> GetRotation()
        {
            try
            {
                var result = _sectorService.GetSectorRotation();
                return Ok(result);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        [HttpPost("rotation-backtest")]
        public ActionResult<RotationBacktestResult> RunRotationBacktest([FromBody] RotationBacktestRequest request)
        {
            try
            {
                var result = _sectorService.RunRotationBacktest(request);
                return Ok(result);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }
    }
}

using Microsoft.AspNetCore.Mvc;
using WAF.DataStores;

namespace WAF.NodeServer.Controllers {
    [ApiController]
    [Route("/api")]
    public class HttpController : ControllerBase {

        public IDataStore DB;
        public HttpController(IDataStore db) => DB = db;

        // Read/Write - BINARY
        [HttpPost]
        [Route("binary")]
        [DisableRequestSizeLimit]
        [RequestSizeLimit(1024 * 1024 * 500)] // 500mb
        public Task<IActionResult> Binary() => this.BinaryResponse();

        //// Read - JSON
        //[HttpGet]
        //[Route("")]
        //public Task<IActionResult> Info() => this.Json(nameof(DB.MaintenanceAsync));

        //// Read - JSON
        //[HttpGet]
        //[HttpPost]
        //[Route("get")]
        //public Task<IActionResult> Get(string? id) => this.Json(nameof(DB.GetAsync), string.IsNullOrEmpty(id) ? this.PostedBody() : id);

        //// Read - JSON
        //[HttpGet]
        //[HttpPost]
        //[Route("query")]
        //public Task<IActionResult> QueryPost(string? query) => this.Json(nameof(DB.QueryAsync), string.IsNullOrEmpty(query) ? this.PostedBody() : query);

        //// Write - JSON
        //[HttpPost]
        //[Route("execute")]
        //public Task<IActionResult> Execute() => this.Json(nameof(DB.ExecuteAsync), this.PostedBody());

        //// Read/Write - JSON
        //[HttpPost]
        //[Route("maintenance")]
        //public Task<IActionResult> Maintenance() => this.Json(nameof(DB.MaintenanceAsync), this.PostedBody());

    }
}
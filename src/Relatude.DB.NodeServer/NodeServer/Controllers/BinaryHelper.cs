using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Relatude.DB.Common;
using Relatude.DB.Connection;
using Relatude.DB.DataStores;
using System.Net.Http.Json;
using System.Reflection.Metadata;

namespace Relatude.DB.NodeServer.Controllers {
    public static class BinaryHelper {
        static public async Task<IActionResult> BinaryResponse(this HttpController controller) {
            using MemoryStream input = new();
            await controller.Request.Body.CopyToAsync(input);
            if (SingleHttpConnection.ValidateChecksums) {
                var checksumS = controller.Request.Headers[HeaderNames.Header_Checksum].FirstOrDefault();
                if (!int.TryParse(checksumS, out var checksum)) throw new Exception("Invalid checksum. ");
                if (checksum != input.ToArray().GetChecksum()) throw new Exception("Data does not validate against header checksum. ");
            }
            if (SingleHttpConnection.ValidateLengths) {
                var lengthS = controller.Request.Headers[HeaderNames.Header_Length].FirstOrDefault();
                if (!long.TryParse(lengthS, out var length)) throw new Exception("Invalid length value in header. ");
                if (length != input.Length) throw new Exception("Data length does not validate against header length value. ");
            }
            input.Position = 0;
            using var output = await controller.DB.BinaryCallAsync(input);
            var responseData = output.ToArray();
            var response = new FileContentResult(responseData, "application/octet-stream");
            if (SingleHttpConnection.ValidateChecksums) {
                controller.Response.Headers.Append(HeaderNames.Header_Checksum, responseData.GetChecksum().ToString());
            }
            if (SingleHttpConnection.ValidateLengths) {
                controller.Response.Headers.Append(HeaderNames.Header_Length, responseData.Length.ToString());
            }
            return response;
        }
    }
}

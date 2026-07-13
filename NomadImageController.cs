using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JellyNomad;

[ApiController]
[Route("JellyNomad")]
[AllowAnonymous]
public class NomadImageController : ControllerBase
{
    [HttpGet("ChannelImage/{channelId}")]
    [ResponseCache(Duration = 86400)]
    public IActionResult GetChannelImage(string channelId)
    {
        var channel = Plugin.Instance?.Configuration.Channels.FirstOrDefault(c => c.Id == channelId);

        var dataUrl = channel?.ChannelImage;
        if (string.IsNullOrEmpty(dataUrl) || !dataUrl.StartsWith("data:", StringComparison.Ordinal))
        {
            return NotFound();
        }

        var commaIndex = dataUrl.IndexOf(',');
        if (commaIndex < 0)
        {
            return NotFound();
        }

        var meta = dataUrl[5..commaIndex];
        var base64 = dataUrl[(commaIndex + 1)..];
        var mimeType = meta.Split(';')[0];

        if (string.IsNullOrEmpty(mimeType))
        {
            mimeType = "image/png";
        }

        byte[] bytes;
        try 
        { 
            bytes = Convert.FromBase64String(base64); 
        }
        catch 
        { 
            return BadRequest(); 
        }

        return File(bytes, mimeType);
    }
}

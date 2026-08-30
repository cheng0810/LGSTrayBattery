using EmbedIO.Routing;
using EmbedIO;
using EmbedIO.WebApi;
using System.Reflection;

namespace LGSTrayCore.HttpServer;

public class HttpControllerFactory
{
    private readonly ILogiDeviceCollection _logiDeviceCollection;
    private readonly int _staleAfterSeconds;

    public HttpControllerFactory(
        ILogiDeviceCollection logiDeviceCollection,
        Microsoft.Extensions.Options.IOptions<LGSTrayPrimitives.AppSettings> appSettings)
    {
        _logiDeviceCollection = logiDeviceCollection;
        _staleAfterSeconds = appSettings.Value.HTTPServer.StaleAfterSeconds;
    }

    public HttpController CreateController()
    {
        return new HttpController(_logiDeviceCollection, _staleAfterSeconds);
    }
}

public class HttpController : WebApiController
{
    private static readonly string _assemblyVersion = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion!;
    private readonly ILogiDeviceCollection _logiDeviceCollection;
    private readonly int _staleAfterSeconds;

    public HttpController(ILogiDeviceCollection logiDeviceCollection, int staleAfterSeconds)
    {
        _logiDeviceCollection = logiDeviceCollection;
        _staleAfterSeconds = staleAfterSeconds;
    }

    private void DefaultResponse(string contentType = "text/html")
    {
        Response.ContentType = contentType;
        Response.DisableCaching();
        Response.KeepAlive = false;
        Response.Headers.Add("Access-Control-Allow-Origin", "*");
    }

    [Route(HttpVerbs.Get, "/")]
    [Route(HttpVerbs.Get, "/devices")]
    public void GetDevices()
    {
        DefaultResponse();

        using var tw = HttpContext.OpenResponseText();
        tw.Write("<html>");

        tw.Write("<b>By Device ID</b><br>");
        foreach (var logiDevice in _logiDeviceCollection.GetDevices())
        {
            tw.Write($"{logiDevice.DeviceName} : <a href=\"/device/{logiDevice.DeviceId}\">{logiDevice.DeviceId}</a><br>");
        }

        tw.Write("<br><b>By Device Name</b><br>");
        foreach (var logiDevice in _logiDeviceCollection.GetDevices())
        {
            tw.Write($"<a href=\"/device/{Uri.EscapeDataString(logiDevice.DeviceName)}\">{logiDevice.DeviceName}</a><br>");
        }

        tw.Write("<br><hr>");
        tw.Write($"<i>LGSTray version: {_assemblyVersion}</i><br>");
        tw.Write("</html>");

        return;
    }

    [Route(HttpVerbs.Get, "/device/{deviceIden}")]
    public void GetDevice(string deviceIden)
    {
        var logiDevice = _logiDeviceCollection.GetDevices().FirstOrDefault(x => x.DeviceId == deviceIden);
        logiDevice ??= _logiDeviceCollection.GetDevices().FirstOrDefault(x => x.DeviceName == deviceIden);

        using var tw = HttpContext.OpenResponseText();
        if (logiDevice == null)
        {
            HttpContext.Response.StatusCode = 404;
            tw.Write($"{deviceIden} not found.");
            return;
        }

        DefaultResponse("text/xml");

        tw.Write(logiDevice.GetXmlData(_staleAfterSeconds));
    }
}

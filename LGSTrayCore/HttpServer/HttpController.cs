using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using LGSTrayCore.Interfaces;
using LGSTrayPrimitives;
using System.Reflection;

namespace LGSTrayCore.HttpServer;

public class HttpControllerFactory
{
    private readonly ILogiDeviceCollection _logiDeviceCollection;

    public HttpControllerFactory(ILogiDeviceCollection logiDeviceCollection)
    {
        _logiDeviceCollection = logiDeviceCollection;
    }

    public HttpController CreateController()
    {
        return new HttpController(_logiDeviceCollection);
    }
}

public class HttpController : WebApiController
{
    private static readonly string _assemblyVersion = Assembly.GetEntryAssembly()?
                                                              .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                                                              .InformationalVersion! + "-strain08";
    private readonly ILogiDeviceCollection _logiDeviceCollection;

    public HttpController(ILogiDeviceCollection logiDeviceCollection)
    {
        _logiDeviceCollection = logiDeviceCollection;
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
            tw.Write($"{logiDevice.DeviceName} : <a href=\"/device/{logiDevice.DeviceSignature}\">{logiDevice.DeviceSignature}</a><br>");
        }

        tw.Write("<br><b>By Device Name</b><br>");
        foreach (var logiDevice in _logiDeviceCollection.GetDevices())
        {
            var source_prefix = logiDevice.DataSource == DataSource.Native ? "N-" : "G-";
            tw.Write($"<a href=\"/device/{Uri.EscapeDataString(logiDevice.DeviceSignature)}\">{source_prefix + logiDevice.DeviceName}</a><br>");
        }

        tw.Write("<br><hr>");
        tw.Write($"<i>LGSTray version: {_assemblyVersion}</i><br>");
        tw.Write("</html>");

        return;
    }

    [Route(HttpVerbs.Get, "/device/{deviceIden}")]
    public void GetDevice(string deviceIden)
    {
        var logiDevice = _logiDeviceCollection.GetDevices().FirstOrDefault(x => x.DeviceSignature == deviceIden);        

        using var tw = HttpContext.OpenResponseText();
        if (logiDevice == null)
        {
            HttpContext.Response.StatusCode = 404;
            tw.Write($"{deviceIden} not found.");
            return;
        }

        DefaultResponse("text/xml");

        tw.Write(logiDevice.GetXmlData());
    }
}

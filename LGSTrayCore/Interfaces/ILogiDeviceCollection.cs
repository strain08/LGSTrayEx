using LGSTrayPrimitives.MessageStructs;

namespace LGSTrayCore.Interfaces;

public interface ILogiDeviceCollection
{
    public IEnumerable<LogiDevice> GetDevices();
    public void OnInitMessage(InitMessage initMessage);
    public void OnUpdateMessage(UpdateMessage updateMessage);
}

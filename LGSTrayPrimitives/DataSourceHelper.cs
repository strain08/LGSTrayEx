namespace LGSTrayPrimitives;

public static class DataSourceHelper
{
    /// <summary>
    /// Determines the data source based on device ID pattern.
    /// GHub devices have "dev" prefix, Native devices use hex format.
    /// </summary>
    public static DataSource GetDataSource(string deviceId)
    {
        return deviceId.StartsWith("dev") ? DataSource.GHub : DataSource.Native;
    }
}
